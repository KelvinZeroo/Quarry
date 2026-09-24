from __future__ import annotations

import unittest

from quarry.adapters.erome import EromeAdapter

SAMPLE_EROME_ALBUM = """
<!DOCTYPE html>
<html>
<head><title>Hot Summer Day</title></head>
<body>
<h1>Hot Summer Day</h1>
<a href="/profile/summergirl" class="username">summergirl</a>
<div class="image-wrapper">
    <img data-src="https://s1.erome.com/123/img1.jpg" src="https://s1.erome.com/123/thumb_img1.jpg">
</div>
<div class="image-wrapper">
    <img data-src="https://s1.erome.com/123/img2.png" src="https://s1.erome.com/123/thumb_img2.png">
</div>
<div class="video-wrapper">
    <video>
        <source src="https://v1.erome.com/123/clip1.mp4" type="video/mp4">
    </video>
</div>
</body>
</html>
"""


class TestErome(unittest.TestCase):
    def setUp(self):
        self.adapter = EromeAdapter()

    def test_collect_album(self):
        fetch = lambda url, referer=None, strict=True: SAMPLE_EROME_ALBUM
        g = self.adapter.collect("https://www.erome.com/a/abc1234", fetch)
        self.assertEqual(g.site, "erome")
        self.assertEqual(g.gallery_id, "abc1234")
        self.assertEqual(g.title, "Hot Summer Day")
        urls = [i.url for i in g.items]
        self.assertEqual(len(urls), 3)
        # data-src (originals) win over thumbs
        self.assertIn("https://s1.erome.com/123/img1.jpg", urls)
        self.assertNotIn("https://s1.erome.com/123/thumb_img1.jpg", urls)
        self.assertIn("https://v1.erome.com/123/clip1.mp4", urls)
        # stable keys include album id
        self.assertTrue(all(i.key.startswith("erome/abc1234/") for i in g.items))

    def test_collect_rejects_non_album(self):
        with self.assertRaises(ValueError):
            self.adapter.collect("https://www.erome.com/", lambda *a, **k: None)


if __name__ == "__main__":
    unittest.main()
