from __future__ import annotations

import unittest

from quarry.adapters import (
    BooruAdapter,
    EromeAdapter,
    ImageFapAdapter,
    NoAdapterError,
    PornPicsAdapter,
    get_adapter,
)


class TestRegistry(unittest.TestCase):
    def test_get_adapter(self):
        self.assertIsInstance(
            get_adapter("https://www.pornpics.com/galleries/test-123/"),
            PornPicsAdapter,
        )
        self.assertIsInstance(
            get_adapter("https://www.imagefap.com/gallery.php?gid=14340248"),
            ImageFapAdapter,
        )
        self.assertIsInstance(
            get_adapter("https://www.erome.com/a/abc1234"), EromeAdapter
        )
        self.assertIsInstance(
            get_adapter("https://rule34.xxx/index.php?page=post&s=list&tags=x"),
            BooruAdapter,
        )

    def test_no_adapter_raises(self):
        with self.assertRaises(NoAdapterError):
            get_adapter("https://example.com/some/page")

    def test_site_hint(self):
        self.assertIsInstance(get_adapter("https://x.test/", site_hint="erome"),
                              EromeAdapter)

    def test_matches_respects_host(self):
        self.assertFalse(ImageFapAdapter.matches("https://www.imagefap.com.evil.test/"))
        self.assertFalse(PornPicsAdapter.matches("https://notpornpics.com/"))


class TestListingDetection(unittest.TestCase):
    def test_pornpics(self):
        a = PornPicsAdapter()
        self.assertFalse(a.is_listing("https://www.pornpics.com/galleries/cool-blonde-12345/"))
        self.assertTrue(a.is_listing("https://www.pornpics.com/tags/cosplay/"))
        self.assertTrue(a.is_listing("https://www.pornpics.com/?q=blonde"))

    def test_imagefap(self):
        a = ImageFapAdapter()
        self.assertFalse(a.is_listing("https://www.imagefap.com/gallery.php?gid=14340248"))
        self.assertFalse(a.is_listing("https://www.imagefap.com/photo/546949874/"))
        self.assertTrue(a.is_listing("https://www.imagefap.com/profile/foo"))
        self.assertTrue(a.is_listing("https://www.imagefap.com/search/hentai"))

    def test_erome(self):
        a = EromeAdapter()
        self.assertFalse(a.is_listing("https://www.erome.com/a/abc1234"))
        self.assertTrue(a.is_listing("https://www.erome.com/search?q=cats"))

    def test_booru_is_not_listing(self):
        # A booru tag page IS the gallery - one tag, all posts.
        a = BooruAdapter()
        self.assertFalse(a.is_listing("https://rule34.xxx/index.php?page=post&s=list&tags=x"))


if __name__ == "__main__":
    unittest.main()
