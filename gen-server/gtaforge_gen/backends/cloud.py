"""Paid cloud backends, called with YOUR API keys from this server (keys never reach the browser).

Meshy  -> what gtax.dev sells as "Prism"  (MESHY_API_KEY)
Tripo  -> what gtax.dev sells as "Atlas"  (TRIPO_API_KEY)
"""
from __future__ import annotations

import base64
import io
import os
import time
from pathlib import Path

import httpx

from .base import Backend, GenInput, Progress

POLL_SECONDS = 3
TIMEOUT_SECONDS = 15 * 60


def _png_data_uri(img) -> str:
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return "data:image/png;base64," + base64.b64encode(buf.getvalue()).decode()


def _download(url: str, out_path: Path):
    with httpx.stream("GET", url, timeout=120, follow_redirects=True) as r:
        r.raise_for_status()
        with open(out_path, "wb") as f:
            for chunk in r.iter_bytes():
                f.write(chunk)


class Meshy(Backend):
    id = "meshy"
    label = "Meshy 7.1 (cloud, ~$0.38-0.60 per model)"
    kind = "cloud"
    multi_view = True
    exclusive = False
    base = "https://api.meshy.ai/openapi/v1"

    def availability(self):
        return (True, "") if os.environ.get("MESHY_API_KEY") else (False, "Set MESHY_API_KEY on the server to enable.")

    def generate(self, inp: GenInput, out_path: Path, progress: Progress):
        o = inp.options
        headers = {"Authorization": f"Bearer {os.environ['MESHY_API_KEY']}"}
        body = {
            "ai_model": os.environ.get("MESHY_MODEL", "latest"),
            "should_texture": True,
            "enable_pbr": o.pbr,
            "should_remesh": True,
            "topology": "triangle",
            "target_polycount": max(100, min(300_000, o.target_faces)),
        }
        multi = len(inp.images) > 1
        if multi:
            endpoint = f"{self.base}/multi-image-to-3d"
            body["image_urls"] = [_png_data_uri(i) for i in inp.images[:4]]
        else:
            endpoint = f"{self.base}/image-to-3d"
            body["image_url"] = _png_data_uri(inp.images[0])
        progress(5, "Uploading to Meshy")
        with httpx.Client(timeout=60) as c:
            r = c.post(endpoint, json=body, headers=headers)
            r.raise_for_status()
            task_id = r.json()["result"]
            start = time.time()
            while time.time() - start < TIMEOUT_SECONDS:
                t = c.get(f"{endpoint}/{task_id}", headers=headers).json()
                status = t.get("status")
                progress(10 + int(t.get("progress", 0) * 0.85), f"Meshy: {status.lower() if status else 'waiting'}")
                if status == "SUCCEEDED":
                    progress(96, "Downloading GLB")
                    _download(t["model_urls"]["glb"], out_path)
                    return
                if status in ("FAILED", "CANCELED"):
                    raise RuntimeError(f"Meshy task {status.lower()}: {t.get('task_error', {}).get('message', '')}")
                time.sleep(POLL_SECONDS)
        raise TimeoutError("Meshy did not finish in 15 minutes.")


class Tripo(Backend):
    id = "tripo"
    label = "Tripo (cloud, ~$0.30-0.60 per model)"
    kind = "cloud"
    multi_view = False
    exclusive = False
    base = "https://openapi.tripo3d.ai/v3"

    def availability(self):
        return (True, "") if os.environ.get("TRIPO_API_KEY") else (False, "Set TRIPO_API_KEY on the server to enable.")

    def generate(self, inp: GenInput, out_path: Path, progress: Progress):
        o = inp.options
        headers = {"Authorization": f"Bearer {os.environ['TRIPO_API_KEY']}"}
        buf = io.BytesIO()
        inp.images[0].save(buf, format="PNG")
        progress(5, "Uploading to Tripo")
        with httpx.Client(timeout=60) as c:
            up = c.post(f"{self.base}/files", headers=headers, files={"file": ("front.png", buf.getvalue(), "image/png")})
            up.raise_for_status()
            token = up.json()["data"]["file_token"]
            body = {"input": token, "texture": True, "pbr": o.pbr, "face_limit": o.target_faces}
            if os.environ.get("TRIPO_MODEL"):
                body["model"] = os.environ["TRIPO_MODEL"]
            r = c.post(f"{self.base}/generation/image-to-model", headers=headers, json=body)
            r.raise_for_status()
            task_id = r.json()["data"]["task_id"]
            start = time.time()
            while time.time() - start < TIMEOUT_SECONDS:
                t = c.get(f"{self.base}/tasks/{task_id}", headers=headers).json()["data"]
                status = t.get("status")
                progress(10 + int(t.get("progress", 0) * 0.85), f"Tripo: {status}")
                if status == "success":
                    progress(96, "Downloading GLB")
                    _download(t["output"]["model_url"], out_path)
                    return
                if status in ("failed", "cancelled", "banned", "expired"):
                    raise RuntimeError(f"Tripo task {status}.")
                time.sleep(POLL_SECONDS)
        raise TimeoutError("Tripo did not finish in 15 minutes.")
