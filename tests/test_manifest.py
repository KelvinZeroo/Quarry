from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from quarry.manifest import Manifest


class TestManifest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.base = Path(self.tmp.name) / "output"

    def tearDown(self):
        self.tmp.cleanup()

    def test_record_and_skip(self):
        m = Manifest(self.base / ".manifests" / "x.jsonl", base_dir=self.base)

        f = self.base / "site" / "gal" / "001_a.jpg"
        f.parent.mkdir(parents=True)
        f.write_bytes(b"12345")

        self.assertFalse(m.is_done("k1"))
        m.record("k1", f, "ok", bytes_=5, sha256="abc", url="http://x")
        self.assertTrue(m.is_done("k1"))

        # persists across reload
        m2 = Manifest(m.path, base_dir=self.base)
        self.assertTrue(m2.is_done("k1"))
        rec = m2.record_for("k1")
        self.assertEqual(rec["status"], "ok")
        self.assertEqual(rec["bytes"], 5)

    def test_not_done_when_file_missing_or_size_mismatch(self):
        m = Manifest(self.base / "m.jsonl", base_dir=self.base)

        f = self.base / "a.jpg"
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_bytes(b"12345")
        m.record("k", f, "ok", bytes_=5)
        self.assertTrue(m.is_done("k"))

        # size mismatch -> re-download
        f.write_bytes(b"12")
        self.assertFalse(m.is_done("k"))

        # failed records never count as done
        f.write_bytes(b"12345")
        m.record("k2", f, "failed", error="HTTP 403")
        self.assertFalse(m.is_done("k2"))

        # missing file -> re-download
        m.record("k3", self.base / "gone.jpg", "ok", bytes_=5)
        self.assertFalse(m.is_done("k3"))

    def test_last_record_wins(self):
        m = Manifest(self.base / "m.jsonl", base_dir=self.base)
        f = self.base / "a.jpg"
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_bytes(b"xx")

        m.record("k", f, "failed", error="boom")
        m.record("k", f, "ok", bytes_=2)
        self.assertTrue(m.is_done("k"))

        lines = (self.base / "m.jsonl").read_text().strip().splitlines()
        self.assertEqual(len(lines), 2)


if __name__ == "__main__":
    unittest.main()
