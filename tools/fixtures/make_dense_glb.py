"""Writes dense_blob.glb: a lumpy UV sphere stored as an unwelded triangle soup (every triangle has its own
3 vertices), like many AI-generated and scanned models. Used to test Remesh & Optimize.
Usage: python make_dense_glb.py OUT_DIR [SEGMENTS]
"""
import json
import math
import struct
import sys
from pathlib import Path


def main(out_dir, seg=200):
    rings, sectors = seg, seg
    grid = []
    for r in range(rings + 1):
        v = r / rings
        phi = v * math.pi
        row = []
        for s in range(sectors + 1):
            u = s / sectors
            th = u * 2 * math.pi
            bump = 1 + 0.08 * math.sin(6 * th) * math.sin(4 * phi)
            row.append(((0.5 * bump * math.sin(phi) * math.cos(th)), 0.5 + 0.5 * bump * math.cos(phi), (0.5 * bump * math.sin(phi) * math.sin(th)), u, v))
        grid.append(row)
    pos, uv = [], []
    for r in range(rings):
        for s in range(sectors):
            a, b, c, d = grid[r][s], grid[r][s + 1], grid[r + 1][s + 1], grid[r + 1][s]
            for tri in ((a, c, b), (a, d, c)):
                for p in tri:
                    pos += p[:3]
                    uv += p[3:]
    n = len(pos) // 3
    blob = struct.pack(f"<{len(pos)}f", *pos) + struct.pack(f"<{len(uv)}f", *uv)
    gltf = {
        "asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [0]}],
        "nodes": [{"name": "dense_blob", "mesh": 0}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "TEXCOORD_0": 1}, "material": 0}]}],
        "materials": [{"name": "clay", "pbrMetallicRoughness": {"baseColorFactor": [0.7, 0.45, 0.3, 1], "metallicFactor": 0}}],
        "buffers": [{"byteLength": len(blob)}],
        "bufferViews": [{"buffer": 0, "byteOffset": 0, "byteLength": len(pos) * 4, "target": 34962},
                        {"buffer": 0, "byteOffset": len(pos) * 4, "byteLength": len(uv) * 4, "target": 34962}],
        "accessors": [{"bufferView": 0, "componentType": 5126, "count": n, "type": "VEC3",
                       "min": [min(pos[i::3]) for i in range(3)], "max": [max(pos[i::3]) for i in range(3)]},
                      {"bufferView": 1, "componentType": 5126, "count": n, "type": "VEC2"}],
    }
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * ((4 - len(js) % 4) % 4)
    glb = struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(blob))
    glb += struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(blob), 0x004E4942) + blob
    out = Path(out_dir) / "dense_blob.glb"
    out.write_bytes(glb)
    print(out, f"{n // 3} triangles, {n} vertices (unwelded)")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".", int(sys.argv[2]) if len(sys.argv) > 2 else 200)
