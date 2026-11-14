import * as THREE from 'three';
import { rel, YUP_TO_GTA, type AnimationSource } from '../lib/exporter';

export interface Key { t: number; pos: [number, number, number]; quat: [number, number, number, number] }
export interface PartTrack { node: THREE.Object3D; keys: Key[] }
export interface Clip { id: string; name: string; duration: number; fps: number; tracks: PartTrack[]; gltf?: THREE.AnimationClip }

export type GtaAxis = 'x' | 'y' | 'z';
export type PivotPoint = 'center' | 'bottom' | 'top' | '-x' | '+x' | '-y' | '+y';

/** GTA axis as a direction in the model's own (glTF, Y-up) space. */
export function gtaAxisInModel(axis: GtaAxis): THREE.Vector3 {
  const gta = new THREE.Vector3(axis === 'x' ? 1 : 0, axis === 'y' ? 1 : 0, axis === 'z' ? 1 : 0);
  return gta.applyMatrix4(YUP_TO_GTA.clone().invert()).normalize();
}

// ---------------------------------------------------------------- rest pose

export class RestPose {
  private readonly saved = new Map<THREE.Object3D, { p: THREE.Vector3; q: THREE.Quaternion; s: THREE.Vector3 }>();
  constructor(root: THREE.Object3D) { this.capture(root); }
  capture(root: THREE.Object3D) {
    this.saved.clear();
    root.traverse(o => this.saved.set(o, { p: o.position.clone(), q: o.quaternion.clone(), s: o.scale.clone() }));
  }
  restore() {
    for (const [o, v] of this.saved) { o.position.copy(v.p); o.quaternion.copy(v.q); o.scale.copy(v.s); }
  }
  get(o: THREE.Object3D) { return this.saved.get(o); }
}

// ---------------------------------------------------------------- sampling

function sampleTrack(track: PartTrack, t: number) {
  const k = track.keys;
  if (!k.length) return;
  let a = k[0], b = k[k.length - 1];
  if (t <= a.t) b = a;
  else if (t >= b.t) a = b;
  else for (let i = 0; i < k.length - 1; i++) if (t >= k[i].t && t <= k[i + 1].t) { a = k[i]; b = k[i + 1]; break; }
  const f = b.t > a.t ? (t - a.t) / (b.t - a.t) : 0;
  const pa = new THREE.Vector3(...a.pos), pb = new THREE.Vector3(...b.pos);
  track.node.position.copy(pa.lerp(pb, f));
  const qa = new THREE.Quaternion(...a.quat), qb = new THREE.Quaternion(...b.quat);
  track.node.quaternion.copy(qa.slerp(qb, f));
}

export function applyClip(clip: Clip, t: number, mixer?: THREE.AnimationMixer) {
  if (clip.gltf && mixer) {
    mixer.stopAllAction();
    const action = mixer.clipAction(clip.gltf);
    action.play();
    mixer.setTime(t);
    return;
  }
  for (const tr of clip.tracks) sampleTrack(tr, t);
}

export function toSource(clip: Clip, mixer?: THREE.AnimationMixer): AnimationSource {
  return { name: clip.name, duration: clip.duration, fps: clip.fps, apply: t => applyClip(clip, t, mixer) };
}

export function upsertKey(track: PartTrack, t: number) {
  const n = track.node;
  const key: Key = { t, pos: [n.position.x, n.position.y, n.position.z], quat: [n.quaternion.x, n.quaternion.y, n.quaternion.z, n.quaternion.w] };
  const i = track.keys.findIndex(k => Math.abs(k.t - t) < 1e-3);
  if (i >= 0) track.keys[i] = key; else track.keys.push(key);
  track.keys.sort((a, b) => a.t - b.t);
}

// ---------------------------------------------------------------- presets

function parentSpaceAxis(root: THREE.Object3D, node: THREE.Object3D, axis: GtaAxis): THREE.Vector3 {
  const modelAxis = gtaAxisInModel(axis);
  if (!node.parent) return modelAxis;
  const q = new THREE.Quaternion();
  rel(root, node.parent).decompose(new THREE.Vector3(), q, new THREE.Vector3());
  return modelAxis.applyQuaternion(q.invert()).normalize();
}

function keyAt(t: number, p: THREE.Vector3, q: THREE.Quaternion): Key {
  return { t, pos: [p.x, p.y, p.z], quat: [q.x, q.y, q.z, q.w] };
}

