from __future__ import annotations

import re
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Callable

from .config import Settings
from .http_client import FetchError, HttpClient
from .manifest import Manifest
from .models import ImageItem

_ILLEGAL = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


def sanitize(name: str, maxlen: int = 140) -> str:
    """Make a string safe as a Windows/POSIX filename."""
    name = _ILLEGAL.sub("_", name).strip(" .")
    name = re.sub(r"\s+", " ", name)
    return name[:maxlen] or "image"


def dest_for(dest_dir: Path, index: int, item: ImageItem) -> Path:
    """0001_short-name.ext: 4-digit order first, then a short (<=16) name.

    Keeps folders easy to scan and sort. Missing extensions are repaired
    later from the server's content type.
    """
    hint = (item.filename_hint or "").strip()
    if not hint:
        return dest_dir / f"{index:04d}.jpg"
    raw = sanitize(hint, maxlen=64)
    m = re.match(r"^(?P<name>.+?)(?P<ext>\.[A-Za-z0-9]{2,5})$", raw)
    name = (m.group("name") if m else raw).strip("-_ .")[:16].strip("-_ .")
    ext = m.group("ext") if m else ""
    if not name:
        return dest_dir / f"{index:04d}{ext or '.jpg'}"
    return dest_dir / f"{index:04d}_{name}{ext}"


def run_downloads(
    client: HttpClient,
    items: list[ImageItem],
    dest_dir: Path,
    settings: Settings,
    manifest: Manifest,
    on_status: Callable[[str], None] | None = None,
    refresher: Callable[[ImageItem], str | None] | None = None,
) -> dict[str, int]:
    """Download items with a small thread pool; returns status counts.

    on_status is invoked from the MAIN thread only (safe for UI updates).
    refresher(item) may re-mint an expired signed URL (e.g. ImageFap 403).
    """
    stats = {"downloaded": 0, "skipped": 0, "failed": 0}

    def work(index_item: tuple[int, ImageItem]) -> tuple[str, str | None]:
        index, item = index_item
        dest = dest_for(dest_dir, index, item)
        if not settings.force and manifest.is_done(item.key):
            return "skipped", None
        try:
            try:
                written, digest, final_dest = client.download(
                    item.url, dest, referer=item.referer
                )
            except FetchError as e:
                # Expired signed CDN token: ask the adapter for a fresh URL once.
                if refresher and e.status in (401, 403):
                    fresh = refresher(item)
                    if not fresh or fresh == item.url:
                        raise
                    written, digest, final_dest = client.download(
                        fresh, dest, referer=item.referer
                    )
                else:
                    raise
            manifest.record(
                item.key, final_dest, "ok",
                bytes_=written, sha256=digest, url=item.url,
            )
            return "downloaded", None
        except Exception as e:
            manifest.record(
                item.key, dest, "failed", url=item.url, error=str(e)[:300]
            )
            return "failed", f"{item.key}: {e}"

    dest_dir.mkdir(parents=True, exist_ok=True)
    errors: list[str] = []
    with ThreadPoolExecutor(max_workers=max(1, settings.workers)) as pool:
        futures = [
            pool.submit(work, p)
            for p in enumerate(items, start=1)
        ]
        for fut in as_completed(futures):
            status, err = fut.result()
            stats[status] += 1
            if err:
                errors.append(err)
            if on_status:
                on_status(status)

    stats["errors"] = len(errors)  # type: ignore[assignment]
    return stats
