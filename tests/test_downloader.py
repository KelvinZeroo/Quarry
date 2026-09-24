from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from quarry.config import Settings
from quarry.downloader import dest_for, run_downloads, sanitize
from quarry.http_client import FetchError
from quarry.manifest import Manifest
from quarry.models import ImageItem


class FakeClient:
    """Records download calls; serves bytes from a dict keyed by URL."""

    def __init__(self, payloads: dict[str, bytes] | None = None,
                 fail: set[str] | None = None):
        self.payloads = payloads or {}
        self.fail = fail or set()
        self.calls: list[str] = []

    def download(self, url: str, dest: Path, referer: str | None = None):
        self.calls.append(url)
        if url in self.fail:
            raise FetchError(url, "HTTP 403", 403)
        data = self.payloads.get(url, b"x" * 2048)
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(data)
        return len(data), "sha", dest


class TestSanitize(unittest.TestCase):
    def test_illegal_chars(self):
        self.assertEqual(sanitize('a<b>c:"d"/e\\f|g?h*i'), "a_b_c__d__e_f_g_h_i")

    def test_empty_falls_back(self):
        self.assertEqual(sanitize("   .  "), "image")

    def test_long_names_truncated(self):
        self.assertEqual(len(sanitize("x" * 500)), 140)

    def test_dest_for_is_numbered(self):
        item = ImageItem(key="k", url="http://x/a.jpg", filename_hint="a.jpg")
        p = dest_for(Path("/out"), 7, item)
        self.assertEqual(p.name, "0007_a.jpg")

    def test_dest_for_pads_to_four_digits(self):
        item = ImageItem(key="k", url="http://x/a.jpg", filename_hint="a.jpg")
        self.assertEqual(dest_for(Path("/out"), 1, item).name, "0001_a.jpg")

    def test_dest_for_truncates_name_to_16(self):
        item = ImageItem(
            key="k", url="http://x/a.jpg",
            filename_hint="a-very-long-gallery-filename.jpg",
        )
        p = dest_for(Path("/out"), 1, item)
        self.assertEqual(p.name, "0001_a-very-long-gall.jpg")
        # 4 (index) + 1 (underscore) + 16 (name) = 21
        self.assertEqual(len(p.stem), 21)

    def test_dest_for_hint_without_extension(self):
        item = ImageItem(key="k", url="http://x/a", filename_hint="18846429")
        self.assertEqual(dest_for(Path("/out"), 7, item).name, "0007_18846429")

    def test_dest_for_no_hint_is_bare_number(self):
        item = ImageItem(key="k", url="http://x/a.jpg")
        self.assertEqual(dest_for(Path("/out"), 7, item).name, "0007.jpg")


class TestRunDownloads(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.out = Path(self.tmp.name)
        self.settings = Settings(out_dir=self.out, workers=2)

    def tearDown(self):
        self.tmp.cleanup()

    def _items(self, n=3):
        return [
            ImageItem(key=f"k{i}", url=f"http://x/{i}.jpg", filename_hint=f"{i}.jpg")
            for i in range(n)
        ]

    def test_download_and_manifest(self):
        client = FakeClient()
        manifest = Manifest(self.out / ".manifests" / "t.jsonl", base_dir=self.out)
        statuses = []
        stats = run_downloads(client, self._items(3), self.out / "g",
                              self.settings, manifest,
                              on_status=statuses.append)
        self.assertEqual(stats["downloaded"], 3)
        self.assertEqual(stats["failed"], 0)
        self.assertEqual(len(statuses), 3)
        self.assertTrue(all((self.out / "g").glob("*.jpg")))
        # manifest now marks them done
        for i in range(3):
            self.assertTrue(manifest.is_done(f"k{i}"))

        # second run: everything skipped, no network
        client2 = FakeClient()
        stats2 = run_downloads(client2, self._items(3), self.out / "g",
                               self.settings, manifest)
        self.assertEqual(stats2["skipped"], 3)
        self.assertEqual(client2.calls, [])

    def test_failures_recorded_and_refresher_used(self):
        client = FakeClient(fail={"http://x/1.jpg"})
        manifest = Manifest(self.out / ".manifests" / "t.jsonl", base_dir=self.out)
        refreshed = []

        def refresher(item):
            refreshed.append(item.key)
            return None  # no fresh URL available -> stays failed

        items = self._items(2)
        stats = run_downloads(client, items, self.out / "g", self.settings,
                              manifest, refresher=refresher)
        self.assertEqual(stats["downloaded"], 1)
        self.assertEqual(stats["failed"], 1)
        self.assertEqual(refreshed, ["k1"])
        self.assertFalse(manifest.is_done("k1"))

    def test_refresher_retry_succeeds(self):
        class OneShot(FakeClient):
            def download(inner_self, url, dest, referer=None):
                if url == "http://x/bad.jpg":
                    raise FetchError(url, "HTTP 403", 403)
                return super().download(url, dest, referer)

        client = OneShot()
        manifest = Manifest(self.out / ".manifests" / "t.jsonl", base_dir=self.out)
        item = ImageItem(key="k", url="http://x/bad.jpg", filename_hint="a.jpg")

        def refresher(_item):
            return "http://x/fresh.jpg"

        stats = run_downloads(client, [item], self.out / "g", self.settings,
                              manifest, refresher=refresher)
        self.assertEqual(stats["downloaded"], 1)
        self.assertIn("http://x/fresh.jpg", client.calls)

    def test_force_redownloads(self):
        client = FakeClient()
        manifest = Manifest(self.out / ".manifests" / "t.jsonl", base_dir=self.out)
        run_downloads(client, self._items(1), self.out / "g", self.settings, manifest)
        self.settings.force = True
        client2 = FakeClient()
        stats = run_downloads(client2, self._items(1), self.out / "g",
                              self.settings, manifest)
        self.assertEqual(stats["downloaded"], 1)
        self.assertEqual(len(client2.calls), 1)


if __name__ == "__main__":
    unittest.main()
