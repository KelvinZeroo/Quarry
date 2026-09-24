from __future__ import annotations

import unittest

from quarry import generic

SAMPLE_PAGE = """
<html>
<head>
  <title>  My Photo   Page </title>
  <meta property="og:image" content="https://cdn.example.com/og-cover.jpg">
</head>
<body>
  <h1>Ignored because title tag wins</h1>
  <img src="/images/a.jpg" alt="a">
  <img data-src="https://cdn.example.com/lazy-b.png">
  <img src="/static/logo.svg">
  <img src="/ui/avatar-42.jpg">
  <img src="/assets/site-logo.png">
  <img srcset="/img/s.jpg 480w, /img/m.jpg 800w, /img/l.jpg 1280w">
  <a href="/files/clip.mp4">watch</a>
  <a href="/about.html">about</a>
  <a href="/gallery.zip">not media</a>
  <video><source src="/v/hero.webm" type="video/webm"></video>
</body>
</html>
"""


class TestGeneric(unittest.TestCase):
    def _fetch(self, html=SAMPLE_PAGE, strict=True):
        # generic.collect calls fetch(url, strict=False) - keep the name.
        return lambda url, referer=None, strict=strict: html

    def test_collect_finds_media_and_skips_junk(self):
        g = generic.collect("https://example.com/posts/1", self._fetch())
        urls = [i.url for i in g.items]

        self.assertIn("https://example.com/images/a.jpg", urls)
        self.assertIn("https://cdn.example.com/lazy-b.png", urls)
        self.assertIn("https://cdn.example.com/og-cover.jpg", urls)
        # largest srcset candidate wins
        self.assertIn("https://example.com/img/l.jpg", urls)
        self.assertNotIn("https://example.com/img/m.jpg", urls)
        # videos too
        self.assertIn("https://example.com/v/hero.webm", urls)
        self.assertIn("https://example.com/files/clip.mp4", urls)

        # junk filtered: svg, logo, avatar, non-media links
        self.assertFalse(any(u.endswith(".svg") for u in urls))
        self.assertFalse(any("logo" in u for u in urls))
        self.assertFalse(any("avatar" in u for u in urls))
        self.assertNotIn("https://example.com/about.html", urls)
        self.assertNotIn("https://example.com/gallery.zip", urls)

        # dedup: og:image already added? it's unique here -> count check
        self.assertEqual(len(urls), len(set(urls)))

    def test_title_and_site(self):
        g = generic.collect("https://www.example.com/posts/1", self._fetch())
        self.assertEqual(g.title, "My Photo Page")
        self.assertEqual(g.site, "example.com")   # www stripped
        self.assertEqual(g.folder, "my-photo-page")

    def test_extra_urls_merge_and_dedupe(self):
        g = generic.collect(
            "https://example.com/posts/1",
            self._fetch(),
            extra_urls=[
                "https://example.com/images/a.jpg",          # dup of HTML
                "https://cdn.example.com/from-dom.gif",      # new
                "https://cdn.example.com/avatar-9.jpg",      # junk
            ],
        )
        urls = [i.url for i in g.items]
        self.assertEqual(urls.count("https://example.com/images/a.jpg"), 1)
        self.assertIn("https://cdn.example.com/from-dom.gif", urls)
        self.assertFalse(any("avatar" in u for u in urls))

    def test_no_html_uses_extra_urls(self):
        g = generic.collect(
            "https://example.com/js-app",
            self._fetch(html=None),
            extra_urls=["https://cdn.example.com/only-dom.jpg"],
        )
        self.assertEqual(len(g.items), 1)
        self.assertEqual(g.site, "example.com")

    def test_nothing_found_raises(self):
        with self.assertRaises(ValueError):
            generic.collect("https://example.com/empty", self._fetch(html="<html></html>"))

    def test_items_are_downloadable_records(self):
        g = generic.collect("https://example.com/posts/1", self._fetch())
        for item in g.items:
            self.assertTrue(item.key.startswith("https://"))
            self.assertEqual(item.referer, "https://example.com/posts/1")


if __name__ == "__main__":
    unittest.main()
