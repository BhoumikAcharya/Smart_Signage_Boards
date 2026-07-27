"""
Metro Signage HMI — local touch panel (developer / bench build)

Targets the verified platform contract in ../pi_agent.md and ../HMI/hmi_contract.md:
  paho-mqtt 2.1.0 (CallbackAPIVersion.VERSION2), pymodbus 3.11.3, mosquitto 2.0.21.

THIS IS THE BENCH / DEVELOPER BUILD.
  All seven modes (0-6) are exposed without a PIN. The technician PIN, the raw
  bitmask panel, and the 1280x800 visual redesign are deliberately deferred —
  this round is "correct the existing screen and give it full control", nothing
  more. See hmi_contract.md §3 and §7.
"""

import logging
import sys
import threading
import tkinter as tk
from tkinter import font

# Network Libraries (Wrapped in try-except so UI still opens if missing during dev)
try:
    import paho.mqtt.client as mqtt
    from paho.mqtt.client import CallbackAPIVersion
    from pymodbus.client import ModbusTcpClient
    NETWORK_ENABLED = True
except ImportError:
    print("Warning: paho-mqtt or pymodbus not installed. Running in UI-Only Mode.")
    NETWORK_ENABLED = False

# --- CONFIGURATION ---
MODBUS_IP = '127.0.0.1'
MODBUS_PORT = 502
MQTT_BROKER = '127.0.0.1'
MQTT_PORT = 1883

REG_ACTUAL_BASE = 0      # 40001+ Active Execution Zone (gateway output — read only)
REG_HMI_BASE = 1000      # 41001+ HMI Command Zone
REG_FLAG_MUX = 3000      # 43001  MUX (0=SCADA, 1=HMI)
MAX_NODES = 100

# Panel is the confirmed Waveshare 10.1" DSI LCD.
SCREEN_W, SCREEN_H = 1280, 800

# Logs to stderr so they survive under systemd and never sit in a silent except.
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)-7s %(message)s",
    stream=sys.stderr,
)
log = logging.getLogger("hmi")

# --- COMMAND ENCODING (see hmi_contract.md §3) ---
# Commands drive the moving ARROWS ONLY. Static Zones 1 and 2 are hardwired
# always-on — current-monitored, never switched. No mode value affects them.
OPERATOR_MODES = [(0, "ARROWS OFF"), (1, "LHS CHASE"), (2, "RHS CHASE")]
TECHNICIAN_MODES = [(3, "BOTH CHASE"), (4, "SOLID LEFT"),
                    (5, "SOLID RIGHT"), (6, "SOLID ALL")]
MODE_LABELS = dict(OPERATOR_MODES + TECHNICIAN_MODES)
TECHNICIAN_VALUES = {v for v, _ in TECHNICIAN_MODES}


def mode_label(value):
    """Human label for any legal command value, including the raw bitmask range."""
    if value in MODE_LABELS:
        return MODE_LABELS[value]
    if 10000 <= value <= 11023:
        return f"BITMASK {value - 10000:010b}"
    return f"INVALID ({value})"


# --- DESIGN SYSTEM VARIABLES ---
COLORS = {
    "bg_lowest": "#0e0e0e",
    "bg_main": "#131313",
    "bg_panel": "#1c1b1b",
    "bg_hover": "#2a2a2a",
    "text_main": "#e5e2e1",
    "text_dim": "#bac9cc",
    "primary": "#00daf3",      # Cyan
    "alert": "#ff8a00",        # Orange
    "success": "#6cec00",      # Lime
    "error": "#ffb4ab",        # Red
    "border": "#353534"
}

# --- GLOBAL THREAD-SAFE STATE ---
data_lock = threading.Lock()
NODE_DATA = {}

# Initialize master state for 100 nodes (1 to 100)
for i in range(1, MAX_NODES + 1):
    NODE_DATA[i] = {
        "id": f"ND-{8090+i}",  # ND-8091 to ND-8190
        "reg": 40000 + i,      # 40001 to 40100
        "ip": "---",
        "status": "OFFLINE",
        "color": COLORS["error"],
        "power": "---",
        "c1": "---",           # LHS arrows      (ACS712 GPIO 34)
        "c2": "---",           # RHS arrows      (ACS712 GPIO 35)
        "c3": "---",           # Static Zone 1   (ACS712 GPIO 32)
        "c4": "---",           # Static Zone 2   (ACS712 GPIO 33)
        "state": "---",        # mode the node reports ACTUALLY executing
        "batt": "---",         # stubbed in firmware — never published
    }

# Metric name -> NODE_DATA key. Single source of truth for MQTT ingest.
METRIC_KEYS = {
    "power": "power",
    "current1": "c1",
    "current2": "c2",
    "current3": "c3",
    "current4": "c4",
    "state": "state",
    "battery_pct": "batt",
}


def four_state_color(value):
    """
    4-state discrepancy strings are ON / OFF / FAIL_OPEN / FAIL_SHORT — never
    OK/FAIL. OFF is a HEALTHY state (intended off, no current) and must not be
    coloured as a fault. The previous build compared against "OK", so every
    sensor row rendered as an error regardless of the real state.
    """
    if value == "ON":
        return COLORS["success"]
    if value == "OFF":
        return COLORS["text_dim"]
    if value in ("FAIL_OPEN", "FAIL_SHORT"):
        return COLORS["error"]
    return COLORS["text_dim"]      # "---" / unknown / node offline


def parse_actual_state(raw):
    """
    Interpret the 'state' topic payload.

    Returns (display_text, colour, numeric_value_or_None).

    A non-numeric payload (firmware publishes "FAULT") means the node cannot
    reach or verify its output driver — it does NOT know what its outputs are
    doing. The gateway maps this to 65535 on diagnostic register +7. It is a
    hardware fault, not a missing reading.
    """
    if raw in (None, "---", ""):
        return "---", COLORS["text_dim"], None
    try:
        value = int(raw)
    except (TypeError, ValueError):
        return "CANNOT VERIFY OUTPUTS", COLORS["error"], None
    if value == 65535:
        return "CANNOT VERIFY OUTPUTS", COLORS["error"], None
    colour = COLORS["alert"] if value in TECHNICIAN_VALUES else COLORS["primary"]
    return mode_label(value), colour, value


