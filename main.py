#!/usr/bin/env python3
"""Quarry - one universal download command.

Usage:
    python main.py <url> [url ...]        # any supported site, or any page at all
    python main.py -f urls.txt            # batch: one URL per line
    python main.py --list-sites           # show supported sites
    python main.py <url> --quiet --open   # summary only, open the folder after

Site is auto-detected from the URL. Unknown sites fall back to generic
mode (grabs every image/GIF/video found in the page).
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

from quarry import __version__
from quarry.config import load_settings
from quarry.pipeline import run

SITES = [
    ("imagefap", "galleries, photo pages, searches, user profiles"),
    ("pornpics", "galleries, tags, channels, models, searches"),
    ("erome", "albums, users, searches (images + videos)"),
    ("rule34", "tag searches and single posts (rule34.xxx)"),
]
KNOWN_SITES = [site for site, _ in SITES] + ["generic"]


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="quarry",
        description="Download images, GIFs and videos from any page. "
        "The site is auto-detected - no per-site flags.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    p.add_argument("urls", nargs="*", metavar="URL",
                   help="page/gallery/search URL (any supported site, or any page at all)")
    p.add_argument("-f", "--file", metavar="FILE",
                   help="text file with one URL per line (# for comments)")
    p.add_argument("-o", "--out", metavar="DIR",
                   help="download folder (default: config.json, else Downloads\\Quarry)")
    p.add_argument("-m", "--max-images", type=int, metavar="N",
                   help="stop after N files per gallery/page")
    p.add_argument("-g", "--max-galleries", type=int, metavar="N",
                   help="stop after N galleries when crawling a search/listing page")
    p.add_argument("-w", "--workers", type=int, metavar="N",
                   help="parallel downloads (default: 8)")
    p.add_argument("-d", "--delay", type=float, metavar="SECONDS",
                   help="min delay between requests to the same host (default: 0.2)")
    p.add_argument("--proxy", metavar="URL",
                   help="HTTP/SOCKS5 proxy, e.g. http://127.0.0.1:7890")
    p.add_argument("--dry-run", action="store_true",
                   help="resolve all URLs, download nothing")
    p.add_argument("--force", action="store_true",
                   help="re-download files even if already downloaded")
    p.add_argument("--list-sites", action="store_true",
                   help="list supported sites and exit")
    p.add_argument("--open", action="store_true",
                   help="open the download folder after a successful run")
    p.add_argument("--quiet", action="store_true",
                   help="summary only (no per-URL/per-file lines)")
    p.add_argument("--site", metavar="NAME",
                   help=f"advanced: force an adapter ({', '.join(KNOWN_SITES)})")
    p.add_argument("--version", action="version",
                   version=f"Quarry {__version__}")
    return p


def load_urls(args: argparse.Namespace) -> list[str]:
    urls = [u.strip() for u in args.urls if u and u.strip()]
    if args.file:
        path = Path(args.file)
        if not path.is_file():
            raise SystemExit(f"error: file not found: {path}")
        for line in path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line and not line.startswith("#"):
                urls.append(line)
    # de-duplicate, keep order
    return list(dict.fromkeys(urls))


_QUIET_KEEP = (
    "  downloaded ", "  found ", "  dry run ok", "  no galleries",
    "  nothing to download", "  warning", "error:", "interrupted",
)


def make_logger(quiet: bool):
    """Console logger; quiet mode drops per-URL and per-file lines."""
    if not quiet:
        return print

    def log(msg: str) -> None:
        if msg.startswith(("[", "    ")):
            return
        if msg.startswith("  ") and not msg.startswith(_QUIET_KEEP):
            return
        print(msg)

    return log


def open_folder(path: Path) -> None:
    """Open a folder in the OS file manager (used by --open)."""
    try:
        if sys.platform == "win32":
            os.startfile(path)  # type: ignore[attr-defined]
        elif sys.platform == "darwin":
            subprocess.Popen(["open", str(path)])
        else:
            subprocess.Popen(["xdg-open", str(path)])
    except OSError:
        pass


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    if args.list_sites:
        print("Supported sites:")
        for site, what in SITES:
            print(f"  {site:<10} {what}")
        print(f"  {'generic':<10} any other page (grabs what the page shows)")
        return 0

    site = (args.site or "").lower().strip()
    if site and site not in KNOWN_SITES:
        print(
            f"error: unknown site '{args.site}' "
            f"(choose from: {', '.join(KNOWN_SITES)})",
            file=sys.stderr,
        )
        return 2

    try:
        urls = load_urls(args)
    except SystemExit as e:
        print(e, file=sys.stderr)
        return 2

    if not urls:
        build_parser().print_help()
        return 2

    settings = load_settings(
        out_dir=args.out,
        workers=args.workers,
        delay=args.delay,
        max_images=args.max_images,
        max_galleries=args.max_galleries,
        proxy=args.proxy,
        dry_run=args.dry_run,
    )
    settings.force = args.force
    settings.forced_site = site or None

    print(f"Quarry - {len(urls)} URL(s) -> {settings.out_dir}")
    code = run(urls, settings, on_log=make_logger(args.quiet))
    if args.open and code == 0 and not settings.dry_run:
        open_folder(settings.out_dir)
    return code


if __name__ == "__main__":
    sys.exit(main())
