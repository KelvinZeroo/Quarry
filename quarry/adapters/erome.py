from __future__ import annotations

import re
from urllib.parse import quote, urljoin, urlparse

from bs4 import BeautifulSoup

from ..models import Gallery, ImageItem
from .base import FetchFn, SiteAdapter

ALBUM_RE = re.compile(r"/a/(?P<id>[a-zA-Z0-9]+)")


def _slugify(text: str, maxlen: int = 80) -> str:
    slug = re.sub(r"[^a-zA-Z0-9]+", "-", text).strip("-").lower()
    return slug[:maxlen] or "untitled"


def _basename(url: str) -> str:
    return urlparse(url).path.rsplit("/", 1)[-1]


class EromeAdapter(SiteAdapter):
    site = "erome"

    @classmethod
    def matches(cls, url: str) -> bool:
        host = urlparse(url).hostname or ""
        return host == "erome.com" or host.endswith(".erome.com")

    def looks_valid(self, html: str) -> bool:
        return (
            'class="image-wrapper"' in html
            or 'class="video-wrapper"' in html
            or "/a/" in html
        )

    def is_listing(self, url: str) -> bool:
        if not url.startswith(("http://", "https://")):
            return True
        return not bool(ALBUM_RE.search(url))

    def crawl_galleries(
        self,
        url: str,
        fetch: FetchFn,
        max_galleries: int | None = None,
    ) -> list[str]:
        """Crawl a search/user page and return album URLs."""
        base_url = url
        limit = max_galleries or 50
        seen_aids: set[str] = set()
        album_urls: list[str] = []

        page = 1
        while len(album_urls) < limit and page <= 20:
            target_url = base_url if page == 1 else f"{base_url}&page={page}"
            html = fetch(target_url, strict=False)
            if not html:
                break

            soup = BeautifulSoup(html, "lxml")
            page_found = 0
            for a in soup.select("a[href*='/a/']"):
                href = a.get("href", "")
                m = ALBUM_RE.search(href)
                if not m:
                    continue
                aid = m.group("id")
                if aid in seen_aids:
                    continue
                seen_aids.add(aid)
                full_url = urljoin("https://www.erome.com", href)
                album_urls.append(full_url)
                page_found += 1
                if len(album_urls) >= limit:
                    break

            if page_found == 0:
                break
            page += 1

        return album_urls

    def collect(
        self, url: str, fetch: FetchFn, *, limit: int | None = None
    ) -> Gallery:
        m = ALBUM_RE.search(url)
        if not m:
            raise ValueError(f"Not an EroMe album URL: {url}")
        aid = m.group("id")

        html = fetch(url)
        if not html:
            raise RuntimeError(f"failed to fetch album page: {url}")

        soup = BeautifulSoup(html, "lxml")

        title = None
        h1 = soup.select_one("h1")
        if h1 and h1.get_text(strip=True):
            title = h1.get_text(strip=True)
        if not title:
            title = f"album-{aid}"

        items: list[ImageItem] = []
        seen_urls: set[str] = set()

        for img in soup.select(".image-wrapper img, img[data-src]"):
            src = img.get("data-src") or img.get("src")
            if src and "thumb_" not in src and "avatar" not in src:
                full_src = urljoin("https://www.erome.com", src)
                if full_src not in seen_urls:
                    seen_urls.add(full_src)
                    items.append(ImageItem(
                        key=f"erome/{aid}/{_basename(full_src)}",
                        url=full_src,
                        filename_hint=_basename(full_src),
                        referer=url,
                    ))

        for video in soup.select(".video-wrapper video, video"):
            source = video.select_one("source")
            src = (source.get("src") if source else None) or video.get("src")
            if src:
                full_src = urljoin("https://www.erome.com", src)
                if full_src not in seen_urls:
                    seen_urls.add(full_src)
                    items.append(ImageItem(
                        key=f"erome/{aid}/{_basename(full_src)}",
                        url=full_src,
                        filename_hint=_basename(full_src),
                        referer=url,
                    ))

        folder = f"{aid}_{_slugify(title)}"
        return Gallery(
            site=self.site,
            url=url,
            gallery_id=aid,
            title=title,
            folder=folder,
            items=items,
            total_expected=len(items),
        )
