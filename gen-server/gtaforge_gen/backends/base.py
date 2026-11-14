from __future__ import annotations

import inspect
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Optional

from PIL import Image


@dataclass
class GenOptions:
    """Options the website can send. Backends ignore what they do not support."""
    target_faces: int = 50_000
    texture_size: int = 2048
    resolution: int = 1024  # TRELLIS.2: 512 / 1024 / 1536
    seed: Optional[int] = None
    pbr: bool = True


@dataclass
class GenInput:
    images: list[Image.Image]  # first = front
    options: GenOptions = field(default_factory=GenOptions)


# progress(percent 0-100, stage text)
Progress = Callable[[int, str], None]


class Backend:
    id: str = ""
    label: str = ""
    kind: str = "local"  # local (your GPU) | cloud (paid API) | test
    multi_view: bool = False
    #: Local GPU backends run one job at a time.
    exclusive: bool = True

    def availability(self) -> tuple[bool, str]:
        """(available, reason shown in the website when it is not)."""
        return True, ""

    def generate(self, inp: GenInput, out_path: Path, progress: Progress) -> None:
        raise NotImplementedError


def call_with_supported(fn, *args, **kwargs):
    """Calls fn with only the keyword arguments its signature accepts (model APIs drift between versions)."""
    try:
        params = inspect.signature(fn).parameters
        if not any(p.kind == p.VAR_KEYWORD for p in params.values()):
            kwargs = {k: v for k, v in kwargs.items() if k in params}
    except (TypeError, ValueError):
        pass
    return fn(*args, **kwargs)
