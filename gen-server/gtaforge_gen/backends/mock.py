"""Test backend: no GPU, no API key. Turns the uploaded image into a textured block (GLB).

It exists so the website workflow (upload -> progress -> preview -> Prop Studio) can be tested
anywhere. Enable with GTAFORGE_ENABLE_MOCK=1.
"""
from __future__ import annotations

import io
import json
import os
import struct
import time
from pathlib import Path

from .base import Backend, GenInput, Progress


def _box(w: float, h: float, d: float):
    """Box (glTF Y-up) sitting on y=0; +Z face carries the full image, other faces a strip."""
    faces = [((0, 0, 1), (1, 0, 0), (0, 1, 0)), ((0, 0, -1), (-1, 0, 0), (0, 1, 0)),
             ((1, 0, 0), (0, 0, -1), (0, 1, 0)), ((-1, 0, 0), (0, 0, 1), (0, 1, 0)),
             ((0, 1, 0), (1, 0, 0), (0, 0, -1)), ((0, -1, 0), (1, 0, 0), (0, 0, 1))]
    size = (w, h, d)
    pos, nrm, uv, idx = [], [], [], []
    for fi, (n, u, v) in enumerate(faces):
        base = len(pos) // 3
        for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            p = [(n[i] + u[i] * su + v[i] * sv) * size[i] / 2 for i in range(3)]
            p[1] += h / 2
            pos += p
            nrm += n
            if fi == 0:
                uv += [(su + 1) / 2, 1 - (sv + 1) / 2]
            else:
                uv += [0.02 + 0.06 * (su + 1) / 2, 0.5]
        idx += [base, base + 1, base + 2, base, base + 2, base + 3]
    return pos, nrm, uv, idx


def write_textured_box_glb(png: bytes, aspect: float, out_path: Path):
    h = 1.0
    w = max(0.2, min(3.0, aspect))
    pos, nrm, uv, idx = _box(w, h, 0.3)
    blob = bytearray()
    views, accessors = [], []

    def view(data: bytes, target=None):
        while len(blob) % 4:
            blob.append(0)
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), **({"target": target} if target else {})})
        blob.extend(data)
        return len(views) - 1

    def acc(vals, comp, typ, count, target=None, minmax=False):
        data = struct.pack(f"<{len(vals)}{comp}", *vals)
        a = {"bufferView": view(data, target), "componentType": {"f": 5126, "I": 5125}[comp], "count": count, "type": typ}
        if minmax:
            a["min"] = [min(vals[i::3]) for i in range(3)]
            a["max"] = [max(vals[i::3]) for i in range(3)]
        accessors.append(a)
        return len(accessors) - 1

    prim = {"attributes": {"POSITION": acc(pos, "f", "VEC3", len(pos) // 3, 34962, True),
                           "NORMAL": acc(nrm, "f", "VEC3", len(nrm) // 3, 34962),
                           "TEXCOORD_0": acc(uv, "f", "VEC2", len(uv) // 2, 34962)},
            "indices": acc(idx, "I", "SCALAR", len(idx), 34963), "material": 0}
    img = view(png)
    gltf = {
        "asset": {"version": "2.0", "generator": "gtaforge-gen mock"},
        "scene": 0, "scenes": [{"nodes": [0]}],
        "nodes": [{"name": "generated", "mesh": 0}],
        "meshes": [{"primitives": [prim]}],
        "materials": [{"name": "generated", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}, "metallicFactor": 0, "roughnessFactor": 0.7}}],
        "textures": [{"source": 0}],
        "images": [{"bufferView": img, "mimeType": "image/png"}],
        "buffers": [{"byteLength": 0}], "bufferViews": views, "accessors": accessors,
    }
    while len(blob) % 4:
        blob.append(0)
    gltf["buffers"][0]["byteLength"] = len(blob)
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * ((4 - len(js) % 4) % 4)
    out = struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(blob))
    out += struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(blob), 0x004E4942) + bytes(blob)
    out_path.write_bytes(out)


class Mock(Backend):
    id = "mock"
    label = "Test generator (no GPU, makes a textured block)"
    kind = "test"
    multi_view = True
    exclusive = False

    def availability(self):
        return (True, "") if os.environ.get("GTAFORGE_ENABLE_MOCK") == "1" else (False, "Test backend is off.")

    def generate(self, inp: GenInput, out_path: Path, progress: Progress):
        img = inp.images[0].convert("RGBA")
        img.thumbnail((512, 512))
        for p, stage in ((20, "Pretending to build the shape"), (55, "Pretending to paint textures"), (85, "Packing GLB")):
            progress(p, stage)
            time.sleep(float(os.environ.get("GTAFORGE_MOCK_DELAY", "0.6")))
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        write_textured_box_glb(buf.getvalue(), img.width / max(1, img.height), out_path)
