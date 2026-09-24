from __future__ import annotations

import hashlib
import re
from datetime import datetime
from pathlib import Path
from typing import Callable

from . import generic
from .adapters import NoAdapterError, get_adapter
from .adapters.base import FetchFn, SiteAdapter
from .config import Settings
from .downloader import run_downloads
from .http_client import FetchError, HttpClient
from .manifest import Manifest

LogFn = Callable[[str], None]
GalleryFn = Callable[[str, int, Path], None]
StatusFn = Callable[[str], None]

_FOLDER_BAD = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


def folder_name(raw: str, gallery_id: str, maxlen: int = 16) -> str:
    """Gallery folder name capped at 16 characters.

    Long names are cut to 11 chars plus a 4-char hash tail of the gallery
    id, so two different galleries can never land in the same folder.
    Names that already fit are kept as-is.
    """
    name = _FOLDER_BAD.sub("-", str(raw))
    name = re.sub(r"\s+", "-", name).strip(" .-")
    if not name:
        return "gallery"
    if len(name) <= maxlen:
        return name
    tail = hashlib.sha1(str(gallery_id).encode("utf-8")).hexdigest()[:4]
    head = name[: maxlen - 5].rstrip("-_ ")
    return f"{head}-{tail}" if head else tail


def resolve_adapter(url: str, settings: Settings) -> SiteAdapter | None:
    """Adapter for a URL; settings.forced_site overrides detection.

    forced_site == "generic" forces the no-adapter path on purpose.
    """
    if settings.forced_site == "generic":
        return None
    try:
        return get_adapter(url, site_hint=settings.forced_site)
    except NoAdapterError:
        return None


def write_info(
    dest_dir: Path,
    *,
    site: str,
    title: str,
    source: str,
    stats: dict,
) -> None:
    """Write info.txt: where this folder came from and what is inside it."""
    try:
        files = sorted(
            p.name
            for p in dest_dir.iterdir()
            if p.is_file() and p.name.lower() != "info.txt"
        )
        lines = [
            "Quarry source info",
            f"{'Site:':<12} {site}",
            f"{'Title:':<12} {title}",
            f"{'Source:':<12} {source}",
            f"{'Downloaded:':<12} {datetime.now().strftime('%Y-%m-%d %H:%M')}",
            f"{'Result:':<12} {stats['downloaded']} downloaded, "
            f"{stats['skipped']} skipped, {stats['failed']} failed",
            "",
            "Files in this folder:",
        ]
        lines += [f"  {name}" for name in files]
        (dest_dir / "info.txt").write_text(
            "\n".join(lines) + "\n", encoding="utf-8"
        )
    except OSError:
        pass  # never fail a completed download over a readme


def make_fetcher(adapter: SiteAdapter | None, settings: Settings, client: HttpClient) -> FetchFn:
    """Page fetcher: HTTP with retries/throttle; validates expected page content.

    fetch(url, referer=None, strict=True):
        strict=True  -> return HTML or raise FetchError
        strict=False -> return HTML or None (used for probes; never raises)
    """

    def fetch(url: str, referer: str | None = None, strict: bool = True) -> str | None:
        try:
            html = client.get_html(url, referer=referer, strict=strict)
        except FetchError:
            if strict:
                raise
            return None
        if html is None:
            return None
        if adapter is None or adapter.looks_valid(html):
            return html
        if not strict:
            return None
        raise FetchError(
            url, "page did not contain expected content (block or layout change)"
        )

    return fetch


def run_one(
    url: str,
    settings: Settings,
    client: HttpClient,
    *,
    adapter: SiteAdapter | None = None,
    extra_media: list[str] | None = None,
    on_log: LogFn = print,
    on_gallery: GalleryFn | None = None,
    on_status: StatusFn | None = None,
) -> bool:
    """Process a single gallery/page URL. Returns True on success."""
    if adapter is None:
        adapter = resolve_adapter(url, settings)

    fetch = make_fetcher(adapter, settings, client)

    if adapter is not None:
        on_log(f"[{adapter.site}] {url}")
        gallery = adapter.collect(url, fetch, limit=settings.max_images)
    else:
        on_log(f"[generic] {url}")
        gallery = generic.collect(url, fetch, extra_urls=extra_media)

    items = gallery.items
    if settings.max_images:
        items = items[: settings.max_images]

    on_log(f"  {gallery.title} - {len(items)} file(s)")

    if not items:
        on_log("  nothing to download")
        return False

    dest_dir = (
        settings.out_dir / gallery.site / folder_name(gallery.folder, gallery.gallery_id)
    )
    if on_gallery:
        on_gallery(gallery.title, len(items), dest_dir)

    if settings.dry_run:
        for i, item in enumerate(items, 1):
            on_log(f"    {i:03d}  {item.url}")
        on_log("  dry run ok - nothing downloaded")
        return True

    manifest = Manifest(
        settings.manifest_path(gallery.site, gallery.gallery_id),
        base_dir=settings.out_dir,
    )

    refresher = None
    if adapter is not None and adapter.refresh_item is not None:
        refresher = lambda item: adapter.refresh_item(item, fetch)  # noqa: E731

    stats = run_downloads(
        client, items, dest_dir, settings, manifest,
        on_status=on_status, refresher=refresher,
    )

    write_info(
        dest_dir, site=gallery.site, title=gallery.title,
        source=gallery.url, stats=stats,
    )

    on_log(
        f"  downloaded {stats['downloaded']}, "
        f"skipped {stats['skipped']}, "
        f"failed {stats['failed']}  -> {dest_dir}"
    )
    return stats["failed"] == 0


def run(
    urls: list[str],
    settings: Settings,
    *,
    client: HttpClient | None = None,
    extra_media: list[str] | None = None,
    on_log: LogFn = print,
    on_gallery: GalleryFn | None = None,
    on_status: StatusFn | None = None,
) -> int:
    """Run over a list of URLs (listing, gallery or any page). Returns exit code."""
    ok = True
    own_client = client is None
    if own_client:
        client = HttpClient(settings)

    try:
        settings.out_dir.mkdir(parents=True, exist_ok=True)
        for url in urls:
            try:
                if not str(url).lower().startswith(("http://", "https://")):
                    raise ValueError(f"not a URL: {url}")

                adapter = resolve_adapter(url, settings)

                if adapter is not None and adapter.is_listing(url):
                    # Search / tag / category page: crawl the galleries it links to.
                    on_log(f"[listing] {url}")
                    fetch = make_fetcher(adapter, settings, client)
                    gallery_urls = adapter.crawl_galleries(
                        url, fetch, max_galleries=settings.max_galleries
                    )
                    on_log(f"  found {len(gallery_urls)} galleries")
                    if not gallery_urls:
                        on_log("  no galleries found (blocked, empty, or layout change)")
                        ok = False
                    for g in gallery_urls:
                        if not run_one(
                            g, settings, client,
                            adapter=adapter,
                            on_log=on_log, on_gallery=on_gallery, on_status=on_status,
                        ):
                            ok = False
                else:
                    if not run_one(
                        url, settings, client,
                        adapter=adapter,
                        extra_media=extra_media,
                        on_log=on_log, on_gallery=on_gallery, on_status=on_status,
                    ):
                        ok = False
            except KeyboardInterrupt:
                on_log("interrupted")
                return 130
            except Exception as e:
                msg = str(e)
                if url not in msg:
                    msg = f"{url}: {msg}"
                on_log(f"error: {msg}")
                ok = False
    finally:
        if own_client:
            client.close()

    return 0 if ok else 2
