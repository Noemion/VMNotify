"""Private D-Bus integration: discovery without configuration and aggregate attention.

Requires python3-dbus, python3-gi, dbus-daemon and gdbus.
Usage: python3 scripts/test-auto-discovery.py /path/to/vmnotify-agent [--icon-name]
"""
import json
import os
import subprocess
import sys
import threading

import dbus
import dbus.service
import dbus.mainloop.glib
from gi.repository import GLib


def main():
    daemon = subprocess.Popen(["dbus-daemon", "--session", "--nofork", "--print-address"],
                              stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
    agent = None
    try:
        address = daemon.stdout.readline().strip()
        dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
        bus = dbus.bus.BusConnection(address)
        watcher_name = dbus.service.BusName("org.kde.StatusNotifierWatcher", bus)
        item_service = "org.example.AutoDiscovery"
        item_name = dbus.service.BusName(item_service, bus)
        paths = ["", "/custom/second", "/unsupported"]

        class Watcher(dbus.service.Object):
            @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
            def Get(self, interface, prop):
                return dbus.Array([item_service + p for p in paths], signature="s", variant_level=1)

        class Icon(dbus.service.Object):
            status = "Active"
            pixel = 0

            @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
            def Get(self, interface, prop):
                if prop == "Status":
                    return dbus.String(self.status, variant_level=1)
                if prop == "Id":
                    return dbus.String("Unconfigured Chat", variant_level=1)
                if prop == "IconName":
                    return dbus.String("chat-" + str(self.pixel) if "--icon-name" in sys.argv else "", variant_level=1)
                if prop == "IconPixmap":
                    if "--icon-name" in sys.argv:
                        return dbus.Array([], signature="(iiay)", variant_level=1)
                    return dbus.Array([(1, 1, dbus.ByteArray(bytes([255, self.pixel, 0, 0])))],
                                      signature="(iiay)", variant_level=1)
                raise dbus.exceptions.DBusException("unsupported")

            @dbus.service.signal("org.kde.StatusNotifierItem", signature="s")
            def NewStatus(self, status):
                pass

            @dbus.service.signal("org.kde.StatusNotifierItem", signature="")
            def NewIcon(self):
                pass

        class Unsupported(dbus.service.Object):
            @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
            def Get(self, interface, prop):
                raise dbus.exceptions.DBusException("unsupported")

        objects = [Watcher(watcher_name, "/StatusNotifierWatcher"), Icon(item_name, "/StatusNotifierItem"),
                   Icon(item_name, "/custom/second"), Unsupported(item_name, "/unsupported")]
        first, second = objects[1:3]
        agent = subprocess.Popen([sys.argv[1]], env=dict(os.environ, DBUS_SESSION_BUS_ADDRESS=address),
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        events = []
        reader = threading.Thread(target=lambda: events.extend(json.loads(line) for line in agent.stdout))
        reader.start()
        loop = GLib.MainLoop()
        step = [0]

        def tick():
            if not any(e["kind"] == "apps" and any(a["id"].startswith("auto-") for a in e["apps"]) for e in events):
                return True
            step[0] += 1
            n = step[0]
            if n == 2:
                first.status = second.status = "NeedsAttention"
                first.NewStatus(first.status)
                second.NewStatus(second.status)
            if n == 6:
                # One instance disappears, while the other still needs attention.
                paths.remove("")
                first.status = "Active"
                first.NewStatus(first.status)
            if n == 20:
                second.status = "Active"
                second.NewStatus(second.status)
            if 24 <= n < 32:
                second.pixel = n % 2
            else:
                second.pixel = 0
            second.NewIcon()
            if n >= 38:
                loop.quit()
                return False
            return True

        GLib.timeout_add(500, tick)
        GLib.timeout_add_seconds(35, lambda: (loop.quit(), False)[1])
        loop.run()
        agent.terminate()
        agent.wait(timeout=10)
        reader.join(timeout=5)
        snapshots = [e["apps"] for e in events if e["kind"] == "apps"]
        discovered = [a for apps in snapshots for a in apps if a["id"].startswith("auto-")]
        assert discovered and all(a["name"] == "Unconfigured Chat" and not a["verified"] for a in discovered), snapshots
        assert all(sum(a["id"].startswith("auto-") for a in apps) == 1 for apps in snapshots), snapshots
        transitions = [e["kind"] for e in events if e["kind"] in ("attention", "cleared")]
        assert transitions == ["attention", "cleared", "attention", "cleared"], transitions
        print("PASS: no-config discovery, unsupported exclusion, service-only/custom paths, multi-instance status and "
              + ("icon-name flashing" if "--icon-name" in sys.argv else "pixel flashing"))
    finally:
        if agent is not None and agent.poll() is None:
            agent.kill()
            agent.wait()
        daemon.terminate()
        daemon.wait(timeout=5)


if __name__ == "__main__":
    main()
