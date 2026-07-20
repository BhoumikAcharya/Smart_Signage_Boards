#!/usr/bin/env python3
"""
Fake ESP32 node(s) for bench-testing the Gateway with NO hardware attached.

Impersonates one or more ESP32s on the MQTT side: publishes the full retained
telemetry set, answers the broadcast PING, and prints every command it receives
so you can watch Modbus -> MQTT actually working.

    python3 simulate_node.py                     # 1 node at register 40001
    python3 simulate_node.py --nodes 5           # 40001..40005
    python3 simulate_node.py --fault             # inject a rotating fault
    python3 simulate_node.py --host 127.0.0.1

Ctrl+C sends a clean OFFLINE for every simulated node.
"""

import argparse
import itertools
import signal
import sys
import time

import paho.mqtt.client as mqtt
from paho.mqtt.client import CallbackAPIVersion

PREFIX = "metro/signage/register"
SCAN_TOPIC = "metro/signage/scan"

# Mirrors the firmware contract: commands drive the arrows only.
MODES = {
    0: ("Arrows OFF",      "OFF", "OFF"),
    1: ("LHS chase",       "ON",  "OFF"),
    2: ("RHS chase",       "OFF", "ON"),
    3: ("Both chase",      "ON",  "ON"),
    4: ("Solid ON left",   "ON",  "OFF"),
    5: ("Solid ON right",  "OFF", "ON"),
    6: ("Solid ON all",    "ON",  "ON"),
}

FAULT_CYCLE = itertools.cycle(["ON", "FAIL_OPEN", "FAIL_SHORT", "ON", "OFF"])


class FakeNode:
    def __init__(self, addr, index, deaf=False):
        self.addr = addr
        self.ip = f"192.168.1.{101 + index}"
        self.deaf = deaf   # accepts commands but never acts on them
        self.command = 0
        self.c1 = "OFF"
        self.c2 = "OFF"
        # Static zones are always-on: only ever ON or FAIL_OPEN.
        self.c3 = "ON"
        self.c4 = "ON"
        self.power = "OK"

    def topic(self, metric):
        return f"{PREFIX}/{self.addr}/{metric}"

    def apply_command(self, value):
        """Derive the expected 4-state telemetry from the received command."""
        if self.deaf:
            # Simulates a node that received the command but did not act on it —
            # exactly the silent divergence the 'state' topic exists to expose.
            return
        self.command = value
        if value in MODES:
            _, self.c1, self.c2 = MODES[value]
        elif 10000 <= value <= 11023:
            mask = value - 10000
            self.c1 = "ON" if (mask & 0x1F) else "OFF"
            self.c2 = "ON" if ((mask >> 5) & 0x1F) else "OFF"
        # Unknown values are ignored, exactly as firmware would.

    def publish_all(self, client):
        client.publish(self.topic("status"), f"ONLINE:{self.ip}", qos=1, retain=True)
        client.publish(self.topic("power"), self.power, retain=True)
        client.publish(self.topic("current1"), self.c1, retain=True)
        client.publish(self.topic("current2"), self.c2, retain=True)
        client.publish(self.topic("current3"), self.c3, retain=True)
        client.publish(self.topic("current4"), self.c4, retain=True)
        # What this node is ACTUALLY executing — not what it was told.
        client.publish(self.topic("state"), str(self.command), retain=True)
        # battery_pct deliberately NOT published — it is stubbed in firmware.
        # The gateway must show the 65535 sentinel, not 0.


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=1883)
    ap.add_argument("--nodes", type=int, default=1, help="how many nodes to fake")
    ap.add_argument("--base", type=int, default=40001, help="first register address")
    ap.add_argument("--fault", action="store_true",
                    help="rotate node 1's LHS through fault states every 10s")
    ap.add_argument("--deaf", type=int, metavar="N", default=None,
                    help="node N (1-based) accepts commands but never acts on them, "
                         "to prove commanded-vs-actual divergence is detected")
    args = ap.parse_args()

    # Python block-buffers stdout when it is a pipe, so `> sim.log` would show
    # nothing until exit. Line-buffer so the log is useful while it runs.
    sys.stdout.reconfigure(line_buffering=True)

    nodes = {args.base + i: FakeNode(args.base + i, i, deaf=(args.deaf == i + 1))
             for i in range(args.nodes)}
    if args.deaf:
        print(f"[!] node {args.deaf} is DEAF — it will report its old state forever")

    def on_connect(client, userdata, flags, reason_code, properties):
        if reason_code != 0:
            print(f"[!] connect failed: {reason_code}")
            return
        print(f"[+] connected to {args.host}:{args.port}")
        client.subscribe(SCAN_TOPIC, qos=0)
        for addr in nodes:
            client.subscribe(f"{PREFIX}/{addr}/value", qos=1)
        for n in nodes.values():
            n.publish_all(client)
        print(f"[+] {len(nodes)} node(s) ONLINE: {', '.join(str(a) for a in nodes)}")
        print("[ ] waiting for commands... (write to 41001+ or 42001+ on the gateway)\n")

    def on_message(client, userdata, msg):
        payload = msg.payload.decode(errors="replace")
        if msg.topic == SCAN_TOPIC:
            if payload == "PING":
                print("[<] PING received -> republishing telemetry")
                for n in nodes.values():
                    n.publish_all(client)
            return

        parts = msg.topic.split("/")
        if len(parts) == 5 and parts[4] == "value":
            addr = int(parts[3])
            node = nodes.get(addr)
            if not node:
                return
            try:
                value = int(payload)
            except ValueError:
                print(f"[!] {addr}: non-integer command {payload!r}")
                return
            label = MODES.get(value, ("raw bitmask",))[0] if value in MODES else (
                f"bitmask {value - 10000:010b}" if 10000 <= value <= 11023 else "UNKNOWN")
            node.apply_command(value)
            node.publish_all(client)
            print(f"[<] {addr}: command {value:<6} -> {label:<16} "
                  f"LHS={node.c1:<10} RHS={node.c2}")

    client = mqtt.Client(CallbackAPIVersion.VERSION2, client_id="fake-esp32-sim")
    client.on_connect = on_connect
    client.on_message = on_message

    # LWT for the first node, mirroring real firmware behaviour.
    first = nodes[args.base]
    client.will_set(first.topic("status"), "OFFLINE", qos=1, retain=True)

    def shutdown(signum=None, frame=None):
        print("\n[-] going OFFLINE cleanly...")
        for n in nodes.values():
            client.publish(n.topic("status"), "OFFLINE", qos=1, retain=True)
        time.sleep(0.5)
        client.loop_stop()
        client.disconnect()
        sys.exit(0)

    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)

    client.connect(args.host, args.port, 60)
    client.loop_start()

    tick = 0
    while True:
        time.sleep(10)
        tick += 1
        if args.fault:
            n = nodes[args.base]
            n.c1 = next(FAULT_CYCLE)
            n.publish_all(client)
            print(f"[~] injected fault on {n.addr}: LHS -> {n.c1}")


if __name__ == "__main__":
    main()
