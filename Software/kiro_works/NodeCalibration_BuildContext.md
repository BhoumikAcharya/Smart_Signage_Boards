# Node Calibration Utility — Build Context

**Project:** `CompanyUtilityApp` — Node Calibration for the Smart Signage System
**Organisation:** I2ST Technologies Pvt. Ltd.
**Version:** 2.0.0
**Date:** 2026-08-20
**Root:** `CompanyUtilityApp\CompanyUtilityApp\`
**Build state:** `dotnet build` → **0 errors, 0 warnings** (was 0 errors, 80 warnings)

This is the handoff document: everything needed to build, configure, run and extend
the application without the conversation that produced it.

---

## 1. Project facts

| | |
|---|---|
| Type | Windows Forms desktop application |
| Framework | .NET 10.0 (`net10.0-windows`) |
| Language | C# 12, nullable reference types enabled and enforced |
| Database | SQL Server LocalDB — `NodeManagementDB` |
| Device transport | USB serial (`System.IO.Ports`), HTTP OTA (`HttpClient`), Modbus TCP (`NModbus4`) |
| Packages | `Microsoft.Data.SqlClient` 7.0.0 · `NModbus4` 2.1.0 · `System.IO.Ports` 10.0.7 |
| Hand-written C# | ~5,400 lines across 22 files |
| Root namespace | `CompanyUtilityApp` |

---

## 2. Solution layout

```
CompanyUtilityApp/
├─ .gitignore
└─ CompanyUtilityApp/
   ├─ CompanyUtilityApp.csproj      metadata, analyser policy, config copying
   ├─ appsettings.json              all externalised settings
   ├─ Program.cs                    entry point, global exception handling
   │
   ├─ Configuration/
   │  └─ AppConfig.cs               strongly typed settings loader
   │
   ├─ Models/
   │  ├─ User.cs                    operator identity + admin predicate
   │  ├─ Area.cs                    panel position (Areas table)
   │  ├─ Node.cs                    ESP32 controller (Nodes table)
   │  ├─ ViewModels.cs              NodeListItem, PanelDeploymentTarget
   │  ├─ DeviceConfigurationPayload.cs   firmware JSON contract + calibration
   │  └─ Settings.cs                technician/network settings + IPv4 validation
   │
   ├─ Security/
   │  └─ PasswordHasher.cs          PBKDF2 with legacy SHA-256 migration
   │
   ├─ Data/
   │  ├─ Db.cs                      connection factory, error translation, health check
   │  ├─ AuthRepository.cs          authentication, transparent hash upgrade
   │  ├─ AreaRepository.cs          panel reads/updates
   │  └─ NodeRepository.cs          node CRUD, calibration status
   │
   ├─ Services/
   │  ├─ DeviceConfigurationService.cs   serial + OTA deployment (static)
   │  ├─ ModbusDiagnosticsService.cs     Modbus TCP reads/writes (IDisposable)
   │  └─ SettingsService.cs              atomic JSON persistence
   │
   ├─ Infrastructure/
   │  ├─ AppLogger.cs               rolling file logger
   │  └─ UserSession.cs             session state + role gating
   │
   ├─ UI/
   │  └─ UserDialog.cs              dialog helpers + BusyScope
   │
   ├─ Forms/                        MainForm, LoginForm, AddEditNodeForm, EditAreaForm
   ├─ UserControls/                 9 screens
   ├─ Properties/                   Resources
   ├─ Resources/                    logo, icons
   └─ DocumentFiles/                user manual PDF
