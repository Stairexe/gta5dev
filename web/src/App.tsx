import { useEffect, useMemo, useState } from 'react';
import { PropStudio } from './pages/PropStudio';
import { PedTool } from './pages/PedTool';
import { GenerateTool } from './pages/GenerateTool';
import { loadEngine } from './lib/engine';

type Route = 'home' | 'props' | 'animator' | 'ped' | 'generate';
type Category = 'All' | 'AI' | 'Props' | 'Animation' | 'Peds' | 'Maps';

interface Tool {
  id: string;
  route?: Exclude<Route, 'home'>;
  title: string;
  text: string;
  category: Exclude<Category, 'All'>;
  badge?: 'new' | 'beta' | 'soon';
  icon: keyof typeof ICONS;
  hue: number;
}

const TOOLS: Tool[] = [
  { id: 'generate', route: 'generate', title: 'Image → 3D', text: 'Turn a photo into a textured 3D model on your own GPU (TRELLIS.2), or through Meshy / Tripo.', category: 'AI', badge: 'new', icon: 'spark', hue: 280 },
  { id: 'props', route: 'props', title: 'Prop Studio', text: 'Any .glb to a streamable prop: origin, scale, collision, LODs and textures.', category: 'Props', icon: 'cube', hue: 38 },
  { id: 'remesh', route: 'props', title: 'Remesh & Optimize', text: 'Bring dense or AI meshes down to a game budget and cap texture sizes before export.', category: 'Props', badge: 'new', icon: 'mesh', hue: 160 },
  { id: 'animator', route: 'animator', title: 'Prop Animator', text: 'Hinge, spin or slide parts, or keyframe them, and export a prop that animates in game.', category: 'Animation', icon: 'play', hue: 200 },
  { id: 'ped', route: 'ped', title: 'YDD → Addon Ped', text: 'Split a rigged ped .ydd into parts with its .ymt, skeleton and peds.meta, ready for FiveM.', category: 'Peds', icon: 'person', hue: 340 },
  { id: 'image-ped', title: 'Image → Ped', text: 'Photo to a rigged, textured addon ped. Needs auto-rigging onto the GTA skeleton.', category: 'AI', badge: 'soon', icon: 'person', hue: 300 },
  { id: 'clothes', title: 'Clothing Generator', text: 'Fit a garment on the freemode body, transfer weights, paint them, export a clothing pack.', category: 'Peds', badge: 'soon', icon: 'shirt', hue: 10 },
  { id: 'ped-clothes', title: 'Freemode Clothes → Ped', text: 'Move mp_freemode clothes onto your custom ped: re-weight, rename, rebuild the .ymt.', category: 'Peds', badge: 'soon', icon: 'shirt', hue: 20 },
  { id: 'mlo', title: 'MLO & Shell Builder', text: 'Block out rooms, doors and portals on a grid and export an interior.', category: 'Maps', badge: 'soon', icon: 'house', hue: 110 },
];

const BANNERS = [
  { route: 'generate' as const, kicker: 'New · AI', title: 'Your photo. Your GPU. A game-ready prop.', text: 'Generate textured models with TRELLIS.2 on your own hardware, remesh them and export to FiveM in one flow.', cta: 'Open Image → 3D', hue: 280 },
  { route: 'animator' as const, kicker: 'Animation', title: 'Make props move.', text: 'Doors, lids, fans and drawers that animate in game, with clips and flags set for you.', cta: 'Open Prop Animator', hue: 200 },
  { route: 'ped' as const, kicker: 'Peds', title: 'From a .ydd to an addon ped.', text: 'Body split by bone, textures named for the variation system, skeleton, .ymt and peds.meta.', cta: 'Open YDD → Ped', hue: 340 },
];

const ICONS = {
  cube: 'M12 2 3 7v10l9 5 9-5V7l-9-5Zm0 2.3 6.7 3.7L12 11.7 5.3 8 12 4.3ZM5 9.7l6 3.3v6.6l-6-3.3V9.7Zm8 9.9V13l6-3.3v6.6l-6 3.3Z',
  spark: 'M12 2l2.2 6.3L20.5 10l-6.3 2.2L12 18.5l-2.2-6.3L3.5 10l6.3-1.7L12 2Zm7 12 1 2.8 2.8 1-2.8 1L19 21.6l-1-2.8-2.8-1 2.8-1L19 14Z',
  mesh: 'M4 4h16v16H4V4Zm2 2v4.6L10.6 6H6Zm7.4 0L6 13.4V18h.6L18 6.6V6h-4.6ZM18 9.4 9.4 18H18V9.4Z',
  play: 'M6 4v16l14-8L6 4Zm2 3.4L16 12l-8 4.6V7.4Z',
  person: 'M12 2a4 4 0 1 1 0 8 4 4 0 0 1 0-8Zm0 2a2 2 0 1 0 0 4 2 2 0 0 0 0-4ZM6 22v-5a6 6 0 0 1 12 0v5h-2v-5a4 4 0 0 0-8 0v5H6Z',
  shirt: 'M8 3l4 2 4-2 5 4-3 4-2-1v11H8V10l-2 1-3-4 5-4Zm.6 2.3L5.8 7.5l.9 1.2L10 7v12h4V7l3.3 1.7.9-1.2-2.8-2.2L12 7.2 8.6 5.3Z',
  house: 'M12 3 2 11h3v10h6v-6h2v6h6V11h3L12 3Zm0 2.6 6 4.8V19h-2v-6H8v6H6v-8.6l6-4.8Z',
};

