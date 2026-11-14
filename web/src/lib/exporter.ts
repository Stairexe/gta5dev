import * as THREE from 'three';
import type { AlphaMode, AnimSpec, BoneSpec, CollisionMode, MaterialSpec, MeshSpec, PropSpec, ShaderChoice, TextureSpec } from './types';
import { normalPng, safeName, specFromRoughnessPng, texturePng } from './textures';
import { compact, simplifyIndices } from './simplify';

/** glTF (Y-up, +Z front) -> GTA (Z-up): (x, y, z) -> (x, -z, y). */
export const YUP_TO_GTA = new THREE.Matrix4().set(
  1, 0, 0, 0,
  0, 0, -1, 0,
  0, 1, 0, 0,
  0, 0, 0, 1,
);

/** World matrix of `obj` relative to `root` (so display wrappers above the root never leak into the export). */
export function rel(root: THREE.Object3D, obj: THREE.Object3D): THREE.Matrix4 {
  return new THREE.Matrix4().copy(root.matrixWorld).invert().multiply(obj.matrixWorld);
}

export type PivotMode = 'bottom-center' | 'center' | 'origin';

export interface PlacementOptions {
  scale: number;
  rotationZ: number; // degrees, around the GTA up axis
  pivot: PivotMode;
}

export interface MaterialOverride {
  shader: ShaderChoice;
  alpha: AlphaMode | 'auto';
  emissiveMultiplier: number;
}

export interface ExportOptions extends PlacementOptions {
  name: string;
  lodDist: number;
  generateLods: boolean;
  collision: CollisionMode;
  collisionMaterial: number;
  dynamic: boolean;
  testCommand: boolean;
  /** Nodes that move independently (become bones). */
  movingParts: THREE.Object3D[];
  animations: AnimationSource[];
  materialOverrides: Map<string, MaterialOverride>;
  /** Longest texture side sent to the engine (GTA streams textures; 1024-2048 is typical for props). */
  maxTextureSize?: number;
}

/** Something that can pose the scene at time t (seconds). */
export interface AnimationSource {
  name: string;
  duration: number;
  fps: number;
  apply(t: number): void;
}

/** Linear (rotation + scale + axis swap) part of the export transform. */
export function linearTransform(o: PlacementOptions): THREE.Matrix4 {
  const s = new THREE.Matrix4().makeScale(o.scale, o.scale, o.scale);
  const r = new THREE.Matrix4().makeRotationZ(THREE.MathUtils.degToRad(o.rotationZ));
  return new THREE.Matrix4().multiplyMatrices(r, s).multiply(YUP_TO_GTA);
}

/** Full export transform (three world space -> GTA object space), including the pivot shift. */
export function exportTransform(root: THREE.Object3D, o: PlacementOptions): THREE.Matrix4 {
  const lin = linearTransform(o);
  root.updateMatrixWorld(true);
  const box = new THREE.Box3();
  const tmp = new THREE.Vector3();
  root.traverse(obj => {
    const m = obj as THREE.Mesh;
    if (!m.isMesh || !m.geometry?.attributes.position) return;
    const pos = m.geometry.attributes.position;
    const mw = new THREE.Matrix4().multiplyMatrices(lin, rel(root, m));
    for (let i = 0; i < pos.count; i++) box.expandByPoint(tmp.fromBufferAttribute(pos, i).applyMatrix4(mw));
  });
  const shift = new THREE.Vector3();
  if (!box.isEmpty() && o.pivot !== 'origin') {
    const c = box.getCenter(new THREE.Vector3());
    shift.set(-c.x, -c.y, o.pivot === 'bottom-center' ? -box.min.z : -c.z);
  }
  return new THREE.Matrix4().makeTranslation(shift.x, shift.y, shift.z).multiply(lin);
}

export interface SceneStats { meshes: number; triangles: number; vertices: number; size: THREE.Vector3; materials: THREE.Material[] }