# --- MQTT BACKGROUND LISTENER ---
def on_mqtt_connect(client, userdata, flags, reason_code, properties):
    """
    ReasonCode 0 is success — and falsy. Compare explicitly.

    Subscribing HERE rather than once at startup is what makes the panel survive
    a broker restart: paho re-runs on_connect on every reconnect, so the
    subscriptions come back with it.
    """
    if reason_code == 0:
        client.subscribe("metro/signage/register/+/+")
        log.info("MQTT connected to %s:%s", MQTT_BROKER, MQTT_PORT)
    else:
        log.error("MQTT connect failed: %s", reason_code)


def on_mqtt_disconnect(client, userdata, flags, reason_code, properties):
    log.warning("MQTT disconnected: %s (auto-reconnect pending)", reason_code)


def on_mqtt_message(client, userdata, msg):
    """Listens to ESP32s and safely updates the UI's central dictionary."""
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
                    node["color"] = COLORS["success"]
                    # split(":", 1) to match the gateway's parse exactly.
                    node["ip"] = payload.split(":", 1)[1] or "---"
                else:
                    node["status"] = "OFFLINE"
                    node["color"] = COLORS["error"]
                    node["ip"] = "---"
            elif metric in METRIC_KEYS:
                node[METRIC_KEYS[metric]] = payload

    except Exception:
        # Malformed packets must not kill the listener — but they are never silent.
        log.warning("Dropped malformed MQTT message on %s", msg.topic, exc_info=True)


class VirtualKeyboard(tk.Toplevel):
    def __init__(self, parent, target_var):
        super().__init__(parent)
        self.title("Virtual Keyboard")
        self.geometry("900x380+190+280")
        self.configure(bg=COLORS["bg_lowest"], highlightbackground=COLORS["border"], highlightthickness=2)
        self.target_var = target_var
        self.overrideredirect(True)
        self.setup_ui()
        self.transient(parent)
        self.update_idletasks()
        self.wait_visibility()
        self.grab_set()

    def setup_ui(self):
        keys = [
            ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'],
            ['Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P'],
            ['A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L', '-'],
            ['Z', 'X', 'C', 'V', 'B', 'N', 'M', '.', 'CLR', 'DEL'],
            ['SPACE', 'CLOSE']
        ]

        key_frame = tk.Frame(self, bg=COLORS["bg_lowest"])
        key_frame.pack(expand=True, fill="both", padx=10, pady=10)
        btn_font = font.Font(family="Helvetica", size=16, weight="bold")

        for r, row in enumerate(keys):
            row_frame = tk.Frame(key_frame, bg=COLORS["bg_lowest"])
            row_frame.pack(fill="x", pady=4)
            for key in row:
                bg_col, fg_col, btn_width = COLORS["bg_panel"], COLORS["text_main"], 4
                if key in ['CLR', 'DEL']: fg_col = COLORS["alert"]
                elif key == 'CLOSE': bg_col, fg_col, btn_width = COLORS["primary"], COLORS["bg_lowest"], 15
                elif key == 'SPACE': btn_width = 30

                btn = tk.Button(row_frame, text=key, width=btn_width, height=2,
                                font=btn_font, bg=bg_col, fg=fg_col, relief="flat",
                                activebackground=COLORS["bg_hover"],
                                command=lambda k=key: self.press(k))
                btn.pack(side="left", padx=4, expand=True, fill="both")

    def press(self, key):
        current = self.target_var.get()
        if key == 'DEL': self.target_var.set(current[:-1])
        elif key == 'CLR': self.target_var.set("")
        elif key == 'CLOSE': self.destroy()
        elif key == 'SPACE': self.target_var.set(current + " ")
        else: self.target_var.set(current + key)


class HMIApp(tk.Tk):
    def __init__(self):
        super().__init__()

        self.title("Metro Signage SCADA HMI")
        self.geometry(f"{SCREEN_W}x{SCREEN_H}")
        self.configure(bg=COLORS["bg_main"])
        # self.attributes('-fullscreen', True)

        self.font_h2 = font.Font(family="Helvetica", size=20, weight="bold")
        self.font_body = font.Font(family="Helvetica", size=14)
        self.font_mono = font.Font(family="Courier", size=14, weight="bold")
        self.font_large = font.Font(family="Helvetica", size=36, weight="bold")

        # --- NETWORK INITIALIZATION ---
        self.mqtt_client = None
        self.mb_client = None

        if NETWORK_ENABLED:
            self.mb_client = ModbusTcpClient(MODBUS_IP, port=MODBUS_PORT)
            try:
                self.mb_client.connect()
            except Exception:
                log.warning("Initial Modbus connect failed; will retry in the sync loop",
                            exc_info=True)

            # paho 2.x: the FIRST positional argument is the callback API version.
            # mqtt.Client() with no args, or mqtt.Client("some-id"), fails outright.
            self.mqtt_client = mqtt.Client(CallbackAPIVersion.VERSION2, client_id="MetroHMI")
            self.mqtt_client.on_connect = on_mqtt_connect
            self.mqtt_client.on_disconnect = on_mqtt_disconnect
            self.mqtt_client.on_message = on_mqtt_message
            try:
                # connect_async + loop_start so the panel starts even if mosquitto
                # is not up yet, and keeps retrying by itself.
                self.mqtt_client.connect_async(MQTT_BROKER, MQTT_PORT, 60)
                self.mqtt_client.loop_start()
            except Exception:
                log.error("MQTT startup failed", exc_info=True)

        # --- UI SETUP ---
        self.container = tk.Frame(self, bg=COLORS["bg_main"])
        self.container.pack(fill="both", expand=True)

        self.frames = {}
        self.active_frame_name = "Dashboard"

        self.frames["Dashboard"] = DashboardFrame(parent=self.container, controller=self)
        self.frames["NodeDetail"] = NodeDetailFrame(parent=self.container, controller=self)
        self.frames["Diagnostics"] = DiagnosticsFrame(parent=self.container, controller=self)

        for frame in self.frames.values():
            frame.place(x=0, y=0, relwidth=1, relheight=1)

        self.show_frame("Dashboard")

        # Start Heartbeat Sync Loop
        self.after(500, self.sync_loop)

    # --- MODBUS HELPERS -------------------------------------------------
    # Centralised so reconnect logic and the pymodbus 3.11.3 keyword-argument
    # requirement live in exactly one place.

    def mb_ready(self):
        """Ensure the socket is open, reconnecting if it dropped."""
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
        """Returns a list of registers, or None on any failure."""
        if not self.mb_ready():
            return None
        try:
            # pymodbus 3.11.3 requires `count` as a KEYWORD argument.
            # read_holding_registers(addr, 1) raises.
            rr = self.mb_client.read_holding_registers(address=address, count=count)
            if rr.isError():
                log.warning("Modbus read error at %s: %s", address, rr)
                return None
            return rr.registers
        except Exception:
            log.warning("Modbus read exception at %s", address, exc_info=True)
            return None

    def mb_write(self, address, value):
        """Returns True on success."""
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

    def show_frame(self, page_name, context=None):
        self.active_frame_name = page_name
        frame = self.frames[page_name]
        if page_name == "NodeDetail" and context is not None:
            frame.load_node(context)
        frame.tkraise()

    def sync_loop(self):
        """The heartbeat of the HMI. Updates the currently active screen seamlessly."""
        active_frame = self.frames[self.active_frame_name]
        if hasattr(active_frame, 'refresh_data'):
            try:
                active_frame.refresh_data()
            except Exception:
                log.error("refresh_data failed on %s", self.active_frame_name, exc_info=True)
        self.after(500, self.sync_loop)


