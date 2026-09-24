from __future__ import annotations

import unittest

from quarry.adapters.pornpics import PornPicsAdapter

SAMPLE_GALLERY_HTML = """
<html>
<head><title>Cool Blonde - PornPics.com</title>
<meta name="description" content="Watch 3 pics of cool blonde here.">
</head>
<body>
<div id="tiles">
    <a class="rel-link" href="https://cdni.pornpics.com/460/1/12345/001.jpg" data-tid="t1">
        <img src="https://cdni.pornpics.com/thumb/001.jpg">
    </a>
    <a class="rel-link" href="https://cdni.pornpics.com/1280/1/12345/002.jpg" data-tid="t2">
        <img src="https://cdni.pornpics.com/thumb/002.jpg">
    </a>
    <a class="rel-link" href="https://cdn.pornpics.com/go/promo-click">OnlyFans</a>
    <a class="rel-link" href="https://cdni.pornpics.com/460/1/12345/003.jpg" data-tid="t1">
        <img src="https://cdni.pornpics.com/thumb/003.jpg">
    </a>
</div>
</body>
</html>
"""

SAMPLE_PAGE2_HTML = """
<html><body><div id="tiles">
    <a class="rel-link" href="https://cdni.pornpics.com/460/1/12345/004.jpg" data-tid="t4"></a>
</div></body></html>
"""

SAMPLE_LISTING_HTML = """
<html><body>
<li class="thumbw"><a class="rel-link" href="/galleries/cool-blonde-12345/" title="Cool Blonde">
    <img src="https://cdni.pornpics.com/thumb/12345.jpg"></a></li>
<li class="thumbw"><a class="rel-link" href="/galleries/beach-fun-67890/" title="Beach Fun">
    <img src="https://cdni.pornpics.com/thumb/67890.jpg"></a></li>
<li class="thumbw"><a class="rel-link" href="/galleries/cool-blonde-12345/" title="dup"></a></li>
</body></html>
"""


class TestPornPics(unittest.TestCase):
    def setUp(self):
        self.adapter = PornPicsAdapter()

    def test_collect_upgrades_thumb_and_filters_promos(self):
        def fetch(url, referer=None, strict=True):
            if "?page=2" in url:
                return None  # total=3 satisfied by page 2 probe result below
            return SAMPLE_GALLERY_HTML

        g = self.adapter.collect("https://www.pornpics.com/galleries/cool-blonde-12345/", fetch)
        self.assertEqual(g.site, "pornpics")
        self.assertEqual(g.gallery_id, "12345")
        self.assertEqual(g.title, "Cool Blonde")
        urls = [i.url for i in g.items]
        self.assertEqual(len(urls), 3)
        # /460/ thumbnails upgraded to /1280/ originals
        self.assertTrue(all("/1280/" in u for u in urls))
        self.assertTrue(all("cdni.pornpics.com" in u for u in urls))
        # promo /go/ link excluded
        self.assertFalse(any("/go/" in u for u in urls))

    def test_crawl_listing_dedupes(self):
        def fetch(url, referer=None, strict=True):
            return SAMPLE_LISTING_HTML

        urls = self.adapter.crawl_galleries(
            "https://www.pornpics.com/tags/cosplay/", fetch, max_galleries=10
        )
        self.assertEqual(len(urls), 2)  # duplicate gid collapsed
        self.assertTrue(all(u.endswith("/") for u in urls))
        self.assertIn("12345", urls[0])


if __name__ == "__main__":
    unittest.main()
