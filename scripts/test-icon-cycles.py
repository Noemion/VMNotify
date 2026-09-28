"""Linux integration check: private D-Bus, no changes to the user's desktop.

Requires python3-dbus, python3-gi, dbus-daemon, gdbus.
Usage: python3 scripts/test-icon-cycles.py /path/to/vmnotify-agent
"""
import json
import os
import subprocess
import sys
import tempfile
import threading

import dbus
import dbus.service
import dbus.mainloop.glib
from gi.repository import GLib


def main():
    daemon = subprocess.Popen(
        ["dbus-daemon", "--session", "--nofork", "--print-address"],
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True,
    )
    agent = None
    try:
        address = daemon.stdout.readline().strip()
        dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
        bus = dbus.bus.BusConnection(address)
        watcher_name = dbus.service.BusName("org.kde.StatusNotifierWatcher", bus)
        item_service = "org.kde.StatusNotifierItem-%d-1" % os.getpid()
        item_name = dbus.service.BusName(item_service, bus)

        class Watcher(dbus.service.Object):
            @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
            def Get(self, interface, prop):
                return dbus.Array([item_service + "/StatusNotifierItem"], signature="s", variant_level=1)

        class Icon(dbus.service.Object):
            pixel = 0

            @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
            def Get(self, interface, prop):
                return dbus.Array([(dbus.Int32(1), dbus.Int32(1), dbus.ByteArray(bytes([255, self.pixel, 0, 0])))], signature="(iiay)", variant_level=1)

            @dbus.service.signal("org.kde.StatusNotifierItem", signature="")
            def NewIcon(self):
                pass

        watcher = Watcher(watcher_name, "/StatusNotifierWatcher")
        icon = Icon(item_name, "/StatusNotifierItem")
        with tempfile.TemporaryDirectory(prefix="vmnotify-icons-") as directory:
            config = os.path.join(directory, "config.json")
            with open(config, "w") as file:
                json.dump({"apps": [{"id": "test", "name": "Test", "process": open("/proc/self/comm").read().strip()}]}, file)
            env = dict(os.environ, DBUS_SESSION_BUS_ADDRESS=address)
            agent = subprocess.Popen([sys.argv[1], "--config", config], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            events = []

            def read():
                for line in agent.stdout:
                    events.append(json.loads(line)["kind"])

            reader = threading.Thread(target=read)
            reader.start()
            loop = GLib.MainLoop()
            step = [0]

            def tick():
                step[0] += 1
                n = step[0]
                icon.pixel = n % 2 if 4 <= n < 10 or 18 <= n < 24 else 0
                # Emit continuously even during the static parts, like Lanxin.
                icon.NewIcon()
                if n >= 30:
                    loop.quit()
                    return False
                return True

            GLib.timeout_add(500, tick)
            loop.run()
            agent.terminate()
            agent.wait(timeout=10)
            reader.join(timeout=5)
            transitions = [e for e in events if e in ("attention", "cleared")]
            assert transitions == ["attention", "cleared", "attention", "cleared"], transitions
            print("PASS: two complete flash/static cycles despite continuous identical NewIcon signals")
            # Keep exported objects alive until the bus is closed.
            _ = watcher
    finally:
        if agent is not None and agent.poll() is None:
            agent.kill()
            agent.wait()
        daemon.terminate()
        daemon.wait(timeout=5)


if __name__ == "__main__":
    main()