class DashboardFrame(tk.Frame):
    def __init__(self, parent, controller):
        super().__init__(parent, bg=COLORS["bg_main"])
        self.controller = controller
        self.current_page = 0
        # 800 px tall panel: 64 top bar + 64 header + 16 pad + 90 bottom bar
        # leaves ~566 px, and each row is 50 px including padding.
        self.nodes_per_page = 11
        self.filtered_node_indices = list(range(1, 101)) # Store integer indices 1-100
        self.visible_rows = [] # Stores references to label widgets for fast updating

        self.setup_ui()
        self.render_page()

    def setup_ui(self):
        # --- TOP BAR ---
        top_bar = tk.Frame(self, bg=COLORS["bg_lowest"], height=64)
        top_bar.pack(fill="x", side="top")
        top_bar.pack_propagate(False)

        tk.Label(top_bar, text="DASHBOARD", font=self.controller.font_h2,
                 bg=COLORS["bg_lowest"], fg=COLORS["primary"]).pack(side="left", padx=24)

        lbl_diag = tk.Label(top_bar, text="⚙", font=("Helvetica", 20),
                            bg=COLORS["bg_lowest"], fg=COLORS["text_dim"], cursor="hand2")
        lbl_diag.pack(side="right", padx=24)
        lbl_diag.bind("<Button-1>", lambda e: self.controller.show_frame("Diagnostics"))

        self.search_var = tk.StringVar(value="")
        self.search_var.trace("w", self.on_search)

        self.display_var = tk.StringVar(value="Search Node or IP...")
        self.search_btn = tk.Label(top_bar, textvariable=self.display_var, font=self.controller.font_body,
                                   bg=COLORS["bg_panel"], fg=COLORS["text_dim"],
                                   highlightbackground=COLORS["border"], highlightthickness=1,
                                   width=25, anchor="w", padx=10)
        self.search_btn.pack(side="right", padx=10, pady=12, fill="y")
        self.search_btn.bind("<Button-1>", lambda e: VirtualKeyboard(self.controller, self.search_var))

        # --- TABLE HEADER ---
        # Columns are RELATIVE (relx) rather than absolute pixels. The previous
        # build hardcoded x=20/300/700 for a 1024x600 panel; on the confirmed
        # 1280x800 screen those sat bunched in the left two-thirds.
        header_frame = tk.Frame(self, bg=COLORS["bg_panel"], height=40)
        header_frame.pack(fill="x", padx=24, pady=(24, 0))
        header_frame.pack_propagate(False)

        tk.Label(header_frame, text="NODE NUMBER", font=self.controller.font_body,
                 bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).place(relx=0.02, y=10)
        tk.Label(header_frame, text="IP ADDRESS", font=self.controller.font_body,
                 bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).place(relx=0.27, y=10)
        tk.Label(header_frame, text="CONNECTION STATUS", font=self.controller.font_body,
                 bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).place(relx=0.62, y=10)

        # --- ROWS CONTAINER ---
        self.rows_container = tk.Frame(self, bg=COLORS["bg_main"])
        self.rows_container.pack(fill="both", expand=True, padx=24, pady=8)

        # --- BOTTOM BAR ---
        bottom_bar = tk.Frame(self, bg=COLORS["bg_main"], height=90)
        bottom_bar.pack(fill="x", side="bottom")
        bottom_bar.pack_propagate(False)

        self.lbl_page_info = tk.Label(bottom_bar, text="1 - 11 / 100", font=self.controller.font_mono, bg=COLORS["bg_main"], fg=COLORS["text_dim"])
        self.lbl_page_info.pack(side="right", padx=24)

        self.btn_next = tk.Button(bottom_bar, text="NEXT PAGE ➔", font=self.controller.font_body,
                                  bg=COLORS["bg_panel"], fg=COLORS["text_main"], relief="flat",
                                  activebackground=COLORS["bg_hover"], activeforeground=COLORS["primary"],
                                  width=14, height=2, command=self.next_page)
        self.btn_next.pack(side="left", padx=(24, 10), pady=10)

        self.btn_prev = tk.Button(bottom_bar, text="🡨 PREV PAGE", font=self.controller.font_body,
                                  bg=COLORS["bg_panel"], fg=COLORS["text_main"], relief="flat",
                                  activebackground=COLORS["bg_hover"], activeforeground=COLORS["primary"],
                                  width=14, height=2, command=self.prev_page, state="disabled")
        self.btn_prev.pack(side="left", padx=10, pady=10)

    def on_search(self, *args):
        query = self.search_var.get().lower()
        if query == "":
            self.display_var.set("Search Node or IP...")
            self.search_btn.config(fg=COLORS["text_dim"])
        else:
            self.display_var.set(query)
            self.search_btn.config(fg=COLORS["primary"])

        self.filtered_node_indices.clear()
        with data_lock:
            for idx, data in NODE_DATA.items():
                if query in data["id"].lower() or query in data["ip"].lower():
                    self.filtered_node_indices.append(idx)

        self.current_page = 0
        self.render_page()

    def next_page(self):
        max_page = (len(self.filtered_node_indices) - 1) // self.nodes_per_page
        if self.current_page < max_page:
            self.current_page += 1
            self.render_page()

    def prev_page(self):
        if self.current_page > 0:
            self.current_page -= 1
            self.render_page()

    def render_page(self):
        """Builds the physical Tkinter Frames for the active page"""
        for widget in self.rows_container.winfo_children():
            widget.destroy()
        self.visible_rows.clear()

        start_idx = self.current_page * self.nodes_per_page
        end_idx = min(start_idx + self.nodes_per_page, len(self.filtered_node_indices))
        current_view_indices = self.filtered_node_indices[start_idx:end_idx]

        total = len(self.filtered_node_indices)
        self.lbl_page_info.config(text=f"{start_idx + 1} - {end_idx} / {total}" if total > 0 else "0 - 0 / 0")
        self.btn_prev.config(state="normal" if self.current_page > 0 else "disabled")
        self.btn_next.config(state="normal" if end_idx < total else "disabled")

        with data_lock:
            for i, idx in enumerate(current_view_indices):
                node = NODE_DATA[idx]
                bg_color = COLORS["bg_main"] if i % 2 == 0 else COLORS["bg_lowest"]

                row = tk.Frame(self.rows_container, bg=bg_color, height=48)
                row.pack(fill="x", pady=1)
                row.pack_propagate(False)

                # Bindings
                cb = lambda e, n_idx=idx: self.controller.show_frame("NodeDetail", n_idx)
                row.bind("<Button-1>", cb)

                lbl_id = tk.Label(row, text=node["id"], font=self.controller.font_mono, bg=bg_color, fg=COLORS["text_main"])
                lbl_id.place(relx=0.02, y=10)
                lbl_id.bind("<Button-1>", cb)

                lbl_ip = tk.Label(row, text=node["ip"], font=self.controller.font_mono, bg=bg_color, fg=COLORS["primary"])
                lbl_ip.place(relx=0.27, y=10)
                lbl_ip.bind("<Button-1>", cb)

                led = tk.Canvas(row, width=16, height=16, bg=bg_color, highlightthickness=0)
                led.create_oval(2, 2, 14, 14, fill=node["color"], outline="")
                led.place(relx=0.62, y=16)
                led.bind("<Button-1>", cb)

                lbl_status = tk.Label(row, text=node["status"], font=self.controller.font_mono, bg=bg_color, fg=node["color"])
                lbl_status.place(relx=0.645, y=10)
                lbl_status.bind("<Button-1>", cb)

                # Save references so refresh_data() doesn't need to rebuild the layout
                self.visible_rows.append({
                    "idx": idx, "lbl_ip": lbl_ip, "led": led, "lbl_status": lbl_status
                })

    def refresh_data(self):
        """Called by sync_loop. Gently updates text without flickering touch targets."""
        with data_lock:
            for rw in self.visible_rows:
                node = NODE_DATA[rw["idx"]]
                rw["lbl_ip"].config(text=node["ip"])
                rw["lbl_status"].config(text=node["status"], fg=node["color"])
                rw["led"].itemconfig(1, fill=node["color"])