function Icon({ name, size = 22 }: { name: keyof typeof ICONS; size?: number }) {
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d={ICONS[name]} /></svg>;
}

const ROUTES: Route[] = ['props', 'animator', 'ped', 'generate'];
const NAV: { route: Exclude<Route, 'home'>; label: string }[] = [
  { route: 'generate', label: 'Image → 3D' },
  { route: 'props', label: 'Prop Studio' },
  { route: 'animator', label: 'Prop Animator' },
  { route: 'ped', label: 'YDD → Ped' },
];

function readRoute(): Route {
  const h = window.location.hash.replace('#/', '');
  return ROUTES.includes(h as Route) ? (h as Route) : 'home';
}

export function App() {
  const [route, setRoute] = useState<Route>(readRoute);
  // Start downloading the in-browser engine as soon as a tool that builds files is opened.
  useEffect(() => { if (route !== 'home' && route !== 'generate') void loadEngine().catch(() => undefined); }, [route]);
  useEffect(() => {
    const on = () => setRoute(readRoute());
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);

  return (
    <div className="app">
      <nav className="topbar">
        <a href="#/" className="brand"><span className="logo">F</span><span>GTA Forge</span></a>
        <div className="nav-links">
          <a href="#/" className={route === 'home' ? 'active' : ''}>All tools</a>
          {NAV.map(n => <a key={n.route} href={`#/${n.route}`} className={route === n.route ? 'active' : ''}>{n.label}</a>)}
        </div>
      </nav>
      {route === 'home' && <Home />}
      {route === 'generate' && <GenerateTool />}
      {route === 'props' && <PropStudio key="props" mode="static" />}
      {route === 'animator' && <PropStudio key="animator" mode="animator" />}
      {route === 'ped' && <PedTool />}
    </div>
  );
}

function Home() {
  const [banner, setBanner] = useState(0);
  const [query, setQuery] = useState('');
  const [cat, setCat] = useState<Category>('All');
  useEffect(() => {
    const t = setInterval(() => setBanner(b => (b + 1) % BANNERS.length), 7000);
    return () => clearInterval(t);
  }, []);
  const tools = useMemo(() => TOOLS.filter(t =>
    (cat === 'All' || t.category === cat) &&
    (!query || `${t.title} ${t.text} ${t.category}`.toLowerCase().includes(query.toLowerCase()))), [cat, query]);
  const b = BANNERS[banner];

  return (
    <div className="home">
      <section className="banner" style={{ ['--h' as string]: b.hue }}>
        <div className="banner-art" aria-hidden="true">
          <div className="orb orb-a" /><div className="orb orb-b" /><div className="grid-floor" />
        </div>
        <div className="banner-copy">
          <span className="kicker">{b.kicker}</span>
          <h1>{b.title}</h1>
          <p>{b.text}</p>
          <a className="btn btn-primary" href={`#/${b.route}`}>{b.cta}</a>
        </div>
        <div className="banner-dots">
          {BANNERS.map((x, i) => <button key={x.route} className={i === banner ? 'on' : ''} onClick={() => setBanner(i)} aria-label={x.title} />)}
        </div>
      </section>

      <div className="catalog-head">
        <div className="chips">
          {(['All', 'AI', 'Props', 'Animation', 'Peds', 'Maps'] as Category[]).map(c => (
            <button key={c} className={`chip ${cat === c ? 'on' : ''}`} onClick={() => setCat(c)}>{c}</button>
          ))}
        </div>
        <input className="search" placeholder="Search tools" value={query} onChange={e => setQuery(e.target.value)} />
      </div>

      <div className="cards">
        {tools.map(t => {
          const inner = (
            <>
              <div className="card-art" style={{ ['--h' as string]: t.hue }}><Icon name={t.icon} size={34} /></div>
              <div className="card-body">
                <div className="card-title"><h2>{t.title}</h2>{t.badge && <span className={`badge badge-${t.badge}`}>{t.badge}</span>}</div>
                <p>{t.text}</p>
              </div>
              <div className="card-foot"><span className="card-cat">{t.category}</span>{t.route ? <span className="card-use">Use →</span> : <span className="muted small">Planned</span>}</div>
            </>
          );
          return t.route
            ? <a key={t.id} href={`#/${t.route}`} className="card">{inner}</a>
            : <div key={t.id} className="card card-soon">{inner}</div>;
        })}
        {tools.length === 0 && <p className="muted">No tools match “{query}”.</p>}
      </div>

      <section className="how">
        <div><span className="how-n">1</span><strong>Bring a model or photo</strong><p>.glb, .ydd, or a reference image.</p></div>
        <div><span className="how-n">2</span><strong>Adjust in 3D</strong><p>Remesh, place, pick collision, animate parts.</p></div>
        <div><span className="how-n">3</span><strong>Build in your browser</strong><p>CodeWalker's engine runs locally as WebAssembly; files are checked after building.</p></div>
        <div><span className="how-n">4</span><strong>Drop into FiveM</strong><p>A ready resource with fxmanifest and a test command.</p></div>
      </section>

      <footer className="home-foot">
        Uses CodeWalker.Core (MIT) by dexyfex · TRELLIS.2 (MIT) by Microsoft. Not affiliated with Rockstar Games or Cfx.re.
      </footer>
    </div>
  );
}
