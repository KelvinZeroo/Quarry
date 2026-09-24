from __future__ import annotations

import unittest

from quarry.adapters.imagefap import ImageFapAdapter

SAMPLE_PHOTO_PAGE = """
<html>
<body>
<div id="cnt_cats">
    <td>Categories:</td>
    <td><a href="/category/anime">Anime / Cartoon</a></td>
</div>
<div><h1>Mixed Hentai 2</h1></div>
<div id="_navi_cavi" data-total="3">
    <input type="image" original="https://cdnc.imagefap.com/images/full/119/546/546949874.jpg?secure=tokenA,1790000000" imageid="546949874">
    <a original="https://cdnc.imagefap.com/images/full/119/449/449563388.jpg?secure=tokenB,1790000000" imageid="449563388"></a>
    <input imageid="99887766" original="https://cdnc.imagefap.com/images/full/1/2/3/99887766.gif?secure=tokenC,1790000000">
</div>
</body>
</html>
"""

SAMPLE_GALLERY_PAGE = """
<html>
<head><title>Mixed Hentai 2 Porn Pics &amp; Porn Gals at ImageFap.com</title></head>
<body>
<a title="View Mixed Hentai 2" class="gal_title" href="/gallery.php?gid=14340248">Mixed Hentai 2</a>
<a href="/photo/546949874/"><img src="thumb1.jpg"></a>
<a href="/photo/449563388/"><img src="thumb2.jpg"></a>
<a href="/photo/99887766/"><img src="thumb3.jpg"></a>
</body>
</html>
"""


def make_fetch(pages: dict[str, str]):
    def fetch(url, referer=None, strict=True):
        base = url.split("?")[0].rstrip("/") + "/"
        for key, html in pages.items():
            if base == key.rstrip("/") + "/" or url == key:
                return html
        if strict:
            raise RuntimeError(f"unexpected fetch: {url}")
        return None
    return fetch


class TestImageFapParse(unittest.TestCase):
    def setUp(self):
        self.adapter = ImageFapAdapter()

    def test_parse_photo_page_both_attr_orders(self):
        entries, total = self.adapter._parse_photo_page(SAMPLE_PHOTO_PAGE)
        self.assertEqual(total, 3)
        self.assertEqual(len(entries), 3)
        # all three attribute orders resolved
        self.assertEqual(entries[0][0], "546949874")
        self.assertEqual(entries[1][0], "449563388")
        self.assertEqual(entries[2][0], "99887766")
        self.assertIn(".gif", entries[2][1])

    def test_parse_gallery_page(self):
        title, ids = self.adapter._parse_gallery_page(SAMPLE_GALLERY_PAGE)
        self.assertEqual(title, "Mixed Hentai 2")
        self.assertEqual(ids, ["546949874", "449563388", "99887766"])

    def test_collect_gallery(self):
        fetch = make_fetch({
            "https://www.imagefap.com/gallery.php": SAMPLE_GALLERY_PAGE,
            "https://www.imagefap.com/photo/546949874/": SAMPLE_PHOTO_PAGE,
        })
        g = self.adapter.collect("https://www.imagefap.com/gallery.php?gid=14340248", fetch)
        self.assertEqual(g.site, "imagefap")
        self.assertEqual(g.gallery_id, "14340248")
        self.assertEqual(g.title, "Mixed Hentai 2")
        self.assertEqual(len(g.items), 3)
        # stable keys ignore the signed URL
        self.assertEqual(g.items[0].key, "photo/546949874")
        self.assertIn("secure=tokenA", g.items[0].url)

    def test_collect_rejects_foreign_url(self):
        with self.assertRaises(ValueError):
            self.adapter.collect("https://www.imagefap.com/", lambda *a, **k: None)

    def test_refresh_item_returns_same_pid(self):
        fetch = make_fetch({
            "https://www.imagefap.com/photo/449563388/": SAMPLE_PHOTO_PAGE,
        })
        item = type("I", (), {
            "key": "photo/449563388",
            "referer": "https://www.imagefap.com/photo/546949874/?gid=14340248",
        })()
        fresh = self.adapter.refresh_item(item, fetch)
        self.assertIsNotNone(fresh)
        self.assertIn("449563388", fresh)
        self.assertIn("secure=tokenB", fresh)


if __name__ == "__main__":
    unittest.main()
