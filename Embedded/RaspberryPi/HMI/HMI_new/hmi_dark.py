"""
Metro Signage HMI — redesigned touch panel (DARK theme, cyan accent).

Runs on the Raspberry Pi 5 + Waveshare 10.1" DSI panel (1280x800), fullscreen
kiosk. This is the "Control Room" redesign: five screens —
  Fleet Overview · Signage List · Node Detail · Per-LED Control · Diagnostics.

Architecture is inherited unchanged from the verified ../HMI.py:
  paho-mqtt 2.1.0 (CallbackAPIVersion.VERSION2), pymodbus 3.11.3, mosquitto 2.0.21.
Only the UI layer was rebuilt; the Modbus/MQTT/threading model is the same.

  DARK / LIGHT: this file is the DARK theme. The LIGHT theme (hmi_light.py) is
  identical except the COLORS palette. Keep the two in sync when editing logic.

  DEMO_MODE: seeds representative telemetry so the design is visible without a
  live gateway. Set DEMO_MODE = False for production on the Pi.
"""

import logging
import sys
import threading
import tkinter as tk
from tkinter import font as tkfont

# Network libraries — wrapped so the UI still opens if they are missing in dev.
try:
    import paho.mqtt.client as mqtt
    from paho.mqtt.client import CallbackAPIVersion
    from pymodbus.client import ModbusTcpClient
    NETWORK_ENABLED = True
except ImportError:
    print("Warning: paho-mqtt or pymodbus not installed. Running in UI-only mode.")
    NETWORK_ENABLED = False

# --- CONFIGURATION (identical to ../HMI.py) ---
MODBUS_IP = '127.0.0.1'
MODBUS_PORT = 502
MQTT_BROKER = '127.0.0.1'
MQTT_PORT = 1883

REG_ACTUAL_BASE = 0      # 40001+  Active Execution Zone (gateway output — read only)
REG_HMI_BASE = 1000      # 41001+  HMI Command Zone (write)
REG_FLAG_MUX = 3000      # 43001   MUX (0 = SCADA/Auto, 1 = HMI/Manual) — GLOBAL
MAX_NODES = 100

SCREEN_W, SCREEN_H = 1280, 800

# Seed fake telemetry so the design is visible with no gateway.
# LIVE by default (reads real Modbus + MQTT). Pass --demo to seed the mock fleet.
DEMO_MODE = "--demo" in sys.argv

# Fonts: DejaVu ships on Raspberry Pi OS; falls back gracefully elsewhere.
FONT_SANS = "DejaVu Sans"
FONT_MONO = "DejaVu Sans Mono"

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)-7s %(message)s",
    stream=sys.stderr,
)
log = logging.getLogger("hmi")

# --- COMMAND ENCODING (see ../hmi_contract.md §3) ---
OPERATOR_MODES = [(0, "Arrows OFF"), (1, "LHS Chase"), (2, "RHS Chase")]
TECHNICIAN_MODES = [(3, "Both Chase"), (4, "Solid Left"),
                    (5, "Solid Right"), (6, "Solid All")]
MODE_LABELS = dict(OPERATOR_MODES + TECHNICIAN_MODES)
TECHNICIAN_VALUES = {v for v, _ in TECHNICIAN_MODES}


def mode_label(value):
    """Human label for any legal command value, including the raw bitmask range."""
    if value is None:
        return "—"
    if value in MODE_LABELS:
        return MODE_LABELS[value]
    if 10000 <= value <= 11023:
        return f"Per-LED {value - 10000:010b}"
    return f"INVALID ({value})"


# --- DESIGN SYSTEM — DARK PALETTE (cyan accent) ---
# Tkinter has no alpha, so the translucent tints from the design are pre-blended
# into solid hex values here.
COLORS = {
    "bg": "#0d0f12", "panel": "#16191d", "elev": "#1b1f24", "border": "#272c33",
    "text": "#e9edf1", "dim": "#8a939d", "faint": "#606a74",
    "accent": "#2ad4e6", "accent_bg": "#10262b", "accent_bd": "#1d545f", "on_accent": "#04191d",
    "ok": "#33c46a", "ok_bg": "#14251b", "ok_bd": "#2c5f3e",
    "warn": "#f0a92b", "warn_bg": "#26200f", "warn_bd": "#5e4a1c",
    "fault": "#f2564f", "fault_bg": "#291817", "fault_bd": "#5f2f2c",
    "off": "#5f6975", "off_bg": "#1a1d22", "off_bd": "#333a42",
    "zebra": "#111417",
}

# --- GLOBAL THREAD-SAFE STATE (identical model to ../HMI.py) ---
data_lock = threading.Lock()
NODE_DATA = {}
for i in range(1, MAX_NODES + 1):
    NODE_DATA[i] = {
        "id": f"ND-{8090 + i}",   # ND-8091 .. ND-8190
        "reg": 40000 + i,          # 40001 .. 40100
        "ip": "---",
        "status": "OFFLINE",
        "power": "---",
        "c1": "---",               # LHS arrows
        "c2": "---",               # RHS arrows
        "c3": "---",               # Static Zone 1
        "c4": "---",               # Static Zone 2
        "state": "---",            # actual executing mode (from MQTT)
        "batt": "---",             # stubbed in firmware
    }

METRIC_KEYS = {
    "power": "power", "current1": "c1", "current2": "c2",
    "current3": "c3", "current4": "c4", "state": "state", "battery_pct": "batt",
}

# Telemetry-label mapping for the four current channels.
SENSOR_LABELS = [("c1", "LHS Arrows"), ("c2", "RHS Arrows"),
                 ("c3", "Static Zone 1"), ("c4", "Static Zone 2")]


def parse_actual_state(raw):
    """Interpret the 'state' payload -> (text, health_key, numeric_or_None)."""
    if raw in (None, "---", ""):
        return "—", "dim", None
    try:
        value = int(raw)
    except (TypeError, ValueError):
        return "CANNOT VERIFY OUTPUTS", "fault", None
    if value == 65535:
        return "CANNOT VERIFY OUTPUTS", "fault", None
    key = "warn" if value in TECHNICIAN_VALUES else "accent"
    return mode_label(value), key, value


def four_state_health(value):
    """Health key for a 4-state current reading. OFF is HEALTHY (dim), not a fault."""
    if value == "ON":
        return "ok"
    if value == "OFF":
        return "dim"
    if value in ("FAIL_OPEN", "FAIL_SHORT"):
        return "fault"
    return "dim"


def node_health(nd):
    """Roll-up health of one node from its telemetry: 'off'/'fault'/'warn'/'ok'."""
    if nd["status"] != "ONLINE":
        return "off"
    if nd["power"] == "FAIL":
        return "fault"
    for k in ("c1", "c2", "c3", "c4"):
        if nd[k] in ("FAIL_OPEN", "FAIL_SHORT"):
            return "fault"
    _text, _key, val = parse_actual_state(nd["state"])
    if _text == "CANNOT VERIFY OUTPUTS":
        return "fault"
    if val in TECHNICIAN_VALUES:
        return "warn"
    return "ok"


HEALTH_COLOR = {"ok": "ok", "warn": "warn", "fault": "fault", "off": "off", "dim": "dim"}
HEALTH_WORD = {"ok": "OK", "warn": "WARNING", "fault": "FAULT", "off": "OFFLINE"}


def counts():
    """Fleet-wide health tally."""
    c = {"online": 0, "ok": 0, "warn": 0, "fault": 0, "off": 0}
    with data_lock:
        for i in range(1, MAX_NODES + 1):
            h = node_health(NODE_DATA[i])
            if h != "off":
                c["online"] += 1
            c[h] += 1
    return c