class NodeDetailFrame(tk.Frame):
    def __init__(self, parent, controller):
        super().__init__(parent, bg=COLORS["bg_main"])
        self.controller = controller

        self.mux_manual = False        # mirrors register 43001, refreshed every cycle
        self.commanded_mode = None     # what the register says the node was told
        self.node_idx = 1              # 1 to 100
        self.modbus_offset = 0         # 0 to 99
        self.mode_buttons = {}

        self.setup_ui()

    def setup_ui(self):
        # --- HEADER ---
        self.header_var = tk.StringVar(value="ND-XXXX")
        top_bar = tk.Frame(self, bg=COLORS["bg_lowest"], height=64)
        top_bar.pack(fill="x", side="top")
        top_bar.pack_propagate(False)

        lbl_back = tk.Label(top_bar, text="← BACK", font=("Helvetica", 12, "bold"),
                            bg=COLORS["bg_lowest"], fg=COLORS["primary"], cursor="hand2")
        lbl_back.pack(side="left", padx=24)
        lbl_back.bind("<Button-1>", lambda e: self.controller.show_frame("Dashboard"))

        tk.Label(top_bar, textvariable=self.header_var, font=("Helvetica", 14, "bold"),
                 bg=COLORS["bg_lowest"], fg=COLORS["primary"]).pack(side="left", expand=True)

        lbl_diag = tk.Label(top_bar, text="diagnostics ⚙", font=("Helvetica", 12, "bold"),
                            bg=COLORS["bg_lowest"], fg=COLORS["primary"], cursor="hand2")
        lbl_diag.pack(side="right", padx=24)
        lbl_diag.bind("<Button-1>", lambda e: self.controller.show_frame("Diagnostics"))

        # --- CONTENT AREA ---
        content_frame = tk.Frame(self, bg=COLORS["bg_main"])
        content_frame.pack(fill="both", expand=True, padx=32, pady=24)

        self.left_col = tk.Frame(content_frame, bg=COLORS["bg_main"])
        self.left_col.pack(side="left", fill="both", expand=True, padx=(0, 20))

        self.right_col = tk.Frame(content_frame, bg=COLORS["bg_main"])
        self.right_col.pack(side="right", fill="both", expand=True, padx=(20, 0))

        self.setup_telemetry_panel()
        self.setup_control_panel()

    def setup_telemetry_panel(self):
        tk.Label(self.left_col, text="❖ HARDWARE TELEMETRY", font=("Helvetica", 14, "bold"),
                 bg=COLORS["bg_main"], fg=COLORS["text_dim"]).pack(anchor="w", pady=(0, 12))

        ip_row = tk.Frame(self.left_col, bg=COLORS["bg_panel"], height=48)
        ip_row.pack(fill="x", pady=3)
        ip_row.pack_propagate(False)
        tk.Label(ip_row, text="IP Address", font=("Helvetica", 12),
                 bg=COLORS["bg_panel"], fg=COLORS["text_main"]).pack(side="left", padx=16)
        self.lbl_ip = tk.Label(ip_row, text="---", font=("Helvetica", 12, "bold"),
                               bg=COLORS["bg_panel"], fg=COLORS["primary"])
        self.lbl_ip.pack(side="right", padx=16)

        self.lbl_conn, self.led_conn = self.create_status_row(self.left_col, "Connection Status", "---", COLORS["error"])
        self.lbl_pwr, self.led_pwr = self.create_status_row(self.left_col, "Main Power Supply", "---", COLORS["error"])

        # Sensor-indexed on the wire (current1..current4); these are the DISPLAY
        # labels. Static zones are hardwired always-on — monitored, never switched.
        self.lbl_l1, self.led_l1 = self.create_status_row(self.left_col, "LHS Arrows", "---", COLORS["text_dim"])
        self.lbl_l2, self.led_l2 = self.create_status_row(self.left_col, "RHS Arrows", "---", COLORS["text_dim"])
        self.lbl_l3, self.led_l3 = self.create_status_row(self.left_col, "Static Zone 1", "---", COLORS["text_dim"])
        self.lbl_l4, self.led_l4 = self.create_status_row(self.left_col, "Static Zone 2", "---", COLORS["text_dim"])

        # --- BATTERY: permanently greyed. Stubbed in firmware, never published. ---
        batt_container = tk.Frame(self.left_col, bg=COLORS["bg_main"])
        batt_container.pack(side="bottom", fill="x", pady=10)

        batt_left = tk.Frame(batt_container, bg=COLORS["bg_main"])
        batt_left.pack(side="left")

        tk.Label(batt_left, text="BACKUP BATTERY:", font=("Helvetica", 10, "bold"),
                 bg=COLORS["bg_main"], fg=COLORS["text_dim"]).pack(anchor="w")

        val_frame = tk.Frame(batt_left, bg=COLORS["bg_main"])
        val_frame.pack(anchor="w", pady=4)

        self.batt_icon = tk.Canvas(val_frame, width=18, height=28, bg=COLORS["bg_main"], highlightthickness=0)
        self.batt_icon.pack(side="left", padx=(0, 8))
        self.draw_battery_icon(0, COLORS["border"])

        # Never rendered as a percentage. 0% would be indistinguishable from a
        # genuinely flat battery; the firmware simply does not publish this yet.
        self.lbl_batt = tk.Label(val_frame, text="N/A", font=("Helvetica", 24, "bold"),
                                 bg=COLORS["bg_main"], fg=COLORS["border"])
        self.lbl_batt.pack(side="left")
        tk.Label(val_frame, text="  NOT FITTED", font=("Helvetica", 9),
                 bg=COLORS["bg_main"], fg=COLORS["border"]).pack(side="left")

    def draw_battery_icon(self, pct, color):
        self.batt_icon.delete("all")
        self.batt_icon.create_rectangle(5, 0, 13, 3, fill=COLORS["border"], outline="")
        self.batt_icon.create_rectangle(0, 3, 18, 28, fill="", outline=COLORS["border"], width=2)
        fill_h = max(1, int(23 * (pct / 100.0)))
        self.batt_icon.create_rectangle(2, 26 - fill_h, 16, 26, fill=color, outline="")

    def create_status_row(self, parent, label_text, status_text, color):
        row = tk.Frame(parent, bg=COLORS["bg_panel"], height=48)
        row.pack(fill="x", pady=3)
        row.pack_propagate(False)
        tk.Label(row, text=label_text, font=("Helvetica", 12), bg=COLORS["bg_panel"], fg=COLORS["text_main"]).pack(side="left", padx=16)
        led = tk.Canvas(row, width=12, height=12, bg=COLORS["bg_panel"], highlightthickness=0)
        led.create_oval(1, 1, 11, 11, fill=color, outline="")
        led.pack(side="right", padx=(10, 16), pady=18)
        lbl = tk.Label(row, text=status_text, font=("Helvetica", 10, "bold"), bg=COLORS["bg_panel"], fg=color)
        lbl.pack(side="right")
        return lbl, led

    def setup_control_panel(self):
        tk.Label(self.right_col, text="≢ MANUAL OVERRIDE CONTROL", font=("Helvetica", 14, "bold"),
                 bg=COLORS["bg_main"], fg=COLORS["text_dim"]).pack(anchor="w", pady=(0, 12))

        # --- MUX ---
        mode_frame = tk.Frame(self.right_col, bg=COLORS["bg_panel"], height=76)
        mode_frame.pack(fill="x", pady=(0, 10))
        mode_frame.pack_propagate(False)

        mode_text_frame = tk.Frame(mode_frame, bg=COLORS["bg_panel"])
        mode_text_frame.pack(side="left", padx=20, pady=12, fill="y")

        # 43001 is a SINGLE global register. Toggling it here changes the control
        # source for ALL 100 nodes, not just the one on screen. Labelled so nobody
        # reads this as a per-node switch.
        tk.Label(mode_text_frame, text="CONTROL SOURCE  ·  GLOBAL (43001)", font=("Helvetica", 9, "bold"),
                 bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).pack(anchor="w")
        self.lbl_mode_val = tk.Label(mode_text_frame, text="SCADA (AUTO)", font=("Helvetica", 16, "bold"),
                                     bg=COLORS["bg_panel"], fg=COLORS["primary"])
        self.lbl_mode_val.pack(anchor="w")

        self.toggle_canvas = tk.Canvas(mode_frame, width=64, height=32, bg=COLORS["bg_panel"], highlightthickness=0)
        self.toggle_canvas.pack(side="right", padx=20, pady=22)
        self.toggle_canvas.bind("<Button-1>", self.toggle_mux)
        self.draw_toggle(False)

        # --- COMMANDED vs ACTUAL ---
        # The node reports what it is ACTUALLY executing on the 'state' topic.
        # A mismatch means the sign is displaying something other than what every
        # screen says it is. Nothing else in the system detects this.
        cmp_frame = tk.Frame(self.right_col, bg=COLORS["bg_panel"], height=86)
        cmp_frame.pack(fill="x", pady=(0, 10))
        cmp_frame.pack_propagate(False)

        cmd_col = tk.Frame(cmp_frame, bg=COLORS["bg_panel"])
        cmd_col.pack(side="left", padx=16, pady=10, fill="y")
        tk.Label(cmd_col, text="COMMANDED", font=("Helvetica", 9, "bold"),
                 bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).pack(anchor="w")
        self.lbl_commanded = tk.Label(cmd_col, text="---", font=("Helvetica", 13, "bold"),
                                      bg=COLORS["bg_panel"], fg=COLORS["text_main"])
        self.lbl_commanded.pack(anchor="w")

        act_col = tk.Frame(cmp_frame, bg=COLORS["bg_panel"])
        act_col.pack(side="left", padx=16, pady=10, fill="y")
        tk.Label(act_col, text="ACTUALLY EXECUTING", font=("Helvetica", 9, "bold"),
                 bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).pack(anchor="w")
        self.lbl_actual = tk.Label(act_col, text="---", font=("Helvetica", 13, "bold"),
                                   bg=COLORS["bg_panel"], fg=COLORS["text_dim"])
        self.lbl_actual.pack(anchor="w")

        self.lbl_mismatch = tk.Label(cmp_frame, text="", font=("Helvetica", 10, "bold"),
                                     bg=COLORS["bg_panel"], fg=COLORS["error"], justify="right")
        self.lbl_mismatch.pack(side="right", padx=16)

        # --- MODE BUTTONS ---
        tk.Label(self.right_col, text="OPERATOR MODES", font=("Helvetica", 9, "bold"),
                 bg=COLORS["bg_main"], fg=COLORS["text_dim"]).pack(anchor="w", pady=(4, 4))
        op_row = tk.Frame(self.right_col, bg=COLORS["bg_main"])
        op_row.pack(fill="x")
        for value, label in OPERATOR_MODES:
            self.mode_buttons[value] = self.create_mode_button(op_row, value, label)

        tk.Label(self.right_col, text="TECHNICIAN MODES  ·  LATCH UNTIL CHANGED",
                 font=("Helvetica", 9, "bold"),
                 bg=COLORS["bg_main"], fg=COLORS["alert"]).pack(anchor="w", pady=(14, 4))
        tech_row = tk.Frame(self.right_col, bg=COLORS["bg_main"])
        tech_row.pack(fill="x")
        for value, label in TECHNICIAN_MODES:
            self.mode_buttons[value] = self.create_mode_button(tech_row, value, label)

        # DEVELOPER BUILD: modes 3-6 are exposed without a PIN. The PIN gate and
        # the raw-bitmask panel are the next HMI task (hmi_contract.md §3, §7).
        tk.Label(self.right_col,
                 text="Developer build — technician modes are unrestricted.\n"
                      "There is no auto-revert: a sign left solid-ON stays lit.",
                 font=("Helvetica", 9), justify="left",
                 bg=COLORS["bg_main"], fg=COLORS["text_dim"]).pack(anchor="w", pady=(8, 0))

        # --- LOCKOUT WARNING ---
        self.warn_frame = tk.Frame(self.right_col, bg="#361a00", highlightthickness=0)
        self.warn_frame.pack(side="bottom", fill="x", pady=(16, 0), ipady=10)
        warn_left = tk.Frame(self.warn_frame, bg=COLORS["alert"], width=4)
        warn_left.pack(side="left", fill="y")
        # Reworded: there is no physical switch. The on-screen toggle above IS the
        # control-source switch, and it writes the global register 43001.
        tk.Label(self.warn_frame,
                 text="⚠  SCADA (Auto) has control — mode buttons are locked.\n"
                      "Switch the control source above to HMI (Manual) to command nodes.",
                 font=("Helvetica", 10), justify="left", bg="#361a00", fg=COLORS["text_dim"]).pack(side="left", padx=16)

    def create_mode_button(self, parent, value, label):
        btn = tk.Button(parent, text=f"{value}\n{label}", font=("Helvetica", 10, "bold"),
                        bg=COLORS["bg_panel"], fg=COLORS["text_main"], relief="flat",
                        activebackground=COLORS["bg_hover"], activeforeground=COLORS["primary"],
                        height=3, command=lambda v=value: self.send_mode(v))
        btn.pack(side="left", expand=True, fill="both", padx=3)
        return btn

    def draw_toggle(self, is_on):
        self.toggle_canvas.delete("all")
        w, h, r, pad = 64, 32, 16, 4
        track_color, knob_x, knob_color = (COLORS["alert"], w-h+pad, COLORS["bg_main"]) if is_on else ("#2a2a2a", pad, COLORS["primary"])
        self.toggle_canvas.create_oval(0, 0, h, h, fill=track_color, outline="")
        self.toggle_canvas.create_oval(w-h, 0, w, h, fill=track_color, outline="")
        self.toggle_canvas.create_rectangle(r, 0, w-r, h, fill=track_color, outline="")
        self.toggle_canvas.create_oval(knob_x, pad, knob_x+h-2*pad, h-pad, fill=knob_color, outline="")

    # --- WRITE ACTIONS (MODBUS) ---
    def toggle_mux(self, event=None):
        """
        Writes the GLOBAL control-source register. The local flag is NOT the
        source of truth — refresh_data() reads 43001 back every cycle, so a
        failed write, a gateway restart, or a PLC-side change corrects the UI
        within 500 ms instead of leaving it silently disagreeing with the gateway.
        """
        target = 0 if self.mux_manual else 1
        if not self.controller.mb_write(REG_FLAG_MUX, target):
            log.warning("MUX write to %s failed; UI will resync from the register", target)
        self.apply_mux_state(target == 1)

    def apply_mux_state(self, is_manual):
        """Update every control-panel visual to match the control source."""
        self.mux_manual = is_manual
        if is_manual:
            self.lbl_mode_val.config(text="HMI (MANUAL)", fg=COLORS["alert"])
            self.draw_toggle(True)
            self.warn_frame.pack_forget()
        else:
            self.lbl_mode_val.config(text="SCADA (AUTO)", fg=COLORS["primary"])
            self.draw_toggle(False)
            self.warn_frame.pack(side="bottom", fill="x", pady=(16, 0), ipady=10)
        self.update_mode_buttons()

    def send_mode(self, value):
        """Write a mode to this node's HMI buffer (41001+offset)."""
        if not self.mux_manual:
            return      # SCADA owns the execution zone; the write would be ignored
        if self.controller.mb_write(REG_HMI_BASE + self.modbus_offset, value):
            log.info("node %d (41%03d) <- %d (%s)",
                     self.node_idx, self.modbus_offset + 1, value, mode_label(value))
            self.commanded_mode = value
            self.update_mode_buttons()

    def update_mode_buttons(self):
        for value, btn in self.mode_buttons.items():
            is_active = (value == self.commanded_mode)
            if not self.mux_manual:
                # Locked: show which mode is active, but dimmed and unclickable.
                btn.config(state="disabled",
                           bg=COLORS["bg_panel"] if not is_active else COLORS["bg_hover"],
                           fg=COLORS["primary"] if is_active else COLORS["border"],
                           disabledforeground=COLORS["primary"] if is_active else COLORS["border"])
            elif is_active:
                # Technician modes are ALERT-coloured so a non-operator mode is
                # unmistakable on a glance (hmi_contract.md §3).
                accent = COLORS["alert"] if value in TECHNICIAN_VALUES else COLORS["primary"]
                btn.config(state="normal", bg=accent, fg=COLORS["bg_lowest"])
            else:
                btn.config(state="normal", bg=COLORS["bg_panel"], fg=COLORS["text_main"])

    # --- READ ACTIONS (SYNC LOOP) ---
    def load_node(self, n_idx):
        """Initializes the view when clicking from the dashboard"""
        self.node_idx = n_idx
        self.modbus_offset = n_idx - 1 # Modbus is 0-indexed (0 to 99)

        with data_lock:
            self.header_var.set(NODE_DATA[n_idx]['id'])

        # Commanded mode is unknown until the next Modbus read fills it in.
        self.commanded_mode = None
        self.update_mode_buttons()

        self.refresh_data()

    def refresh_data(self):
        """Called every 500ms by the App sync_loop"""
        # 1. Telemetry from the local MQTT state dictionary
        with data_lock:
            data = dict(NODE_DATA[self.node_idx])

        self.lbl_ip.config(text=data['ip'])
        self.lbl_conn.config(text=data['status'], fg=data['color'])
        self.led_conn.itemconfig(1, fill=data['color'])

        pwr_color = (COLORS["success"] if data["power"] == "OK"
                     else COLORS["error"] if data["power"] == "FAIL"
                     else COLORS["text_dim"])
        self.lbl_pwr.config(text=data["power"], fg=pwr_color)
        self.led_pwr.itemconfig(1, fill=pwr_color)

        for key, lbl, led in (("c1", self.lbl_l1, self.led_l1),
                              ("c2", self.lbl_l2, self.led_l2),
                              ("c3", self.lbl_l3, self.led_l3),
                              ("c4", self.lbl_l4, self.led_l4)):
            colour = four_state_color(data[key])
            lbl.config(text=data[key], fg=colour)
            led.itemconfig(1, fill=colour)

        # 2. Control source — read the GLOBAL register back rather than trusting
        #    local state. Catches a gateway restart or a PLC-side MUX change.
        mux = self.controller.mb_read(REG_FLAG_MUX)
        if mux is not None:
            is_manual = (mux[0] == 1)
            if is_manual != self.mux_manual:
                log.info("MUX changed externally -> %s", "HMI (MANUAL)" if is_manual else "SCADA (AUTO)")
                self.apply_mux_state(is_manual)

        # 3. Commanded mode. In Manual, read the HMI buffer we write to; in Auto,
        #    read the active execution zone so the panel shows what SCADA sent.
        target_reg = (REG_HMI_BASE if self.mux_manual else REG_ACTUAL_BASE) + self.modbus_offset
        regs = self.controller.mb_read(target_reg)
        if regs is not None and regs[0] != self.commanded_mode:
            self.commanded_mode = regs[0]
            self.update_mode_buttons()

        cmd_text = mode_label(self.commanded_mode) if self.commanded_mode is not None else "---"
        self.lbl_commanded.config(text=cmd_text)

        # 4. Commanded vs actual
        actual_text, actual_color, actual_value = parse_actual_state(data["state"])
        self.lbl_actual.config(text=actual_text, fg=actual_color)

        if actual_value is None:
            # Offline, never reported, or the node cannot verify its outputs.
            # Not a mismatch — an unknown. Flagging it as divergence would alarm
            # constantly on every offline node.
            if actual_text == "CANNOT VERIFY OUTPUTS":
                self.lbl_mismatch.config(text="⚠ HARDWARE FAULT\ncannot control outputs")
            else:
                self.lbl_mismatch.config(text="")
        elif self.commanded_mode is not None and actual_value != self.commanded_mode:
            self.lbl_mismatch.config(text="⚠ MISMATCH\nnode running another mode")
        else:
            self.lbl_mismatch.config(text="")


