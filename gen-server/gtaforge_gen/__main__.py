import os

import uvicorn


def main():
    host = os.environ.get("GTAFORGE_HOST", "127.0.0.1")
    port = int(os.environ.get("GTAFORGE_PORT", "8000"))
    uvicorn.run("gtaforge_gen.server:app", host=host, port=port, log_level="info")


if __name__ == "__main__":
    main()
