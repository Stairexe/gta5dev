import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as THREE from 'three';
import { TransformControls } from 'three/examples/jsm/controls/TransformControls.js';
import type { GLTF } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { Viewport, type ViewportApi } from '../three/Viewport';
import { loadGlb, nameNodes } from '../three/loadModel';
import { buildPropSpec, exportTransform, rel, sceneStats, type MaterialOverride, type PivotMode, type SceneStats } from '../lib/exporter';
import { buildProp, download, type BuildResult } from '../lib/api';
import { COLLISION_MATERIALS, type AlphaMode, type CollisionMode, type ShaderChoice } from '../lib/types';
import { safeName } from '../lib/textures';
import { DropZone, Field, ReportView, Section, Steps } from '../components/ui';
import { countTriangles, remesh, restoreOriginals } from '../lib/remesh';
import { takeHandOff } from '../lib/handoff';
import {
  applyClip, clipTargets, insertPivot, presetKeys, RestPose, toSource, upsertKey,
  type Clip, type GtaAxis, type PivotPoint, type Preset,
} from '../animator/model';

type Mode = 'static' | 'animator';

interface Loaded { root: THREE.Object3D; gltf: GLTF; fileName: string; holder: THREE.Group; rest: RestPose; mixer: THREE.AnimationMixer }

let clipSeq = 0;

