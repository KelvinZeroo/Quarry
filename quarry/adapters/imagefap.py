from __future__ import annotations

import re
from urllib.parse import quote, unquote, urlparse

from bs4 import BeautifulSoup

from ..models import Gallery, ImageItem
from .base import FetchFn, SiteAdapter

GID_RE = re.compile(r"(?:[?&]gid=|/(?:gallery|pictures)/)(\d+)")
PHOTO_RE = re.compile(r"/photo/(\d+)")
GALLERY_ROW_RE = re.compile(r'href="/gallery\.php\?gid=(\d+)"')

_BASE = "https://www.imagefap.com"
_WINDOW_SIZE = 24  # photo pages expose this many images per window


def _slugify(text: str, maxlen: int = 80) -> str:
    slug = re.sub(r"[^a-zA-Z0-9]+", "-", text).strip("-").lower()
    return slug[:maxlen] or "untitled"


def _basename(url: str) -> str:
    return urlparse(url).path.rsplit("/", 1)[-1]


class ImageFapAdapter(SiteAdapter):
    site = "imagefap"

    @classmethod
    def matches(cls, url: str) -> bool:
        host = urlparse(url).hostname or ""
        # exact host or a real subdomain only (never "notimagefap.com.evil")
        return host == "imagefap.com" or host.endswith(".imagefap.com")

    def looks_valid(self, html: str) -> bool:
        return (
            "_navi_cavi" in html
            or "gal_thumb" in html
            or "/photo/" in html
            or "gal_title" in html
            or "gallery.php?gid=" in html
        )

    def is_listing(self, url: str) -> bool:
        """Search/profile pages are listings; gid/photo URLs are single galleries."""
        if not url.startswith(("http://", "https://")):
            return True
        path_query = url.lower()
        if "/search/" in path_query or "search=" in path_query:
            return True
        if "/profile/" in path_query or "/usergallery.php" in path_query:
            return True
        if GID_RE.search(url) or PHOTO_RE.search(url):
            return False
        return True

    def crawl_galleries(
        self,
        url: str,
        fetch: FetchFn,
        max_galleries: int | None = None,
    ) -> list[str]:
        """Crawl ImageFap search results and return individual gallery URLs."""
        base_url = url
        limit = max_galleries or 50
        seen_gids: set[str] = set()
        gallery_urls: list[str] = []

        page = 1
        while len(gallery_urls) < limit and page <= 50:
            target_url = base_url
            if page > 1:
                sep = "&" if "?" in base_url else "?"
                target_url = f"{base_url}{sep}page={page}"

            html = fetch(target_url, strict=False)
            if not html:
                break

            page_found = 0
            for m in GALLERY_ROW_RE.finditer(html):
                gid = m.group(1)
                if gid in seen_gids:
                    continue
                seen_gids.add(gid)
                gallery_urls.append(f"{_BASE}/gallery.php?gid={gid}")
                page_found += 1
                if len(gallery_urls) >= limit:
                    break

            if page_found == 0:
                break
            page += 1

        return gallery_urls

    # ----------------------------------------------------------------- parse

    @staticmethod
    def _parse_photo_page(html: str) -> tuple[list[tuple[str, str]], int | None]:
        """Extract ordered [(imageid, original_url)] plus data-total.

        Every photo page embeds the whole gallery's images as <a> or <input>
        tags with `original` and `imageid` attributes.
        """
        soup = BeautifulSoup(html, "lxml")
        entries: list[tuple[str, str]] = []
        for tag in soup.select(
            "#_navi_cavi a[original], #_navi_cavi input[original], "
            "a[original], input[original]"
        ):
            imageid = tag.get("imageid")
            original = tag.get("original")
            if imageid and original:
                entries.append((imageid, original))

        total: int | None = None
        navi = soup.select_one("#_navi_cavi")
        if navi and navi.get("data-total"):
            try:
                total = int(navi["data-total"])
            except ValueError:
                total = None
        return entries, total

    @staticmethod
    def _parse_gallery_page(html: str) -> tuple[str | None, list[str]]:
        """Return (title, ordered photo ids) from a gallery.php?gid= page."""
        soup = BeautifulSoup(html, "lxml")
        title = None

        link = soup.select_one("a.gal_title")
        if link and link.get_text(strip=True):
            title = link.get_text(strip=True)

        if not title and soup.title and soup.title.string:
            raw = soup.title.string.strip()
            for suffix in (
                " Porn Pics & Porn Gals at ImageFap.com",
                " at ImageFap.com",
                " Porn Pics",
                " - ImageFap",
            ):
                if suffix in raw:
                    raw = raw.split(suffix)[0].strip()
            if raw and not raw.lower().startswith("imagefap"):
                title = raw

        if not title:
            for h in soup.find_all("h1"):
                t = h.get_text(strip=True)
                if t and not any(
                    skip in t.lower()
                    for skip in ["users who added", "favorite", "related", "comment"]
                ):
                    title = t
                    break

        ids: list[str] = []
        seen: set[str] = set()
        for m in PHOTO_RE.finditer(html):
            pid = m.group(1)
            if pid not in seen:
                seen.add(pid)
                ids.append(pid)
        return title, ids

    @staticmethod
    def _gallery_title_from_photo(html: str) -> str | None:
        soup = BeautifulSoup(html, "lxml")
        for h1 in soup.find_all("h1"):
            text = h1.get_text(strip=True)
            if text:
                return text
        return None

    # -------------------------------------------------------------- collect

    def collect(
        self, url: str, fetch: FetchFn, *, limit: int | None = None
    ) -> Gallery:
        gid = None
        m = GID_RE.search(url)
        if m:
            gid = m.group(1)

        title: str | None = None
        gallery_ids: list[str] = []
        seed_photo_url: str | None = None
        photo_html: str | None = None

        if PHOTO_RE.search(url) and not gid:
            # Given a photo page: it carries the gid and all originals.
            seed_photo_url = url.split("?")[0]
            if not seed_photo_url.endswith("/"):
                seed_photo_url += "/"
            photo_html = fetch(seed_photo_url)
            soup = BeautifulSoup(photo_html or "", "lxml")
            inp = soup.select_one("#galleryid_input")
            if inp and inp.get("value"):
                gid = inp["value"]
            title = self._gallery_title_from_photo(photo_html or "")
        else:
            if not gid:
                path_m = re.search(r"/(?:gallery|pictures)/(\d+)", url)
                if path_m:
                    gid = path_m.group(1)
                else:
                    raise ValueError(f"Not an ImageFap gallery or photo URL: {url}")

            gallery_url = f"{_BASE}/gallery.php?gid={gid}"
            gallery_html = fetch(gallery_url)
            if not gallery_html:
                raise RuntimeError(f"failed to fetch gallery page: {gallery_url}")
            title, gallery_ids = self._parse_gallery_page(gallery_html)
            if not gallery_ids:
                raise RuntimeError(
                    f"no photos found in gallery {gid} (layout change or block?)"
                )
            seed_photo_url = f"{_BASE}/photo/{gallery_ids[0]}/?gid={gid}"
            photo_html = fetch(seed_photo_url)

        # Resolve full-size signed originals from photo page(s).
        entries: list[tuple[str, str]] = []
        total: int | None = None
        if photo_html:
            entries, total = self._parse_photo_page(photo_html)

        entry_map: dict[str, str] = {}
        order: list[str] = []
        for imageid, original in entries:
            if imageid not in entry_map:
                order.append(imageid)
            entry_map[imageid] = original

        # Windowed page tiling: resolve missing ids individually.
        expected = total or (len(gallery_ids) or None)
        missing = [pid for pid in gallery_ids if pid not in entry_map]
        attempts = 0
        while missing and attempts < 50:
            pid = missing.pop(0)
            attempts += 1
            purl = f"{_BASE}/photo/{pid}/?gid={gid}" if gid else f"{_BASE}/photo/{pid}/"
            phtml = fetch(purl, strict=False)
            if not phtml:
                continue
            got, _ = self._parse_photo_page(phtml)
            new = 0
            for imageid, original in got:
                if imageid not in entry_map:
                    order.append(imageid)
                    new += 1
                entry_map[imageid] = original
            if pid not in entry_map:
                continue
            if new == 0:
                break

        # The gallery page may only expose the first window of thumbnails;
        # tile the seed photo page's idx windows to discover the rest.
        if expected and len(entry_map) < expected and seed_photo_url:
            idx = _WINDOW_SIZE
            idle = 0
            while (
                len(entry_map) < expected
                and idx <= expected + _WINDOW_SIZE
                and idle < 2
            ):
                sep = "&" if "?" in seed_photo_url else "?"
                phtml = fetch(f"{seed_photo_url}{sep}idx={idx}", strict=False)
                if not phtml:
                    break
                got, _ = self._parse_photo_page(phtml)
                added = 0
                for imageid, original in got:
                    if imageid not in entry_map:
                        entry_map[imageid] = original
                        order.append(imageid)
                        added += 1
                idle = 0 if added else idle + 1
                idx += _WINDOW_SIZE

        if expected and len(entry_map) < expected:
            print(f"  warning: resolved {len(entry_map)} of {expected} images")

        if not title:
            if "/pictures/" in url:
                raw_title = url.split("/pictures/")[1].strip("/").split("/", 1)[-1]
                if raw_title and raw_title != gid:
                    title = unquote(raw_title).replace("+", " ")
            if not title:
                title = f"gallery-{gid}" if gid else "untitled"

        referer = seed_photo_url or f"{_BASE}/gallery.php?gid={gid}"
        items = [
            ImageItem(
                key=f"photo/{imageid}",        # stable, ignores signed URL
                url=entry_map[imageid],        # fresh signed full-size URL
                filename_hint=_basename(entry_map[imageid]),
                referer=referer,
            )
            for imageid in order
        ]

        folder = f"{gid}_{_slugify(title)}" if gid else _slugify(title)

        return Gallery(
            site=self.site,
            url=f"{_BASE}/gallery.php?gid={gid}" if gid else url,
            gallery_id=gid or _slugify(url),
            title=title,
            folder=folder,
            items=items,
            total_expected=expected,
        )

    # -------------------------------------------------------------- refresh

    def refresh_item(self, item: ImageItem, fetch: FetchFn) -> str | None:
        """Re-mint an expired signed CDN token by re-reading photo windows."""
        m = re.match(r"photo/(\d+)$", item.key or "")
        if not m:
            return None
        pid = m.group(1)

        gid_m = re.search(r"[?&]gid=(\d+)", item.referer or "")
        gid = gid_m.group(1) if gid_m else None
        referer = f"{_BASE}/gallery.php?gid={gid}" if gid else (item.referer or None)

        idx = 0
        total = 0
        max_idx = 1000
        while idx < max_idx:
            url = f"{_BASE}/photo/{pid}/?gid={gid}" if gid else f"{_BASE}/photo/{pid}/"
            if idx:
                url += f"&idx={idx}"
            html = fetch(url, referer=referer, strict=False)
            if not html:
                return None
            entries, window_total = self._parse_photo_page(html)
            if not entries:
                return None
            if not total and window_total:
                total = window_total
                max_idx = total + _WINDOW_SIZE
            for imageid, original in entries:
                if imageid == pid:
                    return original
            idx += _WINDOW_SIZE
        return None
