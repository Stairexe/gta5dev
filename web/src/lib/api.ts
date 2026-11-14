import type { BuildReport, PropSpec } from './types';

export interface BuildResult {
  blob: Blob;
  fileName: string;
  report: BuildReport;
}

async function readError(res: Response): Promise<string> {
  try {
    const j = await res.json();
    return j.error ?? `Request failed (${res.status})`;
  } catch {
    return `Request failed (${res.status})`;
  }
}

async function zipResponse(res: Response, fallbackName: string): Promise<BuildResult> {
  if (!res.ok) throw new Error(await readError(res));
  const header = res.headers.get('X-Build-Report');
  const report: BuildReport = header ? JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(header), c => c.charCodeAt(0)))) : { checks: [], warnings: [] };
  const cd = res.headers.get('Content-Disposition') ?? '';
  const m = /filename="?([^";]+)"?/.exec(cd);
  return { blob: await res.blob(), fileName: m?.[1] ?? fallbackName, report };
}

export async function buildProp(spec: PropSpec, files: Map<string, Blob>): Promise<BuildResult> {
  const form = new FormData();
  form.append('spec', JSON.stringify(spec));
  for (const [field, blob] of files) form.append(field, blob, `${field}.png`);
  const res = await fetch('/api/props/build', { method: 'POST', body: form });
  return zipResponse(res, `${spec.name}.zip`);
}

export interface PedSpec {
  name: string;
  pedType: string;
  splitMode: 'auto' | 'split';
  testCommand: boolean;
}

export async function buildPed(spec: PedSpec, ydd: File, ytd?: File | null, yft?: File | null): Promise<BuildResult> {
  const form = new FormData();
  form.append('spec', JSON.stringify(spec));
  form.append('ydd', ydd);
  if (ytd) form.append('ytd', ytd);
  if (yft) form.append('yft', yft);
  const res = await fetch('/api/peds/from-ydd', { method: 'POST', body: form });
  return zipResponse(res, `${spec.name}.zip`);
}

export interface PreviewMesh { drawable: string; shader: number; texture?: string | null; positions: number[]; normals: number[]; uv: number[]; indices: number[] }
export interface PreviewBone { name: string; parent: number; t: number[]; r: number[] }
export interface Preview { file: string; kind: string; drawables: string[]; meshes: PreviewMesh[]; bones: PreviewBone[]; textures: Record<string, string>; triangles: number }

export async function inspect(file: File, ytd?: File | null): Promise<Preview> {
  const form = new FormData();
  form.append('file', file);
  if (ytd) form.append('ytd', ytd);
  const res = await fetch('/api/inspect', { method: 'POST', body: form });
  if (!res.ok) throw new Error(await readError(res));
  return res.json();
}

export function download(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