export function sceneStats(root: THREE.Object3D, o: PlacementOptions): SceneStats {
  const E = exportTransform(root, o);
  const box = new THREE.Box3();
  const v = new THREE.Vector3();
  let meshes = 0, triangles = 0, vertices = 0;
  const mats = new Set<THREE.Material>();
  root.traverse(obj => {
    const m = obj as THREE.Mesh;
    if (!m.isMesh) return;
    meshes++;
    const g = m.geometry;
    const pos = g.attributes.position;
    vertices += pos.count;
    triangles += (g.index ? g.index.count : pos.count) / 3;
    const mw = new THREE.Matrix4().multiplyMatrices(E, rel(root, m));
    for (let i = 0; i < pos.count; i += Math.max(1, Math.floor(pos.count / 2000))) box.expandByPoint(v.fromBufferAttribute(pos, i).applyMatrix4(mw));
    (Array.isArray(m.material) ? m.material : [m.material]).forEach(x => mats.add(x));
  });
  return { meshes, triangles: Math.round(triangles), vertices, size: box.isEmpty() ? new THREE.Vector3() : box.getSize(new THREE.Vector3()), materials: [...mats] };
}

/** Bone index each node maps to: the nearest moving-part ancestor (or itself), 0 = root. */
function ownerBone(obj: THREE.Object3D, parts: THREE.Object3D[]): number {
  for (let o: THREE.Object3D | null = obj; o; o = o.parent) {
    const i = parts.indexOf(o);
    if (i >= 0) return i + 1;
  }
  return 0;
}

/** Orders moving parts so that parents always come before children. */
export function orderParts(parts: THREE.Object3D[]): THREE.Object3D[] {
  const depth = (o: THREE.Object3D) => { let d = 0; for (let p = o.parent; p; p = p.parent) d++; return d; };
  return [...parts].sort((a, b) => depth(a) - depth(b));
}

/** Bone transforms (GTA object space) for the current pose: B = E * W_node * E_lin^-1. Index 0 = root = identity. */
function boneGlobals(root: THREE.Object3D, parts: THREE.Object3D[], E: THREE.Matrix4, linInv: THREE.Matrix4): THREE.Matrix4[] {
  return [new THREE.Matrix4(), ...parts.map(p => new THREE.Matrix4().multiplyMatrices(E, rel(root, p)).multiply(linInv))];
}

function parentBone(part: THREE.Object3D, parts: THREE.Object3D[]): number {
  return part.parent ? ownerBone(part.parent, parts) : 0;
}

/** Warns about moving parts whose world scale is not 1 (bones carry no scale). */
function checkPartScale(root: THREE.Object3D, parts: THREE.Object3D[], warnings: string[]) {
  const s = new THREE.Vector3();
  for (const p of parts) {
    rel(root, p).decompose(new THREE.Vector3(), new THREE.Quaternion(), s);
    if (Math.abs(s.x - 1) > 0.01 || Math.abs(s.y - 1) > 0.01 || Math.abs(s.z - 1) > 0.01)
      warnings.push(`Moving part '${p.name}' is scaled (${s.x.toFixed(2)}); apply its scale in your 3D tool for exact results.`);
  }
}

function locals(parts: THREE.Object3D[], globals: THREE.Matrix4[]) {
  return parts.map((p, i) => {
    const parent = globals[parentBone(p, parts)];
    const local = new THREE.Matrix4().copy(parent).invert().multiply(globals[i + 1]);
    const t = new THREE.Vector3(), r = new THREE.Quaternion(), s = new THREE.Vector3();
    local.decompose(t, r, s);
    return { t, r, s };
  });
}

