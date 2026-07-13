import tkinter as tk
from tkinter import font
import threading
import json

# Network Libraries (Wrapped in try-except so UI still opens if missing during dev)
try:
    import paho.mqtt.client as mqtt
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

REG_ACTUAL_BASE = 0      # 40001+ Output State
REG_HMI_BASE = 1000      # 41001+ HMI Command Zone
REG_FLAG_MUX = 3000      # 43001 MUX (0=SCADA, 1=HMI)
MAX_NODES = 100

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
        "c1": "---",
        "c2": "---",
        "batt": 0
    }

# --- MQTT BACKGROUND LISTENER ---
def on_mqtt_message(client, userdata, msg):
    """Listens to ESP32s and safely updates the UI's central dictionary."""
    try:
        parts = msg.topic.split('/')
        if len(parts) == 5 and parts[0] == "metro":
            reg = int(parts[3])
            metric = parts[4]
            payload = msg.payload.decode()
            
            node_idx = reg - 40000
            if 1 <= node_idx <= MAX_NODES:
                with data_lock:
                    if metric == "status":
                        if payload.startswith("ONLINE:"):
                            NODE_DATA[node_idx]["status"] = "ONLINE"
                            NODE_DATA[node_idx]["color"] = COLORS["success"]
                            NODE_DATA[node_idx]["ip"] = payload.split(":")[1]
                        else:
                            NODE_DATA[node_idx]["status"] = "OFFLINE"
                            NODE_DATA[node_idx]["color"] = COLORS["error"]
                            NODE_DATA[node_idx]["ip"] = "---"
                    elif metric == "power":
                        NODE_DATA[node_idx]["power"] = payload
                    elif metric == "current1":
                        NODE_DATA[node_idx]["c1"] = payload
                    elif metric == "current2":
                        NODE_DATA[node_idx]["c2"] = payload
                    elif metric == "battery_pct":
                        try:
                            NODE_DATA[node_idx]["batt"] = int(payload)
                        except ValueError:
                            pass
    except Exception as e:
        pass


class VirtualKeyboard(tk.Toplevel):
    def __init__(self, parent, target_var):
        super().__init__(parent)
        self.title("Virtual Keyboard")
        self.geometry("900x380+62+180")
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
        self.geometry("1024x600")
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
            # Modbus
            self.mb_client = ModbusTcpClient(MODBUS_IP, port=MODBUS_PORT)
            try:
                self.mb_client.connect()
            except Exception as e:
                print(f"Modbus Connect Error: {e}")
                
            # MQTT
            self.mqtt_client = mqtt.Client()
            self.mqtt_client.on_message = on_mqtt_message
            try:
                self.mqtt_client.connect(MQTT_BROKER, MQTT_PORT, 60)
                self.mqtt_client.subscribe("metro/signage/register/+/+")
                self.mqtt_client.loop_start()
            except Exception as e:
                print(f"MQTT Connect Error: {e}")
        
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
            active_frame.refresh_data()
        self.after(500, self.sync_loop)


