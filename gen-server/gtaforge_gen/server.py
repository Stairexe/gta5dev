"""HTTP API used by the GTA Forge website's Image -> 3D tool.

    GET  /v1/health                -> server info + which backends are usable
    POST /v1/jobs                  -> multipart: images (1-4, front first), backend, options (JSON)
    GET  /v1/jobs/{id}             -> status, progress, stage, error
    GET  /v1/jobs/{id}/model.glb   -> the result
    DELETE /v1/jobs/{id}

Security: set GTAFORGE_TOKEN and the website must send it (X-GtaForge-Token). Without a token
the server only listens on 127.0.0.1 by default, so only this PC can use it.
"""
from __future__ import annotations

import io
import json
import os
import shutil
import tempfile
import threading
import time
import traceback
import uuid
from concurrent.futures import ThreadPoolExecutor
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse, Response
from PIL import Image

from . import __version__
from .backends.base import Backend, GenInput, GenOptions
from .backends.cloud import Meshy, Tripo
from .backends.mock import Mock
from .backends.trellis import Trellis1, Trellis2

MAX_IMAGE_BYTES = 20 * 1024 * 1024
JOB_TTL_SECONDS = 24 * 3600

BACKENDS: dict[str, Backend] = {b.id: b for b in (Trellis2(), Trellis1(), Meshy(), Tripo(), Mock())}
TOKEN = os.environ.get("GTAFORGE_TOKEN", "")
WORK = Path(os.environ.get("GTAFORGE_WORKDIR", tempfile.gettempdir())) / "gtaforge-gen"
WORK.mkdir(parents=True, exist_ok=True)
DEFAULT_ORIGINS = "https://gta-forge-jet.vercel.app,http://localhost:5173,http://localhost:5080,http://localhost:5098,http://127.0.0.1:5173"
ORIGINS = [o.strip() for o in os.environ.get("GTAFORGE_ORIGINS", DEFAULT_ORIGINS).split(",") if o.strip()]


@dataclass
class Job:
    id: str
    backend: str
    status: str = "queued"  # queued | running | done | failed
    progress: int = 0
    stage: str = "Waiting in queue"
    error: Optional[str] = None
    created: float = field(default_factory=time.time)
    finished: Optional[float] = None
    faces_target: int = 0

    def public(self) -> dict:
        d = asdict(self)
        d["modelUrl"] = f"/v1/jobs/{self.id}/model.glb" if self.status == "done" else None
        return d


JOBS: dict[str, Job] = {}
JOBS_LOCK = threading.Lock()
GPU_LOCK = threading.Lock()  # local GPU backends: one job at a time
POOL = ThreadPoolExecutor(max_workers=int(os.environ.get("GTAFORGE_WORKERS", "4")))

app = FastAPI(title="GTA Forge generation server", version=__version__)
app.add_middleware(
    CORSMiddleware,
    allow_origins=ORIGINS if "*" not in ORIGINS else ["*"],
    allow_methods=["GET", "POST", "DELETE", "OPTIONS"],
    allow_headers=["*"],
    max_age=600,
)


@app.middleware("http")
async def private_network_and_token(request: Request, call_next):
    # Chrome's Private Network Access: an https:// site calling http://localhost needs this header
    # on the preflight answer.
    if request.method == "OPTIONS":
        response = await call_next(request)
        response.headers["Access-Control-Allow-Private-Network"] = "true"
        return response
    if TOKEN and request.url.path.startswith("/v1/") and request.url.path != "/v1/health":
        sent = request.headers.get("x-gtaforge-token") or request.query_params.get("token")
        if sent != TOKEN:
            return JSONResponse({"error": "Wrong or missing access token."}, status_code=401)
    return await call_next(request)


def _gpu_name() -> Optional[str]:
    try:
        import torch
        return torch.cuda.get_device_name(0) if torch.cuda.is_available() else None
    except Exception:  # noqa: BLE001
        return None


@app.get("/v1/health")
def health():
    backends = []
    for b in BACKENDS.values():
        ok, reason = b.availability()
        backends.append({"id": b.id, "label": b.label, "kind": b.kind, "multiView": b.multi_view, "available": ok, "reason": reason})
    return {
        "name": "gtaforge-gen",
        "version": __version__,
        "gpu": _gpu_name(),
        "tokenRequired": bool(TOKEN),
        "queue": sum(1 for j in JOBS.values() if j.status in ("queued", "running")),
        "backends": backends,
    }


