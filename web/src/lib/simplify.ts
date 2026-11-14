import { MeshoptSimplifier, type Flags } from 'meshoptimizer';

/** Simplifies an indexed triangle list to roughly `ratio` of its triangles. Returns new indices into the same vertices. */
export async function simplifyIndices(positions: Float32Array, indices: Uint32Array, ratio: number, maxError = 0.02, flags: Flags[] = ['LockBorder']): Promise<Uint32Array> {
  await MeshoptSimplifier.ready;
  const target = Math.max(3, Math.floor((indices.length / 3) * ratio) * 3);
  if (target >= indices.length) return indices;
  const [out] = MeshoptSimplifier.simplify(indices, positions, 3, target, maxError, flags);
  return out.length >= 3 ? out : indices;
}

/** Removes vertices no longer referenced by `indices`, for every per-vertex array given (with its component count). */
export function compact(indices: Uint32Array, arrays: { data: number[]; size: number }[]): { indices: number[]; arrays: number[][] } {
  const map = new Map<number, number>();
  const newIdx: number[] = new Array(indices.length);
  for (let i = 0; i < indices.length; i++) {
    let n = map.get(indices[i]);
    if (n === undefined) { n = map.size; map.set(indices[i], n); }
    newIdx[i] = n;
  }
  const out = arrays.map(({ data, size }) => {
    const r: number[] = new Array(map.size * size);
    for (const [src, dst] of map) for (let k = 0; k < size; k++) r[dst * size + k] = data[src * size + k];
    return r;
  });
  return { indices: newIdx, arrays: out };
}
