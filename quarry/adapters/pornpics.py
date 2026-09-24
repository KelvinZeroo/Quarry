from __future__ import annotations

import re
from urllib.parse import quote, urljoin, urlparse

from bs4 import BeautifulSoup

from ..models import Gallery, ImageItem
from .base import FetchFn, SiteAdapter

# https://www.pornpics.com/galleries/<slug>-<gid>/
GALLERY_RE = re.compile(r"/galleries/[^/]+?-(\d+)/?$")
GALLERY_LINK_RE = re.compile(r"/galleries/[^/]+?-(\d+)/?")


def _basename(url: str) -> str:
    return urlparse(url).path.rsplit("/", 1)[-1]


class PornPicsAdapter(SiteAdapter):
    site = "pornpics"

    @classmethod
    def matches(cls, url: str) -> bool:
        host = urlparse(url).hostname or ""
        return host == "pornpics.com" or host.endswith(".pornpics.com")

    def looks_valid(self, html: str) -> bool:
        return 'id="tiles"' in html or "rel-link" in html or "/galleries/" in html

    def is_listing(self, url: str) -> bool:
        """Tag/category/channel/search pages are listings; /galleries/<slug>-<gid>/ is a single gallery."""
        if not url.startswith(("http://", "https://")):
            return True
        path = urlparse(url).path
        return not bool(GALLERY_RE.search(path))

    def crawl_galleries(
        self,
        url: str,
        fetch: FetchFn,
        max_galleries: int | None = None,
    ) -> list[str]:
        """Crawl a search/tag/channel/category page and return gallery URLs."""
        base_crawl_url = url
        limit = max_galleries or 50
        seen_gids: set[str] = set()
        gallery_urls: list[str] = []

        page = 1
        offset = 0
        while len(gallery_urls) < limit and page <= 50:
            if page == 1:
                target_url = base_crawl_url
            else:
                sep = "&" if "?" in base_crawl_url else "?"
                target_url = f"{base_crawl_url}{sep}page={page}&offset={offset}"

            html = fetch(target_url, strict=False)
            if not html:
                break

            soup = BeautifulSoup(html, "lxml")
            page_found = 0
            for a in soup.select(
                "a.rel-link[href], .thumbw a[href], a[href*='/galleries/']"
            ):
                href = a.get("href", "")
                m = GALLERY_LINK_RE.search(href)
                if not m:
                    continue
                gid = m.group(1)
                if gid in seen_gids:
                    continue
                seen_gids.add(gid)
                full_url = urljoin("https://www.pornpics.com", href)
                if not full_url.endswith("/"):
                    full_url += "/"
                gallery_urls.append(full_url)
                page_found += 1
                if len(gallery_urls) >= limit:
                    break

            if page_found == 0:
                break
            offset += page_found
            page += 1

        return gallery_urls

    # ------------------------------------------------------------------ parse

    @staticmethod
    def _parse_tiles(soup: BeautifulSoup) -> list[tuple[str, str]]:
        """Return [(data-tid, full_size_url)] for real gallery images.

        Gallery pages link tiles straight to the CDN max-size (1280) image;
        promo/ad tiles link to /go/... and are filtered out by the CDN host
        check. The /460/ thumbnail variant is upgraded to /1280/.
        """
        out: list[tuple[str, str]] = []
        links = (
            soup.select("#tiles a.rel-link[href]")
            or soup.select(".thumbw a.rel-link[href]")
            or soup.select("a.rel-link[href]")
        )
        for a in links:
            href = a.get("href", "")
            if "cdni.pornpics.com" not in href and "pics.pornpics.com" not in href:
                continue
            href = href.replace("/460/", "/1280/")
            out.append((a.get("data-tid", ""), href))
        return out

    @staticmethod
    def _title(soup: BeautifulSoup) -> str | None:
        h1 = soup.select_one(".gallery-title h1, #content h1, h1")
        if h1 and h1.get_text(strip=True):
            return h1.get_text(strip=True)
        if soup.title and soup.title.string:
            return soup.title.string.strip().removesuffix(" - PornPics.com")
        return None

    @staticmethod
    def _total(soup: BeautifulSoup, html: str) -> int | None:
        meta = soup.select('meta[name="description"]')
        if meta:
            m = re.search(r"Watch (\d+) pics", meta[0].get("content", ""))
            if m:
                return int(m.group(1))
        return None

    # -------------------------------------------------------------- collect

    def collect(
        self, url: str, fetch: FetchFn, *, limit: int | None = None
    ) -> Gallery:
        base = url.split("?")[0]
        if not base.endswith("/"):
            base += "/"

        html = fetch(base)
        if not html:
            raise RuntimeError(f"failed to fetch gallery page: {base}")
        soup = BeautifulSoup(html, "lxml")

        title = self._title(soup) or "untitled"
        total = self._total(soup, html)

        gid_match = GALLERY_RE.search(urlparse(base).path)
        gallery_id = gid_match.group(1) if gid_match else re.sub(r"\D", "", base)[-12:]
        folder = urlparse(base).path.rstrip("/").rsplit("/", 1)[-1] or gallery_id

        seen: set[str] = set()
        entries: list[tuple[str, str]] = []
        for tid, href in self._parse_tiles(soup):
            if href in seen:
                continue
            seen.add(href)
            entries.append((tid, href))

        # Adaptive pagination: only needed when the gallery advertises more
        # pics than we parsed (galleries up to P_MAX=50 fit on one page).
        page = 2
        while total and len(entries) < total and page <= 200:
            added = False
            for candidate in (f"{base}?page={page}", f"{base}{page}/"):
                html2 = fetch(candidate, strict=False)
                if not html2:
                    continue
                soup2 = BeautifulSoup(html2, "lxml")
                new = [
                    (tid, href)
                    for tid, href in self._parse_tiles(soup2)
                    if href not in seen
                ]
                if new:
                    for tid, href in new:
                        seen.add(href)
                        entries.append((tid, href))
                    added = True
                    break
            if not added:
                break
            page += 1

        if total and len(entries) < total:
            print(
                f"  warning: parsed {len(entries)} of {total} images "
                f"(pagination format may have changed)"
            )

        items = [
            ImageItem(
                key=href,        # stable: unsigned CDN path
                url=href,        # 1280 max-size variant
                filename_hint=_basename(href),
                referer=base,
            )
            for _tid, href in entries
        ]

        return Gallery(
            site=self.site,
            url=base,
            gallery_id=gallery_id,
            title=title,
            folder=folder,
            items=items,
            total_expected=total,
        )
