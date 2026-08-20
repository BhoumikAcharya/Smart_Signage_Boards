# Node Calibration Utility

> Windows desktop application for commissioning and calibrating ESP32 edge controllers
> in a metro smart-signage system.
>
> **I2ST Technologies Pvt. Ltd.** · .NET 10 · Windows Forms · SQL Server LocalDB

---

## Table of Contents

1. [What this is](#1-what-this-is)
2. [My role in the project](#2-my-role-in-the-project)
3. [Features](#3-features)
4. [Architecture](#4-architecture)
5. [Database schema](#5-database-schema)
6. [Device communication](#6-device-communication)
7. [Project structure](#7-project-structure)
8. [Getting started](#8-getting-started)
9. [Configuration](#9-configuration)
10. [Engineering notes](#10-engineering-notes)
11. [Roadmap](#11-roadmap)

---

## 1. What this is

A metro station's smart-signage system runs on a network of **ESP32-based edge
controllers** — one per signage panel — distributed across four routes of up to 100
panels each. Each controller drives relays, monitors two current-sensing channels and
a battery, and publishes its state over MQTT to a Raspberry Pi that bridges it into
Modbus TCP for the station SCADA system.

Before a controller can join that network it has to be told **who it is**: its route,
its panel location, its panel serial number, the Modbus holding register it owns, and
its static IP address. It also needs per-device **calibration constants** that convert
raw ADC counts into amps and volts.

This application is the tool that does both. It is what a technician carries to a
panel with a laptop and a USB cable.

**Node Calibration Utility** provides:

- a managed record of every panel position and the controller installed at it,
- validated data entry with database-enforced uniqueness,
- configuration deployment over **USB serial** or **Ethernet (OTA)**, confirmed by
  the device before it is recorded as done,
- administrator-gated calibration with range checking,
- role-based access control over a SQL Server database.

---

## 2. My role in the project

The smart-signage system as a whole spans embedded firmware, a Python
MQTT-to-Modbus bridge, an ERPS fibre-optic ring across three managed switches, and
this desktop application.

**My contribution is this Windows Forms application** — the desktop tool covered by
this repository. I designed and built the UI, the data layer, the device
communication and the access control.

The firmware, the Raspberry Pi bridge and the network ring were built by other
members of the team. They are described here only where the desktop application has
to agree with them — the JSON contract in
[§6](#6-device-communication) and the register map in the build context document are
interface definitions I had to implement against, not code I wrote.

---

## 3. Features

### 3.1 Node Management
- Route-filtered grid of every installed controller, joined to its panel position.
- Add, edit and delete with full validation: IPv4 format, positive node numbers, and
  uniqueness on node number, IP address and panel serial number.
- Uncalibrated nodes are highlighted, so outstanding commissioning work is visible at
  a glance rather than requiring a column-by-column read.
- Double-click a row to edit.
- Records are re-read before an edit opens, so a node changed or removed from another
  workstation is detected instead of silently overwritten.

### 3.2 Area (Panel Position) Management
- All 100 panel positions for a selected route, from a table pre-seeded with every
  route/panel combination.
- Panel serial number and description are editable; route, panel location and holding
  register are read-only, because the firmware and the Modbus bridge both key off the
  register mapping.
- Changing a serial number requires explicit confirmation — it re-points the node
  foreign key and must match the number physically stencilled on the panel.

### 3.3 Configuration Deployment
- **Two transports**: USB serial for bench and on-panel work, HTTP OTA for
  controllers already on the network.
- **Acknowledgement required.** A deployment is only recorded as successful once the
  controller confirms it. Writing bytes to a COM port proves nothing about the device
  having received or accepted them, so the tool waits for a reply and classifies it.
  If the device stays silent or rejects the payload, the node is explicitly *not*
  marked as calibrated and the operator is told why.
- Live serial monitor with the device's own output, including its reply to the
  deployment.
- Pre-deployment confirmation stating the target, the transport, and whether
  calibration will be overwritten or preserved — the controller restarts on accepting
  a payload.
- Reset-to-defaults command that clears the controller's stored preferences.

### 3.4 Calibration Control
- Administrator-only. Calibration changes the meaning of every reading a node
  publishes, so it is deliberately restricted.
- Current values are pre-loaded from the database, so an operator adjusts from what
  the device is actually running rather than typing into empty boxes.
- Range-checked before transmission: sensitivity is bounded to the physically
  plausible band for the sensors in use, so a typo or a wrong unit is caught before it
  silently corrupts every subsequent measurement.
- Calibration keys are **omitted** from the payload unless supplied, because the
  firmware preserves any constant whose key is absent. Sending zeros would wipe the
  bench calibration.

### 3.5 Authentication and Access Control
- Sign-in against the SQL Server `Users` table.
- **PBKDF2-HMAC-SHA256** password hashing with a per-password random salt and 600,000
  iterations, verified in fixed time.
- Backward compatible: legacy unsalted SHA-256 rows still authenticate and are
  transparently upgraded to PBKDF2 on the next successful sign-in. No user has to
  reset anything.
- Protected screens are genuinely gated — node management, area management, deployment
  and diagnostics all require a signed-in operator.
- Sign-in attempts are throttled after repeated failures, and the same message is
  returned whether or not the username exists, so the screen cannot be used to
  enumerate accounts.

### 3.6 Modbus Diagnostics
- Reads each node's 6-register diagnostic block — status, power, both current
  channels, battery percentage and relay state — through the Raspberry Pi gateway.
- Writes to the MUX, manual relay and SCADA register ranges.
- Connect timeout applied explicitly, because `TcpClient` has none of its own and an
  unreachable gateway would otherwise block for the OS default.

### 3.7 Settings and Network Configuration
- Technician contact details and maintenance-notification preferences.
- Ethernet and Wi-Fi parameters, with the Wi-Fi group shown only when enabled and the
  pre-shared key masked.
- Every populated address field is validated as a genuine dotted-quad IPv4 — a
  controller that comes up on the wrong subnet has to be recovered over USB on site.
- Persisted atomically to per-user application data, so an interrupted save cannot
  leave a file that fails to parse on next launch.

### 3.8 Operational Robustness
- **Responsive UI.** Every database and device call is asynchronous, with
  cancellation, so the window never freezes and a query returning after a screen
  closes cannot touch a disposed control.
- **Diagnostic logging.** A dated rolling log under `%LOCALAPPDATA%`, reachable from
  the Data Transfer menu, recording every repository failure, deployment attempt and
  device reply.
- **Survivable errors.** A UI-thread fault is reported and recovered from rather than
  terminating the process and discarding the operator's other work.
- **Start-up health check.** An unreachable database or missing tables is reported
  immediately, not as a stack trace on first use.
- **Single instance.** Two copies would compete for the same COM port and could send
  conflicting configuration to one controller.
- **Externalised configuration.** Connection string, gateway address, baud rate,
  timeouts and route counts all live in `appsettings.json`; retargeting a deployment
  needs no rebuild.

---

## 4. Architecture

```
┌───────────────────────────────────────────────────────────────┐
│                    Presentation                               │
│   MainForm shell · 4 dialogs · 9 UserControl screens          │
│   UserDialog (uniform prompts) · BusyScope (busy state)       │
└───────────────────────────┬───────────────────────────────────┘
                            │
┌───────────────────────────▼───────────────────────────────────┐
│                    Application                                │
│   UserSession (identity, roles)   AppConfig (settings)        │
│   AppLogger (diagnostics)                                     │
└──────────┬────────────────────────────────────┬───────────────┘
           │                                    │
┌──────────▼──────────────┐      ┌──────────────▼───────────────┐
│         Data            │      │          Services            │
│  Db (connections,       │      │  DeviceConfigurationService  │
│      error translation) │      │    serial + OTA, ack-gated   │
│  AuthRepository         │      │  ModbusDiagnosticsService    │
│  NodeRepository         │      │    IDisposable, timeouts     │
│  AreaRepository         │      │  SettingsService             │
│  + PasswordHasher       │      │    atomic JSON writes        │
└──────────┬──────────────┘      └──────────────┬───────────────┘
           │                                    │
┌──────────▼──────────────┐      ┌──────────────▼───────────────┐
│  SQL Server LocalDB     │      │  ESP32 controllers           │
│  NodeManagementDB       │      │  USB serial · HTTP           │
│  Users · Areas · Nodes  │      │  Modbus TCP via Pi gateway   │
└─────────────────────────┘      └──────────────────────────────┘
```

**Deployment flow** — the path that matters most:

```
Operator selects an uncalibrated node
        ↓
Optional calibration entered (administrator only) → range-checked
        ↓
Payload built · NodeNumber excluded · calibration keys omitted if absent
        ↓
Operator confirms target, transport and calibration impact
        ↓
        ├── USB serial ──→ write · wait for device reply · classify
        └── HTTP OTA ────→ POST /config · HTTP status is the reply
        ↓
   Acknowledged?
        ├── no  → report unconfirmed · database unchanged · reply echoed
        └── yes → store calibration · Calibration = 1 · refresh list
```

---

## 5. Database schema

`NodeManagementDB` on SQL Server LocalDB.

### `Users`
| Column | Type | Constraints |
|---|---|---|
| Id | INT | PK, IDENTITY |
| Username | NVARCHAR(50) | UNIQUE, NOT NULL |
| PasswordHash | NVARCHAR(255) | NOT NULL |
| FullName | NVARCHAR(100) | NOT NULL |
| CreatedAt | DATETIME | DEFAULT GETDATE() |

### `Areas` — 400 pre-seeded rows (4 routes × 100 panels)
| Column | Type | Constraints |
|---|---|---|
| Id | INT | PK, IDENTITY |
| Route | INT | NOT NULL |
| PanelLocation | INT | NOT NULL |
| HoldingRegister | INT | UNIQUE, NOT NULL |
| PanelSerialNumber | INT | UNIQUE, NOT NULL |
| Description | NVARCHAR(255) | NULL |

Plus `UNIQUE(Route, PanelLocation)`.
`HoldingRegister = 40000 + (Route-1)×100 + PanelLocation`, assigned at seed time and
immutable thereafter.

### `Nodes`
| Column | Type | Default |
|---|---|---|
| Id | INT | PK, IDENTITY |
| NodeNumber | INT | UNIQUE, NOT NULL |
| PanelSerialNumber | INT | UNIQUE, NOT NULL, FK → `Areas` |
| LocalIPAddress | NVARCHAR(15) | UNIQUE, NOT NULL |
| Zerovolt_CS1 / _CS2 | FLOAT | 2.40 |
| Sensitivity_CS1 / _CS2 | FLOAT | 0.105 |
| Threshold_CS1 | FLOAT | 0.100 |
| Threshold_CS2 | FLOAT | 0.120 |
| BatteryCalibration | FLOAT | 0.0 |
| BatterySagCompensation | FLOAT | 0.0 |
| PSUThreshold | INT | 1800 |
| Calibration | BIT | 0 |

`Calibration` is set to 1 only after a controller has confirmed its configuration.

---

## 6. Device communication

### Configuration payload

```json
{
  "Route": 2,
  "PanelLocation": 5,
  "PanelSerialNumber": 40005,
  "HoldingRegister": 40005,
  "Description": "Platform 3, Column B",
  "LocalIP": "192.168.1.105"
}
```

Two details are load-bearing:

- **`NodeNumber` is deliberately absent.** The controller derives its node identity
  from the holding register; a second source of truth could disagree with the first.
- **Calibration keys appear only when supplied.** The firmware overwrites a stored
  constant only when its key is present, so omission preserves the bench calibration.
  With calibration, three keys are added: `Sensitivity_CS1`, `Sensitivity_CS2`,
  `BatteryCalibration`.

Reset: `{"ResetConfig": true}`. The controller restarts after accepting either payload.

### Transports

| Transport | Details |
|---|---|
| USB serial | 115200 8-N-1, newline-terminated JSON, configurable acknowledgement timeout |
| HTTP OTA | `POST http://<device-ip>/config`, 5-second timeout, status code is the acknowledgement |
| Modbus TCP | via the Raspberry Pi gateway on port 502, explicit connect timeout |

### Diagnostic register map

Zero-based addresses. Node *N* occupies six registers from `5000 + (N-1)×6`:

| Offset | Meaning |
|---|---|
| +0 | Status (1 = ONLINE) |
| +1 | Power (1 = OK) |
| +2 | Current channel 1 (1 = OK) |
| +3 | Current channel 2 (1 = OK) |
| +4 | Battery percentage |
| +5 | Relay status (bit 0 = relay 1, bit 1 = relay 2) |

Control ranges: MUX at `3000` (Modbus 43001), manual relays from `1000`
(Modbus 41001), SCADA from `2000` (Modbus 42001).

---

## 7. Project structure

```
CompanyUtilityApp/
├─ CompanyUtilityApp.csproj
├─ appsettings.json                  externalised configuration
├─ Program.cs                        entry point, global exception handling
│
├─ Configuration/AppConfig.cs        strongly typed settings
│
├─ Models/
│  ├─ User.cs · Area.cs · Node.cs
│  ├─ ViewModels.cs                  read-only grid projections
│  ├─ DeviceConfigurationPayload.cs  firmware contract + calibration validation
│  └─ Settings.cs                    preferences + IPv4 validation
│
├─ Security/PasswordHasher.cs        PBKDF2 with legacy migration
│
├─ Data/
│  ├─ Db.cs                          connections, error translation, health check
│  ├─ AuthRepository.cs
│  ├─ NodeRepository.cs
│  └─ AreaRepository.cs
│
├─ Services/
│  ├─ DeviceConfigurationService.cs  serial + OTA deployment
│  ├─ ModbusDiagnosticsService.cs    Modbus TCP
│  └─ SettingsService.cs             atomic JSON persistence
│
├─ Infrastructure/
│  ├─ AppLogger.cs                   rolling file log
│  └─ UserSession.cs                 identity and roles
│
├─ UI/UserDialog.cs                  dialogs + BusyScope
│
├─ Forms/                            MainForm · LoginForm · AddEditNodeForm · EditAreaForm
├─ UserControls/                     Home · Node · Area · PanelSettings · Settings
│                                    Network · Tests · IOActivity · Transfer
└─ DocumentFiles/                    user manual
```

Around 5,400 lines of hand-written C# across 22 files, plus designer-generated layout.

---

## 8. Getting started

### Prerequisites

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- SQL Server LocalDB (ships with Visual Studio, or the standalone
  *SQL Server Express LocalDB* installer)
- A USB cable for serial deployment, or network reachability for OTA

### Database setup

Create `NodeManagementDB` with the three tables in [§5](#5-database-schema), then:

1. Seed `Areas` with all 400 route/panel rows.
   `HoldingRegister = 40000 + (Route-1)×100 + PanelLocation`.
2. Insert at least one row into `Users`. Both password formats are accepted, so
   existing credentials work unchanged and are upgraded to PBKDF2 on first sign-in.

### Build and run

```powershell
git clone <repository-url>
cd CompanyUtilityApp\CompanyUtilityApp

dotnet build     # expect 0 errors, 0 warnings
dotnet run
```

If the database is unreachable, the application says so at start-up rather than
failing later.

### Runtime paths

| What | Where |
|---|---|
| Configuration | `appsettings.json`, beside the executable |
| Logs | `%LOCALAPPDATA%\I2ST\NodeCalibration\logs` |
| Settings | `%LOCALAPPDATA%\I2ST\NodeCalibration\settings.json` |

---

## 9. Configuration

All of `appsettings.json` is editable without a rebuild. A malformed file falls back
to built-in defaults and reports why, rather than refusing to start.

```json
{
  "Database": {
    "ConnectionString": "Server=(localdb)\\MSSQLLocalDB;Database=NodeManagementDB;Integrated Security=true;TrustServerCertificate=true",
    "CommandTimeoutSeconds": 30
  },
  "Modbus":  { "GatewayHost": "192.168.1.10", "Port": 502, "ConnectTimeoutMs": 3000 },
  "Serial":  { "BaudRate": 115200, "DeviceAckTimeoutMs": 8000 },
  "Ota":     { "RequestTimeoutSeconds": 5, "ConfigEndpoint": "/config" },
  "Routes":  { "Count": 4, "PanelsPerRoute": 100 },
  "Logging": { "MinimumLevel": "Information", "RetainedFileCount": 14 }
}
```

`DeviceAckTimeoutMs` is the one worth knowing about: it is how long the tool waits for
a controller to confirm a serial deployment before reporting it unconfirmed.

---

## 10. Engineering notes

A few decisions that are easy to misread as arbitrary:

**A serial write is not a deployment.** `SerialPort.Write` returning without throwing
only means the bytes reached the local UART buffer. An unplugged cable, a device in
bootloader mode, or a rejected payload all produce a successful write. Since a node
marked as calibrated disappears from the deployment list, trusting the write would
make a failed commissioning invisible. The tool therefore waits for the controller's
own reply and only then records success.

**Low-stock-style highlighting belongs where the work is.** Uncalibrated nodes are
highlighted in the node grid rather than summarised in a counter, because the person
looking at the grid is the person who has to act on it.

**Settings live in `%LOCALAPPDATA%`, not beside the binary.** A Program Files
directory is not writable by a standard user; saving next to the executable either
fails or silently triggers UAC virtualisation.

**Uniqueness is enforced by the database, not by a pre-check.** A check-then-write
leaves a window in which another workstation can take the value. The pre-checks are
kept for their better error messages, but a constraint violation is handled as a normal
outcome rather than an exception.

**Machine-facing values are formatted with `InvariantCulture`.** Node numbers, IP
addresses and register values must not vary with the operator's regional settings.

**Nullable reference types are enforced, and generated files are excluded.** Designer
fields are assigned inside `InitializeComponent()`, where the analyser cannot see
them, so designer files carry `#nullable disable` — the approach the WinForms
templates themselves use. Hand-written code is fully checked and the build is clean at
zero warnings.

---

## 11. Roadmap

| Area | Status |
|---|---|
| Node management | Complete |
| Area management | Complete |
| Configuration deployment (serial + OTA) | Complete — pending hardware validation |
| Calibration control | Complete |
| Authentication and access control | Complete |
| Settings / network configuration | Complete |
| Modbus transport | Complete — untested against a live gateway |
| Operation and endurance tests | Planned — transport in place, UI outstanding |
| Live I/O activity view | Planned |
| CSV import / export | Planned — export first; import awaits per-row validation and rollback |

Known follow-ups:

- **Validate the acknowledgement logic against real firmware.** The reply
  classification is written to the documented device behaviour but has not been
  observed against a controller; the token lists are deliberately easy to adjust.
- **Migrate from `NModbus4` to `NModbus`.** The former ships .NET Framework binaries
  only and runs on `net10.0` through the compatibility shim.
- **Add a role column to `Users`.** Administrator status is currently inferred from the
  username.
- **Add a test project.** There is none; two methods containing SQL that referenced
  non-existent columns survived in the codebase precisely because nothing exercised
  them.

---

<p align="center">
  <strong>Node Calibration Utility</strong><br/>
  I2ST Technologies Pvt. Ltd. &copy; 2026
</p>