def build_alarms():
    """Active alarm list derived from telemetry, most severe first."""
    labels = dict(SENSOR_LABELS)
    items = []
    with data_lock:
        for i in range(1, MAX_NODES + 1):
            nd = NODE_DATA[i]
            h = node_health(nd)
            if h == "ok":
                continue
            if h == "off":
                items.append(("off", nd["id"], "Node OFFLINE"))
            elif h == "fault":
                if nd["power"] == "FAIL":
                    items.append(("fault", nd["id"], "Main Power FAIL"))
                for k, lab in SENSOR_LABELS:
                    if nd[k] in ("FAIL_OPEN", "FAIL_SHORT"):
                        items.append(("fault", nd["id"], f"{nd[k]} · {lab}"))
                text, _k, _v = parse_actual_state(nd["state"])
                if text == "CANNOT VERIFY OUTPUTS":
                    items.append(("fault", nd["id"], "Cannot verify outputs"))
            else:  # warn
                _text, _k, val = parse_actual_state(nd["state"])
                items.append(("warn", nd["id"], f"Latched · {mode_label(val)}"))
    rank = {"fault": 0, "warn": 1, "off": 2}
    items.sort(key=lambda x: rank[x[0]])
    return items


def seed_demo_data():
    """Representative telemetry so the design is visible without a live gateway."""
    fault = {14, 47, 68, 91}
    warn = {5, 23, 31, 52, 59, 74, 80, 88, 96}
    offline = {3, 12, 19, 27, 35, 42, 55, 63, 71, 77, 84, 93, 99}
    with data_lock:
        for n in range(1, MAX_NODES + 1):
            nd = NODE_DATA[n]
            if n in offline:
                continue
            nd["status"] = "ONLINE"
            nd["ip"] = f"10.20.7.{10 + n}"
            nd["power"] = "OK"
            nd["c1"], nd["c2"], nd["c3"], nd["c4"] = "ON", "OFF", "ON", "ON"
            nd["state"] = "1"
            if n in fault:
                nd["c1"] = "FAIL_OPEN"
            elif n in warn:
                nd["c1"], nd["c2"], nd["state"] = "ON", "ON", "6"


# --- MQTT BACKGROUND LISTENER (identical to ../HMI.py) ---
def on_mqtt_connect(client, userdata, flags, reason_code, properties):
    if reason_code == 0:
        client.subscribe("metro/signage/register/+/+")
        log.info("MQTT connected to %s:%s", MQTT_BROKER, MQTT_PORT)
    else:
        log.error("MQTT connect failed: %s", reason_code)


def on_mqtt_disconnect(client, userdata, flags, reason_code, properties):
    log.warning("MQTT disconnected: %s (auto-reconnect pending)", reason_code)


def on_mqtt_message(client, userdata, msg):
    try:
        parts = msg.topic.split('/')
        if len(parts) != 5 or parts[0] != "metro":
            return
        reg = int(parts[3])
        metric = parts[4]
        payload = msg.payload.decode()
        node_idx = reg - 40000
        if not 1 <= node_idx <= MAX_NODES:
            return
        with data_lock:
            node = NODE_DATA[node_idx]
            if metric == "status":
                if payload.startswith("ONLINE:"):
                    node["status"] = "ONLINE"
                    node["ip"] = payload.split(":", 1)[1] or "---"
                else:
                    node["status"] = "OFFLINE"
                    node["ip"] = "---"
            elif metric in METRIC_KEYS:
                node[METRIC_KEYS[metric]] = payload
    except Exception:
        log.warning("Dropped malformed MQTT message on %s", msg.topic, exc_info=True)


# ======================================================================
#  Small styled-widget helpers (flat rectangles — Tkinter has no radius)
# ======================================================================
def card(parent, **kw):
    """A panel with a hairline border."""
    opts = dict(bg=COLORS["panel"], highlightbackground=COLORS["border"],
                highlightthickness=1, bd=0)
    opts.update(kw)
    return tk.Frame(parent, **opts)


def dot(parent, color, size=10):
    """A small round status LED on a canvas."""
    c = tk.Canvas(parent, width=size, height=size, highlightthickness=0,
                  bg=parent["bg"] if isinstance(parent, tk.Frame) else COLORS["panel"])
    c.create_oval(1, 1, size - 1, size - 1, fill=color, outline="")
    c._fill = color
    return c


def set_dot(canvas, color):
    canvas.delete("all")
    w = int(canvas["width"])
    canvas.create_oval(1, 1, w - 1, w - 1, fill=color, outline="")


# ======================================================================
#  Virtual keyboard (touch data entry) — restyled from ../HMI.py
# ======================================================================
class VirtualKeyboard(tk.Toplevel):
    def __init__(self, parent, target_var):
        super().__init__(parent)
        self.geometry("900x360+190+300")
        self.configure(bg=COLORS["bg"], highlightbackground=COLORS["border"],
                       highlightthickness=2)
        self.target_var = target_var
        self.overrideredirect(True)
        self._build()
        self.transient(parent)
        self.update_idletasks()
        self.wait_visibility()
        self.grab_set()

    def _build(self):
        keys = [
            ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'],
            ['Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P'],
            ['A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L', '-'],
            ['Z', 'X', 'C', 'V', 'B', 'N', 'M', '.', 'CLR', 'DEL'],
            ['SPACE', 'CLOSE'],
        ]
        frame = tk.Frame(self, bg=COLORS["bg"])
        frame.pack(expand=True, fill="both", padx=10, pady=10)
        bf = tkfont.Font(family=FONT_SANS, size=15, weight="bold")
        for row in keys:
            rf = tk.Frame(frame, bg=COLORS["bg"])
            rf.pack(fill="x", pady=4)
            for key in row:
                bg, fg, w = COLORS["panel"], COLORS["text"], 4
                if key in ('CLR', 'DEL'):
                    fg = COLORS["warn"]
                elif key == 'CLOSE':
                    bg, fg, w = COLORS["accent"], COLORS["on_accent"], 15
                elif key == 'SPACE':
                    w = 30
                tk.Button(rf, text=key, width=w, height=2, font=bf, bg=bg, fg=fg,
                          relief="flat", activebackground=COLORS["elev"],
                          command=lambda k=key: self._press(k)).pack(
                    side="left", padx=4, expand=True, fill="both")

    def _press(self, key):
        cur = self.target_var.get()
        if key == 'DEL':
            self.target_var.set(cur[:-1])
        elif key == 'CLR':
            self.target_var.set("")
        elif key == 'CLOSE':
            self.destroy()
        elif key == 'SPACE':
            self.target_var.set(cur + " ")
        else:
            self.target_var.set(cur + key)


