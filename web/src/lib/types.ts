// Wire format of POST /api/props/build (mirrors engine Props/PropRequest.cs).

export type CollisionMode = 'none' | 'box' | 'mesh';
export type AlphaMode = 'opaque' | 'cutout' | 'blend';
export type ShaderChoice = 'auto' | 'default' | 'emissive';

export interface MeshSpec {
  material: number;
  bone: number;
  positions: number[];
  normals: number[];
  uv0: number[];
  colors?: number[];
  indices: number[];
}

export interface MaterialSpec {
  name: string;
  shader: string;
  alpha: AlphaMode;
  diffuse?: string;
  normal?: string;
  specular?: string;
  emissiveMultiplier: number;
  color?: number[];
}

export interface TextureSpec {
  name: string;
  kind: 'diffuse' | 'normal' | 'specular';
  field: string;
}

export interface BoneSpec {
  name: string;
  parent: number;
  t: number[];
  r: number[];
  s: number[];
}

export interface TrackSpec {
  bone: number;
  t?: number[];
  r?: number[];
}

export interface AnimSpec {
  name: string;
  fps: number;
  frameCount: number;
  tracks: TrackSpec[];
}

export interface PropSpec {
  name: string;
  lodDist: number;
  dynamic: boolean;
  testCommand: boolean;
  collision: { mode: CollisionMode; material: number; positions?: number[]; indices?: number[] };
  materials: MaterialSpec[];
  textures: TextureSpec[];
  bones: BoneSpec[];
  lods: { high: MeshSpec[]; medium?: MeshSpec[]; low?: MeshSpec[] };
  animations: AnimSpec[];
}

export interface BuildReport {
  checks: string[];
  warnings: string[];
}

/** Collision materials offered in the UI (index = GTA material id). */
export const COLLISION_MATERIALS: [number, string][] = [
  [0, 'Default'], [1, 'Concrete'], [13, 'Brick'], [11, 'Stone'], [14, 'Marble'], [4, 'Tarmac'],
  [18, 'Sand (loose)'], [47, 'Grass'], [70, 'Wood (solid)'], [75, 'Wood (hollow)'],
  [56, 'Metal (solid)'], [59, 'Metal (hollow)'], [63, 'Metal (corrugated iron)'], [112, 'Glass (shoot-through)'],
  [86, 'Plastic'], [93, 'Rubber'], [104, 'Cardboard box'], [100, 'Cloth'], [109, 'Leather'], [81, 'Ceramic'],
];