```

Namespaces now match folders (`CompanyUtilityApp.Forms`, `.UserControls`, `.Data`, …).
This is not cosmetic — see §4.1.

---

## 3. Database schema

`NodeManagementDB` on SQL Server LocalDB. Unchanged by this work; the application
reads it as it stands.

### Users
| Column | Type | Constraints |
|---|---|---|
| Id | INT | PK, IDENTITY |
| Username | NVARCHAR(50) | UNIQUE, NOT NULL |
| PasswordHash | NVARCHAR(255) | NOT NULL |
| FullName | NVARCHAR(100) | NOT NULL |
| CreatedAt | DATETIME | DEFAULT GETDATE() |

`PasswordHash` now holds either a legacy 64-character SHA-256 hex string or a
PBKDF2 record of the form `PBKDF2$<iterations>$<base64 salt>$<base64 hash>`. Both
are accepted; legacy rows are upgraded on next successful sign-in. The column is
already wide enough (255) for the PBKDF2 format, so no migration is required.

### Areas — 400 pre-seeded rows (4 routes × 100 panels)
| Column | Type | Constraints |
|---|---|---|
| Id | INT | PK, IDENTITY |
| Route | INT | NOT NULL |
| PanelLocation | INT | NOT NULL |
| HoldingRegister | INT | UNIQUE, NOT NULL |
| PanelSerialNumber | INT | UNIQUE, NOT NULL |
| Description | NVARCHAR(255) | NULL |

Plus `UNIQUE(Route, PanelLocation)`.
`HoldingRegister = 40000 + (Route-1)*100 + PanelLocation`, assigned at seed time and
treated as immutable.

### Nodes
| Column | Type | Default |
|---|---|---|
| Id | INT | PK, IDENTITY |
| NodeNumber | INT | UNIQUE, NOT NULL |
| PanelSerialNumber | INT | UNIQUE, NOT NULL, FK → Areas |
| LocalIPAddress | NVARCHAR(15) | UNIQUE, NOT NULL |
| Zerovolt_CS1 / _CS2 | FLOAT | 2.40 |
| Sensitivity_CS1 / _CS2 | FLOAT | 0.105 |
| Threshold_CS1 | FLOAT | 0.100 |
| Threshold_CS2 | FLOAT | 0.120 |
| BatteryCalibration | FLOAT | 0.0 |
| BatterySagCompensation | FLOAT | 0.0 |
| PSUThreshold | INT | 1800 |
| Calibration | BIT | 0 |

The database column names are retained; the C# model uses conventional
casing (`ZeroVoltCs1`, `IsCalibrated`) and maps explicitly in the repository.

---

## 4. What changed and why

The refactor was driven by defects and risks found while reading the code, not by
style preference. The items below are ordered by consequence.

### 4.1 Two methods contained SQL that could never execute

`NodeRepository` had two methods that referenced columns which do not exist. Both
would have thrown `SqlException` the first time they ran:

```sql
-- IpAddressExists: the column is LocalIPAddress, not IPAddress
SELECT COUNT(*) FROM Nodes WHERE IPAddress = @IPAddress

-- PanelLocationExistsForRoute: Nodes has neither Route nor PanelLocation;
-- both live on Areas
SELECT COUNT(*) FROM Nodes WHERE Route = @Route AND PanelLocation = @PanelLocation
```

`IpAddressExists` was replaced by `LocalIpExistsAsync` against the correct column.
`PanelLocationExistsForRoute` was **removed rather than fixed**: the constraint it
was reaching for — one node per panel — is already guaranteed by the unique
`Nodes.PanelSerialNumber` column, so a replacement would be redundant.

Neither was reachable from the UI, so this was latent rather than live. It is listed
first because it is the clearest evidence that the data layer had no test coverage.

### 4.2 A successful serial write was treated as a successful deployment

This was the most consequential defect. The old flow was:

```csharp
port.Write(data);
MessageBox.Show("Configuration sent successfully.");
NodeRepository.MarkCalibrated(selectedNode.PanelSerialNumber);
```

`SerialPort.Write` returning without throwing only means the bytes reached the local
UART buffer. It says nothing about whether a controller was attached, powered, running
the signage firmware, or willing to accept the JSON. An unplugged cable, a device in
bootloader mode, or a malformed payload all produced "sent successfully" and a node
flagged as calibrated **permanently**, while the hardware kept running stale
configuration. Because the Panel Settings screen only lists uncalibrated nodes, the
node then disappeared from the list and the mistake became invisible.

`DeviceConfigurationService` now requires an acknowledgement. It writes the payload,
then reads the controller's reply until the device falls quiet or a configurable
timeout expires, and classifies it:

- reply contains a confirmation token (`config saved`, `restarting`, …) → success
- reply contains a rejection token (`error`, `invalid`, `bad json`, …) → failure
- no reply, or an unrecognised reply → failure, reported as unconfirmed

`MarkCalibratedAsync` is called **only** on success. On any other outcome the operator
is told explicitly that the node has *not* been marked as calibrated, and the device's
raw reply is echoed into the serial monitor so they can see why.

The OTA path already had a real acknowledgement in the HTTP status code; it now
reports the response body on failure as well.

### 4.3 Every screen was reachable without signing in

`MainForm` contained the access-control code, commented out:

```csharp
//// Enable/disable protected features
//Node.Enabled = loggedIn;
//Area.Enabled = loggedIn;
...
```

Node creation, deletion and device deployment were all reachable with no
authentication. Sign-in existed but gated nothing except the visibility of the
username in the menu bar.

Access control now lives in `UserSession` and is applied by
`MainForm.ApplySessionState()`, with `NavigateProtected()` refusing to open a
protected screen and explaining why. Home remains available so there is always
somewhere to be.

The admin-only calibration panel was previously gated by a public `IsAdmin` property
that the host form had to remember to set, with the role decided by a string
comparison inside `MainForm`. It is now `UserSession.CanCalibrate`, decided in one
place.

### 4.4 Passwords were stored as unsalted SHA-256

```csharp
using (SHA256 sha256 = SHA256.Create())
    return BitConverter.ToString(sha256.ComputeHash(...)).Replace("-","").ToLower();