# ======================================================================
#  Application shell — networking + router (inherited from ../HMI.py)
# ======================================================================
class HMIApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Metro Signage HMI")
        self.geometry(f"{SCREEN_W}x{SCREEN_H}")
        self.configure(bg=COLORS["bg"])
        # self.attributes('-fullscreen', True)   # enable in production
        self.bind("<Escape>", lambda e: self.attributes('-fullscreen', False))

        self.f_h1 = tkfont.Font(family=FONT_SANS, size=26, weight="bold")
        self.f_h2 = tkfont.Font(family=FONT_SANS, size=15, weight="bold")
        self.f_body = tkfont.Font(family=FONT_SANS, size=12)
        self.f_small = tkfont.Font(family=FONT_SANS, size=9)
        self.f_label = tkfont.Font(family=FONT_SANS, size=9, weight="bold")
        self.f_mono = tkfont.Font(family=FONT_MONO, size=12)
        self.f_mono_bold = tkfont.Font(family=FONT_MONO, size=12, weight="bold")
        self.f_mono_big = tkfont.Font(family=FONT_MONO, size=30, weight="bold")

        if DEMO_MODE:
            seed_demo_data()

        # --- Networking ---
        self.mqtt_client = None
        self.mb_client = None
        if NETWORK_ENABLED:
            self.mb_client = ModbusTcpClient(MODBUS_IP, port=MODBUS_PORT)
            try:
                self.mb_client.connect()
            except Exception:
                log.warning("Initial Modbus connect failed; will retry in sync loop",
                            exc_info=True)
            self.mqtt_client = mqtt.Client(CallbackAPIVersion.VERSION2, client_id="MetroHMI")
            self.mqtt_client.on_connect = on_mqtt_connect
            self.mqtt_client.on_disconnect = on_mqtt_disconnect
            self.mqtt_client.on_message = on_mqtt_message
            try:
                self.mqtt_client.connect_async(MQTT_BROKER, MQTT_PORT, 60)
                self.mqtt_client.loop_start()
            except Exception:
                log.error("MQTT startup failed", exc_info=True)

        # --- Router ---
        self.container = tk.Frame(self, bg=COLORS["bg"])
        self.container.pack(fill="both", expand=True)
        self.active = "Fleet"
        self.frames = {
            "Fleet": FleetOverviewFrame(self.container, self),
            "Signage": SignageListFrame(self.container, self),
            "NodeDetail": NodeDetailFrame(self.container, self),
            "Diagnostics": DiagnosticsFrame(self.container, self),
        }
        for f in self.frames.values():
            f.place(x=0, y=0, relwidth=1, relheight=1)
        self.show_frame("Fleet")
        self.after(500, self.sync_loop)

    # --- Modbus helpers (pymodbus 3.11.3 — count is keyword) ---
    def mb_ready(self):
        if not self.mb_client:
            return False
        try:
            if self.mb_client.is_socket_open():
                return True
            return bool(self.mb_client.connect())
        except Exception:
            log.warning("Modbus reconnect failed", exc_info=True)
            return False

    def mb_read(self, address, count=1):
        if not self.mb_ready():
            return None
        try:
            rr = self.mb_client.read_holding_registers(address=address, count=count)
            if rr.isError():
                log.warning("Modbus read error at %s: %s", address, rr)
                return None
            return rr.registers
        except Exception:
            log.warning("Modbus read exception at %s", address, exc_info=True)
            return None

    def mb_write(self, address, value):
        if not self.mb_ready():
            return False
        try:
            rr = self.mb_client.write_register(address, value)
            if rr.isError():
                log.warning("Modbus write error at %s: %s", address, rr)
                return False
            return True
        except Exception:
            log.warning("Modbus write exception at %s", address, exc_info=True)
            return False

    def read_mux(self):
        """Global control source: True = HMI/Manual, False = SCADA/Auto."""
        regs = self.mb_read(REG_FLAG_MUX)
        if regs is None:
            return None
        return regs[0] == 1

    def show_frame(self, name, context=None):
        self.active = name
        frame = self.frames[name]
        if name == "NodeDetail" and context is not None:
            frame.load_node(context)
        frame.tkraise()
        if hasattr(frame, "on_show"):
            frame.on_show()

    def sync_loop(self):
        frame = self.frames[self.active]
        if hasattr(frame, "refresh_data"):
            try:
                frame.refresh_data()
            except Exception:
                log.error("refresh_data failed on %s", self.active, exc_info=True)
        self.after(500, self.sync_loop)


# ======================================================================
#  Shared top bar
# ======================================================================
class TopBar(tk.Frame):
    NAV = [("Fleet Overview", "Fleet"), ("Signage List", "Signage"),
           ("Diagnostics", "Diagnostics")]

    def __init__(self, parent, controller, active):
        super().__init__(parent, bg=COLORS["panel"], height=60)
        self.controller = controller
        self.pack_propagate(False)

        brand = tk.Frame(self, bg=COLORS["panel"])
        brand.pack(side="left", padx=22)
        tk.Label(brand, text="»", font=self.controller.f_h2,
                 bg=COLORS["panel"], fg=COLORS["accent"]).pack(side="left", padx=(0, 10))
        bt = tk.Frame(brand, bg=COLORS["panel"])
        bt.pack(side="left")
        tk.Label(bt, text="METRO SIGNAGE", font=self.controller.f_h2,
                 bg=COLORS["panel"], fg=COLORS["text"]).pack(anchor="w")
        tk.Label(bt, text="FLEET CONTROL", font=self.controller.f_small,
                 bg=COLORS["panel"], fg=COLORS["dim"]).pack(anchor="w")

        nav = tk.Frame(self, bg=COLORS["panel"])
        nav.pack(side="left", padx=8)
        for text, key in self.NAV:
            on = (key == active)
            lbl = tk.Label(nav, text=text, font=self.controller.f_body,
                           bg=COLORS["accent_bg"] if on else COLORS["panel"],
                           fg=COLORS["accent"] if on else COLORS["dim"],
                           padx=14, pady=7, cursor="hand2")
            lbl.pack(side="left", padx=2)
            if not on:
                lbl.bind("<Button-1>", lambda e, k=key: self.controller.show_frame(k))

        right = tk.Frame(self, bg=COLORS["panel"])
        right.pack(side="right", padx=22)
        self.clock = tk.Label(right, text="--:--", font=self.controller.f_mono,
                              bg=COLORS["panel"], fg=COLORS["dim"])
        self.clock.pack(side="right", padx=(14, 0))
        pill = tk.Frame(right, bg=COLORS["elev"], highlightbackground=COLORS["border"],
                        highlightthickness=1)
        pill.pack(side="right")
        self.pill_dot = dot(pill, COLORS["ok"], 8)
        self.pill_dot.configure(bg=COLORS["elev"])
        self.pill_dot.pack(side="left", padx=(12, 8), pady=8)
        self.pill_lbl = tk.Label(pill, text="-- / 100 ONLINE", font=self.controller.f_body,
                                 bg=COLORS["elev"], fg=COLORS["text"])
        self.pill_lbl.pack(side="left", padx=(0, 14))

    def refresh(self, online=None):
        import time
        self.clock.config(text=time.strftime("%H:%M"))
        if online is not None:
            self.pill_lbl.config(text=f"{online} / 100 ONLINE")


