from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

DEFAULT_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) "
    "Chrome/131.0.0.0 Safari/537.36"
)

# Project root (folder that contains config.json), derived from this file's location.
PROJECT_ROOT = Path(__file__).resolve().parent.parent
CONFIG_PATH = PROJECT_ROOT / "config.json"


def load_config() -> dict:
    """Read config.json next to the project. Missing/broken file -> {}."""
    try:
        data = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except (OSError, json.JSONDecodeError):
        return {}


def default_out_dir(cfg: dict | None = None) -> Path:
    """downloadDir from config.json, else <user profile>\\Downloads\\Quarry."""
    if cfg is None:
        cfg = load_config()
    configured = str(cfg.get("downloadDir") or "").strip()
    if configured:
        return Path(configured).expanduser()
    return Path.home() / "Downloads" / "Quarry"


@dataclass
class Settings:
    out_dir: Path = field(default_factory=lambda: Path.home() / "Downloads" / "Quarry")
    delay: float = 0.2            # min seconds between requests to the same host
    workers: int = 8              # parallel file downloads
    timeout: float = 30.0         # per-request timeout (seconds)
    max_retries: int = 3          # retries on transport errors / 429 / 5xx
    max_images: int | None = None # cap number of files per gallery/page
    max_galleries: int | None = None  # cap galleries crawled from a listing page
    proxy: str | None = None      # HTTP or SOCKS5 proxy URL
    force: bool = False           # re-download even if manifest says done
    dry_run: bool = False         # resolve URLs but do not download
    forced_site: str | None = None  # CLI --site: force an adapter ("generic" = none)
    min_bytes: int = 1024         # files smaller than this are treated as failed
    user_agent: str = DEFAULT_UA

    @property
    def cookie_path(self) -> Path:
        return self.out_dir / ".cookies" / "cookies.json"

    def manifest_path(self, site: str, gallery_id: str) -> Path:
        return self.out_dir / ".manifests" / site / f"{gallery_id}.jsonl"


def _as_positive_int(value, default: int | None = None) -> int | None:
    try:
        n = int(value)
    except (TypeError, ValueError):
        return default
    return n if n > 0 else None


def load_settings(
    *,
    out_dir: str | Path | None = None,
    workers: int | None = None,
    delay: float | None = None,
    max_images: int | None = None,
    max_galleries: int | None = None,
    proxy: str | None = None,
    dry_run: bool = False,
) -> Settings:
    """Build Settings: explicit flag > config.json > built-in default."""
    cfg = load_config()

    if out_dir:
        resolved_out = Path(out_dir).expanduser()
    else:
        resolved_out = default_out_dir(cfg)

    if workers is None:
        workers = _as_positive_int(cfg.get("workers"), default=8) or 8
    if delay is None:
        try:
            delay = float(cfg.get("delay", 0.2))
        except (TypeError, ValueError):
            delay = 0.2

    # Explicit flag wins; otherwise fall back to config.json.
    max_images = _as_positive_int(max_images) if max_images is not None else _as_positive_int(cfg.get("maxImages"))
    max_galleries = _as_positive_int(max_galleries) if max_galleries is not None else _as_positive_int(cfg.get("maxGalleries"), default=10)

    if not proxy:
        cfg_proxy = str(cfg.get("proxy") or "").strip()
        proxy = cfg_proxy or None

    return Settings(
        out_dir=resolved_out,
        workers=workers,
        delay=delay,
        max_images=max_images,
        max_galleries=max_galleries,
        proxy=proxy,
        dry_run=dry_run,
    )