class DashboardFrame(tk.Frame):
    def __init__(self, parent, controller):
        super().__init__(parent, bg=COLORS["bg_main"])
        self.controller = controller
        self.current_page = 0
        self.nodes_per_page = 8
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
        header_frame = tk.Frame(self, bg=COLORS["bg_panel"], height=40)
        header_frame.pack(fill="x", padx=24, pady=(24, 0))
        header_frame.pack_propagate(False)
        
        tk.Label(header_frame, text="NODE NUMBER", font=self.controller.font_body, bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).place(x=20, y=10)
        tk.Label(header_frame, text="IP ADDRESS", font=self.controller.font_body, bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).place(x=300, y=10)
        tk.Label(header_frame, text="CONNECTION STATUS", font=self.controller.font_body, bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).place(x=700, y=10)

        # --- ROWS CONTAINER ---
        self.rows_container = tk.Frame(self, bg=COLORS["bg_main"])
        self.rows_container.pack(fill="both", expand=True, padx=24, pady=8)
        
        # --- BOTTOM BAR ---
        bottom_bar = tk.Frame(self, bg=COLORS["bg_main"], height=90)
        bottom_bar.pack(fill="x", side="bottom")
        bottom_bar.pack_propagate(False)
        
        self.lbl_page_info = tk.Label(bottom_bar, text="1 - 8 / 100", font=self.controller.font_mono, bg=COLORS["bg_main"], fg=COLORS["text_dim"])
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
                lbl_id.place(x=20, y=10)
                lbl_id.bind("<Button-1>", cb)
                
                lbl_ip = tk.Label(row, text=node["ip"], font=self.controller.font_mono, bg=bg_color, fg=COLORS["primary"])
                lbl_ip.place(x=300, y=10)
                lbl_ip.bind("<Button-1>", cb)
                
                led = tk.Canvas(row, width=16, height=16, bg=bg_color, highlightthickness=0)
                led.create_oval(2, 2, 14, 14, fill=node["color"], outline="")
                led.place(x=700, y=16)
                led.bind("<Button-1>", cb)
                
                lbl_status = tk.Label(row, text=node["status"], font=self.controller.font_mono, bg=bg_color, fg=node["color"])
                lbl_status.place(x=725, y=10)
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
        
        self.mux_manual = False
        self.r1_on = False
        self.r2_on = False
        self.node_idx = 1 # 1 to 100
        self.modbus_offset = 0 # 0 to 99
        
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
        content_frame.pack(fill="both", expand=True, padx=32, pady=32)
        
        self.left_col = tk.Frame(content_frame, bg=COLORS["bg_main"])
        self.left_col.pack(side="left", fill="both", expand=True, padx=(0, 20))
        
        self.right_col = tk.Frame(content_frame, bg=COLORS["bg_main"])
        self.right_col.pack(side="right", fill="both", expand=True, padx=(20, 0))

        self.setup_telemetry_panel()
        self.setup_control_panel()

    def setup_telemetry_panel(self):
        tk.Label(self.left_col, text="❖ HARDWARE TELEMETRY", font=("Helvetica", 14, "bold"), 
                 bg=COLORS["bg_main"], fg=COLORS["text_dim"]).pack(anchor="w", pady=(0, 16))
        
        ip_row = tk.Frame(self.left_col, bg=COLORS["bg_panel"], height=52)
        ip_row.pack(fill="x", pady=4)
        ip_row.pack_propagate(False)
        tk.Label(ip_row, text="IP Address", font=("Helvetica", 12), 
                 bg=COLORS["bg_panel"], fg=COLORS["text_main"]).pack(side="left", padx=16)
        self.lbl_ip = tk.Label(ip_row, text="---", font=("Helvetica", 12, "bold"), 
                               bg=COLORS["bg_panel"], fg=COLORS["primary"])
        self.lbl_ip.pack(side="right", padx=16)
        
        self.lbl_conn, self.led_conn = self.create_status_row(self.left_col, "Connection Status", "---", COLORS["error"])
        self.lbl_pwr, self.led_pwr = self.create_status_row(self.left_col, "Main Power Supply", "---", COLORS["error"])
        self.lbl_l1, self.led_l1 = self.create_status_row(self.left_col, "Load 1 Current Sensor", "---", COLORS["error"])
        self.lbl_l2, self.led_l2 = self.create_status_row(self.left_col, "Load 2 Current Sensor", "---", COLORS["error"])
        
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
        self.draw_battery_icon(0, COLORS["error"])
        
        self.lbl_batt = tk.Label(val_frame, text="0%", font=("Helvetica", 28, "bold"), 
                                 bg=COLORS["bg_main"], fg=COLORS["error"])
        self.lbl_batt.pack(side="left")

    def draw_battery_icon(self, pct, color):
        self.batt_icon.delete("all")
        self.batt_icon.create_rectangle(5, 0, 13, 3, fill=COLORS["text_dim"], outline="")
        self.batt_icon.create_rectangle(0, 3, 18, 28, fill="", outline=COLORS["text_dim"], width=2)
        fill_h = max(1, int(23 * (pct / 100.0)))
        self.batt_icon.create_rectangle(2, 26 - fill_h, 16, 26, fill=color, outline="")

    def create_status_row(self, parent, label_text, status_text, color):
        row = tk.Frame(parent, bg=COLORS["bg_panel"], height=52)
        row.pack(fill="x", pady=4)
        row.pack_propagate(False)
        tk.Label(row, text=label_text, font=("Helvetica", 12), bg=COLORS["bg_panel"], fg=COLORS["text_main"]).pack(side="left", padx=16)
        led = tk.Canvas(row, width=12, height=12, bg=COLORS["bg_panel"], highlightthickness=0)
        led.create_oval(1, 1, 11, 11, fill=color, outline="")
        led.pack(side="right", padx=(10, 16), pady=20)
        lbl = tk.Label(row, text=status_text, font=("Helvetica", 10, "bold"), bg=COLORS["bg_panel"], fg=color)
        lbl.pack(side="right")
        return lbl, led

    def setup_control_panel(self):
        tk.Label(self.right_col, text="≢ MANUAL OVERRIDE CONTROL", font=("Helvetica", 14, "bold"), 
                 bg=COLORS["bg_main"], fg=COLORS["text_dim"]).pack(anchor="w", pady=(0, 16))
        
        mode_frame = tk.Frame(self.right_col, bg=COLORS["bg_panel"], height=80)
        mode_frame.pack(fill="x", pady=(0, 16))
        mode_frame.pack_propagate(False)
        
        mode_text_frame = tk.Frame(mode_frame, bg=COLORS["bg_panel"])
        mode_text_frame.pack(side="left", padx=20, pady=16, fill="y")
        
        tk.Label(mode_text_frame, text="CURRENT MODE", font=("Helvetica", 10, "bold"), bg=COLORS["bg_panel"], fg=COLORS["text_dim"]).pack(anchor="w")
        self.lbl_mode_val = tk.Label(mode_text_frame, text="SCADA (AUTO)", font=("Helvetica", 16, "bold"), bg=COLORS["bg_panel"], fg=COLORS["primary"])
        self.lbl_mode_val.pack(anchor="w")
        
        self.toggle_canvas = tk.Canvas(mode_frame, width=64, height=32, bg=COLORS["bg_panel"], highlightthickness=0)
        self.toggle_canvas.pack(side="right", padx=20, pady=24)
        self.toggle_canvas.bind("<Button-1>", self.toggle_mux)
        self.draw_toggle(False)
        
        self.r1_frame = tk.Frame(self.right_col, bg=COLORS["bg_main"], highlightbackground=COLORS["border"], highlightthickness=1, height=80)
        self.r1_frame.pack(fill="x", pady=8)
        self.r1_frame.pack_propagate(False)
        self.r1_lbl = tk.Label(self.r1_frame, text="RELAY 1: OFF", font=("Helvetica", 14, "bold"), bg=COLORS["bg_main"], fg=COLORS["border"])
        self.r1_lbl.place(relx=0.5, rely=0.5, anchor="center")
        self.r1_frame.bind("<Button-1>", lambda e: self.toggle_relay(1))
        self.r1_lbl.bind("<Button-1>", lambda e: self.toggle_relay(1))
        
        self.r2_frame = tk.Frame(self.right_col, bg=COLORS["bg_main"], highlightbackground=COLORS["border"], highlightthickness=1, height=80)
        self.r2_frame.pack(fill="x", pady=8)
        self.r2_frame.pack_propagate(False)
        self.r2_lbl = tk.Label(self.r2_frame, text="RELAY 2: OFF", font=("Helvetica", 14, "bold"), bg=COLORS["bg_main"], fg=COLORS["border"])
        self.r2_lbl.place(relx=0.5, rely=0.5, anchor="center")
        self.r2_frame.bind("<Button-1>", lambda e: self.toggle_relay(2))
        self.r2_lbl.bind("<Button-1>", lambda e: self.toggle_relay(2))
        
        self.warn_frame = tk.Frame(self.right_col, bg="#361a00", highlightthickness=0)
        self.warn_frame.pack(side="bottom", fill="x", pady=(20, 0), ipady=12)
        warn_left = tk.Frame(self.warn_frame, bg=COLORS["alert"], width=4)
        warn_left.pack(side="left", fill="y")
        tk.Label(self.warn_frame, text="⚠️  Overrides are locked while in SCADA Auto mode. Engage local physical\nswitch to enable manual HMI control.", 
                 font=("Helvetica", 10), justify="left", bg="#361a00", fg=COLORS["text_dim"]).pack(side="left", padx=16)

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
        self.mux_manual = not self.mux_manual
        
        if self.controller.mb_client:
            try:
                # Target: Modbus Register 43001 (Address 3000)
                self.controller.mb_client.write_register(REG_FLAG_MUX, 1 if self.mux_manual else 0)
            except Exception as e:
                print(f"Modbus Write MUX Error: {e}")
                
        if self.mux_manual:
            self.lbl_mode_val.config(text="HMI (MANUAL)", fg=COLORS["alert"])
            self.draw_toggle(True)
            self.warn_frame.pack_forget()
        else:
            self.lbl_mode_val.config(text="SCADA (AUTO)", fg=COLORS["primary"])
            self.draw_toggle(False)
            self.warn_frame.pack(side="bottom", fill="x", pady=(20, 0), ipady=12)
            
        self.update_relay_buttons()

    def toggle_relay(self, relay_num):
        if not self.mux_manual: return

        if relay_num == 1: self.r1_on = not self.r1_on
        else: self.r2_on = not self.r2_on
        
        self.update_relay_buttons()
        
        if self.controller.mb_client:
            cmd = 0
            if self.r1_on and not self.r2_on: cmd = 1
            if not self.r1_on and self.r2_on: cmd = 2
            if self.r1_on and self.r2_on: cmd = 3
            try:
                # Target: Modbus Register 41001+ (Address 1000 + Offset)
                self.controller.mb_client.write_register(REG_HMI_BASE + self.modbus_offset, cmd)
            except Exception as e:
                print(f"Modbus Write Relay Error: {e}")

    def update_relay_buttons(self):
        if not self.mux_manual: 
            # SCADA MODE: Show actual state, but locked (dimmer visuals)
            if self.r1_on:
                self.r1_frame.config(bg=COLORS["bg_main"], highlightbackground=COLORS["primary"])
                self.r1_lbl.config(text="RELAY 1: ON", fg=COLORS["primary"], bg=COLORS["bg_main"])
            else:
                self.r1_frame.config(bg=COLORS["bg_main"], highlightbackground=COLORS["border"])
                self.r1_lbl.config(text="RELAY 1: OFF", fg=COLORS["border"], bg=COLORS["bg_main"])
                
            if self.r2_on:
                self.r2_frame.config(bg=COLORS["bg_main"], highlightbackground=COLORS["primary"])
                self.r2_lbl.config(text="RELAY 2: ON", fg=COLORS["primary"], bg=COLORS["bg_main"])
            else:
                self.r2_frame.config(bg=COLORS["bg_main"], highlightbackground=COLORS["border"])
                self.r2_lbl.config(text="RELAY 2: OFF", fg=COLORS["border"], bg=COLORS["bg_main"])
            return
            
        # HMI MANUAL MODE: Show bold, active clickable colors
        if self.r1_on:
            self.r1_frame.config(bg=COLORS["primary"], highlightbackground=COLORS["primary"])
            self.r1_lbl.config(text="RELAY 1: ON", fg=COLORS["bg_lowest"], bg=COLORS["primary"])
        else:
            self.r1_frame.config(bg=COLORS["bg_panel"], highlightbackground=COLORS["border"])
            self.r1_lbl.config(text="RELAY 1: OFF", fg=COLORS["text_main"], bg=COLORS["bg_panel"])
            
        if self.r2_on:
            self.r2_frame.config(bg=COLORS["primary"], highlightbackground=COLORS["primary"])
            self.r2_lbl.config(text="RELAY 2: ON", fg=COLORS["bg_lowest"], bg=COLORS["primary"])
        else:
            self.r2_frame.config(bg=COLORS["bg_panel"], highlightbackground=COLORS["border"])
            self.r2_lbl.config(text="RELAY 2: OFF", fg=COLORS["text_main"], bg=COLORS["bg_panel"])

    # --- READ ACTIONS (SYNC LOOP) ---
    def load_node(self, n_idx):
        """Initializes the view when clicking from the dashboard"""
        self.node_idx = n_idx
        self.modbus_offset = n_idx - 1 # Modbus is 0-indexed (0 to 99)
        
        with data_lock:
            self.header_var.set(NODE_DATA[n_idx]['id'])
            
        # MUX Mode is global. We DO NOT reset it here. 
        # We just update the visuals to match the existing state.
        if self.mux_manual:
            self.lbl_mode_val.config(text="HMI (MANUAL)", fg=COLORS["alert"])
            self.draw_toggle(True)
            self.warn_frame.pack_forget()
        else:
            self.lbl_mode_val.config(text="SCADA (AUTO)", fg=COLORS["primary"])
            self.draw_toggle(False)
            self.warn_frame.pack(side="bottom", fill="x", pady=(20, 0), ipady=12)
        
        # Reset local relay state until the next sync_loop Modbus read
        self.r1_on = False
        self.r2_on = False
        self.update_relay_buttons()
        
        self.refresh_data()

    def refresh_data(self):
        """Called every 500ms by the App sync_loop"""
        # 1. Update Telemetry from local MQTT State Dictionary
        with data_lock:
            data = NODE_DATA[self.node_idx]
            
            self.lbl_ip.config(text=data['ip'])
            self.lbl_conn.config(text=data['status'], fg=data['color'])
            self.led_conn.itemconfig(1, fill=data['color'])
            
            pwr_color = COLORS["success"] if data["power"] == "OK" else COLORS["error"] if data["power"] == "FAIL" else COLORS["text_dim"]
            self.lbl_pwr.config(text=data["power"], fg=pwr_color)
            self.led_pwr.itemconfig(1, fill=pwr_color)
            
            c1_color = COLORS["success"] if data["c1"] == "OK" else COLORS["error"] if data["c1"] == "FAIL" else COLORS["text_dim"]
            self.lbl_l1.config(text=data["c1"], fg=c1_color)
            self.led_l1.itemconfig(1, fill=c1_color)
            
            c2_color = COLORS["success"] if data["c2"] == "OK" else COLORS["error"] if data["c2"] == "FAIL" else COLORS["text_dim"]
            self.lbl_l2.config(text=data["c2"], fg=c2_color)
            self.led_l2.itemconfig(1, fill=c2_color)
            
            batt = data['batt']
            b_color = COLORS["success"] if batt >= 80 else COLORS["alert"] if batt >= 30 else COLORS["error"]
            self.lbl_batt.config(text=f"{batt}%", fg=b_color)
            self.draw_battery_icon(batt, b_color)

        # 2. Update physical Relay State visually directly from Modbus
        if self.controller.mb_client:
            try:
                # If Auto, poll the actual output. If Manual, poll the HMI memory zone to avoid flicker
                target_reg = (REG_HMI_BASE if self.mux_manual else REG_ACTUAL_BASE) + self.modbus_offset
                rr = self.controller.mb_client.read_holding_registers(target_reg, 1)
                
                if not rr.isError():
                    val = rr.registers[0]
                    self.r1_on = (val == 1 or val == 3)
                    self.r2_on = (val == 2 or val == 3)
                    self.update_relay_buttons()
            except Exception as e:
                pass


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
        
        tk.Label(ping_frame, text="NETWORK HEARTBEAT", font=("Helvetica", 12, "bold"), bg=COLORS["bg_panel"], fg=COLORS["text_main"]).pack(pady=(10, 5))
        tk.Label(ping_frame, text="Broadcast PING to reset 5-minute\nfail-safe timers across the fiber ring.", 
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
        if self.controller.mqtt_client and getattr(self.controller.mqtt_client, 'is_connected', lambda: False)():
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