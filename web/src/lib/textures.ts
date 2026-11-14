import * as THREE from 'three';

const MAX_UPLOAD_SIZE = 2048;

type ImageLike = { width: number; height: number };

function drawable(img: unknown): CanvasImageSource & ImageLike | null {
  if (!img) return null;
  if (typeof ImageBitmap !== 'undefined' && img instanceof ImageBitmap) return img;
  if (img instanceof HTMLImageElement || img instanceof HTMLCanvasElement) return img;
  if (typeof OffscreenCanvas !== 'undefined' && img instanceof OffscreenCanvas) return img;
  return null;
}

function canvasFor(tex: THREE.Texture): HTMLCanvasElement | null {
  const img = drawable(tex.image);
  if (!img || !img.width || !img.height) return null;
  const scale = Math.min(1, MAX_UPLOAD_SIZE / Math.max(img.width, img.height));
  const c = document.createElement('canvas');
  c.width = Math.max(1, Math.round(img.width * scale));
  c.height = Math.max(1, Math.round(img.height * scale));
  const ctx = c.getContext('2d')!;
  ctx.drawImage(img, 0, 0, c.width, c.height);
  return c;
}

function toBlob(c: HTMLCanvasElement): Promise<Blob> {
  return new Promise((res, rej) => c.toBlob(b => (b ? res(b) : rej(new Error('Could not encode texture'))), 'image/png'));
}

/** Plain PNG of a texture (diffuse / emissive). */
export async function texturePng(tex: THREE.Texture): Promise<Blob | null> {
  const c = canvasFor(tex);
  return c ? toBlob(c) : null;
}

/**
 * Normal map converted from glTF (OpenGL, +Y) to GTA (DirectX, -Y) by inverting green.
 */
export async function normalPng(tex: THREE.Texture): Promise<Blob | null> {
  const c = canvasFor(tex);
  if (!c) return null;
  const ctx = c.getContext('2d')!;
  const d = ctx.getImageData(0, 0, c.width, c.height);
  for (let i = 1; i < d.data.length; i += 4) d.data[i] = 255 - d.data[i];
  ctx.putImageData(d, 0, 0);
  return toBlob(c);
}

/**
 * Specular map from a glTF metallic-roughness texture: shininess = 1 - roughness (green channel).
 * GTA reads intensity from red (specMapIntMask = 1,0,0), so the value goes into every channel.
 */
export async function specFromRoughnessPng(tex: THREE.Texture, roughnessFactor: number): Promise<Blob | null> {
  const c = canvasFor(tex);
  if (!c) return null;
  const ctx = c.getContext('2d')!;
  const d = ctx.getImageData(0, 0, c.width, c.height);
  for (let i = 0; i < d.data.length; i += 4) {
    const rough = (d.data[i + 1] / 255) * roughnessFactor;
    const v = Math.round((1 - rough) * 255);
    d.data[i] = v; d.data[i + 1] = v; d.data[i + 2] = v; d.data[i + 3] = 255;
  }
  ctx.putImageData(d, 0, 0);
  return toBlob(c);
}

export function safeName(s: string, fallback = 'tex'): string {
  const n = s.toLowerCase().replace(/[^a-z0-9_]+/g, '_').replace(/^_+|_+$/g, '');
  return n.length ? n.slice(0, 48) : fallback;
}
