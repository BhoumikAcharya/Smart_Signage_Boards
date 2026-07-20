#!/usr/bin/env python3
"""
Stand-in for the PLC — drives the Gateway's Modbus side from the command line.

    python3 scada_probe.py status                  # MUX flag + first 5 nodes
    python3 scada_probe.py mux 1                   # 1 = HMI/Manual, 0 = SCADA/Auto
    python3 scada_probe.py cmd 1 2 --zone hmi      # node 1 -> mode 2 via 41001
    python3 scada_probe.py cmd 1 10031 --zone scada
    python3 scada_probe.py diag 1                  # node 1's 7 diagnostic registers
    python3 scada_probe.py watch                   # live diag poll

Node numbers are 1-based (node 1 = register 40001).
"""

import argparse
import time

from pymodbus.client import ModbusTcpClient

ADDR_ACTIVE, ADDR_HMI, ADDR_SCADA = 0, 1000, 2000
ADDR_MUX, ADDR_DIAG, DIAG_PER_NODE = 3000, 5000, 8
UNKNOWN = 65535

FOUR_STATE = {0: "OFF", 1: "ON", 2: "FAIL_OPEN", 3: "FAIL_SHORT", 99: "---"}
DIAG_FIELDS = ["Network", "Power", "LHS", "RHS", "Battery",
               "Static Z1", "Static Z2", "Actual"]


def read(client, addr, count):
    """pymodbus 3.11 wants count as a keyword; device_id defaults to 1."""
    rr = client.read_holding_registers(address=addr, count=count)
    if rr.isError():
        raise SystemExit(f"Modbus read error at {addr}: {rr}")
    return rr.registers


def fmt_diag(regs):
    out = []
    for name, val in zip(DIAG_FIELDS, regs):
        if name == "Network":
            out.append(f"{name}={'ONLINE' if val else 'OFFLINE'}")
        elif name == "Power":
            out.append(f"{name}={'OK' if val else 'FAIL'}")
        elif name == "Battery":
            out.append(f"{name}={'UNKNOWN' if val == UNKNOWN else str(val) + '%'}")
        elif name == "Actual":
            out.append(f"{name}={'UNKNOWN' if val == UNKNOWN else val}")
        else:
            out.append(f"{name}={FOUR_STATE.get(val, val)}")
    return "  ".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=502)
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("status")
    p_mux = sub.add_parser("mux"); p_mux.add_argument("value", type=int, choices=[0, 1])
    p_cmd = sub.add_parser("cmd")
    p_cmd.add_argument("node", type=int); p_cmd.add_argument("value", type=int)
    p_cmd.add_argument("--zone", choices=["hmi", "scada"], default="scada")
    p_diag = sub.add_parser("diag"); p_diag.add_argument("node", type=int)
    p_watch = sub.add_parser("watch"); p_watch.add_argument("--nodes", type=int, default=3)

    args = ap.parse_args()
    client = ModbusTcpClient(args.host, port=args.port)
    if not client.connect():
        raise SystemExit(f"Could not connect to Modbus at {args.host}:{args.port}")

    try:
        if args.cmd == "status":
            mux = read(client, ADDR_MUX, 1)[0]
            print(f"MUX 43001 = {mux}  ({'HMI/Manual' if mux == 1 else 'SCADA/Auto'})")
            active = read(client, ADDR_ACTIVE, 5)
            hmi = read(client, ADDR_HMI, 5)
            scada = read(client, ADDR_SCADA, 5)
            print(f"{'node':<6}{'active':>8}{'hmi':>8}{'scada':>8}")
            for i in range(5):
                print(f"{i+1:<6}{active[i]:>8}{hmi[i]:>8}{scada[i]:>8}")

        elif args.cmd == "mux":
            client.write_register(ADDR_MUX, args.value)
            print(f"MUX 43001 <- {args.value} "
                  f"({'HMI/Manual' if args.value == 1 else 'SCADA/Auto'})")

        elif args.cmd == "cmd":
            base = ADDR_HMI if args.zone == "hmi" else ADDR_SCADA
            client.write_register(base + args.node - 1, args.value)
            reg = (41001 if args.zone == "hmi" else 42001) + args.node - 1
            print(f"node {args.node} ({reg}) <- {args.value}")
            mux = read(client, ADDR_MUX, 1)[0]
            expected = "hmi" if mux == 1 else "scada"
            if expected != args.zone:
                print(f"  ⚠  MUX is {expected.upper()} — this write will NOT reach the node. "
                      f"Run: scada_probe.py mux {1 if args.zone == 'hmi' else 0}")

        elif args.cmd == "diag":
            base = ADDR_DIAG + (args.node - 1) * DIAG_PER_NODE
            regs = read(client, base, DIAG_PER_NODE)
            reg_no = 45001 + (args.node - 1) * DIAG_PER_NODE
            print(f"node {args.node}  registers {reg_no}-{reg_no + DIAG_PER_NODE - 1}")
            print(f"  raw: {regs}")
            print(f"  {fmt_diag(regs)}")

        elif args.cmd == "watch":
            try:
                while True:
                    print(f"\n--- {time.strftime('%H:%M:%S')} ---")
                    for n in range(1, args.nodes + 1):
                        base = ADDR_DIAG + (n - 1) * DIAG_PER_NODE
                        print(f"node {n}: {fmt_diag(read(client, base, DIAG_PER_NODE))}")
                    time.sleep(2)
            except KeyboardInterrupt:
                pass
    finally:
        client.close()


if __name__ == "__main__":
    main()
