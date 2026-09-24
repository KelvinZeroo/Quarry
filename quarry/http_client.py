from __future__ import annotations

import hashlib
import json
import os
import random
import threading
import time
from pathlib import Path
from urllib.parse import urlparse

import httpx

from .config import Settings

RETRY_STATUSES = {429, 500, 502, 503, 504}

# Content-Type -> canonical extension. Used to verify and repair file extensions.
_CONTENT_TYPE_EXT: dict[str, str] = {
    "image/jpeg": ".jpg",
    "image/jpg": ".jpg",
    "image/png": ".png",
    "image/gif": ".gif",
    "image/webp": ".webp",
    "image/avif": ".avif",
    "image/bmp": ".bmp",
    "image/tiff": ".tiff",
    "video/mp4": ".mp4",
    "video/webm": ".webm",
}


class FetchError(RuntimeError):
    def __init__(self, url: str, message: str, status: int | None = None):
        super().__init__(f"{url}: {message}")
        self.url = url
        self.status = status


class HttpClient:
    """httpx wrapper with per-host rate limiting, retries, cookies and safe downloads."""

    def __init__(self, settings: Settings):
        self.settings = settings
        proxy = (
            settings.proxy
            or os.environ.get("HTTPS_PROXY")
            or os.environ.get("HTTP_PROXY")
            or os.environ.get("ALL_PROXY")
        )
        self._client = httpx.Client(
            headers={
                "User-Agent": settings.user_agent,
                "Accept": (
                    "text/html,application/xhtml+xml,application/xml;q=0.9,"
                    "image/avif,image/webp,*/*;q=0.8"
                ),
                "Accept-Language": "en-US,en;q=0.9",
            },
            timeout=settings.timeout,
            follow_redirects=True,
            proxy=proxy if proxy else None,
        )
        self._locks: dict[str, threading.Lock] = {}
        self._last: dict[str, float] = {}
        self._io_lock = threading.Lock()
        self.cookie_path = settings.cookie_path
        self.reload_cookies()

    # ------------------------------------------------------------------ cookies

    def reload_cookies(self) -> None:
        if not self.cookie_path.exists():
            return
        try:
            data = json.loads(self.cookie_path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            return
        for c in data:
            try:
                self._client.cookies.set(
                    c["name"], c["value"],
                    domain=c.get("domain", ""), path=c.get("path", "/"),
                )
            except Exception:
                continue

    def save_cookies(self) -> None:
        with self._io_lock:
            cookies = []
            for c in self._client.cookies.jar:
                cookies.append(
                    {"name": c.name, "value": c.value,
                     "domain": c.domain, "path": c.path}
                )
            self.cookie_path.parent.mkdir(parents=True, exist_ok=True)
            self.cookie_path.write_text(
                json.dumps(cookies, indent=2), encoding="utf-8"
            )

    def import_cookies(self, cookies: list[dict]) -> None:
        """Merge cookies (e.g. forwarded from the browser extension) into the jar."""
        for c in cookies:
            try:
                self._client.cookies.set(
                    c.get("name", ""), c.get("value", ""),
                    domain=c.get("domain", ""), path=c.get("path", "/"),
                )
            except Exception:
                continue
        self.save_cookies()

    # --------------------------------------------------------------- throttling

    def _throttle(self, url: str) -> None:
        host = urlparse(url).netloc or ""
        lock = self._locks.setdefault(host, threading.Lock())
        with lock:
            last = self._last.get(host, 0.0)
            wait = self.settings.delay - (time.monotonic() - last)
            if wait > 0:
                time.sleep(wait)
            self._last[host] = time.monotonic()

    @staticmethod
    def _backoff(attempt: int, retry_after: str | None = None) -> None:
        if retry_after:
            try:
                time.sleep(min(60.0, float(retry_after)))
                return
            except ValueError:
                pass
        time.sleep(min(30.0, 2.0 ** attempt) + random.uniform(0.0, 0.5))

    # ----------------------------------------------------------------- requests

    def get(self, url: str, referer: str | None = None,
            strict: bool = True) -> httpx.Response | None:
        """GET with per-host throttle and retries.

        strict=True  -> raise FetchError on failure / HTTP errors.
        strict=False -> return None instead of raising (used for probes).
        """
        headers = {"Referer": referer} if referer else {}
        last_exc: Exception | None = None
        last_status: int | None = None

        for attempt in range(self.settings.max_retries + 1):
            self._throttle(url)
            try:
                resp = self._client.get(url, headers=headers)
            except httpx.TransportError as e:
                last_exc, last_status = e, None
                # transient network failures are retried regardless of
                # strict - strictness decides raise-vs-None at the END
                self._backoff(attempt)
                continue

            status = resp.status_code
            if status in RETRY_STATUSES:
                last_exc, last_status = RuntimeError(f"HTTP {status}"), status
                self._backoff(attempt, resp.headers.get("Retry-After"))
                continue
            if status in (401, 403):
                if strict:
                    raise FetchError(url, f"blocked (HTTP {status})", status)
                return None
            if status >= 400:
                if strict:
                    raise FetchError(url, f"HTTP {status}", status)
                return None
            return resp

        if strict:
            raise FetchError(url, f"retries exhausted ({last_exc})", last_status)
        return None

    def get_html(self, url: str, referer: str | None = None,
                 strict: bool = True) -> str | None:
        resp = self.get(url, referer=referer, strict=strict)
        if resp is None:
            return None
        return resp.text

    def download(self, url: str, dest: Path, referer: str | None = None) -> tuple[int, str, Path]:
        """Stream URL to dest atomically (.part -> rename).

        Validates Content-Type (HTML/JSON error pages are rejected, never
        saved as fake images) and repairs the extension when the server
        tells us the real type (e.g. a GIF served behind a .jpg URL).

        Returns (bytes written, sha256, final destination path).
        """
        dest.parent.mkdir(parents=True, exist_ok=True)
        headers = {"Referer": referer} if referer else {}
        last_exc: Exception | None = None

        for attempt in range(self.settings.max_retries + 1):
            self._throttle(url)
            final_dest = dest
            tmp: Path | None = None
            try:
                with self._client.stream("GET", url, headers=headers) as resp:
                    if resp.status_code in RETRY_STATUSES:
                        raise _StatusError(resp.status_code)
                    if resp.status_code >= 400:
                        raise FetchError(
                            url, f"HTTP {resp.status_code}", resp.status_code
                        )

                    # Guard against HTML/JSON error pages saved under a media extension.
                    raw_ct = resp.headers.get("Content-Type") or ""
                    content_type = raw_ct.split(";")[0].strip().lower()
                    if content_type.startswith(
                        ("text/", "application/json", "application/xml")
                    ):
                        raise FetchError(
                            url, f"non-media response ({content_type})", resp.status_code
                        )
                    # Repair the extension when the server reveals the real type.
                    real_ext = _CONTENT_TYPE_EXT.get(content_type)
                    if real_ext and final_dest.suffix.lower() != real_ext:
                        try:
                            final_dest = final_dest.with_suffix(real_ext)
                        except ValueError:
                            pass

                    hasher = hashlib.sha256()
                    written = 0
                    tmp = final_dest.with_name(final_dest.name + ".part")
                    with open(tmp, "wb") as fh:
                        for chunk in resp.iter_bytes():
                            fh.write(chunk)
                            hasher.update(chunk)
                            written += len(chunk)

                if written == 0:
                    raise FetchError(url, "empty response body")
                if written < self.settings.min_bytes:
                    raise FetchError(
                        url, f"file too small ({written} < {self.settings.min_bytes} bytes)"
                    )
                os.replace(tmp, final_dest)
                return written, hasher.hexdigest(), final_dest
            except Exception as e:
                if tmp is not None:
                    try:
                        tmp.unlink(missing_ok=True)
                    except OSError:
                        pass
                last_exc = e
                if isinstance(e, FetchError) and e.status not in RETRY_STATUSES:
                    raise
                if attempt < self.settings.max_retries:
                    self._backoff(attempt)
        raise FetchError(url, f"download failed ({last_exc})")

    def close(self) -> None:
        self.save_cookies()
        self._client.close()

    def __enter__(self) -> "HttpClient":
        return self

    def __exit__(self, *exc) -> None:
        self.close()


class _StatusError(Exception):
    def __init__(self, status: int):
        super().__init__(f"HTTP {status}")
        self.status = status
