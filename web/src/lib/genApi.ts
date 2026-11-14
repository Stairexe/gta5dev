// Client for the GTA Forge generation server (gen-server/), which runs on the user's own GPU PC.

export interface GenBackend { id: string; label: string; kind: 'local' | 'cloud' | 'test'; multiView: boolean; available: boolean; reason: string }
export interface GenHealth { name: string; version: string; gpu: string | null; tokenRequired: boolean; queue: number; backends: GenBackend[] }
export interface GenJob { id: string; backend: string; status: 'queued' | 'running' | 'done' | 'failed'; progress: number; stage: string; error: string | null; modelUrl: string | null }
export interface GenOptions { targetFaces: number; textureSize: number; resolution: number; seed?: number | null; pbr: boolean }

export interface GenConnection { url: string; token: string }

const KEY = 'gtaforge.gen.connection';

export function loadConnection(): GenConnection {
  try {
    const raw = localStorage.getItem(KEY);
    if (raw) return { url: '', token: '', ...JSON.parse(raw) };
  } catch { /* storage unavailable */ }
  return { url: 'http://localhost:8000', token: '' };
}

export function saveConnection(c: GenConnection) {
  try { localStorage.setItem(KEY, JSON.stringify(c)); } catch { /* storage unavailable */ }
}

function base(c: GenConnection) {
  return c.url.trim().replace(/\/+$/, '');
}

function headers(c: GenConnection): HeadersInit {
  return c.token ? { 'X-GtaForge-Token': c.token } : {};
}

async function check(res: Response): Promise<Response> {
  if (res.ok) return res;
  let msg = `Server answered ${res.status}`;
  try {
    const j = await res.json();
    msg = j.detail ?? j.error ?? msg;
  } catch { /* not JSON */ }
  throw new Error(msg);
}

/** Explains the usual reasons a browser cannot reach the server. */
export function explainConnectError(c: GenConnection, e: unknown): string {
  const url = base(c);
  if (window.location.protocol === 'https:' && url.startsWith('http://') && !/^http:\/\/(localhost|127\.0\.0\.1)(:|\/|$)/.test(url))
    return 'This page is HTTPS, so browsers block plain http:// addresses other than localhost. Run the server on this PC (http://localhost:8000) or expose it with an HTTPS tunnel (see the setup guide).';
  return `Could not reach ${url}. Is the generation server running, and does GTAFORGE_ORIGINS include ${window.location.origin}? (${(e as Error)?.message ?? e})`;
}

export async function health(c: GenConnection): Promise<GenHealth> {
  return (await check(await fetch(`${base(c)}/v1/health`, { headers: headers(c) }))).json();
}

export async function createJob(c: GenConnection, backend: string, images: Blob[], options: GenOptions): Promise<GenJob> {
  const form = new FormData();
  images.forEach((img, i) => form.append('images', img, `view_${i}.png`));
  form.append('backend', backend);
  form.append('options', JSON.stringify(options));
  return (await check(await fetch(`${base(c)}/v1/jobs`, { method: 'POST', body: form, headers: headers(c) }))).json();
}

export async function getJob(c: GenConnection, id: string): Promise<GenJob> {
  return (await check(await fetch(`${base(c)}/v1/jobs/${id}`, { headers: headers(c) }))).json();
}

export async function fetchModel(c: GenConnection, job: GenJob): Promise<File> {
  const res = await check(await fetch(`${base(c)}${job.modelUrl}`, { headers: headers(c) }));
  return new File([await res.blob()], `generated_${job.id}.glb`, { type: 'model/gltf-binary' });
}
