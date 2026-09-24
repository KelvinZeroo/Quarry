from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from quarry.adapters import NoAdapterError, get_adapter
from quarry.config import Settings
from quarry.http_client import FetchError
from quarry.pipeline import folder_name, make_fetcher, run, write_info


class FakeHtmlClient:
    def __init__(self, html: str | None):
        self.html = html

    def get_html(self, url, referer=None, strict=True):
        return self.html


class _TmpOut(unittest.TestCase):
    """Base: settings pointed at a throwaway folder."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.settings = Settings(
            out_dir=Path(self.tmp.name), delay=0.0, workers=1
        )
        self.logs: list[str] = []

    def tearDown(self):
        self.tmp.cleanup()


class TestMakeFetcher(_TmpOut):
    def test_valid_html_returned(self):
        adapter = get_adapter("https://www.erome.com/a/x")
        fetch = make_fetcher(
            adapter, self.settings, FakeHtmlClient('<html>class="image-wrapper"</html>')
        )
        self.assertIn("image-wrapper", fetch("https://www.erome.com/a/x"))

    def test_invalid_html_raises_when_strict(self):
        adapter = get_adapter("https://www.erome.com/a/x")
        fetch = make_fetcher(
            adapter, self.settings, FakeHtmlClient("<html>captcha page</html>")
        )
        with self.assertRaises(FetchError):
            fetch("https://www.erome.com/a/x")
        # non-strict returns None instead
        self.assertIsNone(fetch("https://www.erome.com/a/x", strict=False))

    def test_adapter_none_accepts_any_html(self):
        fetch = make_fetcher(
            None, self.settings, FakeHtmlClient("<html>whatever</html>")
        )
        self.assertIn("whatever", fetch("https://example.com/page"))


class TestRunValidation(_TmpOut):
    def test_rejects_non_http_urls(self):
        code = run(["not-a-url"], self.settings, on_log=self.logs.append)
        self.assertEqual(code, 2)
        self.assertTrue(any("not a URL" in line for line in self.logs))

    def test_unknown_site_falls_back_to_generic_and_errors_cleanly(self):
        # .invalid can never resolve -> generic path must fail without raising
        code = run(
            ["https://quarry-nonexistent-test.invalid/page"],
            self.settings,
            on_log=self.logs.append,
        )
        self.assertEqual(code, 2)
        self.assertTrue(any("[generic]" in line for line in self.logs))
        self.assertTrue(any("error:" in line for line in self.logs))

    def test_no_adapter_error_type(self):
        self.assertTrue(issubclass(NoAdapterError, ValueError))


class TestFolderName(unittest.TestCase):
    def test_short_names_unchanged(self):
        self.assertEqual(folder_name("tag_blonde-hair", "blonde-hair"), "tag_blonde-hair")

    def test_long_names_capped_at_16(self):
        raw = "redhead-beauty-ani-blackfox-having-her-shaved-pussy-21319853"
        name = folder_name(raw, "21319853")
        self.assertEqual(len(name), 16)
        # first 11 chars + "-" + 4-char hash of the gallery id
        self.assertEqual(name[:12], "redhead-bea-")
        self.assertRegex(name[12:], r"^[0-9a-f]{4}$")

    def test_hash_tail_is_deterministic_and_id_based(self):
        raw = "x" * 40
        self.assertEqual(folder_name(raw, "gid-1"), folder_name(raw, "gid-1"))
        self.assertNotEqual(folder_name(raw, "gid-1"), folder_name(raw, "gid-2"))

    def test_illegal_chars_removed(self):
        name = folder_name('ga<ll>ery:"ti"tle', "g")
        for ch in '<>:"/\\|?*':
            self.assertNotIn(ch, name)

    def test_empty_falls_back(self):
        self.assertEqual(folder_name("   ", "g1"), "gallery")


class TestWriteInfo(_TmpOut):
    def _make_dir(self) -> Path:
        d = self.settings.out_dir / "pornpics" / "gal"
        d.mkdir(parents=True)
        (d / "0001_a.jpg").write_bytes(b"x")
        (d / "0002_b.jpg").write_bytes(b"x")
        return d

    def test_info_file_written(self):
        d = self._make_dir()
        write_info(
            d, site="pornpics", title="My Gallery",
            source="https://www.pornpics.com/galleries/x-1/",
            stats={"downloaded": 2, "skipped": 0, "failed": 0},
        )
        text = (d / "info.txt").read_text(encoding="utf-8")
        self.assertIn("pornpics", text)
        self.assertIn("My Gallery", text)
        self.assertIn("https://www.pornpics.com/galleries/x-1/", text)
        self.assertIn("2 downloaded, 0 skipped, 0 failed", text)
        self.assertIn("0001_a.jpg", text)
        self.assertIn("0002_b.jpg", text)
        self.assertLess(text.index("0001_a.jpg"), text.index("0002_b.jpg"))

    def test_info_file_excludes_itself_on_rewrite(self):
        d = self._make_dir()
        stats = {"downloaded": 2, "skipped": 0, "failed": 0}
        kw = dict(site="pornpics", title="T", source="https://x.test/", stats=stats)
        write_info(d, **kw)
        write_info(d, **kw)
        text = (d / "info.txt").read_text(encoding="utf-8")
        self.assertEqual(text.count("info.txt"), 0)

    def test_missing_dir_does_not_raise(self):
        write_info(
            self.settings.out_dir / "nope", site="s", title="t",
            source="https://x.test/", stats={"downloaded": 0, "skipped": 0, "failed": 0},
        )


if __name__ == "__main__":
    unittest.main()
