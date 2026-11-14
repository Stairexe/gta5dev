"""Writes chest.glb: a textured box with a hinged lid node and an 'open' animation.

Used to test the prop and animator pipelines end to end without any third-party assets.
Usage: python make_chest_glb.py OUT_DIR
"""
import json
import math
import struct
import sys
import zlib
from pathlib import Path


def png_checker(size=64, a=(160, 100, 50), b=(110, 65, 30)):
    rows = []
    for y in range(size):
        row = bytearray([0])
        for x in range(size):
            c = a if ((x // 8) + (y // 8)) % 2 == 0 else b
            row += bytes(c) + b"\xff"
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")


def box(sx, sy, sz, ox=0.0, oy=0.0, oz=0.0):
    """Axis-aligned box (glTF Y-up) with per-face normals/uvs."""
    faces = [((1, 0, 0), (0, 0, -1), (0, 1, 0)), ((-1, 0, 0), (0, 0, 1), (0, 1, 0)),
             ((0, 1, 0), (1, 0, 0), (0, 0, -1)), ((0, -1, 0), (1, 0, 0), (0, 0, 1)),
             ((0, 0, 1), (1, 0, 0), (0, 1, 0)), ((0, 0, -1), (-1, 0, 0), (0, 1, 0))]
    pos, nrm, uv, idx = [], [], [], []
    for n, u, v in faces:
        base = len(pos) // 3
        for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            p = [(n[i] + u[i] * su + v[i] * sv) * (sx, sy, sz)[i] / 2 + (ox, oy, oz)[i] for i in range(3)]
            pos += p
            nrm += n
            uv += [(su + 1) / 2, 1 - (sv + 1) / 2]
        idx += [base, base + 1, base + 2, base, base + 2, base + 3]
    return pos, nrm, uv, idx


def main(out_dir):
    blob = bytearray()
    views, accessors = [], []

    def add_view(data, target=None):
        while len(blob) % 4:
            blob.append(0)
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), **({"target": target} if target else {})})
        blob.extend(data)
        return len(views) - 1

    def add_accessor(values, comp, typ, count, target=None, minmax=False):
        fmt = {"f": 5126, "I": 5125}[comp]
        data = struct.pack(f"<{len(values)}{comp}", *values)
        acc = {"bufferView": add_view(data, target), "componentType": fmt, "count": count, "type": typ}
        if minmax:
            n = {"VEC3": 3, "SCALAR": 1}[typ]
            acc["min"] = [min(values[i::n]) for i in range(n)]
            acc["max"] = [max(values[i::n]) for i in range(n)]
        accessors.append(acc)
        return len(accessors) - 1

    def mesh(pos, nrm, uv, idx):
        return {"primitives": [{"attributes": {
            "POSITION": add_accessor(pos, "f", "VEC3", len(pos) // 3, 34962, True),
            "NORMAL": add_accessor(nrm, "f", "VEC3", len(nrm) // 3, 34962),
            "TEXCOORD_0": add_accessor(uv, "f", "VEC2", len(uv) // 2, 34962)},
            "indices": add_accessor(idx, "I", "SCALAR", len(idx), 34963), "material": 0}]}

    # Base: 1.0 x 0.6 x 0.6 (w, h, d) sitting on y=0. Lid: hinged on the back edge (z = -0.3) at the top.
    base = mesh(*box(1.0, 0.6, 0.6, 0, 0.3, 0))
    lid = mesh(*box(1.0, 0.12, 0.6, 0, 0.06, 0.3))  # extends forward from the hinge
    img_view = add_view(png_checker())

    times = [0.0, 1.0, 2.0]
    quats = []
    for ang in (0.0, -100.0, 0.0):
        h = math.radians(ang) / 2
        quats += [math.sin(h), 0.0, 0.0, math.cos(h)]  # around X
    t_acc = add_accessor(times, "f", "SCALAR", 3, minmax=True)
    q_acc = add_accessor(quats, "f", "VEC4", 3)

    gltf = {
        "asset": {"version": "2.0", "generator": "gtaforge-fixture"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [
            {"name": "chest", "children": [1, 2]},
            {"name": "base", "mesh": 0},
            {"name": "lid", "mesh": 1, "translation": [0, 0.6, -0.3]},
        ],
        "meshes": [base, lid],
        "materials": [{"name": "wood", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}, "metallicFactor": 0, "roughnessFactor": 0.8}}],
        "textures": [{"source": 0}],
        "images": [{"bufferView": img_view, "mimeType": "image/png", "name": "wood_checker"}],
        "animations": [{"name": "open", "samplers": [{"input": t_acc, "output": q_acc, "interpolation": "LINEAR"}],
                        "channels": [{"sampler": 0, "target": {"node": 2, "path": "rotation"}}]}],
        "buffers": [{"byteLength": 0}],
        "bufferViews": views,
        "accessors": accessors,
    }
    while len(blob) % 4:
        blob.append(0)
    gltf["buffers"][0]["byteLength"] = len(blob)
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * ((4 - len(js) % 4) % 4)
    glb = struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(blob))
    glb += struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(blob), 0x004E4942) + bytes(blob)
    out = Path(out_dir) / "chest.glb"
    out.write_bytes(glb)
    print(out, len(glb), "bytes")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
