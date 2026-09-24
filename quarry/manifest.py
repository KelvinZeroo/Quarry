from __future__ import annotations

import json
import threading
import time
from pathlib import Path


class Manifest:
    """Append-only JSONL manifest used to skip already-downloaded files.

    Dedup is by a stable per-item key (never by short-lived signed URLs).
    File paths are stored relative to base_dir so the output folder stays
    portable. Re-running is therefore free: completed files are skipped,
    failed ones are retried automatically.
    """

    def __init__(self, path: Path, base_dir: Path):
        self.path = path
        self.base_dir = base_dir
        self._lock = threading.Lock()
        self._records: dict[str, dict] = {}
        if path.exists():
            for line in path.read_text(encoding="utf-8").splitlines():
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if isinstance(rec, dict) and "key" in rec:
                    self._records[rec["key"]] = rec

    def record_for(self, key: str) -> dict | None:
        return self._records.get(key)

    def is_done(self, key: str) -> bool:
        """True when this key was fully downloaded and the file still checks out."""
        rec = self._records.get(key)
        if not rec or rec.get("status") != "ok" or not rec.get("file"):
            return False
        file = self.base_dir / rec["file"]
        if not file.exists():
            return False
        size = rec.get("bytes")
        if size is not None and file.stat().st_size != size:
            return False
        return True

    def record(
        self,
        key: str,
        file: Path,
        status: str,
        bytes_: int | None = None,
        sha256: str | None = None,
        url: str | None = None,
        error: str | None = None,
    ) -> None:
        try:
            rel = str(file.relative_to(self.base_dir))
        except ValueError:
            rel = str(file)
        rec = {
            "key": key,
            "file": rel,
            "status": status,
            "bytes": bytes_,
            "sha256": sha256,
            "url": url,
            "error": error,
            "ts": time.strftime("%Y-%m-%dT%H:%M:%S"),
        }
        with self._lock:
            self._records[key] = rec
            self.path.parent.mkdir(parents=True, exist_ok=True)
            with open(self.path, "a", encoding="utf-8") as fh:
                fh.write(json.dumps(rec, ensure_ascii=False) + "\n")

    def counts(self) -> dict[str, int]:
        out: dict[str, int] = {}
        for rec in self._records.values():
            out[rec.get("status", "?")] = out.get(rec.get("status", "?"), 0) + 1
        return out