export interface HingePreset { kind: 'hinge'; axis: GtaAxis; angle: number; duration: number; returnToStart: boolean }
export interface SpinPreset { kind: 'spin'; axis: GtaAxis; duration: number; reverse: boolean }
export interface SlidePreset { kind: 'slide'; axis: GtaAxis; distance: number; duration: number; returnToStart: boolean }
export type Preset = HingePreset | SpinPreset | SlidePreset;

/** Keys for a preset motion, relative to the node's rest transform. */
export function presetKeys(root: THREE.Object3D, node: THREE.Object3D, rest: { p: THREE.Vector3; q: THREE.Quaternion }, preset: Preset): Key[] {
  const axis = parentSpaceAxis(root, node, preset.axis);
  const rot = (deg: number) => new THREE.Quaternion().setFromAxisAngle(axis, THREE.MathUtils.degToRad(deg)).multiply(rest.q);
  const d = preset.duration;
  switch (preset.kind) {
    case 'hinge':
      return preset.returnToStart
        ? [keyAt(0, rest.p, rest.q), keyAt(d / 2, rest.p, rot(preset.angle)), keyAt(d, rest.p, rest.q)]
        : [keyAt(0, rest.p, rest.q), keyAt(d, rest.p, rot(preset.angle))];
    case 'spin': {
      const s = preset.reverse ? -1 : 1;
      return [0, 1, 2, 3, 4].map(i => keyAt((d * i) / 4, rest.p, rot(s * 90 * i)));
    }
    case 'slide': {
      const moved = rest.p.clone().addScaledVector(axis, preset.distance);
      return preset.returnToStart
        ? [keyAt(0, rest.p, rest.q), keyAt(d / 2, moved, rest.q), keyAt(d, rest.p, rest.q)]
        : [keyAt(0, rest.p, rest.q), keyAt(d, moved, rest.q)];
    }
  }
}

// ---------------------------------------------------------------- pivots

/**
 * Inserts a pivot group between `node` and its parent, placed at a point of the node's bounds
 * (e.g. the -x side for a door hinge). Returns the pivot group, which becomes the moving part.
 */
export function insertPivot(root: THREE.Object3D, node: THREE.Object3D, where: PivotPoint): THREE.Object3D {
  const parent = node.parent;
  if (!parent) return node;
  root.updateMatrixWorld(true);
  // Bounds of the node's geometry in model space.
  const box = new THREE.Box3();
  const v = new THREE.Vector3();
  node.traverse(o => {
    const m = o as THREE.Mesh;
    if (!m.isMesh) return;
    const mw = rel(root, m);
    const pos = m.geometry.attributes.position;
    for (let i = 0; i < pos.count; i++) box.expandByPoint(v.fromBufferAttribute(pos, i).applyMatrix4(mw));
  });
  if (box.isEmpty()) return node;
  // Work in GTA axes so "-x / +y / bottom" mean the same thing as in game.
  const toGta = YUP_TO_GTA, toModel = YUP_TO_GTA.clone().invert();
  const g = box.clone().applyMatrix4(toGta);
  const c = g.getCenter(new THREE.Vector3());
  const p = c.clone();
  if (where === 'bottom') p.z = g.min.z;
  if (where === 'top') p.z = g.max.z;
  if (where === '-x') p.x = g.min.x;
  if (where === '+x') p.x = g.max.x;
  if (where === '-y') p.y = g.min.y;
  if (where === '+y') p.y = g.max.y;
  const pModel = p.applyMatrix4(toModel);
  const pParent = pModel.applyMatrix4(rel(root, parent).invert());

  const pivot = new THREE.Group();
  pivot.name = node.name || 'part';
  node.name = `${pivot.name}_geo`;
  pivot.position.copy(pParent);
  parent.add(pivot);
  parent.remove(node);
  pivot.add(node);
  node.position.sub(pParent);
  return pivot;
}

/** Nodes animated by a glTF clip (they must become moving parts). */
export function clipTargets(root: THREE.Object3D, clip: THREE.AnimationClip): THREE.Object3D[] {
  const out = new Set<THREE.Object3D>();
  for (const track of clip.tracks) {
    const parsed = THREE.PropertyBinding.parseTrackName(track.name);
    const node = THREE.PropertyBinding.findNode(root, parsed.nodeName);
    if (node && node !== root) out.add(node);
  }
  return [...out];
}
