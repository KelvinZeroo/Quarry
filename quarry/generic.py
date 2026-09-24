"""Universal fallback extractor: grab every image/GIF/video on any page.

Used when no site adapter recognizes the URL. Combines what is found in the
page HTML with optional media URLs collected live from the browser DOM
(extra_urls, sent by the extension), so it also works on JS-heavy pages.
"""

from __future__ import annotations

import hashlib
import os
import re
from urllib.parse import unquote, urljoin, urlparse

from bs4 import BeautifulSoup

from .models import Gallery, ImageItem
from .adapters.base import FetchFn

MEDIA_EXTS = {".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".bmp", ".tiff",
              ".mp4", ".m4v", ".webm"}

# Obvious UI chrome we never want to download.
BLOCK_EXTS = {".html", ".htm", ".css", ".js", ".mjs", ".json", ".xml", ".svg",
              ".ico", ".woff", ".woff2", ".ttf", ".eot"}
JUNK_RE = re.compile(
    r"avatar|logo|sprite|placeholder|spinner|1x1|pixel|loading|favicon|tracking",
    re.I,
)


def _slugify(text: str, maxlen: int = 80) -> str:
    slug = re.sub(r"[^a-zA-Z0-9]+", "-", text).strip("-").lower()
    return slug[:maxlen] or "page"


def _title_from_html(html: str, url: str) -> str:
    soup = BeautifulSoup(html, "lxml")
    if soup.title and soup.title.string:
        t = re.sub(r"\s+", " ", soup.title.string).strip()
        if t:
            return t[:120]
    h1 = soup.find("h1")
    if h1:
        t = re.sub(r"\s+", " ", h1.get_text()).strip()
        if t:
            return t[:120]
    path = urlparse(url).path.rstrip("/")
    last = unquote(path.rsplit("/", 1)[-1]) if path else ""
    return last or urlparse(url).netloc or "page"


def _best_from_srcset(ss: str) -> str | None:
    """Pick the largest candidate from a srcset attribute."""
    best, best_w = None, -1
    last = None
    for entry in ss.split(","):
        parts = entry.strip().split()
        if not parts:
            continue
        last = parts[0]
        if len(parts) >= 2 and parts[1].lower().endswith("w"):
            try:
                w = int(float(parts[1].rstrip("wW")))
            except ValueError:
                continue
            if w > best_w:
                best, best_w = parts[0], w
    return best or last


def _extract_from_html(html: str, base: str, sink: list[str], seen: set[str]) -> None:
    def add(u: str | None, require_media_ext: bool = False) -> None:
        if not u:
            return
        u = u.strip()
        if u.startswith(("data:", "blob:", "javascript:", "mailto:", "#")):
            return
        try:
            absu = urljoin(base, u)
        except ValueError:
            return
        if not absu.lower().startswith(("http://", "https://")):
            return
        absu = absu.split("#")[0]
        parsed = urlparse(absu)
        path = parsed.path.lower()
        ext = os.path.splitext(path)[1]
        if ext in BLOCK_EXTS:
            return
        if ext and JUNK_RE.search(path):
            return
        if require_media_ext and ext not in MEDIA_EXTS:
            return
        if absu in seen:
            return
        seen.add(absu)
        sink.append(absu)

    soup = BeautifulSoup(html, "lxml")

    for tag in soup.find_all(["img", "source", "video"]):
        for attr in ("src", "data-src", "data-original", "data-lazy"):
            add(tag.get(attr))
        ss = tag.get("srcset") or tag.get("data-srcset")
        if ss:
            add(_best_from_srcset(ss))

    og = soup.find("meta", attrs={"property": "og:image"})
    if og and og.get("content"):
        add(og.get("content"))

    # Links must look like real media files (avoids nav links etc).
    for a in soup.find_all("a", href=True):
        add(a.get("href"), require_media_ext=True)


def collect(url: str, fetch: FetchFn, extra_urls: list[str] | None = None) -> Gallery:
    """Collect all media for an arbitrary page. Raises ValueError when nothing is found."""
    html = fetch(url, strict=False)
    sink: list[str] = []
    seen: set[str] = set()

    if html:
        _extract_from_html(html, url, sink, seen)
    if extra_urls:
        # Media already present in the live DOM (sent by the extension).
        for u in extra_urls:
            if isinstance(u, str) and u.startswith("http"):
                u_norm = u.split("#")[0]
                if u_norm not in seen and not JUNK_RE.search(urlparse(u_norm).path):
                    seen.add(u_norm)
                    sink.append(u_norm)

    if not sink:
        raise ValueError(
            "no media found on this page (blocked, empty page, or no images)"
        )

    title = _title_from_html(html, url) if html else (urlparse(url).netloc or "page")
    host = (urlparse(url).hostname or "page").lower()
    if host.startswith("www."):
        host = host[4:]

    items = []
    for u in sink:
        base = unquote(os.path.basename(urlparse(u).path))
        items.append(
            ImageItem(
                key=u,                     # stable: full URL
                url=u,
                filename_hint=base or None,
                referer=url,
            )
        )

    folder = _slugify(title)
    gallery_id = hashlib.md5(url.encode("utf-8")).hexdigest()[:12]

    return Gallery(
        site=host,
        url=url,
        gallery_id=gallery_id,
        title=title,
        folder=folder,
        items=items,
        total_expected=len(items),
    )
