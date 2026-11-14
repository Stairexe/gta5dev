import { useEffect, useState } from 'react';
import { PropStudio } from './pages/PropStudio';
import { PedTool } from './pages/PedTool';
import { loadEngine } from './lib/engine';

type Route = 'home' | 'props' | 'animator' | 'ped';

const TOOLS: { id: Exclude<Route, 'home'>; title: string; tag: string; text: string; out: string }[] = [
  { id: 'props', title: 'Prop Studio', tag: 'GLB → prop', text: 'Turn any .glb into a streamable prop: origin, scale, collision, LODs and textures handled for you.', out: '.ydr  .ytd  .ytyp' },
  { id: 'animator', title: 'Prop Animator', tag: 'Animated props', text: 'Pick the parts that move, set a hinge, spin or slide (or key it by hand) and export a prop that animates in game.', out: '.ydr  .ycd  .ytyp' },
  { id: 'ped', title: 'YDD → Addon Ped', tag: 'Peds', text: 'Drop a rigged ped .ydd. It is split into head / upper / lower / feet with the variation .ymt, skeleton and peds.meta.', out: '.ydd  .ytd  .yft  .ymt  peds.meta' },
];

function readRoute(): Route {
  const h = window.location.hash.replace('#/', '');
  return (['props', 'animator', 'ped'] as Route[]).includes(h as Route) ? (h as Route) : 'home';
}

export function App() {
  const [route, setRoute] = useState<Route>(readRoute);
  // Start downloading the in-browser engine as soon as a tool is opened.
  useEffect(() => { if (route !== 'home') void loadEngine().catch(() => undefined); }, [route]);
  useEffect(() => {
    const on = () => setRoute(readRoute());
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);

  return (
    <div className="app">
      <nav className="topbar">
        <a href="#/" className="brand"><span className="logo">F</span>GTA Forge</a>
        <div className="nav-links">
          {TOOLS.map(t => <a key={t.id} href={`#/${t.id}`} className={route === t.id ? 'active' : ''}>{t.title}</a>)}
        </div>
      </nav>
      {route === 'home' && <Home />}
      {route === 'props' && <PropStudio key="props" mode="static" />}
      {route === 'animator' && <PropStudio key="animator" mode="animator" />}
      {route === 'ped' && <PedTool />}
    </div>
  );
}

function Home() {
  return (
    <div className="home">
      <header className="hero">
        <h1>From 3D model to FiveM resource.</h1>
        <p>Build GTA V props, animated props and addon peds in your browser. Every file is compiled on your own machine with CodeWalker's format engine (running as WebAssembly) and read back before you download it. Nothing is uploaded.</p>
      </header>
      <div className="cards">
        {TOOLS.map(t => (
          <a key={t.id} href={`#/${t.id}`} className="card">
            <span className="card-tag">{t.tag}</span>
            <h2>{t.title}</h2>
            <p>{t.text}</p>
            <code>{t.out}</code>
          </a>
        ))}
      </div>
      <footer className="home-foot">
        Uses CodeWalker.Core (MIT) by dexyfex. Not affiliated with Rockstar Games or Cfx.re.
      </footer>
    </div>
  );
}