/** Converts the scene into the engine request + the texture files to upload. */
export async function buildPropSpec(root: THREE.Object3D, opts: ExportOptions): Promise<{ spec: PropSpec; files: Map<string, Blob>; warnings: string[] }> {
  const warnings: string[] = [];
  const parts = orderParts(opts.movingParts);
  root.updateMatrixWorld(true);
  const E = exportTransform(root, opts);
  const lin = linearTransform(opts);
  const linInv = lin.clone().invert();

  // ---- bones (rest pose) ----
  checkPartScale(root, parts, warnings);
  const restGlobals = boneGlobals(root, parts, E, linInv);
  const usedNames = new Set<string>(['root']);
  const bones: BoneSpec[] = [{ name: 'root', parent: -1, t: [0, 0, 0], r: [0, 0, 0, 1], s: [1, 1, 1] }];
  locals(parts, restGlobals).forEach(({ t, r }, i) => {
    let n = safeName(parts[i].name || `part_${i + 1}`, `part_${i + 1}`);
    while (usedNames.has(n)) n += '_';
    usedNames.add(n);
    bones.push({ name: n, parent: parentBone(parts[i], parts), t: [t.x, t.y, t.z], r: [r.x, r.y, r.z, r.w], s: [1, 1, 1] });
  });

  // ---- materials + textures ----
  const materials: MaterialSpec[] = [];
  const textures: TextureSpec[] = [];
  const files = new Map<string, Blob>();
  const matIndex = new Map<THREE.Material, number>();
  const texNames = new Map<string, string>();

  async function addTexture(tex: THREE.Texture, kind: TextureSpec['kind'], base: string, make: () => Promise<Blob | null>): Promise<string | undefined> {
    const key = `${tex.uuid}:${kind}`;
    const existing = texNames.get(key);
    if (existing) return existing;
    const blob = await make();
    if (!blob) { warnings.push(`Texture of '${base}' could not be read; it was skipped.`); return undefined; }
    let n = safeName(`${base}_${kind === 'diffuse' ? 'd' : kind === 'normal' ? 'n' : 's'}`);
    while ([...texNames.values()].includes(n)) n += '_';
    const field = `tex${textures.length}`;
    textures.push({ name: n, kind, field });
    files.set(field, blob);
    texNames.set(key, n);
    return n;
  }

  async function materialFor(m: THREE.Material): Promise<number> {
    const known = matIndex.get(m);
    if (known !== undefined) return known;
    const std = m as THREE.MeshStandardMaterial;
    const base = safeName(m.name || `${opts.name}_m${materials.length}`, `m${materials.length}`);
    const ov = opts.materialOverrides.get(m.uuid);
    const spec: MaterialSpec = { name: m.name || base, shader: 'auto', alpha: 'opaque', emissiveMultiplier: ov?.emissiveMultiplier ?? 1 };
    const useEmissive = ov?.shader === 'emissive' || (ov?.shader !== 'default' && !!std.emissiveMap && !std.map);
    const diffuseTex = useEmissive && std.emissiveMap ? std.emissiveMap : std.map;
    if (diffuseTex) spec.diffuse = await addTexture(diffuseTex, 'diffuse', base, () => texturePng(diffuseTex, opts.maxTextureSize));
    if (!spec.diffuse) {
      const c = (useEmissive ? std.emissive : std.color) ?? new THREE.Color(0.8, 0.8, 0.8);
      const lin = c.clone().convertLinearToSRGB();
      spec.color = [Math.round(lin.r * 255), Math.round(lin.g * 255), Math.round(lin.b * 255), Math.round((std.opacity ?? 1) * 255)];
    }
    if (!useEmissive && std.normalMap) spec.normal = await addTexture(std.normalMap, 'normal', base, () => normalPng(std.normalMap!, opts.maxTextureSize));
    if (!useEmissive && std.roughnessMap) spec.specular = await addTexture(std.roughnessMap, 'specular', base, () => specFromRoughnessPng(std.roughnessMap!, std.roughness ?? 1, opts.maxTextureSize));
    spec.shader = useEmissive ? 'emissive' : 'auto';
    const autoAlpha: AlphaMode = m.transparent && (m.opacity < 1 || !!std.map) ? 'blend' : m.alphaTest > 0 ? 'cutout' : 'opaque';
    spec.alpha = ov && ov.alpha !== 'auto' ? ov.alpha : autoAlpha;
    materials.push(spec);
    matIndex.set(m, materials.length - 1);
    return materials.length - 1;
  }

  // ---- meshes (high LOD) ----
  const high: MeshSpec[] = [];
  const simplifySources: { spec: MeshSpec; pos: Float32Array }[] = [];
  const meshes: THREE.Mesh[] = [];
  root.traverse(o => { if ((o as THREE.Mesh).isMesh && (o as THREE.Mesh).visible) meshes.push(o as THREE.Mesh); });
  for (const mesh of meshes) {
    if ((mesh as THREE.SkinnedMesh).isSkinnedMesh) warnings.push(`'${mesh.name}' is a skinned mesh; it was exported in its bind pose as a rigid part.`);
    const bone = ownerBone(mesh, parts);
    // Vertex transform into the owning bone's space.
    const M = bone === 0
      ? new THREE.Matrix4().multiplyMatrices(E, rel(root, mesh))
      : new THREE.Matrix4().multiplyMatrices(lin, rel(root, parts[bone - 1]).invert()).multiply(rel(root, mesh));
    const N = new THREE.Matrix3().getNormalMatrix(M);
    const flip = M.determinant() < 0;
    const g = mesh.geometry;
    const pos = g.attributes.position, nrm = g.attributes.normal, uv = g.attributes.uv, col = g.attributes.color;
    const index = g.index ? Array.from(g.index.array as ArrayLike<number>) : Array.from({ length: pos.count }, (_, i) => i);
    const groups = g.groups.length ? g.groups : [{ start: 0, count: index.length, materialIndex: 0 }];
    const mats = Array.isArray(mesh.material) ? mesh.material : [mesh.material];

    const P: number[] = new Array(pos.count * 3), Nn: number[] = new Array(pos.count * 3), U: number[] = new Array(pos.count * 2);
    const C: number[] | undefined = col ? new Array(pos.count * 4) : undefined;
    const v = new THREE.Vector3();
    for (let i = 0; i < pos.count; i++) {
      v.fromBufferAttribute(pos, i).applyMatrix4(M);
      P[i * 3] = v.x; P[i * 3 + 1] = v.y; P[i * 3 + 2] = v.z;
      if (nrm) v.fromBufferAttribute(nrm, i).applyMatrix3(N).normalize(); else v.set(0, 0, 1);
      Nn[i * 3] = v.x; Nn[i * 3 + 1] = v.y; Nn[i * 3 + 2] = v.z;
      U[i * 2] = uv ? uv.getX(i) : 0; U[i * 2 + 1] = uv ? uv.getY(i) : 0;
      if (C && col) {
        const c = new THREE.Color(col.getX(i), col.getY(i), col.getZ(i)).convertLinearToSRGB();
        C[i * 4] = Math.round(c.r * 255); C[i * 4 + 1] = Math.round(c.g * 255); C[i * 4 + 2] = Math.round(c.b * 255);
        C[i * 4 + 3] = Math.round((col.itemSize > 3 ? col.getW(i) : 1) * 255);
      }
    }
    for (const grp of groups) {
      const mat = mats[grp.materialIndex ?? 0] ?? mats[0];
      const tri = index.slice(grp.start, grp.start + Math.min(grp.count, index.length - grp.start));
      if (flip) for (let i = 0; i + 2 < tri.length; i += 3) [tri[i + 1], tri[i + 2]] = [tri[i + 2], tri[i + 1]];
      const arrays = [{ data: P, size: 3 }, { data: Nn, size: 3 }, { data: U, size: 2 }, ...(C ? [{ data: C, size: 4 }] : [])];
      const c = compact(Uint32Array.from(tri), arrays);
      const spec: MeshSpec = {
        material: await materialFor(mat), bone,
        positions: c.arrays[0], normals: c.arrays[1], uv0: c.arrays[2], colors: C ? c.arrays[3] : undefined,
        indices: c.indices,
      };
      if (spec.indices.length >= 3) {
        high.push(spec);
        simplifySources.push({ spec, pos: Float32Array.from(spec.positions) });
      }
    }
  }
  if (!high.length) throw new Error('The model has no visible triangles.');

  // ---- LODs ----
  async function lodLevel(ratio: number): Promise<MeshSpec[]> {
    const out: MeshSpec[] = [];
    for (const { spec, pos } of simplifySources) {
      const tris = spec.indices.length / 3;
      const idx = tris < 64 ? Uint32Array.from(spec.indices) : await simplifyIndices(pos, Uint32Array.from(spec.indices), ratio);
      const arrays = [{ data: spec.positions, size: 3 }, { data: spec.normals, size: 3 }, { data: spec.uv0, size: 2 }, ...(spec.colors ? [{ data: spec.colors, size: 4 }] : [])];
      const c = compact(idx, arrays);
      out.push({ ...spec, positions: c.arrays[0], normals: c.arrays[1], uv0: c.arrays[2], colors: spec.colors ? c.arrays[3] : undefined, indices: c.indices });
    }
    return out;
  }
  const totalTris = high.reduce((s, m) => s + m.indices.length / 3, 0);
  const medium = opts.generateLods && totalTris > 300 ? await lodLevel(0.5) : undefined;
  const low = opts.generateLods && totalTris > 300 ? await lodLevel(0.2) : undefined;

  // ---- collision mesh (object space, simplified) ----
  let colPositions: number[] | undefined, colIndices: number[] | undefined;
  if (opts.collision === 'mesh') {
    const P: number[] = [], I: number[] = [];
    for (const m of low ?? high) {
      const bg = restGlobals[m.bone];
      const base = P.length / 3;
      const v = new THREE.Vector3();
      for (let i = 0; i < m.positions.length; i += 3) {
        v.set(m.positions[i], m.positions[i + 1], m.positions[i + 2]).applyMatrix4(bg);
        P.push(v.x, v.y, v.z);
      }
      for (const i of m.indices) I.push(i + base);
    }
    const target = Math.min(1, 4000 / Math.max(1, I.length / 3));
    const idx = await simplifyIndices(Float32Array.from(P), Uint32Array.from(I), target, 0.05);
    const c = compact(idx, [{ data: P, size: 3 }]);
    colPositions = c.arrays[0];
    colIndices = c.indices;
  }

  // ---- animations: sample every frame and convert to bone-local transforms ----
  const animations: AnimSpec[] = [];
  for (const a of opts.animations) {
    if (!parts.length) { warnings.push(`Animation '${a.name}' was skipped: mark at least one moving part.`); continue; }
    const frames = Math.max(2, Math.round(a.duration * a.fps) + 1);
    const T: number[][] = parts.map(() => []), R: number[][] = parts.map(() => []);
    for (let f = 0; f < frames; f++) {
      a.apply(Math.min(a.duration, f / a.fps));
      root.updateMatrixWorld(true);
      locals(parts, boneGlobals(root, parts, E, linInv)).forEach(({ t, r }, i) => {
        T[i].push(t.x, t.y, t.z);
        R[i].push(r.x, r.y, r.z, r.w);
      });
    }
    a.apply(0);
    root.updateMatrixWorld(true);
    const varies = (arr: number[], n: number) => {
      for (let i = n; i < arr.length; i++) if (Math.abs(arr[i] - arr[i % n]) > 1e-5) return true;
      return false;
    };
    const tracks = parts.map((_, i) => ({ bone: i + 1, t: varies(T[i], 3) ? T[i] : undefined, r: varies(R[i], 4) ? R[i] : undefined }))
      .filter(tr => tr.t || tr.r);
    if (!tracks.length) { warnings.push(`Animation '${a.name}' does not move any part; it was skipped.`); continue; }
    animations.push({ name: safeName(a.name, 'anim'), fps: a.fps, frameCount: frames, tracks });
  }

  const spec: PropSpec = {
    name: safeName(opts.name, 'my_prop'),
    lodDist: opts.lodDist,
    dynamic: opts.dynamic,
    testCommand: opts.testCommand,
    collision: { mode: opts.collision, material: opts.collisionMaterial, positions: colPositions, indices: colIndices },
    materials,
    textures,
    bones,
    lods: { high, medium, low },
    animations,
  };
  return { spec, files, warnings };
}
