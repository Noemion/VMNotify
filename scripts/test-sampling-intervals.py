"""Verify independent per-app polling and signal throttling on a private D-Bus."""
import ctypes
import json
import multiprocessing
import os
import subprocess
import sys
import tempfile
import threading

import dbus
import dbus.service
import dbus.mainloop.glib
from gi.repository import GLib


def serve(address, label, queue):
    ctypes.CDLL(None).prctl(15, ("vmnotify-" + label).encode(), 0, 0, 0)
    dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
    bus = dbus.bus.BusConnection(address)
    name = dbus.service.BusName("org.example." + label, bus)

    class Item(dbus.service.Object):
        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
        def Get(self, interface, prop):
            if prop in ("IconPixmap", "Id"):
                queue.put((label, prop))
            if prop == "IconPixmap":
                return dbus.Array([(1, 1, dbus.ByteArray(bytes([255, 128, 128, 128])))], signature="(iiay)", variant_level=1)
            return dbus.String({"IconName": "", "Status": "Active", "Id": label}[prop], variant_level=1)

        @dbus.service.signal("org.kde.StatusNotifierItem", signature="")
        def NewIcon(self):
            pass

    item = Item(name, "/StatusNotifierItem")
    queue.put((label, "ready"))
    GLib.timeout_add(25, lambda: (item.NewIcon(), True)[1])
    GLib.MainLoop().run()


def main():
    daemon = subprocess.Popen(["dbus-daemon", "--session", "--nofork", "--print-address"], stdout=subprocess.PIPE, text=True)
    children = []
    agent = None
    try:
        address = daemon.stdout.readline().strip()
        queue = multiprocessing.Queue()
        for label in ("fast", "slow"):
            child = multiprocessing.Process(target=serve, args=(address, label, queue))
            child.start()
            children.append(child)
        assert {queue.get(timeout=10), queue.get(timeout=10)} == {("fast", "ready"), ("slow", "ready")}
        dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
        bus = dbus.bus.BusConnection(address)
        name = dbus.service.BusName("org.kde.StatusNotifierWatcher", bus)

        class Watcher(dbus.service.Object):
            @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
            def Get(self, interface, prop):
                return dbus.Array(["org.example.fast", "org.example.slow"], signature="s", variant_level=1)

        watcher = Watcher(name, "/StatusNotifierWatcher")
        with tempfile.NamedTemporaryFile(mode="w", suffix=".json") as config:
            json.dump({"apps": [{"id": label, "name": label, "process": "vmnotify-" + label} for label in ("fast", "slow")]}, config)
            config.flush()
            agent = subprocess.Popen([sys.argv[1], "--config", config.name, "--sample-intervals", '{"slow":1000}'],
                                     env=dict(os.environ, DBUS_SESSION_BUS_ADDRESS=address), stdout=subprocess.PIPE, text=True)
            events = []
            reader = threading.Thread(target=lambda: events.extend(json.loads(line) for line in agent.stdout))
            reader.start()
            loop = GLib.MainLoop()
            GLib.timeout_add_seconds(9, lambda: (loop.quit(), False)[1])
            loop.run()
            agent.terminate()
            agent.wait(timeout=10)
            reader.join(timeout=5)
        counts = {label: {"IconPixmap": 0, "Id": 0} for label in ("fast", "slow")}
        while not queue.empty():
            label, prop = queue.get(timeout=1)
            counts[label][prop] += 1
        polls = {label: count["IconPixmap"] - count["Id"] for label, count in counts.items()}
        assert 6 <= polls["slow"] <= 12 and polls["fast"] >= polls["slow"] * 3, polls
        assert {e["app_id"] for e in events if "icon" in e} >= {"fast", "slow"}, "missing app observations"
        print("PASS: independent 250ms/1000ms sampling, 25ms signal bursts cannot bypass interval:", polls)
    finally:
        if agent is not None and agent.poll() is None:
            agent.kill()
            agent.wait()
        for child in children:
            child.terminate()
            child.join(timeout=5)
        daemon.terminate()
        daemon.wait(timeout=5)


if __name__ == "__main__":
    main()
