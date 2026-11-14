import { useRef, useState } from 'react';
import * as THREE from 'three';
import { Viewport, type ViewportApi } from '../three/Viewport';
import { buildPed, download, inspect, type BuildResult, type Preview } from '../lib/api';
import { safeName } from '../lib/textures';
import { DropZone, Field, ReportView, Section } from '../components/ui';

const PED_TYPES = ['CIVMALE', 'CIVFEMALE', 'COP', 'MEDIC', 'FIREMAN', 'ARMY', 'SWAT'];

function previewToGroup(p: Preview): THREE.Group {
  const g = new THREE.Group();
  const loader = new THREE.TextureLoader();
  const texCache = new Map<string, THREE.Texture>();
  const palette = [0xc9a27e, 0x7aa2d6, 0x8bc48a, 0xd68a7a, 0xb59ad6, 0xd6c27a];
  p.meshes.forEach((m, i) => {
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(m.positions, 3));
    if (m.normals.some(v => v !== 0)) geo.setAttribute('normal', new THREE.Float32BufferAttribute(m.normals, 3));
    geo.setAttribute('uv', new THREE.Float32BufferAttribute(m.uv, 2));
    geo.setIndex(m.indices);
    if (!geo.attributes.normal) geo.computeVertexNormals();
    const mat = new THREE.MeshStandardMaterial({ color: palette[p.drawables.indexOf(m.drawable) % palette.length] ?? palette[i % palette.length], roughness: 0.7, side: THREE.DoubleSide });
    const url = m.texture ? p.textures[m.texture] : undefined;
    if (url) {
      let t = texCache.get(url);
      if (!t) { t = loader.load(url); t.colorSpace = THREE.SRGBColorSpace; t.flipY = false; texCache.set(url, t); }
      mat.map = t;
      mat.color.set(0xffffff);
    }
    g.add(new THREE.Mesh(geo, mat));
  });
  return g;
}

export function PedTool() {
  const api = useRef<ViewportApi | null>(null);
  const shown = useRef<THREE.Object3D | null>(null);
  const [ydd, setYdd] = useState<File | null>(null);
  const [ytd, setYtd] = useState<File | null>(null);
  const [yft, setYft] = useState<File | null>(null);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [name, setName] = useState('my_addon_ped');
  const [pedType, setPedType] = useState('CIVMALE');
  const [forceSplit, setForceSplit] = useState(false);
  const [testCommand, setTestCommand] = useState(true);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<BuildResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refreshPreview = async (yddFile: File, ytdFile: File | null) => {
    setPreviewError(null);
    try {
      const p = await inspect(yddFile, ytdFile);
      setPreview(p);
      const v = api.current!;
      if (shown.current) v.content.remove(shown.current);
      const g = previewToGroup(p);
      v.content.add(g);
      shown.current = g;
      setTimeout(() => v.frame(), 0);
    } catch (e) {
      setPreview(null);
      setPreviewError((e as Error).message);
    }
  };

  const onFiles = (files: File[]) => {
    let nextYdd = ydd, nextYtd = ytd;
    for (const f of files) {
      const n = f.name.toLowerCase();
      if (n.endsWith('.ydd')) { nextYdd = f; setYdd(f); setName(safeName(f.name.replace(/\.ydd$/i, ''), 'my_addon_ped')); }
      else if (n.endsWith('.ytd')) { nextYtd = f; setYtd(f); }
      else if (n.endsWith('.yft')) setYft(f);
    }
    setResult(null); setError(null);
    if (nextYdd) void refreshPreview(nextYdd, nextYtd);
  };

  const build = async () => {
    if (!ydd) return;
    setBusy(true); setError(null); setResult(null);
    try {
      const r = await buildPed({ name, pedType, splitMode: forceSplit ? 'split' : 'auto', testCommand }, ydd, ytd, yft);
      setResult(r);
      download(r.blob, r.fileName);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const fileRow = (label: string, f: File | null, clear: () => void, hint: string) => (
    <div className="file-row">
      <span className="file-kind">{label}</span>
      {f ? <><span className="file-name">{f.name}</span><button className="btn btn-small btn-ghost" onClick={clear}>✕</button></> : <span className="muted small">{hint}</span>}
    </div>
  );

  return (
    <div className="tool">
      <aside className="panel">
        <Section title="Ped files">
          <DropZone accept=".ydd,.ytd,.yft" multiple label="Drop .ydd (+ .ytd, .yft)" sub="A full body rigged to the GTA ped skeleton, or already split into parts" onFiles={onFiles} compact={!!ydd} />
          {fileRow('.ydd', ydd, () => { setYdd(null); setPreview(null); }, 'required: the body')}
          {fileRow('.ytd', ytd, () => setYtd(null), 'optional: textures (else embedded or grey)')}
          {fileRow('.yft', yft, () => setYft(null), 'optional: skeleton + ragdoll of a ped with the same rig')}
          {previewError && <p className="error">{previewError}</p>}
          {preview && (
            <dl className="stats">
              <div><dt>Drawables</dt><dd>{preview.drawables.join(', ')}</dd></div>
              <div><dt>Triangles</dt><dd>{preview.triangles.toLocaleString()}</dd></div>
              <div><dt>Bones</dt><dd>{preview.bones.length || 'none in .ydd'}</dd></div>
            </dl>
          )}
          {preview && preview.bones.length > 0 && preview.bones.length !== 98 && preview.bones.length !== 128 && (
            <p className="warn">{preview.bones.length} bones. GTA ped skeletons have 98 (ambient) or 128 (freemode); other rigs may not animate correctly.</p>
          )}
        </Section>

        <Section title="Ped setup">
          <Field label="Spawn name" hint="The model name in scripts and peds.meta">
            <input value={name} onChange={e => setName(safeName(e.target.value, ''))} />
          </Field>
          <Field label="Ped type">
            <select value={pedType} onChange={e => setPedType(e.target.value)}>
              {PED_TYPES.map(t => <option key={t} value={t}>{t}</option>)}
            </select>
          </Field>
          <label className="check"><input type="checkbox" checked={forceSplit} onChange={e => setForceSplit(e.target.checked)} /> Re-split even if parts are already named (head_000_r…)</label>
          <label className="check"><input type="checkbox" checked={testCommand} onChange={e => setTestCommand(e.target.checked)} /> Add /{name || 'name'} command to become the ped</label>
        </Section>

        <Section title="Export">
          <button className="btn btn-primary btn-wide" disabled={!ydd || busy || !name} onClick={build}>{busy ? 'Building…' : 'Build addon ped'}</button>
          <p className="muted small">Produces {name}.ydd (head / uppr / lowr / feet), .ytd, .yft, .ymt, peds.meta and an fxmanifest.</p>
          <ReportView report={result?.report} error={error} fileName={result?.fileName} onDownload={result ? () => download(result.blob, result.fileName) : undefined} />
        </Section>
      </aside>
      <main className="stage">
        <Viewport onReady={v => { api.current = v; }} />
        {!ydd && <div className="stage-empty"><p>Drop a ped .ydd to preview it.</p></div>}
      </main>
    </div>
  );
}