export function PropStudio({ mode }: { mode: Mode }) {
  const api = useRef<ViewportApi | null>(null);
  const model = useRef<Loaded | null>(null);
  const gizmo = useRef<TransformControls | null>(null);
  const colHelper = useRef<THREE.Object3D | null>(null);
  const [, setVersion] = useState(0);
  const bump = useCallback(() => setVersion(v => v + 1), []);

  const [loadError, setLoadError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [name, setName] = useState('my_prop');
  const [scale, setScale] = useState(1);
  const [rotationZ, setRotationZ] = useState(0);
  const [pivot, setPivot] = useState<PivotMode>('bottom-center');
  const [collision, setCollision] = useState<CollisionMode>('box');
  const [collisionMaterial, setCollisionMaterial] = useState(0);
  const [lodDist, setLodDist] = useState(150);
  const [generateLods, setGenerateLods] = useState(true);
  const [dynamic, setDynamic] = useState(false);
  const [testCommand, setTestCommand] = useState(true);
  const [overrides, setOverrides] = useState<Record<string, MaterialOverride>>({});
  const [stats, setStats] = useState<SceneStats | null>(null);
  const [maxTexture, setMaxTexture] = useState(2048);
  // remesh / optimize
  const originals = useRef(new Map<THREE.Mesh, THREE.BufferGeometry>());
  const [sourceTris, setSourceTris] = useState(0);
  const [targetTris, setTargetTris] = useState(30000);
  const [weld, setWeld] = useState(false);
  const [aggressive, setAggressive] = useState(false);
  const [remeshing, setRemeshing] = useState(false);
  const [remeshNote, setRemeshNote] = useState<string | null>(null);
  const [fromGenerator, setFromGenerator] = useState(false);

  // animator state
  const [parts, setParts] = useState<THREE.Object3D[]>([]);
  const [selected, setSelected] = useState<THREE.Object3D | null>(null);
  const [clips, setClips] = useState<Clip[]>([]);
  const [activeClipId, setActiveClipId] = useState<string | null>(null);
  const [time, setTime] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [gizmoMode, setGizmoMode] = useState<'rotate' | 'translate'>('rotate');
  const [pivotPoint, setPivotPoint] = useState<PivotPoint>('-x');
  const [preset, setPreset] = useState<Preset>({ kind: 'hinge', axis: 'z', angle: 90, duration: 2, returnToStart: true });

  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<BuildResult | null>(null);
  const [buildError, setBuildError] = useState<string | null>(null);
  const [clientWarnings, setClientWarnings] = useState<string[]>([]);

  const placement = useMemo(() => ({ scale, rotationZ, pivot }), [scale, rotationZ, pivot]);
  const activeClip = clips.find(c => c.id === activeClipId) ?? null;
  const isAnimator = mode === 'animator';

  // ---------------------------------------------------------------- placement & collision preview
  useEffect(() => {
    const m = model.current, v = api.current;
    if (!m || !v) return;
    m.rest.restore();
    const E = exportTransform(m.root, placement);
    m.holder.matrix.copy(E);
    m.holder.matrixWorldNeedsUpdate = true;
    m.holder.updateMatrixWorld(true);
    setStats(sceneStats(m.root, placement));

    if (colHelper.current) { v.content.remove(colHelper.current); colHelper.current = null; }
    if (collision !== 'none') {
      const box = new THREE.Box3();
      const p = new THREE.Vector3();
      m.root.traverse(o => {
        const mesh = o as THREE.Mesh;
        if (!mesh.isMesh) return;
        const mw = new THREE.Matrix4().multiplyMatrices(E, rel(m.root, mesh));
        const pos = mesh.geometry.attributes.position;
        for (let i = 0; i < pos.count; i += Math.max(1, Math.floor(pos.count / 3000))) box.expandByPoint(p.fromBufferAttribute(pos, i).applyMatrix4(mw));
      });
      const helper = new THREE.Box3Helper(box, collision === 'box' ? 0xf2b33d : 0x22c55e);
      v.content.add(helper);
      colHelper.current = helper;
    }
  }, [placement, collision, parts, bump]);

  // ---------------------------------------------------------------- playback
  useEffect(() => {
    const v = api.current;
    if (!v) return;
    return v.onTick(dt => {
      if (!playing || !activeClip || !model.current) return;
      setTime(t => {
        const nt = (t + dt) % Math.max(0.001, activeClip.duration);
        applyClip(activeClip, nt, model.current!.mixer);
        return nt;
      });
    });
  }, [playing, activeClip]);

  const scrub = (t: number) => {
    setTime(t);
    if (activeClip && model.current) applyClip(activeClip, t, model.current.mixer);
  };

  // ---------------------------------------------------------------- gizmo
  useEffect(() => {
    const v = api.current;
    if (!v || !isAnimator) return;
    const g = new TransformControls(v.camera, v.renderer.domElement);
    g.setSize(0.8);
    g.addEventListener('dragging-changed', e => { v.controls.enabled = !(e as unknown as { value: boolean }).value; });
    v.scene.add(g.getHelper());
    gizmo.current = g;
    return () => { g.detach(); v.scene.remove(g.getHelper()); g.dispose(); gizmo.current = null; };
  }, [isAnimator, api.current]);

  useEffect(() => {
    const g = gizmo.current;
    if (!g) return;
    g.setMode(gizmoMode);
    g.setSpace('local');
    if (selected && parts.includes(selected)) g.attach(selected); else g.detach();
  }, [selected, parts, gizmoMode]);

  // ---------------------------------------------------------------- loading
  // A model handed over from another tool (Image -> 3D).
  useEffect(() => {
    if (!api.current) return;
    const f = takeHandOff();
    if (f) {
      setFromGenerator(true);
      setWeld(true);
      void onFiles([f]);
    }
  }, [api.current]); // eslint-disable-line react-hooks/exhaustive-deps

  const runRemesh = async () => {
    const m = model.current;
    if (!m) return;
    setRemeshing(true);
    try {
      m.rest.restore();
      const r = await remesh(m.root, { targetTriangles: targetTris, weld, aggressive }, originals.current);
      setRemeshNote(`${r.before.toLocaleString()} → ${r.after.toLocaleString()} triangles`);
      bump();
      setStats(sceneStats(m.root, placement));
    } finally {
      setRemeshing(false);
    }
  };

  const undoRemesh = () => {
    const m = model.current;
    if (!m) return;
    restoreOriginals(originals.current);
    setRemeshNote(null);
    setStats(sceneStats(m.root, placement));
    bump();
  };

  const onFiles = async (files: File[]) => {
    const f = files.find(x => /\.(glb|gltf)$/i.test(x.name));
    if (!f) { setLoadError('Drop a .glb file (or a .gltf with embedded data).'); return; }
    const v = api.current!;
    setLoading(true); setLoadError(null); setResult(null); setBuildError(null);
    try {
      const gltf = await loadGlb(f);
      if (model.current) v.content.remove(model.current.holder);
      const root = gltf.scene;
      nameNodes(root);
      const holder = new THREE.Group();
      holder.matrixAutoUpdate = false;
      // the holder lives in GTA space; the GLB root is Y-up, the holder matrix (export transform) converts it.
      holder.add(root);
      v.content.add(holder);
      const mixer = new THREE.AnimationMixer(root);
      model.current = { root, gltf, fileName: f.name, holder, rest: new RestPose(root), mixer };
      originals.current.clear();
      const tris = countTriangles(root);
      setSourceTris(tris);
      setTargetTris(Math.min(tris, 30000));
      setRemeshNote(null);
      setName(safeName(f.name.replace(/\.(glb|gltf)$/i, ''), 'my_prop'));
      setOverrides({});
      setParts([]);
      setSelected(null);
      const glbClips: Clip[] = gltf.animations.map(a => ({ id: `c${++clipSeq}`, name: a.name || `anim_${clipSeq}`, duration: a.duration, fps: 30, tracks: [], gltf: a }));
      setClips(glbClips);
      setActiveClipId(glbClips[0]?.id ?? null);
      setTime(0);
      setPlaying(false);
      const E = exportTransform(root, placement);
      holder.matrix.copy(E);
      holder.updateMatrixWorld(true);
      setStats(sceneStats(root, placement));
      // Auto-fix common unit mistakes (cm exports).
      const s = sceneStats(root, { ...placement, scale: 1 });
      if (s.size.length() > 150) setScale(0.01);
      setTimeout(() => v.frame(), 0);
      bump();
    } catch (e) {
      setLoadError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  // ---------------------------------------------------------------- animator actions
  const meshNodes = useMemo(() => {
    const m = model.current;
    if (!m) return [] as { node: THREE.Object3D; depth: number }[];
    const out: { node: THREE.Object3D; depth: number }[] = [];
    const walk = (o: THREE.Object3D, depth: number) => {
      let hasMesh = false;
      o.traverse(c => { if ((c as THREE.Mesh).isMesh) hasMesh = true; });
      if (!hasMesh) return;
      if (o !== m.root) out.push({ node: o, depth });
      o.children.forEach(c => walk(c, o === m.root ? depth : depth + 1));
    };
    walk(m.root, 0);
    return out.slice(0, 400);
  }, [model.current, parts]);

  const togglePart = (node: THREE.Object3D) => {
    setParts(p => (p.includes(node) ? p.filter(x => x !== node) : [...p, node]));
    setSelected(node);
  };

  const movePivot = () => {
    const m = model.current;
    if (!m || !selected) return;
    m.rest.restore();
    const pivotNode = insertPivot(m.root, selected, pivotPoint);
    m.rest.capture(m.root);
    setParts(p => [...p.filter(x => x !== selected), pivotNode]);
    setClips(cs => cs.map(c => ({ ...c, tracks: c.tracks.map(t => (t.node === selected ? { ...t, node: pivotNode } : t)) })));
    setSelected(pivotNode);
    bump();
  };

  const ensureClip = (): Clip => {
    if (activeClip && !activeClip.gltf) return activeClip;
    const c: Clip = { id: `c${++clipSeq}`, name: `anim_${clipSeq}`, duration: 2, fps: 30, tracks: [] };
    setClips(cs => [...cs, c]);
    setActiveClipId(c.id);
    return c;
  };

  const applyPreset = () => {
    const m = model.current;
    if (!m || !selected) return;
    if (!parts.includes(selected)) setParts(p => [...p, selected]);
    const clip = ensureClip();
    m.rest.restore();
    const rest = m.rest.get(selected)!;
    const keys = presetKeys(m.root, selected, rest, preset);
    const updated: Clip = { ...clip, duration: preset.duration, tracks: [...clip.tracks.filter(t => t.node !== selected), { node: selected, keys }] };
    setClips(cs => (cs.some(c => c.id === clip.id) ? cs.map(c => (c.id === clip.id ? updated : c)) : [...cs, updated]));
    setActiveClipId(updated.id);
    setTime(0);
    setPlaying(true);
  };

  const addKey = () => {
    if (!selected) return;
    if (!parts.includes(selected)) setParts(p => [...p, selected]);
    const clip = ensureClip();
    const track = clip.tracks.find(t => t.node === selected) ?? { node: selected, keys: [] };
    upsertKey(track, time);
    const updated: Clip = { ...clip, tracks: [...clip.tracks.filter(t => t.node !== selected), track] };
    setClips(cs => (cs.some(c => c.id === clip.id) ? cs.map(c => (c.id === clip.id ? updated : c)) : [...cs, updated]));
  };

  const useGltfClip = (c: Clip) => {
    const m = model.current;
    if (!m || !c.gltf) return;
    const targets = clipTargets(m.root, c.gltf);
    setParts(p => [...new Set([...p, ...targets])]);
    setActiveClipId(c.id);
    setTime(0);
    setPlaying(true);
  };

  const removeClip = (id: string) => {
    setClips(cs => cs.filter(c => c.id !== id));
    if (activeClipId === id) setActiveClipId(null);
  };

  const updateClip = (id: string, patch: Partial<Clip>) => setClips(cs => cs.map(c => (c.id === id ? { ...c, ...patch } : c)));

  // ---------------------------------------------------------------- build
  const build = async () => {
    const m = model.current;
    if (!m) return;
    setBusy(true); setBuildError(null); setResult(null); setPlaying(false);
    try {
      m.mixer.stopAllAction();
      m.rest.restore();
      m.root.updateMatrixWorld(true);
      const ovMap = new Map(Object.entries(overrides));
      const animClips = isAnimator ? clips.filter(c => c.gltf || c.tracks.some(t => t.keys.length > 1)) : [];
      const { spec, files, warnings } = await buildPropSpec(m.root, {
        name, scale, rotationZ, pivot, lodDist, generateLods, collision, collisionMaterial, dynamic, testCommand,
        movingParts: isAnimator ? parts : [],
        maxTextureSize: maxTexture,
        animations: animClips.map(c => toSource(c, m.mixer)),
        materialOverrides: ovMap,
      });
      m.mixer.stopAllAction();
      m.rest.restore();
      setClientWarnings(warnings);
      const r = await buildProp(spec, files);
      setResult(r);
      download(r.blob, r.fileName);
    } catch (e) {
      console.error(e);
      setBuildError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const loaded = !!model.current;
  const sizeWarn = stats && (stats.size.length() < 0.05 ? 'This is tiny. Was it modelled in metres?' : stats.size.length() > 200 ? 'This is huge. Try the cm → m scale.' : null);

  return (
    <div className="tool">
      <aside className="panel">
        <Steps steps={[
          { id: 'ps-model', label: 'Model', done: loaded },
          { id: 'ps-optimize', label: 'Optimize', done: !!remeshNote, disabled: !loaded },
          { id: 'ps-place', label: 'Placement', disabled: !loaded },
          { id: 'ps-collision', label: 'Collision', disabled: !loaded },
          ...(isAnimator ? [{ id: 'ps-parts', label: 'Animate', done: clips.length > 0, disabled: !loaded }] : []),
          { id: 'ps-export', label: 'Export', done: !!result, disabled: !loaded },
        ]} />
        <Section title="Model" step={1} id="ps-model">
          <DropZone accept=".glb,.gltf" label={loaded ? `Replace ${model.current!.fileName}` : 'Drop a .glb model'} sub="Textures embedded. Any size, any orientation." onFiles={onFiles} compact={loaded} />
          {loading && <p className="muted">Loading…</p>}
          {loadError && <p className="error">{loadError}</p>}
          {stats && (
            <dl className="stats">
              <div><dt>Triangles</dt><dd>{stats.triangles.toLocaleString()}</dd></div>
              <div><dt>Size (m)</dt><dd>{stats.size.x.toFixed(2)} × {stats.size.y.toFixed(2)} × {stats.size.z.toFixed(2)}</dd></div>
              <div><dt>Materials</dt><dd>{stats.materials.length}</dd></div>
            </dl>
          )}
          {sizeWarn && <p className="warn">{sizeWarn}</p>}
          {stats && stats.triangles > 37000 && <p className="warn">High-poly for a prop ({stats.triangles.toLocaleString()} tris). Use Optimize below.</p>}
          {fromGenerator && <p className="muted small">Model from Image → 3D. Remesh it to a game budget below.</p>}
        </Section>

        {loaded && (
          <>
            <Section title="Optimize mesh" step={2} id="ps-optimize" right={remeshNote && <span className="pill pill-ok">{remeshNote}</span>}>
              <Field label={`Target: ${targetTris.toLocaleString()} triangles (from ${sourceTris.toLocaleString()})`} hint="Props in GTA are usually 2k-40k triangles. LODs are generated from this.">
                <input type="range" min={Math.min(500, sourceTris)} max={Math.max(sourceTris, 1000)} step={Math.max(1, Math.round(sourceTris / 200))} value={targetTris} onChange={e => setTargetTris(+e.target.value)} />
              </Field>
              <div className="btn-group">
                {[2000, 10000, 30000].filter(t => t < sourceTris).map(t => (
                  <button key={t} className="btn btn-small" onClick={() => setTargetTris(t)}>{t / 1000}k</button>
                ))}
              </div>
              <label className="check"><input type="checkbox" checked={weld} onChange={e => setWeld(e.target.checked)} /> Weld vertices first (AI / scanned models; smooths normals)</label>
              <label className="check"><input type="checkbox" checked={aggressive} onChange={e => setAggressive(e.target.checked)} /> Aggressive (collapse seams, drop floating bits)</label>
              <div className="row">
                <button className="btn" onClick={runRemesh} disabled={remeshing || targetTris >= sourceTris}>{remeshing ? 'Remeshing…' : 'Remesh'}</button>
                {remeshNote && <button className="btn btn-ghost" onClick={undoRemesh}>Undo</button>}
              </div>
              <Field label="Max texture size" hint="Larger textures stream slower and use more memory in game.">
                <select value={maxTexture} onChange={e => setMaxTexture(+e.target.value)}>
                  {[512, 1024, 2048].map(s => <option key={s} value={s}>{s} px</option>)}
                </select>
              </Field>
            </Section>

            <Section title="Placement" step={3} id="ps-place">
              <Field label="Spawn name" hint="Used in scripts: CreateObject(`name`, ...)">
                <input value={name} onChange={e => setName(safeName(e.target.value, ''))} />
              </Field>
              <div className="row">
                <Field label="Scale">
                  <input type="number" step="0.01" min="0.0001" value={scale} onChange={e => setScale(Math.max(0.0001, +e.target.value || 1))} />
                </Field>
                <div className="btn-group">
                  <button className="btn btn-small" onClick={() => setScale(0.01)} title="Model was made in centimetres">cm → m</button>
                  <button className="btn btn-small" onClick={() => setScale(1)}>1:1</button>
                </div>
              </div>
              <div className="row">
                <Field label="Turn">
                  <div className="btn-group">
                    <button className="btn btn-small" onClick={() => setRotationZ(r => (r + 270) % 360)}>⟲ 90°</button>
                    <button className="btn btn-small" onClick={() => setRotationZ(r => (r + 90) % 360)}>⟳ 90°</button>
                    <span className="muted">{rotationZ}°</span>
                  </div>
                </Field>
              </div>
              <Field label="Origin" hint="GTA spawns props at their origin. Bottom-centre sits them on the ground.">
                <select value={pivot} onChange={e => setPivot(e.target.value as PivotMode)}>
                  <option value="bottom-center">Bottom centre (recommended)</option>
                  <option value="center">Centre</option>
                  <option value="origin">Keep the model's origin</option>
                </select>
              </Field>
            </Section>

            <Section title="Collision" step={4} id="ps-collision">
              <div className="seg">
                {(['none', 'box', 'mesh'] as CollisionMode[]).map(c => (
                  <button key={c} className={collision === c ? 'on' : ''} onClick={() => setCollision(c)}>{c === 'none' ? 'None' : c === 'box' ? 'Box' : 'Precise'}</button>
                ))}
              </div>
              <p className="muted small">{collision === 'none' ? 'Players and cars pass through it.' : collision === 'box' ? 'One box around the model. Cheapest, fine for most props.' : 'Follows the shape (simplified to ~4k triangles).'}</p>
              {collision !== 'none' && (
                <Field label="Surface material" hint="Footstep sounds, bullet impacts">
                  <select value={collisionMaterial} onChange={e => setCollisionMaterial(+e.target.value)}>
                    {COLLISION_MATERIALS.map(([i, n]) => <option key={i} value={i}>{n}</option>)}
                  </select>
                </Field>
              )}
            </Section>

            <Section title="Streaming" id="ps-stream">
              <Field label={`Draw distance: ${lodDist} m`}>
                <input type="range" min={20} max={600} step={10} value={lodDist} onChange={e => setLodDist(+e.target.value)} />
              </Field>
              <label className="check"><input type="checkbox" checked={generateLods} onChange={e => setGenerateLods(e.target.checked)} /> Generate LODs (50% and 20%)</label>
              <label className="check"><input type="checkbox" checked={dynamic} onChange={e => setDynamic(e.target.checked)} /> Dynamic (can be knocked over)</label>
              <label className="check"><input type="checkbox" checked={testCommand} onChange={e => setTestCommand(e.target.checked)} /> Add /{name || 'name'} test command</label>
            </Section>

            {stats && stats.materials.length > 0 && (
              <Section title="Materials">
                {stats.materials.map(mat => {
                  const ov = overrides[mat.uuid] ?? { shader: 'auto', alpha: 'auto', emissiveMultiplier: 1 };
                  const set = (p: Partial<MaterialOverride>) => setOverrides(o => ({ ...o, [mat.uuid]: { ...ov, ...p } }));
                  const std = mat as THREE.MeshStandardMaterial;
                  const maps = [std.map && 'colour', std.normalMap && 'normal', std.roughnessMap && 'rough→spec', std.emissiveMap && 'emissive'].filter(Boolean).join(', ') || 'colour only';
                  return (
                    <div className="mat" key={mat.uuid}>
                      <div className="mat-name">{mat.name || 'unnamed'} <span className="muted small">{maps}</span></div>
                      <div className="row">
                        <select value={ov.shader} onChange={e => set({ shader: e.target.value as ShaderChoice })}>
                          <option value="auto">Shader: auto</option>
                          <option value="default">Shader: default</option>
                          <option value="emissive">Shader: emissive (glows)</option>
                        </select>
                        <select value={ov.alpha} onChange={e => set({ alpha: e.target.value as AlphaMode | 'auto' })}>
                          <option value="auto">Alpha: auto</option>
                          <option value="opaque">Opaque</option>
                          <option value="cutout">Cutout</option>
                          <option value="blend">Transparent</option>
                        </select>
                      </div>
                      {ov.shader === 'emissive' && (
                        <Field label={`Glow: ${ov.emissiveMultiplier}`}>
                          <input type="range" min={0} max={16} step={0.5} value={ov.emissiveMultiplier} onChange={e => set({ emissiveMultiplier: +e.target.value })} />
                        </Field>
                      )}
                    </div>
                  );
                })}
              </Section>
            )}

            {isAnimator && (
              <>
                <Section title="Moving parts" step={5} id="ps-parts" right={<span className="muted small">{parts.length} selected</span>}>
                  <p className="muted small">Tick the parts that move (lid, door, fan blade). Each becomes a bone.</p>
                  <div className="tree">
                    {meshNodes.map(({ node, depth }) => (
                      <div key={node.uuid} className={`tree-row ${selected === node ? 'sel' : ''}`} style={{ paddingLeft: 6 + depth * 14 }} onClick={() => setSelected(node)}>
                        <input type="checkbox" checked={parts.includes(node)} onChange={() => togglePart(node)} onClick={e => e.stopPropagation()} />
                        <span>{node.name}</span>
                      </div>
                    ))}
                  </div>
                  {selected && (
                    <div className="row">
                      <Field label="Pivot (hinge point)">
                        <select value={pivotPoint} onChange={e => setPivotPoint(e.target.value as PivotPoint)}>
                          <option value="-x">−X side</option><option value="+x">+X side</option>
                          <option value="-y">−Y side (front)</option><option value="+y">+Y side (back)</option>
                          <option value="bottom">Bottom</option><option value="top">Top</option><option value="center">Centre</option>
                        </select>
                      </Field>
                      <button className="btn btn-small" onClick={movePivot}>Move pivot</button>
                    </div>
                  )}
                </Section>

                <Section title="Motion preset">
                  {!selected ? <p className="muted small">Select a part above.</p> : (
                    <>
                      <div className="seg">
                        {(['hinge', 'spin', 'slide'] as const).map(k => (
                          <button key={k} className={preset.kind === k ? 'on' : ''} onClick={() => setPreset(k === 'hinge'
                            ? { kind: 'hinge', axis: 'z', angle: 90, duration: 2, returnToStart: true }
                            : k === 'spin' ? { kind: 'spin', axis: 'z', duration: 2, reverse: false }
                              : { kind: 'slide', axis: 'y', distance: 0.4, duration: 2, returnToStart: true })}>{k}</button>
                        ))}
                      </div>
                      <div className="row">
                        <Field label="Axis">
                          <select value={preset.axis} onChange={e => setPreset({ ...preset, axis: e.target.value as GtaAxis })}>
                            <option value="z">Up (Z)</option><option value="x">Side (X)</option><option value="y">Forward (Y)</option>
                          </select>
                        </Field>
                        {preset.kind === 'hinge' && <Field label="Angle °"><input type="number" value={preset.angle} onChange={e => setPreset({ ...preset, angle: +e.target.value })} /></Field>}
                        {preset.kind === 'slide' && <Field label="Distance m"><input type="number" step="0.05" value={preset.distance} onChange={e => setPreset({ ...preset, distance: +e.target.value })} /></Field>}
                        <Field label="Seconds"><input type="number" step="0.1" min="0.1" value={preset.duration} onChange={e => setPreset({ ...preset, duration: Math.max(0.1, +e.target.value) })} /></Field>
                      </div>
                      {preset.kind !== 'spin'
                        ? <label className="check"><input type="checkbox" checked={preset.returnToStart} onChange={e => setPreset({ ...preset, returnToStart: e.target.checked })} /> Return to start (loops cleanly)</label>
                        : <label className="check"><input type="checkbox" checked={preset.reverse} onChange={e => setPreset({ ...preset, reverse: e.target.checked })} /> Reverse</label>}
                      <button className="btn" onClick={applyPreset}>Apply to {selected.name}</button>
                    </>
                  )}
                </Section>

                <Section title="Clips">
                  {clips.length === 0 && <p className="muted small">No clips yet. Apply a preset or add keys.</p>}
                  {clips.map(c => (
                    <div key={c.id} className={`clip ${c.id === activeClipId ? 'sel' : ''}`} onClick={() => { setActiveClipId(c.id); scrub(0); }}>
                      <input value={c.name} onChange={e => updateClip(c.id, { name: safeName(e.target.value, '') })} onClick={e => e.stopPropagation()} />
                      <span className="muted small">{c.gltf ? 'from GLB' : `${c.tracks.length} part(s)`} · {c.duration.toFixed(1)}s</span>
                      {c.gltf && <button className="btn btn-small" onClick={e => { e.stopPropagation(); useGltfClip(c); }}>Use</button>}
                      <button className="btn btn-small btn-ghost" onClick={e => { e.stopPropagation(); removeClip(c.id); }}>✕</button>
                    </div>
                  ))}
                  <button className="btn btn-small" onClick={() => { const c: Clip = { id: `c${++clipSeq}`, name: `anim_${clipSeq}`, duration: 2, fps: 30, tracks: [] }; setClips(cs => [...cs, c]); setActiveClipId(c.id); }}>+ New clip</button>
                </Section>
              </>
            )}

            <Section title="Export" step={isAnimator ? 6 : 5} id="ps-export">
              <button className="btn btn-primary btn-wide" disabled={busy || !name} onClick={build}>
                {busy ? 'Building…' : 'Build FiveM resource'}
              </button>
              <p className="muted small">Produces {name}.ydr, .ytd, .ytyp{isAnimator && clips.length ? ', .ycd' : ''} and an fxmanifest.</p>
              {clientWarnings.length > 0 && <ul className="report-warn">{clientWarnings.map((w, i) => <li key={i}>{w}</li>)}</ul>}
              <ReportView report={result?.report} error={buildError} fileName={result?.fileName} onDownload={result ? () => download(result.blob, result.fileName) : undefined} />
            </Section>
          </>
        )}
      </aside>

      <main className="stage">
        <Viewport onReady={v => { api.current = v; bump(); }} />
        {!loaded && <div className="stage-empty"><p>{isAnimator ? 'Load a model with separate parts (lid, door, blades) to animate it.' : 'Load a .glb to turn it into a FiveM prop.'}</p></div>}
        {isAnimator && loaded && (
          <div className="timeline">
            <button className="btn btn-small" onClick={() => setPlaying(p => !p)} disabled={!activeClip}>{playing ? '❚❚' : '▶'}</button>
            <input type="range" min={0} max={activeClip?.duration ?? 1} step={0.01} value={time} onChange={e => { setPlaying(false); scrub(+e.target.value); }} disabled={!activeClip} />
            <span className="mono">{time.toFixed(2)}s</span>
            {activeClip && !activeClip.gltf && (
              <>
                <label className="muted small">Length <input className="num" type="number" step="0.1" min="0.1" value={activeClip.duration} onChange={e => updateClip(activeClip.id, { duration: Math.max(0.1, +e.target.value) })} />s</label>
                <div className="seg seg-small">
                  <button className={gizmoMode === 'rotate' ? 'on' : ''} onClick={() => setGizmoMode('rotate')}>Rotate</button>
                  <button className={gizmoMode === 'translate' ? 'on' : ''} onClick={() => setGizmoMode('translate')}>Move</button>
                </div>
                <button className="btn btn-small" onClick={addKey} disabled={!selected || !parts.includes(selected)}>◆ Key {selected?.name ?? ''}</button>
                <div className="keys">
                  {activeClip.tracks.flatMap(tr => tr.keys.map(k => (
                    <span key={`${tr.node.uuid}-${k.t}`} className="key" style={{ left: `${(k.t / activeClip.duration) * 100}%` }} title={`${tr.node.name} @ ${k.t.toFixed(2)}s`} onClick={() => { setPlaying(false); scrub(k.t); }} />
                  )))}
                </div>
              </>
            )}
          </div>
        )}
      </main>
    </div>
  );
}
