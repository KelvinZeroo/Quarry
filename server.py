#!/usr/bin/env python3
"""Local server for the Quarry browser extension.

Binds to 127.0.0.1 only. Endpoints:
    GET  /health           -> {ok, version, downloadDir}
    POST /download         -> start a download job      {jobId}
    GET  /status/<jobId>   -> job status (progress, folder, errors)
    GET  /status           -> all jobs
    POST /reveal           -> open a folder in the file manager

State-changing requests (POST) are rejected unless they come from no
Origin (curl, scripts) or an extension Origin (chrome-extension://,
moz-extension://) - random websites cannot drive this server (CSRF).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import threading
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse

from quarry import __version__
from quarry.config import default_out_dir, load_config, load_settings
from quarry.http_client import HttpClient
from quarry.pipeline import run

MAX_BODY = 5 * 1024 * 1024  # 5 MB (cookies + media URL lists)
ORIGIN_OK = re.compile(r"^(chrome-extension|moz-extension)://")
STATUS_OK = re.compile(r"^(downloaded|skipped|failed)$")

JOBS: dict[str, dict] = {}
JOBS_LOCK = threading.Lock()


# --------------------------------------------------------------------- jobs

def _new_job(url: str, dest_dir: str) -> dict:
    return {
        "id": uuid.uuid4().hex[:12],
        "url": url,
        "state": "queued",       # queued -> running -> done | error
        "title": "",
        "folder": dest_dir,      # final folder once known
        "total": 0,
        "downloaded": 0,
        "skipped": 0,
        "failed": 0,
        "error": "",
        "log": [],
    }


def _update(job: dict, **kw) -> None:
    with JOBS_LOCK:
        job.update(kw)


def _log(job: dict, msg: str) -> None:
    with JOBS_LOCK:
        job["log"].append(str(msg))
        del job["log"][:-25]  # keep the tail only


def _run_job(job: dict, payload: dict) -> None:
    url = job["url"]
    _update(job, state="running")
    try:
        settings = load_settings(
            out_dir=payload.get("destDir") or None,
            max_images=payload.get("maxImages") or None,
        )

        with HttpClient(settings) as client:
            cookies = payload.get("cookies") or []
            if isinstance(cookies, list) and cookies:
                client.import_cookies(
                    [c for c in cookies if isinstance(c, dict) and c.get("name")]
                )

            def on_log(msg: str) -> None:
                _log(job, msg)

            def on_gallery(title: str, total: int, folder: Path) -> None:
                _update(job, title=title, total=total, folder=str(folder))

            def on_status(status: str) -> None:
                if STATUS_OK.match(status):
                    with JOBS_LOCK:
                        job[status] += 1

            code = run(
                [url],
                settings,
                client=client,
                extra_media=payload.get("pageMedia") or [],
                on_log=on_log,
                on_gallery=on_gallery,
                on_status=on_status,
            )

        if code == 0:
            _update(job, state="done")
        else:
            err = next(
                (line for line in reversed(job.get("log", [])) if "error" in line),
                "download failed - see log",
            )
            _update(job, state="error", error=err)
    except Exception as e:  # noqa: BLE001 - report anything to the popup
        _update(job, state="error", error=str(e) or e.__class__.__name__)


# ------------------------------------------------------------------ handler

class Handler(BaseHTTPRequestHandler):
    server_version = f"Quarry/{__version__}"

    def log_message(self, fmt: str, *args) -> None:  # keep the console quiet
        pass

    # -------------------------------------------------------------- helpers

    def _origin_allowed(self) -> bool:
        origin = self.headers.get("Origin") or ""
        return origin == "" or bool(ORIGIN_OK.match(origin))

    def _send_json(self, obj, status: int = 200) -> None:
        body = json.dumps(obj).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self._send_cors()
        self.end_headers()
        self.wfile.write(body)

    def _send_cors(self) -> None:
        origin = self.headers.get("Origin") or ""
        if origin and ORIGIN_OK.match(origin):
            self.send_header("Access-Control-Allow-Origin", origin)
            self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
            self.send_header("Access-Control-Allow-Headers", "Content-Type")
            self.send_header("Vary", "Origin")

    def _read_json(self) -> dict | None:
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            return None
        if length <= 0 or length > MAX_BODY:
            return None
        try:
            data = json.loads(self.rfile.read(length).decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            return None
        return data if isinstance(data, dict) else None

    # --------------------------------------------------------------- routes

    def do_OPTIONS(self) -> None:  # CORS preflight from the extension
        if self._origin_allowed():
            self.send_response(204)
            self._send_cors()
            self.end_headers()
        else:
            self._send_json({"error": "forbidden origin"}, 403)

    def do_GET(self) -> None:
        path = urlparse(self.path).path

        if path == "/health":
            self._send_json({
                "ok": True,
                "version": __version__,
                "downloadDir": str(default_out_dir()),
            })
            return

        if path == "/status":
            with JOBS_LOCK:
                snapshot = [dict(j) for j in JOBS.values()]
            self._send_json({"jobs": snapshot})
            return

        m = re.match(r"^/status/([0-9a-f]{6,32})$", path)
        if m:
            with JOBS_LOCK:
                job = JOBS.get(m.group(1))
                snapshot = dict(job) if job else None
            if snapshot is None:
                self._send_json({"error": "unknown job"}, 404)
            else:
                self._send_json(snapshot)
            return

        self._send_json({"error": "not found"}, 404)

    def do_POST(self) -> None:
        if not self._origin_allowed():
            self._send_json({"error": "forbidden origin"}, 403)
            return

        path = urlparse(self.path).path
        payload = self._read_json()
        if payload is None:
            self._send_json({"error": "invalid JSON body"}, 400)
            return

        if path == "/download":
            url = str(payload.get("url") or "").strip()
            if not url.lower().startswith(("http://", "https://")):
                self._send_json({"error": "missing or invalid url"}, 400)
                return
            job = _new_job(url, str(payload.get("destDir") or default_out_dir()))
            with JOBS_LOCK:
                JOBS[job["id"]] = job
            threading.Thread(
                target=_run_job, args=(job, payload), daemon=True
            ).start()
            self._send_json({"jobId": job["id"]})
            return

        if path == "/reveal":
            target = Path(str(payload.get("path") or "")).expanduser()
            if not str(target) or not target.exists():
                self._send_json({"error": "path not found"}, 404)
                return
            try:
                if hasattr(os, "startfile"):        # Windows
                    os.startfile(str(target))       # type: ignore[attr-defined]
                elif sys.platform == "darwin":
                    subprocess.Popen(["open", str(target)])
                else:
                    subprocess.Popen(["xdg-open", str(target)])
            except OSError as e:
                self._send_json({"error": str(e)}, 500)
                return
            self._send_json({"ok": True})
            return

        self._send_json({"error": "not found"}, 404)


# -------------------------------------------------------------------- main

def main(argv: list[str] | None = None) -> int:
    cfg = load_config()
    parser = argparse.ArgumentParser(description="Quarry local server")
    parser.add_argument("--port", type=int,
                        default=int(cfg.get("port") or 8765),
                        help="port to listen on (default: config.json)")
    args = parser.parse_args(argv)

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    server.daemon_threads = True
    print(f"Quarry server v{__version__}")
    print(f"  listening on http://127.0.0.1:{args.port}  (localhost only)")
    print(f"  download folder: {default_out_dir(cfg)}")
    print("  Ctrl+C to stop")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nstopped.")
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