def _run(job: Job, inp: GenInput):
    backend = BACKENDS[job.backend]
    out = WORK / job.id / "model.glb"

    def progress(p: int, stage: str):
        job.progress = max(job.progress, min(99, int(p)))
        job.stage = stage

    try:
        if backend.exclusive:
            job.stage = "Waiting for the GPU"
            GPU_LOCK.acquire()
        try:
            job.status = "running"
            progress(1, "Starting")
            backend.generate(inp, out, progress)
        finally:
            if backend.exclusive:
                GPU_LOCK.release()
        if not out.exists() or out.stat().st_size == 0:
            raise RuntimeError("The backend finished without writing a model.")
        job.status, job.progress, job.stage = "done", 100, "Done"
    except Exception as e:  # noqa: BLE001
        traceback.print_exc()
        job.status, job.error, job.stage = "failed", str(e) or e.__class__.__name__, "Failed"
    finally:
        job.finished = time.time()


def _cleanup():
    now = time.time()
    with JOBS_LOCK:
        for jid, j in list(JOBS.items()):
            if j.finished and now - j.finished > JOB_TTL_SECONDS:
                shutil.rmtree(WORK / jid, ignore_errors=True)
                del JOBS[jid]


@app.post("/v1/jobs")
async def create_job(images: list[UploadFile] = File(...), backend: str = Form(...), options: str = Form("{}")):
    _cleanup()
    b = BACKENDS.get(backend)
    if b is None:
        raise HTTPException(400, f"Unknown backend '{backend}'.")
    ok, reason = b.availability()
    if not ok:
        raise HTTPException(400, reason)
    if not 1 <= len(images) <= 4:
        raise HTTPException(400, "Send 1 to 4 images (front first).")
    pil = []
    for f in images:
        data = await f.read()
        if len(data) > MAX_IMAGE_BYTES:
            raise HTTPException(413, "Images must be under 20 MB.")
        try:
            im = Image.open(io.BytesIO(data))
            im.load()
        except Exception:  # noqa: BLE001
            raise HTTPException(400, f"'{f.filename}' is not an image.")
        pil.append(im.convert("RGBA"))
    if not b.multi_view:
        pil = pil[:1]
    try:
        o = json.loads(options or "{}")
    except json.JSONDecodeError:
        raise HTTPException(400, "options must be JSON.")
    opts = GenOptions(
        target_faces=int(min(500_000, max(500, o.get("targetFaces", 50_000)))),
        texture_size=int(o.get("textureSize", 2048)) if int(o.get("textureSize", 2048)) in (512, 1024, 2048, 4096) else 2048,
        resolution=int(o.get("resolution", 1024)) if int(o.get("resolution", 1024)) in (512, 1024, 1536) else 1024,
        seed=int(o["seed"]) if o.get("seed") not in (None, "") else None,
        pbr=bool(o.get("pbr", True)),
    )
    job = Job(id=uuid.uuid4().hex[:12], backend=backend, faces_target=opts.target_faces)
    (WORK / job.id).mkdir(parents=True, exist_ok=True)
    with JOBS_LOCK:
        JOBS[job.id] = job
    POOL.submit(_run, job, GenInput(images=pil, options=opts))
    return job.public()


@app.get("/v1/jobs/{job_id}")
def get_job(job_id: str):
    job = JOBS.get(job_id)
    if job is None:
        raise HTTPException(404, "No such job (results are kept for 24 hours).")
    return job.public()


@app.get("/v1/jobs/{job_id}/model.glb")
def get_model(job_id: str):
    job = JOBS.get(job_id)
    path = WORK / job_id / "model.glb"
    if job is None or job.status != "done" or not path.exists():
        raise HTTPException(404, "Model not ready.")
    return FileResponse(path, media_type="model/gltf-binary", filename=f"generated_{job_id}.glb")


@app.delete("/v1/jobs/{job_id}")
def delete_job(job_id: str):
    with JOBS_LOCK:
        JOBS.pop(job_id, None)
    shutil.rmtree(WORK / job_id, ignore_errors=True)
    return Response(status_code=204)
