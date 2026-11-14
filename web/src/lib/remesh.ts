import * as THREE from 'three';
import { mergeVertices } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { simplifyIndices } from './simplify';

export interface RemeshOptions {
  /** Total triangle budget for the whole model. */
  targetTriangles: number;
  /** Merge duplicate vertices first (AI and scanned meshes often have every triangle separate). */
  weld: boolean;
  /** Let edges and UV seams collapse and drop tiny floating fragments. Stronger, may stretch textures at seams. */
  aggressive: boolean;
}

export function countTriangles(root: THREE.Object3D): number {
  let n = 0;
  root.traverse(o => {
    const m = o as THREE.Mesh;
    if (m.isMesh) n += (m.geometry.index ? m.geometry.index.count : m.geometry.attributes.position.count) / 3;
  });
  return Math.round(n);
}

function positionsOf(g: THREE.BufferGeometry): Float32Array {
  const a = g.attributes.position;
  const out = new Float32Array(a.count * 3);
  for (let i = 0; i < a.count; i++) { out[i * 3] = a.getX(i); out[i * 3 + 1] = a.getY(i); out[i * 3 + 2] = a.getZ(i); }
  return out;
}

/**
 * Simplifies every mesh under `root` towards a total triangle budget (each mesh keeps its share).
 * `originals` stores the untouched geometries so the operation can be undone or re-run from scratch.
 */
export async function remesh(root: THREE.Object3D, opts: RemeshOptions, originals: Map<THREE.Mesh, THREE.BufferGeometry>): Promise<{ before: number; after: number }> {
  const meshes: THREE.Mesh[] = [];
  root.traverse(o => { if ((o as THREE.Mesh).isMesh) meshes.push(o as THREE.Mesh); });
  for (const m of meshes) if (!originals.has(m)) originals.set(m, m.geometry);

  const before = meshes.reduce((s, m) => {
    const g = originals.get(m)!;
    return s + (g.index ? g.index.count : g.attributes.position.count) / 3;
  }, 0);
  const ratio = Math.min(1, opts.targetTriangles / Math.max(1, before));

  for (const mesh of meshes) {
    let g = originals.get(mesh)!.clone();
    if (opts.weld) {
      // Normals/tangents differ per corner on faceted meshes and would block welding; drop and rebuild them.
      g.deleteAttribute('tangent');
      const hadNormals = !!g.attributes.normal;
      if (hadNormals) g.deleteAttribute('normal');
      g = mergeVertices(g, 1e-5);
      g.computeVertexNormals();
    }
    if (!g.index) g.setIndex(Array.from({ length: g.attributes.position.count }, (_, i) => i));
    const pos = positionsOf(g);
    const idx = Uint32Array.from(g.index!.array as ArrayLike<number>);
    const groups = g.groups.length ? g.groups : [{ start: 0, count: idx.length, materialIndex: 0 }];
    const newIndex: number[] = [];
    const newGroups: { start: number; count: number; materialIndex?: number }[] = [];
    for (const grp of groups) {
      const part = idx.subarray(grp.start, grp.start + grp.count);
      const simplified = ratio >= 1 ? part : await simplifyIndices(pos, Uint32Array.from(part), ratio,
        opts.aggressive ? 0.08 : 0.02, opts.aggressive ? ['Prune'] : ['LockBorder']);
      newGroups.push({ start: newIndex.length, count: simplified.length, materialIndex: grp.materialIndex });
      for (const i of simplified) newIndex.push(i);
    }
    g.setIndex(newIndex);
    g.clearGroups();
    if (newGroups.length > 1) for (const ng of newGroups) g.addGroup(ng.start, ng.count, ng.materialIndex);
    if (mesh.geometry !== originals.get(mesh)) mesh.geometry.dispose();
    mesh.geometry = g;
  }
  return { before: Math.round(before), after: countTriangles(root) };
}

export function restoreOriginals(originals: Map<THREE.Mesh, THREE.BufferGeometry>) {
  for (const [mesh, g] of originals) {
    if (mesh.geometry !== g) mesh.geometry.dispose();
    mesh.geometry = g;
  }
  originals.clear();
}
