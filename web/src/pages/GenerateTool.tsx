import { useEffect, useRef, useState } from 'react';
import * as THREE from 'three';
import { Viewport, type ViewportApi } from '../three/Viewport';
import { loadGlb } from '../three/loadModel';
import {
  createJob, explainConnectError, fetchModel, getJob, health, loadConnection, saveConnection,
  type GenBackend, type GenConnection, type GenHealth, type GenJob,
} from '../lib/genApi';
import { download } from '../lib/api';
import { handOff } from '../lib/handoff';
import { Field, Section, Steps } from '../components/ui';

type Slot = 'front' | 'back' | 'left' | 'right';
const SLOTS: Slot[] = ['front', 'back', 'left', 'right'];

interface Result { job: GenJob; file: File; url: string; triangles: number; size: THREE.Vector3; thumb?: string }

const KIND_LABEL: Record<GenBackend['kind'], string> = { local: 'Your GPU', cloud: 'Cloud API', test: 'Test' };

export function GenerateTool() {
  const api = useRef<ViewportApi | null>(null);
  const shown = useRef<THREE.Object3D | null>(null);
  const [conn, setConn] = useState<GenConnection>(loadConnection);
  const [server, setServer] = useState<GenHealth | null>(null);
  const [connecting, setConnecting] = useState(false);
  const [connError, setConnError] = useState<string | null>(null);

  const [images, setImages] = useState<Partial<Record<Slot, File>>>({});
  const [backend, setBackend] = useState<string>('');
  const [targetFaces, setTargetFaces] = useState(30000);
  const [textureSize, setTextureSize] = useState(2048);
  const [resolution, setResolution] = useState(1024);
  const [seed, setSeed] = useState('');

  const [job, setJob] = useState<GenJob | null>(null);
  const [genError, setGenError] = useState<string | null>(null);
  const [results, setResults] = useState<Result[]>([]);
  const [current, setCurrent] = useState<Result | null>(null);

  const chosen = server?.backends.find(b => b.id === backend);
  const busy = !!job && (job.status === 'queued' || job.status === 'running');

  const connect = async () => {
    setConnecting(true); setConnError(null);
    saveConnection(conn);
    try {
      const h = await health(conn);
      setServer(h);
      const first = h.backends.find(b => b.available && b.kind === 'local') ?? h.backends.find(b => b.available);
      setBackend(b => (h.backends.some(x => x.id === b && x.available) ? b : first?.id ?? ''));
    } catch (e) {
      setServer(null);
      setConnError(explainConnectError(conn, e));
    } finally {
      setConnecting(false);
    }
  };

  // Try the saved connection once on open.
  useEffect(() => { void connect(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const show = async (r: Result) => {
    const v = api.current;
    if (!v) return;
    const gltf = await loadGlb(r.file);
    if (shown.current) v.scene.remove(shown.current);
    v.scene.add(gltf.scene); // generated GLBs are Y-up like the display
    shown.current = gltf.scene;
    setCurrent(r);
    setTimeout(() => v.frame(new THREE.Box3().setFromObject(gltf.scene)), 0);
  };

  const generate = async () => {
    if (!images.front || !chosen) return;
    setGenError(null);
    const views = SLOTS.map(s => images[s]).filter((f): f is File => !!f);
    try {
      let j = await createJob(conn, chosen.id, chosen.multiView ? views : [images.front], {
        targetFaces, textureSize, resolution, pbr: true, seed: seed === '' ? null : Number(seed),
      });
      setJob(j);
      while (j.status === 'queued' || j.status === 'running') {
        await new Promise(r => setTimeout(r, 1200));
        j = await getJob(conn, j.id);
        setJob(j);
      }
      if (j.status === 'failed') throw new Error(j.error ?? 'Generation failed.');
      const file = await fetchModel(conn, j);
      const gltf = await loadGlb(file);
      let triangles = 0;
      gltf.scene.traverse(o => {
        const m = o as THREE.Mesh;
        if (m.isMesh) triangles += (m.geometry.index?.count ?? m.geometry.attributes.position.count) / 3;
      });
      const size = new THREE.Box3().setFromObject(gltf.scene).getSize(new THREE.Vector3());
      const r: Result = { job: j, file, url: URL.createObjectURL(file), triangles: Math.round(triangles), size, thumb: images.front ? URL.createObjectURL(images.front) : undefined };
      setResults(rs => [r, ...rs]);
      await show(r);
    } catch (e) {
      setGenError((e as Error).message);
    }
  };

  const pick = (slot: Slot, f?: File) => setImages(im => ({ ...im, [slot]: f }));

  return (
    <div className="tool">
      <aside className="panel">
        <Steps steps={[
          { id: 'gen-connect', label: 'Connect', done: !!server },
          { id: 'gen-images', label: 'Images', done: !!images.front },
          { id: 'gen-engine', label: 'Engine', done: !!chosen?.available },
          { id: 'gen-run', label: 'Generate', done: !!current },
          { id: 'gen-use', label: 'Use it', disabled: !current },
        ]} />

        <Section title="Generation server" step={1} id="gen-connect" right={server && <span className="pill pill-ok">Connected</span>}>
          <p className="muted small">Runs on your GPU PC (TRELLIS.2) or forwards to Meshy / Tripo with your keys. <a className="link" href="https://github.com/Stairexe/gta5dev/tree/claude/amazing-euler-xroar2/gen-server" target="_blank" rel="noreferrer">Setup guide</a></p>
          <Field label="Server address">
            <input value={conn.url} onChange={e => setConn({ ...conn, url: e.target.value })} placeholder="http://localhost:8000" />
          </Field>
          <Field label="Access token" hint={server?.tokenRequired ? 'This server requires a token.' : undefined}>
            <input type="password" value={conn.token} onChange={e => setConn({ ...conn, token: e.target.value })} placeholder="GTAFORGE_TOKEN (optional)" />
          </Field>
          <button className="btn" onClick={connect} disabled={connecting}>{connecting ? 'Connecting…' : server ? 'Reconnect' : 'Connect'}</button>
          {connError && <p className="error small">{connError}</p>}
          {server && (
            <dl className="stats">
              <div><dt>GPU</dt><dd>{server.gpu ?? 'none detected'}</dd></div>
              <div><dt>Queue</dt><dd>{server.queue} job(s)</dd></div>
            </dl>
          )}
        </Section>

        <Section title="Reference images" step={2} id="gen-images">
          <p className="muted small">One object, plain background, not cropped. Front is required; the other views help engines that support multi-view.</p>
          <div className="slots">
            {SLOTS.map(s => (
              <ImageSlot key={s} label={s} file={images[s]} required={s === 'front'} onPick={f => pick(s, f)} />
            ))}
          </div>
        </Section>

        <Section title="Engine and quality" step={3} id="gen-engine">
          {!server && <p className="muted small">Connect to a server to see its engines.</p>}
          {server && (
            <div className="choices">
              {server.backends.map(b => (
                <label key={b.id} className={`choice ${backend === b.id ? 'on' : ''} ${b.available ? '' : 'off'}`} title={b.reason}>
                  <input type="radio" name="backend" disabled={!b.available} checked={backend === b.id} onChange={() => setBackend(b.id)} />
                  <span className="choice-main">
                    <strong>{b.label}</strong>
                    <span className="muted small">{b.available ? `${KIND_LABEL[b.kind]}${b.multiView ? ' · multi-view' : ''}` : b.reason}</span>
                  </span>
                </label>
              ))}
            </div>
          )}
          <Field label={`Target triangles: ${targetFaces.toLocaleString()}`} hint="Game props: 5k-40k. You can remesh further in Prop Studio.">
            <input type="range" min={2000} max={200000} step={1000} value={targetFaces} onChange={e => setTargetFaces(+e.target.value)} />
          </Field>
          <div className="row">
            <Field label="Texture">
              <select value={textureSize} onChange={e => setTextureSize(+e.target.value)}>
                {[1024, 2048, 4096].map(s => <option key={s} value={s}>{s} px</option>)}
              </select>
            </Field>
            <Field label="Detail (TRELLIS.2)">
              <select value={resolution} onChange={e => setResolution(+e.target.value)}>
                <option value={512}>Fast (512³)</option>
                <option value={1024}>Standard (1024³)</option>
                <option value={1536}>High (1536³)</option>
              </select>
            </Field>
            <Field label="Seed">
              <input value={seed} onChange={e => setSeed(e.target.value.replace(/[^0-9]/g, ''))} placeholder="random" />
            </Field>
          </div>
        </Section>

        <Section title="Generate" step={4} id="gen-run">
          <button className="btn btn-primary btn-wide" disabled={!server || !images.front || !chosen?.available || busy} onClick={generate}>
            {busy ? 'Generating…' : 'Generate 3D model'}
          </button>
          {job && busy && (
            <div className="progress">
              <div className="progress-bar"><span style={{ width: `${job.progress}%` }} /></div>
              <span className="muted small">{job.stage} · {job.progress}%</span>
            </div>
          )}
          {genError && <p className="error small">{genError}</p>}
        </Section>

        <Section title="Use it" step={5} id="gen-use">
          {!current ? <p className="muted small">Your model appears here when it is ready.</p> : (
            <>
              <dl className="stats">
                <div><dt>Triangles</dt><dd>{current.triangles.toLocaleString()}</dd></div>
                <div><dt>Engine</dt><dd>{current.job.backend}</dd></div>
              </dl>
              <button className="btn btn-primary btn-wide" onClick={() => handOff(current.file, 'props')}>Make it a FiveM prop →</button>
              <div className="row">
                <button className="btn" onClick={() => handOff(current.file, 'animator')}>Animate parts</button>
                <button className="btn" onClick={() => download(current.file, current.file.name)}>Download .glb</button>
              </div>
            </>
          )}
          {results.length > 1 && (
            <div className="history">
              {results.map(r => (
                <button key={r.job.id} className={`history-item ${current === r ? 'on' : ''}`} onClick={() => show(r)} title={`${r.job.backend} · ${r.triangles.toLocaleString()} tris`}>
                  {r.thumb ? <img src={r.thumb} alt="" /> : r.job.id}
                </button>
              ))}
            </div>
          )}
        </Section>
      </aside>
      <main className="stage">
        <Viewport onReady={v => { api.current = v; }} />
        {!current && (
          <div className="stage-empty">
            <div className="empty-card">
              <strong>Image → 3D</strong>
              <p>Upload a reference image, generate a textured model on your own GPU, then turn it into a prop.</p>
            </div>
          </div>
        )}
      </main>
    </div>
  );
}

function ImageSlot({ label, file, required, onPick }: { label: string; file?: File; required?: boolean; onPick: (f?: File) => void }) {
  const input = useRef<HTMLInputElement>(null);
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!file) { setUrl(null); return; }
    const u = URL.createObjectURL(file);
    setUrl(u);
    return () => URL.revokeObjectURL(u);
  }, [file]);
  return (
    <div
      className={`slot ${file ? 'slot-full' : ''}`}
      onClick={() => input.current?.click()}
      onDragOver={e => e.preventDefault()}
      onDrop={e => { e.preventDefault(); const f = [...e.dataTransfer.files].find(x => x.type.startsWith('image/')); if (f) onPick(f); }}
      role="button"
      tabIndex={0}
    >
      <input ref={input} type="file" accept="image/*" hidden data-slot={label} onChange={e => { onPick(e.target.files?.[0]); e.target.value = ''; }} />
      {url ? <img src={url} alt={label} /> : <span className="slot-plus">+</span>}
      <span className="slot-label">{label}{required ? ' *' : ''}</span>
      {file && <button className="slot-clear" onClick={e => { e.stopPropagation(); onPick(undefined); }} aria-label={`Remove ${label}`}>✕</button>}
    </div>
  );
}
