"""Verify the embedded optional GTK rasterizer without a display server."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import gi
gi.require_version("GdkPixbuf", "2.0")
from gi.repository import GdkPixbuf

script = Path(__file__).resolve().parent.parent / "agent/src/theme_icon.py"
with tempfile.TemporaryDirectory(prefix="vmnotify-theme-test-") as folder:
    icon = Path(folder) / "vmnotify-test-icon.png"
    pixbuf = GdkPixbuf.Pixbuf.new(GdkPixbuf.Colorspace.RGB, True, 8, 2, 2)
    pixbuf.fill(0xFF000080)
    pixbuf.savev(str(icon), "png", [], [])
    env = dict(os.environ, DISPLAY="")
    def run(name, directory=""):
        return subprocess.run([sys.executable, str(script), name, directory], env=env, capture_output=True, text=True, timeout=5)
    for name, directory in [(str(icon), ""), ("vmnotify-test-icon", folder)]:
        result = run(name, directory)
        assert result.returncode == 0, result.stderr
        image = json.loads(result.stdout)
        assert 0 < image["width"] <= 32 and 0 < image["height"] <= 32, image
        assert image["argb_hex"] == "80ff0000" * (image["width"] * image["height"]), image
    assert run("../vmnotify-test-icon").returncode != 0
    assert run("vmnotify-no-such-icon").returncode != 0
    oversized = Path(folder) / "oversized.png"
    with oversized.open("wb") as output:
        output.truncate(4 * 1024 * 1024 + 1)
    assert run(str(oversized)).returncode != 0
print("PASS: headless theme/custom-path and absolute-file icons, alpha preservation, missing/oversized input fallback")
