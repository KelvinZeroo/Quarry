from __future__ import annotations

import re
from html import unescape
from urllib.parse import parse_qs, quote, urlparse

from ..models import Gallery, ImageItem
from .base import FetchFn, SiteAdapter

SITE = "https://rule34.xxx"
POST_ID_RE = re.compile(r"[?&]id=(\d+)")
LIST_ID_RE = re.compile(r"s=view&(?:amp;)?id=(\d+)")
# pagination hrefs: ?page=post&amp;s=list&amp;tags=...&amp;pid=42
LIST_PAGINATION_RE = re.compile(r'href="[^"]*s=list[^"]*pid=(\d+)"')
PAGE_SIZE = 42  # rule34 list pages advance by an offset of 42
OG_MEDIA_RE = re.compile(r'property="og:image"[^>]*content="([^"]+)"')
OG_MEDIA_ALT_RE = re.compile(r'content="([^"]+)"[^>]*property="og:image"')
MEDIA_SRC_RE = re.compile(
    r'src="(https://[^"]+?\.(?:jpe?g|png|gif|webp|mp4|webm)(?:\?[^"]*)?)"',
    re.I,
)
BAD_MEDIA = ("merch", "icame", "banner", "/static/", "logo", "avatar")
MAX_LIST_PAGES = 20


def _clean(text: str) -> str:
    """Collapse whitespace and strip HTML tags from tag strings."""
    text = re.sub(r"<[^>]+>", " ", text)
    return re.sub(r"\s+", " ", text).strip()


def _slugify(text: str, maxlen: int = 80) -> str:
    slug = re.sub(r"[^a-zA-Z0-9]+", "-", text).strip("-").lower()
    return slug[:maxlen] or "untitled"


def _ext_from_url(url: str) -> str:
    path = urlparse(url).path
    ext = path[path.rfind(".") :].lower() if "." in path else ""
    return ext if re.fullmatch(r"\.[a-z0-9]{2,5}", ext) else ".jpg"


