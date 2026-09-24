from __future__ import annotations

import tempfile
import time
import unittest
from pathlib import Path

from quarry.config import Settings
from quarry.http_client import FetchError, HttpClient


class TestFetchError(unittest.TestCase):
    def test_message_and_status(self):
        e = FetchError("http://x/", "blocked (HTTP 403)", 403)
        self.assertEqual(e.status, 403)
        self.assertIn("http://x/", str(e))


class TestThrottle(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.tmp.cleanup()

    def _client(self, delay: float = 0.0) -> HttpClient:
        return HttpClient(Settings(out_dir=Path(self.tmp.name), delay=delay))

    def test_per_host_locks_are_distinct(self):
        client = self._client()
        try:
            client._throttle("http://a.example/x")
            client._throttle("http://b.example/x")
            self.assertEqual(set(client._locks), {"a.example", "b.example"})
        finally:
            client.close()

    def test_delay_zero_is_instant(self):
        client = self._client()
        try:
            start = time.monotonic()
            for _ in range(5):
                client._throttle("http://same.example/x")
            self.assertLess(time.monotonic() - start, 1.0)
        finally:
            client.close()


if __name__ == "__main__":
    unittest.main()
