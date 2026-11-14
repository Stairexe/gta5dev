# GTA Forge

Browser tools that turn 3D models into ready-to-install GTA V / FiveM resources.

| Tool | Input | Output |
|---|---|---|
| **Prop Studio** | `.glb` | `stream/<name>.ydr` `.ytd` `.ytyp`, `fxmanifest.lua`, `/name` test command |
| **Prop Animator** | `.glb` with separate parts (and optionally its own animations) | the above plus a skeleton and `stream/<name>.ycd`; archetype flags *Has Anim* + *Auto Start Anim* |
| **YDD → Addon Ped** | rigged ped `.ydd` (+ `.ytd`, `.yft` optional) | `<name>.ydd` (head / uppr / lowr / feet), `.ytd`, `.yft`, `.ymt`, `peds.meta`, `fxmanifest.lua` |

## How it works

Everything runs in the visitor's browser. The format engine (.NET 8 + CodeWalker.Core) is compiled
to WebAssembly, so the site is fully static and no model is ever uploaded.

```
UI (React + three.js)                            engine (.NET 8 + CodeWalker.Core, WebAssembly)
-------------------------------------------      ------------------------------------------------
GLB -> scene -> GTA space (Z-up, metres)         request JSON -> CodeWalker XML -> binary RAGE files
pivot, scale, LODs (meshoptimizer),       --->   DDS textures (BCnEncoder), collision, archetype,
collision mesh, textures, moving parts,          clips, ped split, variation .ymt, peds.meta
animation sampled per frame                      every file is read back before it is zipped
```

Projects: `engine/GtaForge.Core` (all builders), `engine/GtaForge.Wasm` (JavaScript bridge, published
into `web/public/engine`), `engine/GtaForge.Engine` (optional HTTP server exposing the same builders),
`engine/GtaForge.Tests`, `web/` (the UI).

* The browser does all editing and preview. It converts glTF (Y-up) to GTA space (Z-up),
  places the origin, simplifies LODs and collision with meshoptimizer, flips normal-map green for
  DirectX, derives spec maps from roughness, and samples animations at 30 fps into bone-local
  transforms.
* The engine (`engine/GtaForge.Core`) writes CodeWalker XML and compiles it with the vendored
  [CodeWalker.Core](https://github.com/dexyfex/CodeWalker) (MIT). After building, it loads every
  file again and checks it (vertex counts, shaders, textures, archetype name and flags, clip and
  animation maps). A file that does not read back fails the build instead of shipping.

### Rules baked in (sources: game data measured by [muto-atlas](https://github.com/B7Kompirine/muto-atlas), Sollumz, shipped files)

* Props: origin at bottom centre; `physicsDictionary` = the prop's own name when it has collision
  (empty means players walk through it); bounds cover every animation frame; collision uses the
  "General" type/include flag preset.
* Animation: bone tags from the ElfHash rule, root tag 0; clip tracks sorted by (track, bone);
  read-back fails if `AnimMap` is empty (the classic "PlayEntityAnim returns true but nothing
  moves" bug).
* Skeleton signatures (`Unknown50/54/58`) are non-zero and unique per model, because two addon
  models sharing them corrupt each other's bones when spawned together.
* Peds: drawables named `<comp>_000_u`, dictionary entries sorted by hash (the game binary-searches
  them), textures renamed to `<comp>_diff_000_a_uni` / `_normal_000` / `_spec_000` so the
  variation system finds them; `.ymt` written as PSO `CPedVariationInfo`; `peds.meta` values match
  shipped addon peds.

## Run it

Requirements: .NET 8 SDK, Node 18+.

```bash
scripts/build-static.sh        # WebAssembly engine + UI -> web/dist (a static site)
cd web/dist && python3 -m http.server 8080
```

Any static host works (Vercel, Netlify, GitHub Pages, nginx). On Vercel, set the build command to
`bash scripts/vercel-build.sh` (it installs the .NET SDK first) and the output directory to `web/dist`.

For UI work: run `scripts/build-static.sh` once (it fills `web/public/engine`), then `npm run dev` in `web/`.

Tests:

```bash
cd engine && dotnet test
```

`tools/fixtures/make_chest_glb.py` writes a small textured chest with a hinged lid and an `open`
animation, for trying the prop tools without any third-party assets.

## Status

Verified here: every tool end to end in a browser (Playwright), plus unit tests for the writers
(round trip of Sollumz' sample YDRs, clip values decoded back to the requested rotation, ped split
by bone, ymt fields). **Not yet verified in a running game.** Before relying on it, load the outputs on a FiveM test server:

1. Prop: `ensure <name>`, then `/<name>`: textures, scale, collision and the shadow look right.
2. Animated prop: the clip plays on spawn and placed in a ymap.
3. Addon ped: `/<name>`; walking, aiming and ragdoll (ragdoll needs a `.yft` uploaded).

Known limits: skinned GLB meshes are exported rigid in their bind pose (props only); ped textures
use one texture variation (`a`) per part; a generated ped `.yft` has no ragdoll physics.

## Licences

* CodeWalker.Core: MIT, (c) dexyfex, vendored in `engine/vendor/CodeWalker.Core`.
* Sollumz sample files in `engine/GtaForge.Tests/Assets` are used only as test data (GPL-3.0).
* Not affiliated with Rockstar Games or Cfx.re. Do not ship Rockstar's game files with this app;
  users supply their own.
