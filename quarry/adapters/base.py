from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Callable

from ..models import Gallery, ImageItem

# fetch(url, referer=None, strict=True) -> html (str) | None
FetchFn = Callable[..., str | None]


class SiteAdapter(ABC):
    site: str = ""

    # Optional hook: when a download fails with 401/403 (e.g. an expired
    # signed CDN token), the pipeline asks the adapter to mint a fresh URL.
    refresh_item: Callable[[ImageItem, FetchFn], str | None] | None = None

    @classmethod
    def matches(cls, url: str) -> bool:
        raise NotImplementedError

    def is_listing(self, url: str) -> bool:
        """True if this URL is a search/tag/category/listing page instead of a single gallery."""
        return False

    def crawl_galleries(
        self,
        url: str,
        fetch: FetchFn,
        max_galleries: int | None = None,
    ) -> list[str]:
        """Crawl a listing page and return individual gallery URLs."""
        return []

    @abstractmethod
    def collect(
        self, url: str, fetch: FetchFn, *, limit: int | None = None
    ) -> Gallery:
        """Fetch the gallery and resolve all full-size media URLs.

        limit: optional --max-images cap. Adapters that resolve media
        request-by-request should stop early; single-page adapters may
        ignore it (the pipeline truncates afterwards either way).
        """

    @abstractmethod
    def looks_valid(self, html: str) -> bool:
        """Heuristic: does this response contain the structure we expect?"""


class NoAdapterError(ValueError):
    pass


def get_adapter(url: str, site_hint: str | None = None) -> SiteAdapter:
    from .booru import BooruAdapter
    from .erome import EromeAdapter
    from .imagefap import ImageFapAdapter
    from .pornpics import PornPicsAdapter

    adapters = (PornPicsAdapter, ImageFapAdapter, EromeAdapter, BooruAdapter)

    if site_hint:
        site_clean = site_hint.lower().strip()
        for cls in adapters:
            if cls.site == site_clean:
                return cls()

    for cls in adapters:
        if cls.matches(url):
            return cls()

    # Fallback heuristic for odd URLs (mirrors, subdomains, query params).
    url_lower = url.lower()
    if "pornpics" in url_lower:
        return PornPicsAdapter()
    if "imagefap" in url_lower:
        return ImageFapAdapter()
    if "erome" in url_lower:
        return EromeAdapter()
    if "rule34" in url_lower:
        return BooruAdapter()

    raise NoAdapterError(f"No adapter supports URL: {url}")
