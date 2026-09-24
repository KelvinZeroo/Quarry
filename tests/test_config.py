from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from quarry.config import Settings, load_settings


class TestSettings(unittest.TestCase):
    def test_explicit_out_dir_wins(self):
        with patch("quarry.config.load_config", return_value={"downloadDir": "/from/cfg"}):
            s = load_settings(out_dir="/explicit")
        self.assertEqual(Path("/explicit"), Path(s.out_dir))

    def test_config_out_dir_used_when_no_flag(self):
        with patch("quarry.config.load_config",
                   return_value={"downloadDir": str(Path(tempfile.gettempdir()) / "cfgdl")}):
            s = load_settings()
        self.assertTrue(str(s.out_dir).endswith("cfgdl"))

    def test_default_out_dir_when_config_empty(self):
        with patch("quarry.config.load_config", return_value={"downloadDir": ""}):
            s = load_settings()
        self.assertEqual(s.out_dir, Path.home() / "Downloads" / "Quarry")

    def test_config_defaults_for_workers_delay(self):
        with patch("quarry.config.load_config",
                   return_value={"workers": 4, "delay": 0.7}):
            s = load_settings()
        self.assertEqual(s.workers, 4)
        self.assertAlmostEqual(s.delay, 0.7)

    def test_explicit_overrides_config(self):
        with patch("quarry.config.load_config",
                   return_value={"workers": 4, "delay": 0.7}):
            s = load_settings(workers=16, delay=0.0)
        self.assertEqual(s.workers, 16)
        self.assertAlmostEqual(s.delay, 0.0)

    def test_broken_config_values_fall_back(self):
        with patch("quarry.config.load_config",
                   return_value={"workers": "lots", "delay": "soon"}):
            s = load_settings()
        self.assertEqual(s.workers, 8)
        self.assertAlmostEqual(s.delay, 0.2)

    def test_manifest_path_layout(self):
        s = Settings(out_dir=Path("/dl"))
        p = s.manifest_path("imagefap", "14340248")
        self.assertEqual(p, Path("/dl/.manifests/imagefap/14340248.jsonl"))


if __name__ == "__main__":
    unittest.main()