```

Two problems. SHA-256 is designed to be fast, so an attacker holding a copy of the
Users table can test billions of candidates per second on commodity hardware. And
with no salt, identical passwords produce identical hashes, so one precomputed table
breaks every account at once.

`PasswordHasher` now writes PBKDF2-HMAC-SHA256 with a 16-byte random salt and 600,000
iterations (OWASP's 2023 figure for this algorithm). Verification is fixed-time.

**Existing logins keep working.** `Verify` recognises the legacy 64-character hex
format, and on a successful sign-in against a legacy hash the stored value is
transparently rewritten as PBKDF2 — the one moment the plaintext is available. Nobody
has to reset anything, and a failed upgrade is logged without blocking the sign-in.

### 4.5 The application had no diagnostics and no crash handling

There was no logging of any kind, and no global exception handler. A dropped database
connection produced the Windows crash dialog and terminated the process, leaving
nothing to reconstruct the fault from — which for a tool used on site, on a
commissioning deadline, is expensive.

- `AppLogger` writes a dated rolling log to
  `%LOCALAPPDATA%\I2ST\NodeCalibration\logs`, pruned to 14 files. Every repository
  failure, deployment attempt, device reply and rejected sign-in is recorded.
- `Program` installs `Application.ThreadException`,
  `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`. A
  UI-thread fault is now reported and **survivable** — the operator keeps their other
  work. A background fault is logged before the runtime tears down.
- A named mutex enforces a single instance. Two copies would compete for the same COM
  port and could send conflicting configuration to one controller.
- `Db.CheckHealthAsync` runs once at start-up and reports an unreachable database or
  missing tables immediately, rather than as a stack trace on first use.

### 4.6 Every database call blocked the UI thread

All ADO.NET calls were synchronous and made directly from event handlers, so the
window froze for the duration of each query. Everything is now `async`, with
`CancellationToken` support; controls cancel in-flight work when they are torn down,
so a query that returns after the screen closed cannot touch a disposed control.

`BusyScope` shows the wait cursor and disables the triggering control for the duration
of an operation, restoring both on dispose even if the operation throws — the previous
hand-rolled equivalents could leave a button permanently disabled on an early return.

### 4.7 The serial port leaked and could be used after close

```csharp
if (_serialPort != null && _serialPort.IsOpen)
{
    _serialPort.Close();
    _serialPort.DataReceived -= SerialPort_DataReceived;   // after the close
    _serialPort = null;                                    // never disposed
}
```

The handler was detached *after* the close, leaving a window in which a final
`DataReceived` could fire against a closing port, and the port was never disposed, so
the handle leaked on every connect/disconnect cycle. `DataReceived` also called
`this.Invoke` with no check that the handle still existed, which throws if the control
is being torn down while data arrives.

Now: handler detached first, then close, then `Dispose`. The receive path checks
`IsDisposed`/`IsHandleCreated`, uses `BeginInvoke`, and catches
`ObjectDisposedException` for the race that remains. The monitor buffer is capped at
100,000 characters — a chatty controller left connected for hours would otherwise grow
the text box until the process ran out of memory.

Disposal is wired through the designer's `Dispose(bool)` (as `ReleaseResources()`),
which is the reliable point: it runs when `MainForm` replaces the screen.

### 4.8 Replaced screens were never disposed

```csharp
panel2.Controls.Clear();   // detaches, does not dispose
```

A screen holding a serial port or Modbus socket kept it open for the life of the
process. `MainForm.Navigate()` now disposes the control it replaces, and disposes the
hosted screen on form close.

### 4.9 A dialog both did and did not save, depending on how it was opened

`AddEditNodeForm` returned data to the caller in add mode, but wrote to the database
itself in edit mode — calling `NodeRepository.UpdateNode` and
`AreaRepository.UpdateArea` from inside the dialog, and discarding the latter's
success/failure return value. The caller had no way to know which had happened.

The dialog now only validates and collects. Persistence belongs to the caller,
uniformly. It also carries the stored calibration constants through an edit
untouched — a detail worth noting, because rebuilding the `Node` without them would
have reset the record to model defaults on the next write.

### 4.10 Race-prone uniqueness checks

The original pattern was check-then-write:

```csharp
if (NodeRepository.NodeNumberExists(nodeNum)) { /* complain */ }
// ... another workstation can take the value here ...
NodeRepository.AddNode(node);
```

The pre-checks are kept, because they give a better message than a constraint
violation. But the database's unique constraints are now treated as the actual
guarantee: `AddAsync`/`UpdateAsync` catch SQL error 2627/2601 and return it as a
normal outcome with a message naming the offending field, parsed from the index name.

### 4.11 The Open File menu item ran the New File handler

```csharp
openFileToolStripMenuItem.Click += newFileToolStripMenuItem_Click;
```

Choosing "Open File" reported "New File created." The real `openFileToolStripMenuItem_Click`
existed but was never wired. Fixed in the designer. The `Upload/Download` button had
no handler wired at all; it is now connected.

### 4.12 A latent resource-lookup bug from folder/namespace mismatch

`MainForm.Designer.cs` uses:

```csharp
var resources = new ComponentResourceManager(typeof(MainForm));
TSB.Image = (Image)resources.GetObject("TSB.Image");
```

`ComponentResourceManager` looks up `<class namespace>.MainForm.resources`, while the
embedded resource name is derived from `RootNamespace` + folder path. The file lived in
`WindowForms\` while the class was in namespace `CompanyUtilityApp`, so the lookup
name (`CompanyUtilityApp.MainForm.resources`) did not match the manifest name
(`CompanyUtilityApp.WindowForms.MainForm.resources`). `GetObject` returned null and
the toolbar user icon silently never loaded.

The same mismatch affected every control in `UserControls\` except
`PanelSettingsControl`, which happened to already declare the matching namespace —
which is why that one worked.

Fixed by renaming `WindowForms\` → `Forms\` and aligning namespaces to folders
throughout. This is the reason the namespace normalisation was worth doing.

### 4.13 Two settings screens were entirely inert

`SettingsControl` and `NetworkControl` had designer layouts and code-behinds
containing nothing but empty label handlers. Anything typed was discarded when the
screen was replaced, and the Wi-Fi fields never hid because
`checkBox1_CheckedChanged` was empty. Neither screen had a Save button.

Both now load and save through `SettingsService`, with validation
(`Models\Settings.cs`) and a Save button added in code. The Wi-Fi group shows and
hides correctly. The Wi-Fi password field is masked — a visible pre-shared key on a
shared commissioning workstation is a real exposure.

`settings.json` is written to `%LOCALAPPDATA%\I2ST\NodeCalibration`, not beside the
executable: a Program Files directory is not writable by a standard user, so saving
next to the binary either fails or triggers UAC virtualisation. Writes are atomic
(temp file then replace), so an interrupted save cannot leave a file that fails to
parse on next launch.

### 4.14 Hardcoded deployment values

The connection string was a `private static readonly string` in `DatabaseHelper`, and
the Raspberry Pi address was one in `ModbusHelper`. Retargeting a deployment required
a rebuild.

All of it now lives in `appsettings.json`, loaded once into `AppConfig`. A malformed
file falls back to built-in defaults and reports why in the log and on start-up rather
than refusing to start — a site engineer should not be stranded by a stray comma.

### 4.15 Modbus connections leaked and could hang

`ModbusHelper.CreateMaster()` returned an `IModbusMaster` while keeping the underlying
`TcpClient` private, so callers had no way to close the socket — every connection
attempt leaked one. `TcpClient.Connect` also has no timeout of its own, so an
unreachable gateway blocked for the OS default (roughly 20 seconds on Windows),
freezing the caller.

`ModbusDiagnosticsService` owns both objects, implements `IDisposable`, and applies a
configurable connect timeout via a linked `CancellationTokenSource`. It decodes the
6-register diagnostic block into a typed `NodeDiagnostics` record and forces a
reconnect after any failure.

### 4.16 Calibration values were sent unvalidated

Any number that parsed was pushed to the device. Calibration constants translate raw
ADC counts into amps and volts, so a typo or a wrong unit silently corrupts every
subsequent reading the node publishes. `CalibrationOverride.TryValidate` now bounds
sensitivity to 0.001–1.0 V/A (the ACS712-class sensors on these panels sit in the
tens-to-hundreds of mV/A) and battery calibration to ±5 V.

Deployment is also now confirmed, spelling out whether calibration will be overwritten
or preserved, because the controller restarts on accepting a payload.

### 4.17 Miscellaneous

- `ToolStripStatusLabel_Click` set the label to the **literal string**
  `"_currentUser.FullName"`. Handler removed along with its designer wiring.
- Eight empty event handlers and their designer wiring removed.
- `AuthRepository` imported `Microsoft.VisualBasic.ApplicationServices`, which defines
  a competing `User` type; it worked by accident of resolution order. Removed.
- `MainForm.Designer.cs` was missing its `Dispose(bool)` body, which is why
  `components` produced CS0414. Restored.
- `Print` opened a `PrintDialog` and discarded the result — a dialog that could not
  print. It now says so plainly.
- The empty `Models\` folder declared in the csproj now contains actual models.
- Machine-local `CompanyUtilityApp.csproj.user` removed; `.gitignore` added.
- Nullable reference types were enabled but ignored (80 warnings). Now enforced:
  `WarningsAsErrors` covers the CS86xx nullable set, and generated designer files are
  excluded with `#nullable disable` — the approach the WinForms templates use, since
  designer fields are assigned inside `InitializeComponent()` where the analyser
  cannot see them.
