import { useEffect, useRef } from 'react';
import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js';

export interface ViewportApi {
  scene: THREE.Scene;
  camera: THREE.PerspectiveCamera;
  renderer: THREE.WebGLRenderer;
  controls: OrbitControls;
  /** Content root; everything under it is in GTA space (Z-up) - the viewport rotates it to Y-up for display. */
  content: THREE.Group;
  frame(box?: THREE.Box3): void;
  onTick(fn: (dt: number) => void): () => void;
}

/** A three.js viewport. Content is authored in GTA space (Z-up, metres). */
export function Viewport({ onReady, showHuman = true }: { onReady: (api: ViewportApi) => void; showHuman?: boolean }) {
  const host = useRef<HTMLDivElement>(null);
  const ready = useRef(onReady);
  ready.current = onReady;

  useEffect(() => {
    const el = host.current!;
    const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    el.appendChild(renderer.domElement);

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x111318);
    const pmrem = new THREE.PMREMGenerator(renderer);
    scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
    scene.add(new THREE.HemisphereLight(0xffffff, 0x334455, 0.6));
    const sun = new THREE.DirectionalLight(0xffffff, 1.6);
    sun.position.set(4, 8, 5);
    scene.add(sun);

    const camera = new THREE.PerspectiveCamera(45, 1, 0.01, 2000);
    camera.position.set(3, 2.2, 3.5);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.target.set(0, 0.8, 0);

    const grid = new THREE.GridHelper(20, 20, 0x3a4150, 0x23272f);
    scene.add(grid);

    // GTA space (Z-up) -> display (Y-up).
    const gtaToDisplay = new THREE.Group();
    gtaToDisplay.rotation.x = -Math.PI / 2;
    scene.add(gtaToDisplay);
    const content = new THREE.Group();
    gtaToDisplay.add(content);

    if (showHuman) {
      // 1.8 m reference figure standing 1.5 m to the side.
      const mat = new THREE.MeshStandardMaterial({ color: 0x3b82f6, transparent: true, opacity: 0.25, depthWrite: false });
      const body = new THREE.Mesh(new THREE.CapsuleGeometry(0.22, 1.1, 4, 12), mat);
      body.position.set(-1.5, 0.22 + 0.55, 0);
      const head = new THREE.Mesh(new THREE.SphereGeometry(0.13, 16, 12), mat);
      head.position.set(-1.5, 1.67, 0);
      scene.add(body, head);
    }

    const tickers = new Set<(dt: number) => void>();
    const clock = new THREE.Clock();
    let raf = 0;
    const loop = () => {
      raf = requestAnimationFrame(loop);
      const dt = clock.getDelta();
      tickers.forEach(t => t(dt));
      controls.update();
      renderer.render(scene, camera);
    };
    loop();

    const resize = () => {
      const w = el.clientWidth, h = el.clientHeight;
      if (!w || !h) return;
      renderer.setSize(w, h);
      camera.aspect = w / h;
      camera.updateProjectionMatrix();
    };
    const ro = new ResizeObserver(resize);
    ro.observe(el);
    resize();

    const api: ViewportApi = {
      scene, camera, renderer, controls, content,
      frame(box) {
        const b = box ?? new THREE.Box3().setFromObject(content);
        if (b.isEmpty()) return;
        const size = b.getSize(new THREE.Vector3()).length();
        const center = b.getCenter(new THREE.Vector3());
        controls.target.copy(center);
        const dir = new THREE.Vector3(1, 0.6, 1.2).normalize();
        camera.position.copy(center).addScaledVector(dir, Math.max(1.5, size * 1.3));
        camera.near = Math.max(0.005, size / 500);
        camera.far = Math.max(100, size * 50);
        camera.updateProjectionMatrix();
      },
      onTick(fn) { tickers.add(fn); return () => tickers.delete(fn); },
    };
    ready.current(api);

    return () => {
      cancelAnimationFrame(raf);
      ro.disconnect();
      controls.dispose();
      pmrem.dispose();
      renderer.dispose();
      el.removeChild(renderer.domElement);
    };
  }, [showHuman]);

  return <div ref={host} className="viewport" />;
}