# ======================================================================
#  Screen 1 — Fleet Overview
# ======================================================================
class FleetOverviewFrame(tk.Frame):
    def __init__(self, parent, controller):
        super().__init__(parent, bg=COLORS["bg"])
        self.controller = controller
        self.tiles = {}
        self.alarm_rows = []
        self._build()

    def _build(self):
        self.topbar = TopBar(self, self.controller, "Fleet")
        self.topbar.pack(fill="x", side="top")

        body = tk.Frame(self, bg=COLORS["bg"])
        body.pack(fill="both", expand=True)

        # --- main content ---
        content = tk.Frame(body, bg=COLORS["bg"])
        content.pack(side="left", fill="both", expand=True, padx=22, pady=18)

        head = tk.Frame(content, bg=COLORS["bg"])
        head.pack(fill="x", pady=(0, 14))
        left = tk.Frame(head, bg=COLORS["bg"])
        left.pack(side="left")
        self.big = tk.Label(left, text="0", font=self.controller.f_mono_big,
                            bg=COLORS["bg"], fg=COLORS["text"])
        self.big.pack(side="left")
        tk.Label(left, text=" / 100", font=self.controller.f_h2,
                 bg=COLORS["bg"], fg=COLORS["dim"]).pack(side="left", pady=(10, 0))
        tk.Label(left, text="  NODES ONLINE", font=self.controller.f_label,
                 bg=COLORS["bg"], fg=COLORS["dim"]).pack(side="left", pady=(12, 0))

        chips = tk.Frame(head, bg=COLORS["bg"])
        chips.pack(side="right")
        self.chip_vals = {}
        for key, name in (("ok", "HEALTHY"), ("warn", "WARNING"),
                          ("fault", "FAULT"), ("off", "OFFLINE")):
            ch = card(chips, bg=COLORS["panel"])
            ch.pack(side="left", padx=5)
            inner = tk.Frame(ch, bg=COLORS["panel"])
            inner.pack(padx=12, pady=8)
            sq = tk.Frame(inner, bg=COLORS[key], width=9, height=9)
            sq.pack(side="left", padx=(0, 8))
            sq.pack_propagate(False)
            txt = tk.Frame(inner, bg=COLORS["panel"])
            txt.pack(side="left")
            v = tk.Label(txt, text="0", font=self.controller.f_mono_bold,
                         bg=COLORS["panel"], fg=COLORS["text"])
            v.pack(anchor="w")
            tk.Label(txt, text=name, font=self.controller.f_small,
                     bg=COLORS["panel"], fg=COLORS["dim"]).pack(anchor="w")
            self.chip_vals[key] = v

        # --- node grid (10 x 10) ---
        grid = tk.Frame(content, bg=COLORS["bg"])
        grid.pack(fill="both", expand=True)
        for r in range(10):
            grid.rowconfigure(r, weight=1, uniform="row")
        for c in range(10):
            grid.columnconfigure(c, weight=1, uniform="col")
        for n in range(1, MAX_NODES + 1):
            r, c = (n - 1) // 10, (n - 1) % 10
            tile = tk.Frame(grid, bg=COLORS["panel"], highlightbackground=COLORS["border"],
                            highlightthickness=1, cursor="hand2")
            tile.grid(row=r, column=c, sticky="nsew", padx=4, pady=4)
            tile.pack_propagate(False)
            num = tk.Label(tile, text=f"{n:02d}", font=self.controller.f_mono_bold,
                           bg=COLORS["panel"], fg=COLORS["text"])
            num.place(x=8, y=6)
            nid = tk.Label(tile, text=f"ND-{8090 + n}", font=self.controller.f_small,
                           bg=COLORS["panel"], fg=COLORS["faint"])
            nid.place(relx=0.0, rely=1.0, x=8, y=-6, anchor="sw")
            for w in (tile, num, nid):
                w.bind("<Button-1>", lambda e, i=n: self.controller.show_frame("NodeDetail", i))
            self.tiles[n] = (tile, num, nid)

        # --- alarms rail ---
        rail = tk.Frame(body, bg=COLORS["panel"], width=344,
                        highlightbackground=COLORS["border"], highlightthickness=1)
        rail.pack(side="right", fill="y")
        rail.pack_propagate(False)
        rh = tk.Frame(rail, bg=COLORS["panel"])
        rh.pack(fill="x", padx=20, pady=(18, 14))
        tk.Label(rh, text="ACTIVE ALARMS", font=self.controller.f_h2,
                 bg=COLORS["panel"], fg=COLORS["text"]).pack(side="left")
        self.badge = tk.Label(rh, text="0", font=self.controller.f_mono_bold,
                              bg=COLORS["fault_bg"], fg=COLORS["fault"], padx=9)
        self.badge.pack(side="right")
        tk.Frame(rail, bg=COLORS["border"], height=1).pack(fill="x")
        self.alarm_box = tk.Frame(rail, bg=COLORS["panel"])
        self.alarm_box.pack(fill="both", expand=True, padx=14, pady=12)

    def refresh_data(self):
        c = counts()
        self.topbar.refresh(online=c["online"])
        self.big.config(text=str(c["online"]))
        for k in ("ok", "warn", "fault", "off"):
            self.chip_vals[k].config(text=str(c[k]))

        with data_lock:
            healths = {n: node_health(NODE_DATA[n]) for n in range(1, MAX_NODES + 1)}
        for n, (tile, num, nid) in self.tiles.items():
            h = healths[n]
            bg = COLORS[f"{h}_bg"]
            bd = COLORS[f"{h}_bd"]
            fg = COLORS[h] if h != "off" else COLORS["off"]
            tile.config(bg=bg, highlightbackground=bd)
            num.config(bg=bg, fg=fg)
            nid.config(bg=bg, fg=COLORS["faint"] if h != "off" else COLORS["off"])

        self._render_alarms(build_alarms())

    def _render_alarms(self, items):
        self.badge.config(text=str(len(items)))
        # Only rebuild if the set changed materially (cheap signature).
        sig = tuple((s, i, c) for s, i, c in items[:9])
        if sig == tuple(self.alarm_rows):
            return
        self.alarm_rows = list(sig)
        for w in self.alarm_box.winfo_children():
            w.destroy()
        if not items:
            tk.Label(self.alarm_box, text="No active alarms.", font=self.controller.f_body,
                     bg=COLORS["panel"], fg=COLORS["dim"]).pack(anchor="w", pady=6)
            return
        for sev, nid, cause in items[:9]:
            row = tk.Frame(self.alarm_box, bg=COLORS["elev"],
                           highlightbackground=COLORS["border"], highlightthickness=1)
            row.pack(fill="x", pady=4)
            stripe = tk.Frame(row, bg=COLORS[sev], width=3)
            stripe.pack(side="left", fill="y")
            inner = tk.Frame(row, bg=COLORS["elev"])
            inner.pack(side="left", fill="x", expand=True, padx=12, pady=10)
            tk.Label(inner, text=nid, font=self.controller.f_mono_bold,
                     bg=COLORS["elev"], fg=COLORS["text"]).pack(anchor="w")
            tk.Label(inner, text=cause, font=self.controller.f_small,
                     bg=COLORS["elev"], fg=COLORS["dim"]).pack(anchor="w")
        if len(items) > 9:
            tk.Label(self.alarm_box, text=f"+ {len(items) - 9} more",
                     font=self.controller.f_small, bg=COLORS["panel"],
                     fg=COLORS["dim"]).pack(anchor="w", pady=(6, 0))


