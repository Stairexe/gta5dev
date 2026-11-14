import * as THREE from 'three';
import { GLTFLoader, type GLTF } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { MeshoptDecoder } from 'three/examples/jsm/libs/meshopt_decoder.module.js';

/** Loads a .glb (or self-contained .gltf) from a File. */
export async function loadGlb(file: File): Promise<GLTF> {
  const loader = new GLTFLoader();
  loader.setMeshoptDecoder(MeshoptDecoder);
  const data = file.name.toLowerCase().endsWith('.gltf') ? await file.text() : await file.arrayBuffer();
  return new Promise((resolve, reject) => loader.parse(data, '', resolve, err => reject(new Error(err?.message ?? 'Could not read the model file.'))));
}

/** Gives every node a stable, readable name. */
export function nameNodes(root: THREE.Object3D) {
  let i = 0;
  root.traverse(o => { if (!o.name) o.name = `${o.type.toLowerCase()}_${i}`; i++; });
}
