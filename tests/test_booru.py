from __future__ import annotations

import unittest

from quarry.adapters.booru import BooruAdapter


def list_page(*ids: str) -> str:
    links = "".join(
        f'<a href="index.php?page=post&amp;s=view&amp;id={i}">'
        f'<img src="https://rule34.xxx/thumbnails/1/t{i}.jpg?{i}"></a>'
        for i in ids
    )
    return (
        '<html><head><link rel="canonical" '
        'href="https://rule34.xxx/index.php?page=post&amp;s=list&amp;tags=1girl" />'
        f"</head><body><div class='posts'>{links}</div></body></html>"
    )


def view_page(media: str) -> str:
    return (
        "<html><head>"
        f'<meta property="og:image" itemprop="image" content="{media}" />'
        "</head><body></body></html>"
    )


class TestBooruAdapter(unittest.TestCase):
    def setUp(self):
        self.adapter = BooruAdapter()

    # ------------------------------------------------------------ matching

    def test_matches(self):
        self.assertTrue(
            BooruAdapter.matches("https://rule34.xxx/index.php?page=post&s=list&tags=x")
        )
        self.assertTrue(
            BooruAdapter.matches("https://www.rule34.xxx/index.php?page=post&s=list")
        )
        # gelbooru/danbooru go to generic mode (different markup)
        self.assertFalse(
            BooruAdapter.matches("https://gelbooru.com/index.php?page=post&s=list")
        )
        self.assertFalse(BooruAdapter.matches("https://danbooru.donmai.us/posts?tags=x"))
        self.assertFalse(BooruAdapter.matches("https://example.com/"))

    def test_looks_valid(self):
        self.assertTrue(self.adapter.looks_valid(list_page("1001")))
        self.assertTrue(
            self.adapter.looks_valid(
                view_page("https://wimg.rule34.xxx//images/a.jpg?1")
            )
        )
        self.assertTrue(self.adapter.looks_valid("[]"))  # JSON still accepted
        self.assertFalse(self.adapter.looks_valid(""))
        # CAPTCHA check wins even if list markers are present
        self.assertFalse(
            self.adapter.looks_valid(
                "<html><title>Rule34.xxx CAPTCHA</title>s=view</html>"
            )
        )
        self.assertFalse(self.adapter.looks_valid("<html>nothing here</html>"))

    # --------------------------------------------------------- single post

    def test_collect_single_post(self):
        calls = []

        def fetch(url, referer=None, strict=True):
            calls.append(url)
            self.assertIn("s=view", url)
            self.assertIn("id=1001", url)
            self.assertIn("page=post", url)
            return view_page("https://wimg.rule34.xxx//images/1327/hash.mp4?1001")

        g = self.adapter.collect(
            "https://rule34.xxx/index.php?page=post&s=view&id=1001", fetch
        )
        self.assertEqual(g.site, "rule34")
        self.assertEqual(g.gallery_id, "1001")
        self.assertEqual(g.title, "post-1001")
        self.assertEqual(len(g.items), 1)
        item = g.items[0]
        self.assertEqual(item.key, "post/1001")
        self.assertIn(".mp4?", item.url)
        self.assertEqual(item.filename_hint, "1001.mp4")
        self.assertIn("s=view", item.referer)
        self.assertEqual(len(calls), 1)

    def test_collect_video_page_preserves_page_param(self):
        def fetch(url, referer=None, strict=True):
            self.assertIn("page=video", url)
            return view_page("https://wimg.rule34.xxx//images/v.mp4?55")

        g = self.adapter.collect(
            "https://rule34.xxx/index.php?page=video&s=view&id=55", fetch
        )
        self.assertEqual(g.items[0].filename_hint, "55.mp4")

    def test_collect_single_post_without_media_raises(self):
        def fetch(url, referer=None, strict=True):
            return "<html>no media markers</html>"

        with self.assertRaises(RuntimeError):
            self.adapter.collect(
                "https://rule34.xxx/index.php?page=post&s=view&id=9", fetch
            )

    # ------------------------------------------------------------ tag crawl

    def test_collect_tag_walks_pages_and_resolves(self):
        views = {
            "1001": "https://wimg.rule34.xxx/images/a1001.jpg",
            "1002": "https://wimg.rule34.xxx/images/a1002.jpg",
            "1003": "https://wimg.rule34.xxx/images/a1003.jpg",
        }
        list_calls, view_calls = [], []

        def fetch(url, referer=None, strict=True):
            if "s=list" in url:
                list_calls.append(url)
                if "pid=0" in url:
                    return list_page("1001", "1002")
                if "pid=42" in url:  # offset stride: one page = 42 posts
                    return list_page("1002", "1003")  # 1002 dup, 1003 new
                return list_page()  # nothing new -> stop
            view_calls.append(url)
            pid = url.split("id=")[1]
            return view_page(views[pid])

        g = self.adapter.collect(
            "https://rule34.xxx/index.php?page=post&s=list&tags=blonde+hair", fetch
        )
        self.assertEqual(len(list_calls), 3)  # pid 0, 42, 84 (stops on 84)
        self.assertIn("pid=0", list_calls[0])
        self.assertIn("pid=42", list_calls[1])
        self.assertIn("pid=84", list_calls[2])
        self.assertEqual(len(view_calls), 3)
        self.assertEqual(
            [i.key for i in g.items],
            ["post/1001", "post/1002", "post/1003"],
        )
        self.assertEqual(g.items[0].filename_hint, "1001.jpg")
        self.assertTrue(g.items[1].url.endswith("a1002.jpg"))
        self.assertIn("tags=blonde%20hair", list_calls[0])
        self.assertIn("s=view", g.items[0].referer)
        self.assertEqual(g.title, "tag_blonde hair")
        self.assertEqual(g.folder, "tag_blonde-hair")
        self.assertEqual(g.gallery_id, "blonde-hair")

    def test_collect_tag_respects_limit(self):
        list_calls, view_calls = [], []

        def fetch(url, referer=None, strict=True):
            if "s=list" in url:
                list_calls.append(url)
                return list_page("1001", "1002", "1003")
            view_calls.append(url)
            return view_page("https://wimg.rule34.xxx/images/x.jpg")

        g = self.adapter.collect(
            "https://rule34.xxx/index.php?page=post&s=list&tags=1girl",
            fetch,
            limit=2,
        )
        # first list page already satisfies the limit -> no second page
        self.assertEqual(len(list_calls), 1)
        self.assertEqual(len(view_calls), 2)
        self.assertEqual(len(g.items), 2)
        self.assertEqual(g.items[0].key, "post/1001")
        self.assertEqual(g.items[1].key, "post/1002")

    def test_stride_discovered_from_pagination_hrefs(self):
        list_calls, view_calls = [], []

        def fetch(url, referer=None, strict=True):
            if "s=list" in url:
                list_calls.append(url)
                if "pid=0" in url:
                    return (
                        list_page("1001")
                        + '<a href="?page=post&amp;s=list&amp;tags=1girl'
                        '&amp;pid=25">next</a>'
                    )
                if "pid=25" in url:
                    return list_page("1002")
                return list_page()
            view_calls.append(url)
            return view_page("https://wimg.rule34.xxx/images/x.jpg")

        g = self.adapter.collect(
            "https://rule34.xxx/index.php?page=post&s=list&tags=1girl", fetch
        )
        # stride 25 taken from the page's own pagination link
        self.assertIn("pid=25", list_calls[1])
        self.assertEqual(len(g.items), 2)
        self.assertEqual(len(view_calls), 2)

    def test_collect_empty_tag_raises(self):
        def fetch(url, referer=None, strict=True):
            return list_page()  # valid page, zero posts

        with self.assertRaises(RuntimeError) as ctx:
            self.adapter.collect(
                "https://rule34.xxx/index.php?page=post&s=list&tags=nothingxx",
                fetch,
            )
        self.assertIn("no posts found", str(ctx.exception))

    def test_collect_resolve_failure_raises(self):
        def fetch(url, referer=None, strict=True):
            if "s=list" in url:
                return list_page("1001")
            return None  # every view page blocked

        with self.assertRaises(RuntimeError) as ctx:
            self.adapter.collect(
                "https://rule34.xxx/index.php?page=post&s=list&tags=1girl", fetch
            )
        self.assertIn("could not resolve", str(ctx.exception))

    # ------------------------------------------------------- media extraction

    def test_media_prefers_og_image(self):
        html = (
            '<meta property="og:image" content="https://wimg.rule34.xxx//'
            'images/real.jpg?7" />'
            '<img src="https://rule34.xxx/images/rule34_merch.jpg">'
            '<img src="https://rule34.xxx/static/icame.png">'
        )
        self.assertEqual(
            self.adapter._media_from_view(html),
            "https://wimg.rule34.xxx//images/real.jpg?7",
        )

    def test_media_falls_back_to_src_skipping_junk(self):
        html = (
            '<img src="https://rule34.xxx/images/rule34_merch.jpg">'
            '<video><source src="https://ws-cdn-video.rule34.xxx//'
            'images/v.mp4?9"></video>'
        )
        self.assertEqual(
            self.adapter._media_from_view(html),
            "https://ws-cdn-video.rule34.xxx//images/v.mp4?9",
        )

    def test_media_none_when_absent(self):
        self.assertIsNone(self.adapter._media_from_view("<html>nope</html>"))

    # ------------------------------------------------------------- url parse

    def test_parsing(self):
        self.assertEqual(
            self.adapter._tag_from_url(
                "https://rule34.xxx/index.php?page=post&s=list&tags=blonde+hair"
            ),
            "blonde hair",
        )
        self.assertEqual(
            self.adapter._tag_from_url("https://rule34.xxx/index.php?page=post&s=list"),
            "",
        )
        self.assertEqual(
            self.adapter._post_id_from_url(
                "https://rule34.xxx/index.php?page=post&s=view&id=42"
            ),
            "42",
        )
        self.assertIsNone(
            self.adapter._post_id_from_url(
                "https://rule34.xxx/index.php?page=post&s=list&tags=x"
            )
        )


if __name__ == "__main__":
    unittest.main()
