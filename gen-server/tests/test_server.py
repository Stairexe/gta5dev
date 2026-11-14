import importlib
import io
import struct
import time

import pytest
from fastapi.testclient import TestClient
from PIL import Image


def make_client(monkeypatch, token=""):
    monkeypatch.setenv("GTAFORGE_ENABLE_MOCK", "1")
    monkeypatch.setenv("GTAFORGE_MOCK_DELAY", "0.01")
    monkeypatch.setenv("GTAFORGE_TOKEN", token)
    import gtaforge_gen.server as server
    server = importlib.reload(server)
    return TestClient(server.app)


def png(w=64, h=32, color=(200, 40, 40, 255)):
    buf = io.BytesIO()
    Image.new("RGBA", (w, h), color).save(buf, format="PNG")
    return buf.getvalue()


def wait(client, job_id, headers=None):
    for _ in range(200):
        j = client.get(f"/v1/jobs/{job_id}", headers=headers or {}).json()
        if j["status"] in ("done", "failed"):
            return j
        time.sleep(0.02)
    raise AssertionError("job did not finish")


def test_health_lists_backends_with_reasons(monkeypatch):
    c = make_client(monkeypatch)
    h = c.get("/v1/health").json()
    ids = {b["id"]: b for b in h["backends"]}
    assert ids["mock"]["available"]
    assert not ids["trellis2"]["available"] and ids["trellis2"]["reason"]
    assert not ids["meshy"]["available"] and "MESHY_API_KEY" in ids["meshy"]["reason"]


def test_mock_job_produces_valid_glb(monkeypatch):
    c = make_client(monkeypatch)
    r = c.post("/v1/jobs", data={"backend": "mock", "options": '{"targetFaces": 20000}'},
               files=[("images", ("front.png", png(), "image/png"))])
    assert r.status_code == 200, r.text
    j = wait(c, r.json()["id"])
    assert j["status"] == "done" and j["progress"] == 100
    glb = c.get(j["modelUrl"]).content
    magic, version, length = struct.unpack("<III", glb[:12])
    assert magic == 0x46546C67 and version == 2 and length == len(glb)


def test_unavailable_backend_is_refused(monkeypatch):
    c = make_client(monkeypatch)
    r = c.post("/v1/jobs", data={"backend": "meshy"}, files=[("images", ("f.png", png(), "image/png"))])
    assert r.status_code == 400 and "MESHY_API_KEY" in r.json()["detail"]


def test_not_an_image_is_refused(monkeypatch):
    c = make_client(monkeypatch)
    r = c.post("/v1/jobs", data={"backend": "mock"}, files=[("images", ("f.png", b"nope", "image/png"))])
    assert r.status_code == 400


def test_token_is_enforced(monkeypatch):
    c = make_client(monkeypatch, token="s3cret")
    assert c.get("/v1/health").status_code == 200  # health stays open so the site can say "token needed"
    assert c.get("/v1/health").json()["tokenRequired"] is True
    r = c.post("/v1/jobs", data={"backend": "mock"}, files=[("images", ("f.png", png(), "image/png"))])
    assert r.status_code == 401
    r = c.post("/v1/jobs", data={"backend": "mock"}, files=[("images", ("f.png", png(), "image/png"))],
               headers={"X-GtaForge-Token": "s3cret"})
    assert r.status_code == 200


def test_preflight_allows_private_network(monkeypatch):
    c = make_client(monkeypatch)
    r = c.options("/v1/jobs", headers={
        "Origin": "https://gta-forge-jet.vercel.app",
        "Access-Control-Request-Method": "POST",
        "Access-Control-Request-Private-Network": "true",
    })
    assert r.headers.get("access-control-allow-origin") == "https://gta-forge-jet.vercel.app"
    assert r.headers.get("access-control-allow-private-network") == "true"
