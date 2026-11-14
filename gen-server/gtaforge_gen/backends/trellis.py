"""Self-hosted Microsoft TRELLIS.2 (4B, PBR) and TRELLIS v1 (image-large).

Both are MIT-licensed. They must be installed in the same Python environment as this server
(see gen-server/README.md). Models load lazily on the first job and stay in GPU memory.
"""
from __future__ import annotations

import importlib.util
import os
import threading
from pathlib import Path

from .base import Backend, GenInput, Progress, call_with_supported


def _gpu() -> tuple[bool, str, int]:
    try:
        import torch  # noqa: WPS433
    except ImportError:
        return False, "PyTorch is not installed in this environment.", 0
    if not torch.cuda.is_available():
        return False, "No CUDA GPU visible to PyTorch.", 0
    props = torch.cuda.get_device_properties(0)
    return True, props.name, int(props.total_memory / (1024 ** 3))


class Trellis2(Backend):
    id = "trellis2"
    label = "TRELLIS.2 (your GPU, PBR)"
    kind = "local"
    multi_view = False

    def __init__(self):
        self._pipeline = None
        self._lock = threading.Lock()

    def availability(self):
        if importlib.util.find_spec("trellis2") is None or importlib.util.find_spec("o_voxel") is None:
            return False, "TRELLIS.2 is not installed (trellis2 / o_voxel packages not found)."
        ok, name, vram = _gpu()
        if not ok:
            return False, name
        if vram < 23:
            return False, f"{name} has {vram} GB; TRELLIS.2 needs 24 GB or more. Use TRELLIS (v1) instead."
        return True, name

    def _load(self, progress: Progress):
        with self._lock:
            if self._pipeline is None:
                progress(3, "Loading TRELLIS.2 (first run downloads ~16 GB of weights)")
                os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")
                from trellis2.pipelines import Trellis2ImageTo3DPipeline
                pipe = Trellis2ImageTo3DPipeline.from_pretrained(os.environ.get("TRELLIS2_MODEL", "microsoft/TRELLIS.2-4B"))
                pipe.cuda()
                self._pipeline = pipe
        return self._pipeline

    def generate(self, inp: GenInput, out_path: Path, progress: Progress):
        import o_voxel

        pipe = self._load(progress)
        o = inp.options
        progress(15, f"Generating shape and materials ({o.resolution}³)")
        res = {512: "512", 1024: "1024_cascade", 1536: "1536_cascade"}.get(o.resolution, "1024_cascade")
        kwargs = {"pipeline_type": res}
        if o.seed is not None:
            kwargs["seed"] = o.seed
        mesh = call_with_supported(pipe.run, inp.images[0], **kwargs)[0]
        progress(70, "Simplifying")
        mesh.simplify(16777216)
        progress(78, f"Remeshing to ~{o.target_faces:,} faces and baking {o.texture_size}px textures")
        glb = o_voxel.postprocess.to_glb(
            vertices=mesh.vertices,
            faces=mesh.faces,
            attr_volume=mesh.attrs,
            coords=mesh.coords,
            attr_layout=mesh.layout,
            voxel_size=mesh.voxel_size,
            aabb=[[-0.5, -0.5, -0.5], [0.5, 0.5, 0.5]],
            decimation_target=max(1000, o.target_faces),
            texture_size=o.texture_size,
            remesh=True,
            remesh_band=1,
            remesh_project=0,
            verbose=False,
        )
        progress(95, "Writing GLB")
        glb.export(str(out_path))


class Trellis1(Backend):
    id = "trellis"
    label = "TRELLIS v1 (your GPU, 16 GB+)"
    kind = "local"
    multi_view = True

    def __init__(self):
        self._pipeline = None
        self._lock = threading.Lock()

    def availability(self):
        if importlib.util.find_spec("trellis") is None:
            return False, "TRELLIS is not installed (trellis package not found)."
        ok, name, vram = _gpu()
        if not ok:
            return False, name
        if vram < 15:
            return False, f"{name} has {vram} GB; TRELLIS needs 16 GB or more."
        return True, name

    def _load(self, progress: Progress):
        with self._lock:
            if self._pipeline is None:
                progress(3, "Loading TRELLIS (first run downloads the weights)")
                from trellis.pipelines import TrellisImageTo3DPipeline
                pipe = TrellisImageTo3DPipeline.from_pretrained(os.environ.get("TRELLIS_MODEL", "JeffreyXiang/TRELLIS-image-large"))
                pipe.cuda()
                self._pipeline = pipe
        return self._pipeline

    def generate(self, inp: GenInput, out_path: Path, progress: Progress):
        from trellis.utils import postprocessing_utils

        pipe = self._load(progress)
        o = inp.options
        seed = o.seed if o.seed is not None else 1
        progress(15, f"Generating from {len(inp.images)} view(s)")
        if len(inp.images) > 1 and hasattr(pipe, "run_multi_image"):
            out = call_with_supported(pipe.run_multi_image, inp.images, seed=seed, formats=["gaussian", "mesh"])
        else:
            out = call_with_supported(pipe.run, inp.images[0], seed=seed, formats=["gaussian", "mesh"])
        progress(70, f"Baking {o.texture_size}px texture")
        # `simplify` is the fraction of faces removed; aim for roughly target_faces.
        raw_faces = int(out["mesh"][0].faces.shape[0]) if hasattr(out["mesh"][0], "faces") else 500_000
        simplify = min(0.98, max(0.0, 1 - o.target_faces / max(1, raw_faces)))
        glb = postprocessing_utils.to_glb(out["gaussian"][0], out["mesh"][0], simplify=simplify, texture_size=o.texture_size)
        progress(95, "Writing GLB")
        glb.export(str(out_path))
