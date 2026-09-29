"""Optional desktop-theme rasterizer. Arguments are data, never shell commands."""
import json
import os
import stat
import sys

import gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, GdkPixbuf, Gio

name, theme_path = sys.argv[1:3]
if not name or len(name) > 1024:
    sys.exit(1)

def load(path):
    info = os.stat(path)
    if not stat.S_ISREG(info.st_mode) or info.st_size > 4 * 1024 * 1024:
        raise ValueError("icon is not a bounded regular file")
    metadata = GdkPixbuf.Pixbuf.get_file_info(path)
    if not metadata or metadata[1] <= 0 or metadata[2] <= 0 or metadata[1] > 1024 or metadata[2] > 1024:
        raise ValueError("icon dimensions are invalid or too large")
    image = GdkPixbuf.Pixbuf.new_from_file(path)
    width, height = image.get_width(), image.get_height()
    if max(width, height) > 32:
        scale = max(width, height)
        image = image.scale_simple(max(1, width * 32 // scale), max(1, height * 32 // scale), GdkPixbuf.InterpType.NEAREST)
    return image

pixbuf = None
if os.path.isabs(name):
    pixbuf = load(name)
else:
    if "/" in name or "\\" in name or name in (".", ".."):
        sys.exit(1)
    theme = Gtk.IconTheme.new()
    if os.path.isabs(theme_path) and os.path.isdir(theme_path):
        theme.prepend_search_path(theme_path)
    themes = []
    source = Gio.SettingsSchemaSource.get_default()
    for schema_name in ("org.mate.interface", "org.gnome.desktop.interface"):
        schema = source.lookup(schema_name, True) if source else None
        if schema and schema.has_key("icon-theme"):
            themes.append(Gio.Settings.new_full(schema, None, None).get_string("icon-theme"))
    for theme_name in themes + ["hicolor", "Adwaita"]:
        theme.set_custom_theme(theme_name)
        icon = theme.lookup_icon(name, 32, Gtk.IconLookupFlags.FORCE_SIZE)
        if icon and icon.get_filename():
            try:
                pixbuf = load(icon.get_filename())
                break
            except Exception:
                pass
if pixbuf is None:
    sys.exit(1)
width, height = pixbuf.get_width(), pixbuf.get_height()
channels, stride = pixbuf.get_n_channels(), pixbuf.get_rowstride()
pixels = pixbuf.get_pixels()
argb = bytearray()
for y in range(height):
    for x in range(width):
        offset = y * stride + x * channels
        argb.extend((pixels[offset + 3] if channels == 4 else 255, *pixels[offset:offset + 3]))
print(json.dumps(dict(width=width, height=height, argb_hex=argb.hex())))
