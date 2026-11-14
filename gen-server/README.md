# GTA Forge generation server

Runs on your GPU PC and turns 1-4 reference images into a textured `.glb`, which the website's
**Image → 3D** tool previews and sends straight into Prop Studio or the Prop Animator.

| Backend | Where it runs | Needs | Cost per model |
|---|---|---|---|
| `trellis2` | your GPU | NVIDIA, **24 GB+ VRAM**, Linux (WSL2 on Windows), TRELLIS.2 installed | electricity |
| `trellis` | your GPU | NVIDIA, 16 GB+ VRAM, TRELLIS v1 installed; supports multi-view | electricity |
| `meshy` | Meshy cloud | `MESHY_API_KEY` | 30 credits ≈ $0.38-0.60 |
| `tripo` | Tripo cloud | `TRIPO_API_KEY` | $0.30 (+$0.10 HD texture) |
| `mock` | anywhere | `GTAFORGE_ENABLE_MOCK=1` | free; makes a textured block, for testing |

API keys stay on this server; the browser never sees them.

## 1. Install the server

```bash
cd gen-server
python -m venv .venv && source .venv/bin/activate      # Windows: .venv\Scripts\activate
pip install -r requirements.txt
```

## 2. Install TRELLIS.2 (for your own GPU)

TRELLIS.2 (Microsoft, MIT licence) is tested on Linux with CUDA 12.4. On Windows use WSL2 (Ubuntu).

```bash
git clone -b main https://github.com/microsoft/TRELLIS.2.git --recursive
cd TRELLIS.2
. ./setup.sh --new-env --basic --flash-attn --nvdiffrast --nvdiffrec --cumesh --o-voxel --flexgemm
conda activate trellis2
pip install -r /path/to/gen-server/requirements.txt       # install the server into the same env
export PYTHONPATH=$PWD:$PYTHONPATH                        # so `import trellis2` works
```

The first job downloads the `microsoft/TRELLIS.2-4B` weights (~16 GB) from Hugging Face.
Speeds from the TRELLIS.2 README (H100): 512³ ~3 s, 1024³ ~17 s, 1536³ ~60 s. Expect several times
longer on consumer cards. GPUs with 16-23 GB: install [TRELLIS v1](https://github.com/microsoft/TRELLIS)
instead and pick the `trellis` backend.

## 3. Run it

```bash
export GTAFORGE_TOKEN=pick-a-long-random-string     # the website asks for this
export MESHY_API_KEY=...                            # optional
export TRIPO_API_KEY=...                            # optional
python -m gtaforge_gen                              # http://127.0.0.1:8000
```

| Variable | Default | Meaning |
|---|---|---|
| `GTAFORGE_HOST` / `GTAFORGE_PORT` | `127.0.0.1` / `8000` | listen address. Keep 127.0.0.1 unless you tunnel. |
| `GTAFORGE_TOKEN` | empty | required access token (`X-GtaForge-Token`). Always set it when the server is reachable from outside. |
| `GTAFORGE_ORIGINS` | the live site + localhost dev ports | websites allowed to call the server (CORS). |
| `GTAFORGE_WORKERS` | 4 | parallel jobs (GPU backends still run one at a time). |
| `TRELLIS2_MODEL`, `TRELLIS_MODEL`, `MESHY_MODEL`, `TRIPO_MODEL` | vendor defaults | model overrides |

## 4. Connect the website

Open **Image → 3D**, enter the server address and token, press **Connect**.

* **Same PC as the browser:** `http://localhost:8000` works even from the HTTPS site (browsers trust localhost).
* **Another PC / your team:** browsers block plain `http://` LAN addresses on an HTTPS page, so put the
  server behind HTTPS, for example a Cloudflare Tunnel:

  ```bash
  cloudflared tunnel --url http://localhost:8000
  ```

  Use the `https://….trycloudflare.com` address it prints, and keep `GTAFORGE_TOKEN` set.

## API

```
GET    /v1/health                 server info and which backends are usable (and why not)
POST   /v1/jobs                   multipart: images (1-4, front first), backend, options JSON
                                  options: targetFaces, textureSize (512-4096), resolution (512/1024/1536), seed, pbr
GET    /v1/jobs/{id}              status (queued|running|done|failed), progress, stage, error, modelUrl
GET    /v1/jobs/{id}/model.glb    the result (kept 24 h)
DELETE /v1/jobs/{id}
```

Tests: `pip install pytest && python -m pytest tests`.
