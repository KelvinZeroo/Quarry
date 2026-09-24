from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class ImageItem:
    """A single file to download.

    key: stable identity that never changes between runs (used for the
         manifest / dedup). Never store short-lived signed CDN URLs here.
    url: full-size download URL (may be signed and short-lived; always
         resolved freshly at collect time).
    """

    key: str
    url: str
    filename_hint: str | None = None
    referer: str | None = None


@dataclass
class Gallery:
    site: str
    url: str
    gallery_id: str
    title: str
    folder: str
    items: list[ImageItem] = field(default_factory=list)
    total_expected: int | None = None