class DiagnosticsFrame(tk.Frame):
    def __init__(self, parent, controller):
        super().__init__(parent, bg=COLORS["bg_main"])
        self.controller = controller
        self.setup_ui()

    def setup_ui(self):
        top_bar = tk.Frame(self, bg=COLORS["bg_lowest"], height=64)
        top_bar.pack(fill="x", side="top")
        top_bar.pack_propagate(False)

        lbl_back = tk.Label(top_bar, text="← BACK", font=("Helvetica", 12, "bold"), bg=COLORS["bg_lowest"], fg=COLORS["primary"], cursor="hand2")
        lbl_back.pack(side="left", padx=24)
        lbl_back.bind("<Button-1>", lambda e: self.controller.show_frame("Dashboard"))

        tk.Label(top_bar, text="SYSTEM DIAGNOSTICS", font=("Helvetica", 14, "bold"), bg=COLORS["bg_lowest"], fg=COLORS["primary"]).pack(side="left", expand=True)

        content = tk.Frame(self, bg=COLORS["bg_main"])
        content.pack(fill="both", expand=True, padx=32, pady=32)

        left_col = tk.Frame(content, bg=COLORS["bg_main"])
        left_col.pack(side="left", fill="both", expand=True, padx=(0, 20))

        right_col = tk.Frame(content, bg=COLORS["bg_main"])
        right_col.pack(side="right", fill="both", expand=True, padx=(20, 0))

        tk.Label(left_col, text="❖ GATEWAY STATUS", font=("Helvetica", 14, "bold"), bg=COLORS["bg_main"], fg=COLORS["text_dim"]).pack(anchor="w", pady=(0, 16))

        self.lbl_mqtt_stat = self.create_info_row(left_col, "MQTT Broker (Paho)", "---", COLORS["alert"])
        self.lbl_mb_stat = self.create_info_row(left_col, "Modbus TCP Server", "---", COLORS["alert"])
        self.create_info_row(left_col, "Master Gateway IP", MODBUS_IP, COLORS["primary"])

        tk.Label(left_col, text="❖ NETWORK HEALTH", font=("Helvetica", 14, "bold"), bg=COLORS["bg_main"], fg=COLORS["text_dim"]).pack(anchor="w", pady=(32, 16))

        count_frame = tk.Frame(left_col, bg=COLORS["bg_main"])
        count_frame.pack(fill="x")

        self.lbl_online = self.create_counter_box(count_frame, "ONLINE NODES", "0", COLORS["success"])
        self.lbl_offline = self.create_counter_box(count_frame, "OFFLINE / FAULT", "100", COLORS["error"])

        tk.Label(right_col, text="≢ MASTER ACTIONS", font=("Helvetica", 14, "bold"), bg=COLORS["bg_main"], fg=COLORS["text_dim"]).pack(anchor="w", pady=(0, 16))

        ping_frame = tk.Frame(right_col, bg=COLORS["bg_panel"], highlightbackground=COLORS["border"], highlightthickness=1)
        ping_frame.pack(fill="x", pady=8, ipady=16)

        tk.Label(ping_frame, text="TELEMETRY REFRESH", font=("Helvetica", 12, "bold"), bg=COLORS["bg_panel"], fg=COLORS["text_main"]).pack(pady=(10, 5))
        # CORRECTED: the previous text claimed this "resets 5-minute fail-safe
        # timers". No such timer exists anywhere in the system. Nodes HOLD their
        # last command on network loss — there is no forced-ON and no forced-OFF.
        tk.Label(ping_frame,
                 text="Asks every node to re-publish its full telemetry.\n"
                      "This is a refresh, not a fail-safe: nodes hold their\n"
                      "last command when the network drops.",
                 font=("Helvetica", 10), justify="center", bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).pack(pady=(0, 16))

        self.btn_ping = tk.Button(ping_frame, text="BROADCAST PING", font=("Helvetica", 14, "bold"),
                                  bg=COLORS["primary"], fg=COLORS["bg_lowest"], relief="flat",
                                  width=20, height=2, activebackground=COLORS["bg_hover"],
                                  command=self.send_ping)
        self.btn_ping.pack()

    def create_info_row(self, parent, label_text, value_text, color):
        row = tk.Frame(parent, bg=COLORS["bg_panel"], height=52)
        row.pack(fill="x", pady=4)
        row.pack_propagate(False)
        tk.Label(row, text=label_text, font=("Helvetica", 12), bg=COLORS["bg_panel"], fg=COLORS["text_main"]).pack(side="left", padx=16)
        lbl = tk.Label(row, text=value_text, font=("Helvetica", 12, "bold"), bg=COLORS["bg_panel"], fg=color)
        lbl.pack(side="right", padx=16)
        return lbl

    def create_counter_box(self, parent, label_text, value_text, color):
        box = tk.Frame(parent, bg=COLORS["bg_panel"], highlightbackground=COLORS["border"], highlightthickness=1)
        box.pack(side="left", expand=True, fill="both", padx=4)
        lbl_val = tk.Label(box, text=value_text, font=("Helvetica", 36, "bold"), bg=COLORS["bg_panel"], fg=color)
        lbl_val.pack(pady=(16, 0))
        tk.Label(box, text=label_text, font=("Helvetica", 10, "bold"), bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).pack(pady=(0, 16))
        return lbl_val

    def send_ping(self):
        if self.controller.mqtt_client:
            self.controller.mqtt_client.publish("metro/signage/scan", "PING")
            self.btn_ping.config(text="PING SENT!", bg=COLORS["success"])
            self.after(2000, lambda: self.btn_ping.config(text="BROADCAST PING", bg=COLORS["primary"]))

    def refresh_data(self):
        """Called every 500ms by the App sync_loop"""
        # Update Service Connect Statuses
        if self.controller.mqtt_client and self.controller.mqtt_client.is_connected():
            self.lbl_mqtt_stat.config(text="CONNECTED", fg=COLORS["success"])
        else:
            self.lbl_mqtt_stat.config(text="NOT CONNECTED", fg=COLORS["error"])

        if self.controller.mb_client and self.controller.mb_client.is_socket_open():
            self.lbl_mb_stat.config(text=f"RUNNING (Port {MODBUS_PORT})", fg=COLORS["success"])
        else:
            self.lbl_mb_stat.config(text="NOT CONNECTED", fg=COLORS["error"])

        # Update Node Health Counts
        with data_lock:
            online_count = sum(1 for d in NODE_DATA.values() if d["status"] == "ONLINE")
        offline_count = MAX_NODES - online_count

        self.lbl_online.config(text=str(online_count))
        self.lbl_offline.config(text=str(offline_count))


if __name__ == "__main__":
    app = HMIApp()
    app.mainloop()
