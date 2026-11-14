import type { BuildReport, PropSpec } from './types';
import { withFiles } from './engine';

export interface BuildResult {
  blob: Blob;
  fileName: string;
  report: BuildReport;
}

function zipResult(bytes: Uint8Array, reportJson: string, fileName: string): BuildResult {
  return { blob: new Blob([bytes], { type: 'application/zip' }), fileName, report: JSON.parse(reportJson) as BuildReport };
}

export async function buildProp(spec: PropSpec, files: Map<string, Blob>): Promise<BuildResult> {
  const { result, report } = await withFiles([...files], e => e.BuildProp(JSON.stringify(spec)));
  return zipResult(result, report, `${spec.name}.zip`);
}

export interface PedSpec {
  name: string;
  pedType: string;
  splitMode: 'auto' | 'split';
  testCommand: boolean;
}

export async function buildPed(spec: PedSpec, ydd: File, ytd?: File | null, yft?: File | null): Promise<BuildResult> {
  const files: [string, Blob][] = [['ydd', ydd]];
  if (ytd) files.push(['ytd', ytd]);
  if (yft) files.push(['yft', yft]);
  const { result, report } = await withFiles(files, e => e.BuildPed(JSON.stringify(spec)));
  return zipResult(result, report, `${spec.name}.zip`);
}

export interface PreviewMesh { drawable: string; shader: number; texture?: string | null; positions: number[]; normals: number[]; uv: number[]; indices: number[] }
export interface PreviewBone { name: string; parent: number; t: number[]; r: number[] }
export interface Preview { file: string; kind: string; drawables: string[]; meshes: PreviewMesh[]; bones: PreviewBone[]; textures: Record<string, string>; triangles: number }

export async function inspect(file: File, ytd?: File | null): Promise<Preview> {
  const files: [string, Blob][] = [['file', file]];
  if (ytd) files.push(['ytd', ytd]);
  const { result } = await withFiles(files, e => e.Inspect(file.name));
  return JSON.parse(result) as Preview;
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