class BooruAdapter(SiteAdapter):
    """Rule34 by scraping its HTML pages.

    The public JSON API now requires an API key, so we walk the HTML list
    pages (`page=post&s=list&tags=...`) and resolve each post through its
    view page, which exposes the original file via og:image (images and
    videos alike). Gelbooru/Danbooru intentionally do NOT match - their
    markup differs, so those URLs fall through to generic mode.
    """

    site = "rule34"

    @classmethod
    def matches(cls, url: str) -> bool:
        host = urlparse(url).hostname or ""
        return host == "rule34.xxx" or host.endswith(".rule34.xxx")

    def looks_valid(self, html: str) -> bool:
        if not html:
            return False
        if html.lstrip()[:1] in ("[", "{"):
            return True  # JSON payload (legacy API / proxies)
        if "CAPTCHA" in html:
            return False
        # list pages carry s=view links + a canonical s=list URL,
        # view pages carry og:image (and related s=view links)
        return any(m in html for m in ("s=view", "s=list", 'property="og:image"'))

    # -------------------------------------------------------------- helpers

    def _tag_from_url(self, url: str) -> str:
        query = parse_qs(urlparse(url).query)
        tags = query.get("tags", [""])[0]
        return _clean(tags.replace("+", " "))

    def _post_id_from_url(self, url: str) -> str | None:
        query = parse_qs(urlparse(url).query)
        if query.get("s", [""])[0] != "view":
            return None
        m = POST_ID_RE.search(url)
        return m.group(1) if m else None

    @staticmethod
    def _list_url(tag: str, pid: int) -> str:
        return f"{SITE}/index.php?page=post&s=list&tags={quote(tag)}&pid={pid}"

    @staticmethod
    def _discover_stride(html: str) -> int:
        """Smallest pid the page's own pagination links step by (else 42)."""
        steps = [int(v) for v in LIST_PAGINATION_RE.findall(html)]
        positive = [s for s in steps if s > 0]
        return min(positive) if positive else PAGE_SIZE

    @staticmethod
    def _view_url(post_id: str, page: str = "post") -> str:
        return f"{SITE}/index.php?page={quote(page)}&s=view&id={quote(post_id)}"

    @staticmethod
    def _media_from_view(html: str) -> str | None:
        """Original file URL from a post view page (og:image first)."""
        m = OG_MEDIA_RE.search(html) or OG_MEDIA_ALT_RE.search(html)
        if m:
            url = unescape(m.group(1)).strip()
            if url.startswith(("http://", "https://")):
                return url
        for cand in MEDIA_SRC_RE.findall(html):
            if any(b in cand.lower() for b in BAD_MEDIA):
                continue
            return unescape(cand)
        return None

    def _item_for_post(self, post_id: str, html: str, referer: str) -> ImageItem | None:
        media = self._media_from_view(html)
        if not media:
            return None
        return ImageItem(
            key=f"post/{post_id}",
            url=media,
            filename_hint=f"{post_id}{_ext_from_url(media)}",
            referer=referer,
        )

    # -------------------------------------------------------------- collect

    def collect(self, url: str, fetch: FetchFn, *, limit: int | None = None) -> Gallery:
        post_id = self._post_id_from_url(url)

        if post_id:
            # Single post: resolve its view page directly.
            page_val = parse_qs(urlparse(url).query).get("page", ["post"])[0] or "post"
            view = self._view_url(post_id, page=page_val)
            html = fetch(view)
            if not html:
                raise RuntimeError(f"failed to fetch post page: {view}")
            item = self._item_for_post(post_id, html, referer=view)
            if item is None:
                raise RuntimeError(f"no media found on post {post_id}")
            return Gallery(
                site=self.site,
                url=url,
                gallery_id=post_id,
                title=f"post-{post_id}",
                folder=f"post-{post_id}",
                items=[item],
                total_expected=1,
            )

        # Tag search: walk list pages, then resolve each post's view page.
        tag = self._tag_from_url(url)
        items, count = self._resolve_tag(tag, fetch, limit=limit)
        gallery_id = _slugify(tag) if tag else "all"
        title = f"tag_{tag}" if tag else "rule34-all"
        if not count:
            raise RuntimeError(
                f"no posts found for tag '{tag}' (empty tag, layout change, or block?)"
            )
        if not items:
            raise RuntimeError(
                f"found {count} posts for tag '{tag}' but could not resolve any "
                "media (blocked or layout change?)"
            )
        return Gallery(
            site=self.site,
            url=url,
            gallery_id=gallery_id,
            title=title,
            folder=f"tag_{gallery_id}" if tag else "rule34-all",
            items=items,
            total_expected=len(items),
        )

    def _resolve_tag(
        self, tag: str, fetch: FetchFn, limit: int | None = None
    ) -> tuple[list[ImageItem], int]:
        # 1) collect post ids from the HTML list pages.
        #    pid is an OFFSET stepping by one page of results (42 posts).
        ids: list[str] = []
        seen: set[str] = set()
        stride = PAGE_SIZE
        for page in range(MAX_LIST_PAGES):
            if limit and len(ids) >= limit:
                break
            html = fetch(self._list_url(tag, page * stride), strict=(page == 0))
            if not html:
                break
            if page == 0:
                stride = self._discover_stride(html)
            new = 0
            for found in LIST_ID_RE.findall(html):
                if found in seen:
                    continue
                seen.add(found)
                ids.append(found)
                new += 1
                if limit and len(ids) >= limit:
                    break
            if new == 0:
                break
        if limit:
            ids = ids[:limit]

        # 2) resolve each post's original file through its view page
        items: list[ImageItem] = []
        for post_id in ids:
            view = self._view_url(post_id)
            html = fetch(view, strict=False)
            if not html:
                continue
            item = self._item_for_post(post_id, html, referer=view)
            if item is not None:
                items.append(item)
        return items, len(ids)