- All machine-facing conversions pinned to `InvariantCulture`. Node numbers, IP
  addresses and register values must not vary with the operator's regional settings.
- Placeholder screens (Tests, I/O Activity, Upload/Download) now state their status
  instead of presenting controls that do nothing.

---

## 5. Firmware contract

The JSON sent to the ESP32. Two properties are load-bearing:

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

1. **`NodeNumber` is deliberately absent.** The controller derives its node identity
   from the holding register. Sending a second source of truth would let the two
   disagree.

2. **Calibration keys are omitted entirely unless an administrator supplied them.**
   The firmware only overwrites a stored constant when the corresponding key is
   present, so an absent key preserves the bench calibration. Sending nulls or zeros
   would wipe it. This is enforced by
   `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on the three
   optional fields, not by convention.

With calibration, three additional keys appear: `Sensitivity_CS1`,
`Sensitivity_CS2`, `BatteryCalibration`.

Reset payload: `{"ResetConfig": true}` — clears stored preferences and restarts.

The controller restarts unconditionally after accepting configuration.

---

## 6. Modbus register map

Zero-based addresses, as used by `ModbusDiagnosticsService`.

| Purpose | Address | Notes |
|---|---|---|
| Node diagnostics block | `5000 + (N-1)*6` | 6 registers per node |
| ↳ +0 Status | | 1 = ONLINE |
| ↳ +1 Power | | 1 = OK |
| ↳ +2 Current 1 | | 1 = OK |
| ↳ +3 Current 2 | | 1 = OK |
| ↳ +4 Battery | | percent (0 / 50 / 100) |
| ↳ +5 Relay status | | 2-bit field, bit 0 = relay 1 |
| MUX | `3000` | Modbus 43001 |
| Manual relay | `1000 + (N-1)` | Modbus 41001+ |
| SCADA | `2000 + (N-1)` | Modbus 42001+ |

---

## 7. Configuration reference

`appsettings.json`, copied to the output directory on every build.

| Section | Key | Default | Purpose |
|---|---|---|---|
| Database | ConnectionString | LocalDB / NodeManagementDB | |
| | CommandTimeoutSeconds | 30 | |
| Modbus | GatewayHost | 192.168.1.10 | Raspberry Pi bridge |
| | Port | 502 | |
| | SlaveId | 1 | |
| | ConnectTimeoutMs | 3000 | prevents the 20 s OS default |
| | ReadWriteTimeoutMs | 3000 | |
| Serial | BaudRate | 115200 | |
| | DataBits / Parity / StopBits | 8 / None / One | |
| | ReadTimeoutMs / WriteTimeoutMs | 2000 | |
| | **DeviceAckTimeoutMs** | 8000 | how long to wait for the controller to confirm |
| Ota | RequestTimeoutSeconds | 5 | |
| | ConfigEndpoint | /config | |
| Routes | Count | 4 | drives every route dropdown |
| | PanelsPerRoute | 100 | |
| Logging | MinimumLevel | Information | Debug / Information / Warning / Error |
| | RetainedFileCount | 14 | |

---

## 8. Build and run

```powershell
cd CompanyUtilityApp\CompanyUtilityApp
dotnet build          # expect 0 errors, 0 warnings
dotnet run
```

Prerequisites:

1. **SQL Server LocalDB** with `NodeManagementDB` present, containing the `Users`,
   `Areas` and `Nodes` tables. The application reports an unreachable database or
   missing tables at start-up.
2. `Areas` seeded with the 400 route/panel rows.
3. At least one row in `Users`. Both password formats are accepted, so existing
   seeded credentials work unchanged.

Runtime paths:

| What | Where |
|---|---|
| Logs | `%LOCALAPPDATA%\I2ST\NodeCalibration\logs` |
| Settings | `%LOCALAPPDATA%\I2ST\NodeCalibration\settings.json` |
| Configuration | `appsettings.json` beside the executable |

---

## 9. Invariants to preserve

1. **`NodeNumber` is never sent to the controller.**
2. **Calibration keys are omitted unless explicitly supplied.** An absent key preserves
   the device's stored value.
3. **`Calibration = 1` is written only after the device acknowledges.** Never on a
   successful write alone.
4. **`HoldingRegister` is immutable** — the firmware and the Modbus bridge both key off
   it.
5. **`Areas` is update-only.** Never inserted into or deleted from at runtime.
6. **`Nodes.PanelSerialNumber` is not updatable.** Moving a controller is a
   delete-and-re-add.
7. **Node number, IP address and panel serial number are each unique**, enforced by the
   database.
8. **Machine-facing values use `InvariantCulture`.**
9. **Calibration is administrator-only.**
10. **Dialogs validate and collect; callers persist.**

---

## 10. Verified vs unverified

**Verified**
- `dotnet build` → 0 errors, 0 warnings (from 80 warnings).
- Application launches, `MainForm` constructs, message loop runs.
- `appsettings.json` is copied to the output directory and parsed.
- `AppLogger` creates its directory and writes the session banner.

**Not verified — needs hardware and a database**
- No SQL Server instance was available, so no query was executed against real data.
  All repository work is verified by reading code, not by round-tripping rows.
- No ESP32 was available. The acknowledgement logic in
  `DeviceConfigurationService` — including the token sets that classify a reply — is
  **written against the documented firmware behaviour and has not been observed
  against a real controller.** This is the single most important thing to test
  before trusting the tool on site. If the firmware's actual output does not contain
  the expected tokens, deployments will be reported as unconfirmed even when they
  succeeded. The token lists are near the top of the service and are deliberately
  easy to adjust.
- No Modbus gateway was available; `ModbusDiagnosticsService` is untested at runtime.
- The PBKDF2 legacy-upgrade path is unexercised without a real `Users` table.

---

## 11. Recommended follow-up

1. **Test the deployment acknowledgement against real hardware** and tune the token
   lists. See §10.
2. **Migrate off `NModbus4`.** It ships .NET Framework binaries only; restore reports
   NU1701 on a `net10.0` target, currently suppressed with justification in the
   csproj. The maintained `NModbus` package targets .NET Standard. The change is
   contained to `ModbusDiagnosticsService` but needs gateway testing.
3. **Add a role column to `Users`.** Administrator status is currently decided by
   comparing the username to `"admin"`, which is documented in `Models\User.cs` but is
   not a durable design.
4. **Complete the Tests, I/O Activity and Upload/Download screens.** The transports
   and repository methods they need are in place. CSV import in particular should not
   ship until per-row validation and rollback are done — a partially applied bulk write
   would leave node identity inconsistent with installed hardware.
5. **Add a test project.** There is none. The two dead SQL methods in §4.1 would have
   been caught by a single integration test against a scratch database.
6. **Consider an audit trail.** Node changes and deployments are logged to file, but
   nothing records who changed what in the database.
7. **Rename `panel2`** in `MainForm` to something meaningful. It is the content host;
   an unused `panelMainContent` also exists. Left alone to avoid designer churn.
