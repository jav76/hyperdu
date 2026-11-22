#!/usr/bin/env python3
"""Unit tests for render-package-template.py."""

import importlib.util
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parent / "render-package-template.py"
spec = importlib.util.spec_from_file_location("render_package_template", SCRIPT)
rpt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rpt)

SUMS = {"hyperdu-linux-x64.tar.gz": "ab" * 32}


class RenderTests(unittest.TestCase):
    def test_fills_version_and_hash(self):
        out = rpt.render('v@VERSION@ "@SHA256:hyperdu-linux-x64.tar.gz@"', "2.0.0", SUMS)
        self.assertEqual(out, f'v2.0.0 "{"ab" * 32}"')

    def test_missing_asset_fails(self):
        with self.assertRaises(KeyError):
            rpt.render("@SHA256:hyperdu-win-arm64.zip@", "2.0.0", SUMS)

    def test_unknown_placeholder_fails(self):
        with self.assertRaises(ValueError):
            rpt.render("@VERSION@ @RELEASE_DATE@", "2.0.0", SUMS)

    def test_load_sums_handles_binary_marker_and_case(self):
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write("ABCD *hyperdu-osx-arm64.tar.gz\nbad line here x\n")
        self.assertEqual(rpt.load_sums(f.name), {"hyperdu-osx-arm64.tar.gz": "abcd"})

    def test_repo_templates_render_against_full_asset_set(self):
        sums = {name: "0" * 64 for name in (
            "hyperdu-linux-x64.tar.gz", "hyperdu-linux-arm64.tar.gz",
            "hyperdu-osx-x64.tar.gz", "hyperdu-osx-arm64.tar.gz")}
        packaging = SCRIPT.parents[2] / "packaging"
        for template in (packaging / "homebrew/hyperdu.rb.tmpl", packaging / "aur/PKGBUILD.tmpl"):
            with self.subTest(template=template.name):
                out = rpt.render(template.read_text(), "9.9.9", sums)
                self.assertIn("9.9.9", out)


if __name__ == "__main__":
    unittest.main()