# ======================================================================
#  Screen 2 — Signage List
# ======================================================================
class SignageListFrame(tk.Frame):
    PER_PAGE = 12

    def __init__(self, parent, controller):
        super().__init__(parent, bg=COLORS["bg"])
        self.controller = controller
        self.page = 0
        self.mux_manual = False
        self.search_var = tk.StringVar(value="")
        self.filtered = list(range(1, MAX_NODES + 1))
        self.row_widgets = []
        self._build()

    def _build(self):
        self.topbar = TopBar(self, self.controller, "Signage")
        self.topbar.pack(fill="x", side="top")

        toolbar = tk.Frame(self, bg=COLORS["bg"])
        toolbar.pack(fill="x", padx=24, pady=(18, 14))

        search = tk.Frame(toolbar, bg=COLORS["panel"], highlightbackground=COLORS["border"],
                          highlightthickness=1, cursor="hand2")
        search.pack(side="left")
        self.search_lbl = tk.Label(search, text="Search node ID or IP…",
                                   font=self.controller.f_body, bg=COLORS["panel"],
                                   fg=COLORS["dim"], width=34, anchor="w", padx=14, pady=9)
        self.search_lbl.pack(side="left")
        self.search_lbl.bind("<Button-1>",
                             lambda e: VirtualKeyboard(self.controller, self.search_var))
        self.search_var.trace_add("write", self._on_search)

        # --- global control-source toggle ---
        ctl = tk.Frame(toolbar, bg=COLORS["bg"])
        ctl.pack(side="right")
        lbls = tk.Frame(ctl, bg=COLORS["bg"])
        lbls.pack(side="left", padx=(0, 14))
        tk.Label(lbls, text="CONTROL SOURCE", font=self.controller.f_label,
                 bg=COLORS["bg"], fg=COLORS["dim"]).pack(anchor="e")
        tk.Label(lbls, text="governs all 100 nodes", font=self.controller.f_small,
                 bg=COLORS["bg"], fg=COLORS["faint"]).pack(anchor="e")
        seg = tk.Frame(ctl, bg=COLORS["elev"], highlightbackground=COLORS["border"],
                       highlightthickness=1)
        seg.pack(side="left")
        self.opt_remote = tk.Label(seg, text="REMOTE\nSCADA · AUTO", font=self.controller.f_label,
                                   justify="center", padx=16, pady=7, cursor="hand2")
        self.opt_remote.pack(side="left", padx=3, pady=3)
        self.opt_local = tk.Label(seg, text="LOCAL\nHMI · MANUAL", font=self.controller.f_label,
                                  justify="center", padx=16, pady=7, cursor="hand2")
        self.opt_local.pack(side="left", padx=3, pady=3)
        self.opt_remote.bind("<Button-1>", lambda e: self._set_mux(False))
        self.opt_local.bind("<Button-1>", lambda e: self._set_mux(True))

        # --- pagination footer (reserved at the bottom so it can't be pushed off) ---
        foot = tk.Frame(self, bg=COLORS["bg"])
        foot.pack(side="bottom", fill="x", padx=24, pady=12)
        self.page_info = tk.Label(foot, text="", font=self.controller.f_mono,
                                  bg=COLORS["bg"], fg=COLORS["dim"])
        self.page_info.pack(side="left")
        self.btn_next = tk.Button(foot, text="Next ›", font=self.controller.f_body,
                                  bg=COLORS["panel"], fg=COLORS["text"], relief="flat",
                                  activebackground=COLORS["elev"], padx=18, pady=8,
                                  command=lambda: self._turn(1))
        self.btn_next.pack(side="right", padx=(10, 0))
        self.btn_prev = tk.Button(foot, text="‹ Prev", font=self.controller.f_body,
                                  bg=COLORS["panel"], fg=COLORS["text"], relief="flat",
                                  activebackground=COLORS["elev"], padx=18, pady=8,
                                  command=lambda: self._turn(-1))
        self.btn_prev.pack(side="right")

        # --- table ---
        wrap = card(self, bg=COLORS["panel"])
        wrap.pack(fill="both", expand=True, padx=24)
        header = tk.Frame(wrap, bg=COLORS["elev"])
        header.pack(fill="x")
        for text, w in (("NODE ID", 16), ("IP ADDRESS", 20), ("REGISTER", 12), ("STATUS", 14)):
            tk.Label(header, text=text, font=self.controller.f_label, bg=COLORS["elev"],
                     fg=COLORS["dim"], width=w, anchor="w", padx=12, pady=11).pack(side="left")
        self.rows = tk.Frame(wrap, bg=COLORS["panel"])
        self.rows.pack(fill="both", expand=True)

        self._apply_mux_style()
        self._render_rows()

    def on_show(self):
        m = self.controller.read_mux()
        if m is not None:
            self.mux_manual = m
            self._apply_mux_style()

    def _on_search(self, *_):
        q = self.search_var.get().lower()
        self.search_lbl.config(
            text=q if q else "Search node ID or IP…",
            fg=COLORS["accent"] if q else COLORS["dim"])
        with data_lock:
            self.filtered = [i for i in range(1, MAX_NODES + 1)
                             if q in NODE_DATA[i]["id"].lower() or q in NODE_DATA[i]["ip"].lower()]
        self.page = 0
        self._render_rows()

    def _turn(self, d):
        maxp = max(0, (len(self.filtered) - 1) // self.PER_PAGE)
        self.page = min(maxp, max(0, self.page + d))
        self._render_rows()

    def _set_mux(self, manual):
        target = 1 if manual else 0
        if not self.controller.mb_write(REG_FLAG_MUX, target):
            log.warning("MUX write to %s failed; will resync from register", target)
        self.mux_manual = manual
        self._apply_mux_style()

    def _apply_mux_style(self):
        if self.mux_manual:
            self.opt_local.config(bg=COLORS["warn_bg"], fg=COLORS["warn"])
            self.opt_remote.config(bg=COLORS["elev"], fg=COLORS["dim"])
        else:
            self.opt_remote.config(bg=COLORS["accent_bg"], fg=COLORS["accent"])
            self.opt_local.config(bg=COLORS["elev"], fg=COLORS["dim"])

    def _render_rows(self):
        for w in self.rows.winfo_children():
            w.destroy()
        self.row_widgets = []
        start = self.page * self.PER_PAGE
        end = min(start + self.PER_PAGE, len(self.filtered))
        total = len(self.filtered)
        self.page_info.config(
            text=f"Showing {start + 1}–{end} of {total}" if total else "No matches")
        self.btn_prev.config(state="normal" if self.page > 0 else "disabled")
        self.btn_next.config(state="normal" if end < total else "disabled")

        with data_lock:
            for k, idx in enumerate(self.filtered[start:end]):
                nd = NODE_DATA[idx]
                bg = COLORS["panel"] if k % 2 == 0 else COLORS["zebra"]
                row = tk.Frame(self.rows, bg=bg, cursor="hand2")
                row.pack(fill="x")
                tk.Frame(row, bg=COLORS["border"], height=1).pack(fill="x", side="bottom")
                idl = tk.Label(row, text=nd["id"], font=self.controller.f_mono_bold, bg=bg,
                               fg=COLORS["text"], width=16, anchor="w", padx=12, pady=10)
                idl.pack(side="left")
                ipl = tk.Label(row, text=nd["ip"], font=self.controller.f_mono, bg=bg,
                               fg=COLORS["dim"], width=20, anchor="w", padx=12)
                ipl.pack(side="left")
                rgl = tk.Label(row, text=str(nd["reg"]), font=self.controller.f_mono, bg=bg,
                               fg=COLORS["dim"], width=12, anchor="w", padx=12)
                rgl.pack(side="left")
                h = node_health(nd)
                st = tk.Frame(row, bg=bg, width=self.controller.f_body.measure("X") * 14)
                st.pack(side="left", padx=12)
                d = dot(st, COLORS[h] if h != "off" else COLORS["off"], 9)
                d.configure(bg=bg)
                d.pack(side="left", pady=10)
                tk.Label(st, text=HEALTH_WORD[h], font=self.controller.f_label, bg=bg,
                         fg=COLORS[h] if h != "off" else COLORS["off"]).pack(side="left", padx=8)
                for w in (row, idl, ipl, rgl):
                    w.bind("<Button-1>", lambda e, i=idx: self.controller.show_frame("NodeDetail", i))
                self.row_widgets.append((idx, ipl, st, d))

    def refresh_data(self):
        self.topbar.refresh(online=counts()["online"])
        m = self.controller.read_mux()
        if m is not None and m != self.mux_manual:
            self.mux_manual = m
            self._apply_mux_style()


# ======================================================================
#  Screen 3 — Node Detail
# ======================================================================
class NodeDetailFrame(tk.Frame):
    def __init__(self, parent, controller):
        super().__init__(parent, bg=COLORS["bg"])
        self.controller = controller
        self.node_idx = 1
        self.offset = 0
        self.mux_manual = False
        self.commanded = None
        self.mode_btns = {}
        self.tel = {}
        self.popup = None
        self._build()

    def _build(self):
        self.topbar = TopBar(self, self.controller, "Signage")
        self.topbar.pack(fill="x", side="top")

        sub = tk.Frame(self, bg=COLORS["bg"], height=54)
        sub.pack(fill="x")
        sub.pack_propagate(False)
        back = tk.Label(sub, text="‹ Signage List", font=self.controller.f_h2,
                        bg=COLORS["bg"], fg=COLORS["accent"], cursor="hand2", padx=22)
        back.pack(side="left")
        back.bind("<Button-1>", lambda e: self.controller.show_frame("Signage"))
        self.sub_dot = dot(sub, COLORS["off"], 10)
        self.sub_dot.configure(bg=COLORS["bg"])
        self.sub_dot.pack(side="left", pady=20)
        self.node_id_lbl = tk.Label(sub, text="ND-----", font=self.controller.f_h2,
                                    bg=COLORS["bg"], fg=COLORS["text"], padx=10)
        self.node_id_lbl.pack(side="left")

        chips = tk.Frame(sub, bg=COLORS["bg"])
        chips.pack(side="right", padx=22)
        self.chip_ip = self._subchip(chips, "IP ADDRESS", "---", COLORS["accent"])
        self.chip_reg = self._subchip(chips, "REGISTER", "-----", COLORS["text"])
        self.chip_ctl = self._subchip(chips, "CONTROL", "—", COLORS["accent"])
        tk.Frame(self, bg=COLORS["border"], height=1).pack(fill="x")

        cols = tk.Frame(self, bg=COLORS["bg"])
        cols.pack(fill="both", expand=True)
        self.colL = tk.Frame(cols, bg=COLORS["bg"])
        self.colL.pack(side="left", fill="both", expand=True, padx=22, pady=18)
        tk.Frame(cols, bg=COLORS["border"], width=1).pack(side="left", fill="y")
        self.colR = tk.Frame(cols, bg=COLORS["bg"])
        self.colR.pack(side="left", fill="both", expand=True, padx=22, pady=18)

        self._build_telemetry()
        self._build_control()

    def _subchip(self, parent, key, val, color):
        c = card(parent, bg=COLORS["panel"])
        c.pack(side="left", padx=5)
        inner = tk.Frame(c, bg=COLORS["panel"])
        inner.pack(padx=13, pady=6)
        tk.Label(inner, text=key, font=self.controller.f_small,
                 bg=COLORS["panel"], fg=COLORS["dim"]).pack(anchor="w")
        v = tk.Label(inner, text=val, font=self.controller.f_mono,
                     bg=COLORS["panel"], fg=color)
        v.pack(anchor="w")
        return v

    def _build_telemetry(self):
        tk.Label(self.colL, text="HARDWARE TELEMETRY", font=self.controller.f_h2,
                 bg=COLORS["bg"], fg=COLORS["dim"]).pack(anchor="w", pady=(0, 12))
        for key, label in [("status", "Connection Status"), ("power", "Main Power")] + SENSOR_LABELS:
            self.tel[key] = self._tel_row(label)
        # battery — permanently greyed
        bat = card(self.colL, bg=COLORS["panel"])
        bat.pack(fill="x", pady=(14, 0))
        bi = tk.Frame(bat, bg=COLORS["panel"])
        bi.pack(fill="x", padx=16, pady=13)
        tk.Label(bi, text="Backup Battery", font=self.controller.f_body,
                 bg=COLORS["panel"], fg=COLORS["dim"]).pack(side="left")
        tk.Label(bi, text="N/A · NOT FITTED", font=self.controller.f_mono,
                 bg=COLORS["panel"], fg=COLORS["faint"]).pack(side="right")

    def _tel_row(self, label):
        row = card(self.colL, bg=COLORS["panel"])
        row.pack(fill="x", pady=4)
        inner = tk.Frame(row, bg=COLORS["panel"])
        inner.pack(fill="x", padx=16, pady=11)
        tk.Label(inner, text=label, font=self.controller.f_body,
                 bg=COLORS["panel"], fg=COLORS["text"]).pack(side="left")
        d = dot(inner, COLORS["off"], 9)
        d.configure(bg=COLORS["panel"])
        val = tk.Label(inner, text="—", font=self.controller.f_label,
                       bg=COLORS["panel"], fg=COLORS["dim"])
        val.pack(side="right")
        d.pack(side="right", padx=(10, 8))
        return (val, d)

    def _build_control(self):
        tk.Label(self.colR, text="MANUAL OVERRIDE CONTROL", font=self.controller.f_h2,
                 bg=COLORS["bg"], fg=COLORS["dim"]).pack(anchor="w", pady=(0, 12))

        self.band = card(self.colR, bg=COLORS["accent_bg"], highlightbackground=COLORS["accent_bd"])
        self.band.pack(fill="x", pady=(0, 12))
        bi = tk.Frame(self.band, bg=COLORS["accent_bg"])
        bi.pack(fill="x", padx=14, pady=11)
        self.band_dot = dot(bi, COLORS["accent"], 9)
        self.band_dot.configure(bg=COLORS["accent_bg"])
        self.band_dot.pack(side="left", padx=(0, 10))
        self.band_lbl = tk.Label(bi, text="—", font=self.controller.f_body,
                                 bg=COLORS["accent_bg"], fg=COLORS["accent"])
        self.band_lbl.pack(side="left")

        cmp = tk.Frame(self.colR, bg=COLORS["bg"])
        cmp.pack(fill="x", pady=(0, 14))
        self.cmd_val = self._cmp_cell(cmp, "COMMANDED")
        self.act_val = self._cmp_cell(cmp, "ACTUALLY EXECUTING")
        self.sync_lbl = tk.Label(cmp, text="", font=self.controller.f_label,
                                 bg=COLORS["bg"], fg=COLORS["ok"])
        self.sync_lbl.pack(side="left", padx=8)

        # operator
        tk.Label(self.colR, text="OPERATOR MODES", font=self.controller.f_label,
                 bg=COLORS["bg"], fg=COLORS["dim"]).pack(anchor="w", pady=(2, 6))
        oprow = tk.Frame(self.colR, bg=COLORS["bg"])
        oprow.pack(fill="x")
        for v, lab in OPERATOR_MODES:
            self.mode_btns[v] = self._mode_btn(oprow, v, lab, tech=False)
        # technician
        tk.Label(self.colR, text="TECHNICIAN MODES · LATCH UNTIL CHANGED",
                 font=self.controller.f_label, bg=COLORS["bg"],
                 fg=COLORS["warn"]).pack(anchor="w", pady=(14, 6))
        trow = tk.Frame(self.colR, bg=COLORS["bg"])
        trow.pack(fill="x")
        for v, lab in TECHNICIAN_MODES:
            self.mode_btns[v] = self._mode_btn(trow, v, lab, tech=True)

        # advanced + lockout
        self.lockout = tk.Frame(self.colR, bg=COLORS["warn_bg"],
                                highlightbackground=COLORS["warn_bd"], highlightthickness=1)
        tk.Label(self.lockout,
                 text="⚠  SCADA (Auto) has control — mode buttons are locked.\n"
                      "Switch the control source to LOCAL on the Signage List screen.",
                 font=self.controller.f_small, justify="left", bg=COLORS["warn_bg"],
                 fg=COLORS["text"]).pack(anchor="w", padx=14, pady=10)

        adv = tk.Frame(self.colR, bg=COLORS["bg"])
        adv.pack(side="bottom", fill="x", pady=(14, 0))
        self.adv_btn = tk.Button(adv, text="⚙  Per-LED Control", font=self.controller.f_body,
                                 bg=COLORS["elev"], fg=COLORS["text"], relief="flat",
                                 activebackground=COLORS["panel"], padx=16, pady=10,
                                 command=self._open_popup)
        self.adv_btn.pack(side="right")

    def _cmp_cell(self, parent, title):
        c = card(parent, bg=COLORS["panel"])
        c.pack(side="left", fill="x", expand=True, padx=(0, 10))
        inner = tk.Frame(c, bg=COLORS["panel"])
        inner.pack(fill="x", padx=14, pady=11)
        tk.Label(inner, text=title, font=self.controller.f_small,
                 bg=COLORS["panel"], fg=COLORS["dim"]).pack(anchor="w")
        v = tk.Label(inner, text="—", font=self.controller.f_mono_bold,
                     bg=COLORS["panel"], fg=COLORS["text"])
        v.pack(anchor="w")
        return v

    def _mode_btn(self, parent, value, label, tech):
        b = tk.Button(parent, text=f"{value}\n{label}", font=self.controller.f_label,
                      relief="flat", bg=COLORS["panel"], fg=COLORS["text"],
                      activebackground=COLORS["elev"], height=3,
                      command=lambda v=value: self._send_mode(v))
        b._tech = tech
        b.pack(side="left", expand=True, fill="both", padx=3)
        return b

    # --- data ---
    def load_node(self, idx):
        self.node_idx = idx
        self.offset = idx - 1
        self.commanded = None
        with data_lock:
            self.node_id_lbl.config(text=NODE_DATA[idx]["id"])
            self.chip_reg.config(text=str(NODE_DATA[idx]["reg"]))
        self.refresh_data()

    def _send_mode(self, value):
        if not self.mux_manual:
            return
        if self.controller.mb_write(REG_HMI_BASE + self.offset, value):
            log.info("node %d (41%03d) <- %d (%s)", self.node_idx, self.offset + 1,
                     value, mode_label(value))
            self.commanded = value
            self._update_mode_btns()

    def _open_popup(self):
        if self.popup and self.popup.winfo_exists():
            return
        self.popup = PerLedDialog(self, self.controller, self.node_idx, self.offset,
                                  enabled=self.mux_manual)

    def _apply_mux(self, manual):
        self.mux_manual = manual
        if manual:
            self.band.config(bg=COLORS["accent_bg"], highlightbackground=COLORS["accent_bd"])
            for w in self.band.winfo_children()[0].winfo_children():
                pass
            self.band_lbl.config(text="LOCAL (MANUAL) — buttons live", fg=COLORS["accent"])
            self.chip_ctl.config(text="LOCAL · MANUAL", fg=COLORS["accent"])
            self.lockout.pack_forget()
        else:
            self.band_lbl.config(text="REMOTE (AUTO) — SCADA has control", fg=COLORS["dim"])
            self.chip_ctl.config(text="REMOTE · AUTO", fg=COLORS["dim"])
            self.lockout.pack(fill="x", pady=(14, 0))
        self._update_mode_btns()

    def _update_mode_btns(self):
        for v, b in self.mode_btns.items():
            active = (v == self.commanded)
            if not self.mux_manual:
                b.config(state="disabled",
                         bg=COLORS["elev"] if active else COLORS["panel"],
                         disabledforeground=COLORS["accent"] if active else COLORS["faint"])
            elif active:
                accent = COLORS["warn"] if b._tech else COLORS["accent"]
                b.config(state="normal", bg=accent, fg=COLORS["on_accent"],
                         activebackground=accent)
            else:
                bg = COLORS["warn_bg"] if b._tech else COLORS["panel"]
                fg = COLORS["warn"] if b._tech else COLORS["text"]
                b.config(state="normal", bg=bg, fg=fg, activebackground=COLORS["elev"])

    def refresh_data(self):
        self.topbar.refresh(online=counts()["online"])
        with data_lock:
            nd = dict(NODE_DATA[self.node_idx])

        # telemetry
        online = nd["status"] == "ONLINE"
        cv, cd = self.tel["status"]
        cv.config(text="ONLINE" if online else "OFFLINE",
                  fg=COLORS["ok"] if online else COLORS["off"])
        set_dot(cd, COLORS["ok"] if online else COLORS["off"])
        self.chip_ip.config(text=nd["ip"])
        self.set_sub_dot(node_health(nd))

        pv, pd = self.tel["power"]
        pcol = COLORS["ok"] if nd["power"] == "OK" else COLORS["fault"] if nd["power"] == "FAIL" else COLORS["dim"]
        pv.config(text=nd["power"] if nd["power"] != "---" else "—", fg=pcol)
        set_dot(pd, pcol)

        for key, _label in SENSOR_LABELS:
            v, d = self.tel[key]
            h = four_state_health(nd[key])
            col = COLORS[h] if h != "dim" else COLORS["dim"]
            v.config(text=nd[key] if nd[key] != "---" else "—", fg=col)
            set_dot(d, col)

        # control source (global readback)
        mux = self.controller.read_mux()
        if mux is not None and mux != self.mux_manual:
            self._apply_mux(mux)
        elif self.band_lbl.cget("text") == "—":
            self._apply_mux(self.mux_manual)

        # commanded (register) vs actual (telemetry)
        target = (REG_HMI_BASE if self.mux_manual else REG_ACTUAL_BASE) + self.offset
        regs = self.controller.mb_read(target)
        if regs is not None and regs[0] != self.commanded:
            self.commanded = regs[0]
            self._update_mode_btns()
        self.cmd_val.config(text=mode_label(self.commanded) if self.commanded is not None else "—")

        atext, akey, aval = parse_actual_state(nd["state"])
        acol = COLORS.get(akey, COLORS["dim"]) if akey in COLORS else COLORS["dim"]
        self.act_val.config(text=atext, fg=acol if akey != "dim" else COLORS["dim"])

        if aval is None:
            self.sync_lbl.config(
                text="⚠ HARDWARE FAULT" if atext == "CANNOT VERIFY OUTPUTS" else "",
                fg=COLORS["fault"])
        elif self.commanded is None:
            self.sync_lbl.config(text="")          # commanded unknown — don't claim sync
        elif aval != self.commanded:
            self.sync_lbl.config(text="⚠ MISMATCH", fg=COLORS["fault"])
        else:
            self.sync_lbl.config(text="● IN SYNC", fg=COLORS["ok"])

    def set_sub_dot(self, health):
        set_dot(self.sub_dot, COLORS[health] if health != "off" else COLORS["off"])


# ======================================================================
#  Per-LED Control — modal dialog (10 clickable LEDs)
# ======================================================================
class PerLedDialog(tk.Toplevel):
    def __init__(self, parent, controller, node_idx, offset, enabled):
        super().__init__(parent)
        self.controller = controller
        self.node_idx = node_idx
        self.offset = offset
        self.enabled = enabled
        self.left = [False] * 5
        self.right = [False] * 5
        self.lamps = {}
        self.overrideredirect(True)
        self.configure(bg=COLORS["panel"], highlightbackground=COLORS["border"],
                       highlightthickness=1)
        self.geometry(f"600x560+{(SCREEN_W - 600) // 2}+{(SCREEN_H - 560) // 2}")
        self._build()
        self.transient(parent)
        self.update_idletasks()
        self.wait_visibility()
        self.grab_set()

    def _build(self):
        hd = tk.Frame(self, bg=COLORS["panel"])
        hd.pack(fill="x", padx=22, pady=(18, 12))
        tk.Label(hd, text="Per-LED Control", font=self.controller.f_h1,
                 bg=COLORS["panel"], fg=COLORS["text"]).pack(anchor="w")
        with data_lock:
            nid = NODE_DATA[self.node_idx]["id"]
        tk.Label(hd, text=f"{nid} · direct MOSFET override · raw bitmask",
                 font=self.controller.f_mono, bg=COLORS["panel"],
                 fg=COLORS["dim"]).pack(anchor="w")

        warn = tk.Frame(self, bg=COLORS["warn_bg"])
        warn.pack(fill="x")
        tk.Frame(warn, bg=COLORS["warn"], width=3).pack(side="left", fill="y")
        tk.Label(warn, text="Technician-level. LEDs stay latched in a non-standard state\n"
                            "until a normal mode is re-commanded.",
                 font=self.controller.f_small, justify="left", bg=COLORS["warn_bg"],
                 fg=COLORS["text"]).pack(side="left", padx=14, pady=10)

        body = tk.Frame(self, bg=COLORS["panel"])
        body.pack(fill="both", expand=True, padx=22, pady=16)
        self._lamp_row(body, "left", "‹  LEFT ARROW · TAP AN LED")
        self._lamp_row(body, "right", "RIGHT ARROW · TAP AN LED  ›")

        quick = tk.Frame(body, bg=COLORS["panel"])
        quick.pack(fill="x", pady=(4, 14))
        tk.Button(quick, text="All LEDs ON", font=self.controller.f_label, relief="flat",
                  bg=COLORS["elev"], fg=COLORS["text"], activebackground=COLORS["panel"],
                  padx=14, pady=8, command=lambda: self._all(True)).pack(side="left")
        tk.Button(quick, text="All OFF", font=self.controller.f_label, relief="flat",
                  bg=COLORS["elev"], fg=COLORS["text"], activebackground=COLORS["panel"],
                  padx=14, pady=8, command=lambda: self._all(False)).pack(side="left", padx=9)

        mask = card(body, bg=COLORS["elev"])
        mask.pack(fill="x")
        mi = tk.Frame(mask, bg=COLORS["elev"])
        mi.pack(fill="x", padx=14, pady=11)
        tk.Label(mi, text="RAW VALUE", font=self.controller.f_small,
                 bg=COLORS["elev"], fg=COLORS["dim"]).pack(side="left")
        self.mask_val = tk.Label(mi, text="10000", font=self.controller.f_mono_bold,
                                 bg=COLORS["elev"], fg=COLORS["accent"])
        self.mask_val.pack(side="right")
        self.mask_bits = tk.Label(mi, text="00000 00000", font=self.controller.f_mono,
                                  bg=COLORS["elev"], fg=COLORS["dim"])
        self.mask_bits.pack(side="right", padx=14)

        ft = tk.Frame(self, bg=COLORS["panel"])
        ft.pack(fill="x", padx=22, pady=(0, 16))
        tk.Frame(self, bg=COLORS["border"], height=1).pack(fill="x", before=ft)
        tk.Button(ft, text="Apply", font=self.controller.f_body, relief="flat",
                  bg=COLORS["accent"] if self.enabled else COLORS["elev"],
                  fg=COLORS["on_accent"] if self.enabled else COLORS["faint"],
                  padx=22, pady=9, command=self._apply).pack(side="right")
        tk.Button(ft, text="Cancel", font=self.controller.f_body, relief="flat",
                  bg=COLORS["panel"], fg=COLORS["text"], activebackground=COLORS["elev"],
                  highlightbackground=COLORS["border"], highlightthickness=1,
                  padx=20, pady=9, command=self.destroy).pack(side="right", padx=10)

        if not self.enabled:
            tk.Label(ft, text="Read-only — control source is REMOTE",
                     font=self.controller.f_small, bg=COLORS["panel"],
                     fg=COLORS["faint"]).pack(side="left")
        self._update()

    def _lamp_row(self, parent, side, title):
        tk.Label(parent, text=title, font=self.controller.f_label,
                 bg=COLORS["panel"], fg=COLORS["dim"]).pack(anchor="w", pady=(4, 8))
        row = tk.Frame(parent, bg=COLORS["panel"])
        row.pack(fill="x", pady=(0, 14))
        self.lamps[side] = []
        for i in range(5):
            lamp = tk.Canvas(row, width=52, height=52, bg=COLORS["panel"],
                             highlightthickness=0, cursor="hand2")
            lamp.pack(side="left", padx=8)
            lamp.bind("<Button-1>", lambda e, s=side, idx=i: self._toggle(s, idx))
            self.lamps[side].append(lamp)

    def _draw_lamp(self, side, i):
        on = (self.left if side == "left" else self.right)[i]
        c = self.lamps[side][i]
        c.delete("all")
        fill = COLORS["accent"] if on else COLORS["elev"]
        outline = COLORS["accent"] if on else COLORS["border"]
        txt = COLORS["on_accent"] if on else COLORS["faint"]
        c.create_oval(4, 4, 48, 48, fill=fill, outline=outline, width=2)
        c.create_text(26, 26, text=str(i + 1), fill=txt,
                      font=self.controller.f_mono_bold)

    def _toggle(self, side, i):
        if not self.enabled:
            return
        arr = self.left if side == "left" else self.right
        arr[i] = not arr[i]
        self._update()

    def _all(self, on):
        if not self.enabled:
            return
        self.left = [on] * 5
        self.right = [on] * 5
        self._update()

    def _mask(self):
        m = 0
        for i, v in enumerate(self.left):
            if v:
                m |= (1 << i)
        for i, v in enumerate(self.right):
            if v:
                m |= (1 << (5 + i))
        return m

    def _update(self):
        for side in ("left", "right"):
            for i in range(5):
                self._draw_lamp(side, i)
        m = self._mask()
        bits = "".join(str((m >> k) & 1) for k in range(9, 4, -1)) + " " + \
               "".join(str((m >> k) & 1) for k in range(4, -1, -1))
        self.mask_val.config(text=str(10000 + m))
        self.mask_bits.config(text=bits)

    def _apply(self):
        if not self.enabled:
            return
        value = 10000 + self._mask()
        if self.controller.mb_write(REG_HMI_BASE + self.offset, value):
            log.info("node %d (41%03d) <- %d (per-LED)", self.node_idx, self.offset + 1, value)
        self.destroy()


# ======================================================================
#  Screen 5 — Diagnostics
# ======================================================================
class DiagnosticsFrame(tk.Frame):
    def __init__(self, parent, controller):
        super().__init__(parent, bg=COLORS["bg"])
        self.controller = controller
        self._build()

    def _build(self):
        self.topbar = TopBar(self, self.controller, "Diagnostics")
        self.topbar.pack(fill="x", side="top")

        cols = tk.Frame(self, bg=COLORS["bg"])
        cols.pack(fill="both", expand=True)
        left = tk.Frame(cols, bg=COLORS["bg"])
        left.pack(side="left", fill="both", expand=True, padx=24, pady=20)
        tk.Frame(cols, bg=COLORS["border"], width=1).pack(side="left", fill="y")
        right = tk.Frame(cols, bg=COLORS["bg"])
        right.pack(side="left", fill="both", expand=True, padx=24, pady=20)

        tk.Label(left, text="GATEWAY & SERVICES", font=self.controller.f_h2,
                 bg=COLORS["bg"], fg=COLORS["dim"]).pack(anchor="w", pady=(0, 12))
        self.svc_mqtt = self._srow(left, "MQTT Broker (Paho)")
        self.svc_mb = self._srow(left, "Modbus TCP Server")
        self._srow(left, "Master Gateway IP", static=MODBUS_IP)
        self._srow(left, "Panel", static=f"{SCREEN_W} × {SCREEN_H}")

        tk.Label(right, text="NETWORK HEALTH", font=self.controller.f_h2,
                 bg=COLORS["bg"], fg=COLORS["dim"]).pack(anchor="w", pady=(0, 12))
        tiles = tk.Frame(right, bg=COLORS["bg"])
        tiles.pack(fill="x")
        self.big_online = self._bigtile(tiles, "ONLINE NODES", COLORS["ok"])
        self.big_offline = self._bigtile(tiles, "OFFLINE", COLORS["fault"])
        subs = tk.Frame(right, bg=COLORS["bg"])
        subs.pack(fill="x", pady=(12, 0))
        self.sub_warn = self._subcount(subs, "WARNING", COLORS["warn"])
        self.sub_fault = self._subcount(subs, "IN FAULT", COLORS["fault"])

        ping = card(right, bg=COLORS["panel"])
        ping.pack(fill="x", pady=(18, 0), ipady=6)
        tk.Label(ping, text="TELEMETRY REFRESH", font=self.controller.f_h2,
                 bg=COLORS["panel"], fg=COLORS["text"]).pack(pady=(14, 4))
        tk.Label(ping, text="Asks every node to re-publish its full telemetry.\n"
                            "A refresh — not a fail-safe; nodes hold their last\n"
                            "command when the network drops.",
                 font=self.controller.f_small, justify="center", bg=COLORS["panel"],
                 fg=COLORS["dim"]).pack(pady=(0, 14))
        self.btn_ping = tk.Button(ping, text="BROADCAST PING", font=self.controller.f_body,
                                  bg=COLORS["accent"], fg=COLORS["on_accent"], relief="flat",
                                  activebackground=COLORS["elev"], padx=30, pady=12,
                                  command=self._ping)
        self.btn_ping.pack(pady=(0, 16))

    def _srow(self, parent, label, static=None):
        row = card(parent, bg=COLORS["panel"])
        row.pack(fill="x", pady=5)
        inner = tk.Frame(row, bg=COLORS["panel"])
        inner.pack(fill="x", padx=18, pady=14)
        tk.Label(inner, text=label, font=self.controller.f_body,
                 bg=COLORS["panel"], fg=COLORS["text"]).pack(side="left")
        v = tk.Label(inner, text=static or "—", font=self.controller.f_mono_bold,
                     bg=COLORS["panel"], fg=COLORS["accent"] if static else COLORS["dim"])
        v.pack(side="right")
        return v

    def _bigtile(self, parent, label, color):
        c = card(parent, bg=COLORS["panel"])
        c.pack(side="left", fill="both", expand=True, padx=4)
        v = tk.Label(c, text="0", font=self.controller.f_mono_big, bg=COLORS["panel"], fg=color)
        v.pack(pady=(18, 0))
        tk.Label(c, text=label, font=self.controller.f_label, bg=COLORS["panel"],
                 fg=COLORS["dim"]).pack(pady=(6, 18))
        return v

    def _subcount(self, parent, label, color):
        c = tk.Frame(parent, bg=COLORS["elev"], highlightbackground=COLORS["border"],
                     highlightthickness=1)
        c.pack(side="left", fill="x", expand=True, padx=4)
        inner = tk.Frame(c, bg=COLORS["elev"])
        inner.pack(padx=14, pady=11)
        v = tk.Label(inner, text="0", font=self.controller.f_mono_bold, bg=COLORS["elev"], fg=color)
        v.pack(side="left", padx=(0, 8))
        tk.Label(inner, text=label, font=self.controller.f_label, bg=COLORS["elev"],
                 fg=COLORS["dim"]).pack(side="left")
        return v

    def _ping(self):
        if self.controller.mqtt_client:
            self.controller.mqtt_client.publish("metro/signage/scan", "PING")
        self.btn_ping.config(text="PING SENT", bg=COLORS["ok"])
        self.after(2000, lambda: self.btn_ping.config(text="BROADCAST PING", bg=COLORS["accent"]))

    def refresh_data(self):
        c = counts()
        self.topbar.refresh(online=c["online"])
        mc = self.controller.mqtt_client
        if mc and mc.is_connected():
            self.svc_mqtt.config(text="CONNECTED", fg=COLORS["ok"])
        else:
            self.svc_mqtt.config(text="NOT CONNECTED", fg=COLORS["fault"])
        mb = self.controller.mb_client
        if mb and mb.is_socket_open():
            self.svc_mb.config(text=f"RUNNING · {MODBUS_PORT}", fg=COLORS["ok"])
        else:
            self.svc_mb.config(text="NOT CONNECTED", fg=COLORS["fault"])
        self.big_online.config(text=str(c["online"]))
        self.big_offline.config(text=str(c["off"]))
        self.sub_warn.config(text=str(c["warn"]))
        self.sub_fault.config(text=str(c["fault"]))


if __name__ == "__main__":
    app = HMIApp()
    app.mainloop()
