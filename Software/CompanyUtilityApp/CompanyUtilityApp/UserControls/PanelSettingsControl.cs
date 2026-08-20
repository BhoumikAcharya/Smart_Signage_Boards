using System.Globalization;
using System.IO.Ports;
using System.Text;
using CompanyUtilityApp.Configuration;
using CompanyUtilityApp.Data;
using CompanyUtilityApp.Infrastructure;
using CompanyUtilityApp.Models;
using CompanyUtilityApp.Services;
using CompanyUtilityApp.UI;

namespace CompanyUtilityApp.UserControls;

/// <summary>
/// Panel Settings: pushes identity configuration to an uncalibrated controller
/// over USB serial or Ethernet, and optionally applies calibration constants.
///
/// The material change from the original is what "success" means. Previously the
/// serial path called <c>MarkCalibrated</c> straight after
/// <c>SerialPort.Write</c>, so an unplugged cable or a controller in bootloader
/// mode still flagged the node as calibrated forever while it ran stale
/// configuration. Deployment now goes through
/// <see cref="DeviceConfigurationService"/>, which requires the device to answer
/// before the database is touched.
///
/// Other corrections:
///  * Admin gating comes from <see cref="UserSession"/> rather than a public
///    property the host form had to remember to set.
///  * The serial monitor's port is disposed, and its event handler detached
///    before closing, so the handle is released when the screen is replaced.
///  * Calibration values are range-checked before they are sent.
/// </summary>
public partial class PanelSettingsControl : UserControl
{
    private readonly CancellationTokenSource _cts = new();

    private SerialPort? _monitorPort;
    private List<PanelDeploymentTarget> _targets = new();

    /// <summary>Guards the two linked dropdowns against recursive change events.</summary>
    private bool _synchronising;

    private bool _deploying;

    public PanelSettingsControl()
    {
        InitializeComponent();

        cmbRoute.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbRoute.Items.Clear();
        foreach (var route in AppConfig.Current.Routes.RouteNumbers)
            cmbRoute.Items.Add(route);

        cmbPanelLocation.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbIPAddress.DropDownStyle = ComboBoxStyle.DropDownList;

        txtDescription.ReadOnly = true;
        txtPanelSerialNumber.ReadOnly = true;
        txtNodeNumber.ReadOnly = true;
        txtSerialOutput.ReadOnly = true;

        // Calibration alters the meaning of every reading the node reports, so
        // it is administrator-only. Driven by the session rather than a property
        // the caller must set.
        btnManualCalibrate.Visible = UserSession.CanCalibrate;
        btnManualCalibrate.Enabled = UserSession.CanCalibrate;
        pnlManual.Visible = false;

        txtTargetIP.Enabled = false;
        txtTargetIP.PlaceholderText = "192.168.1.105";

        btnConnect.Enabled = true;
        btnDisconnect.Enabled = false;

        if (cmbRoute.Items.Count > 0)
            cmbRoute.SelectedIndex = 0;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        if (DesignMode)
            return;

        RefreshComPorts();
        _ = LoadTargetsAsync();
    }

    private int SelectedRoute => cmbRoute.SelectedItem is int route ? route : 0;

    private PanelDeploymentTarget? SelectedTarget => cmbPanelLocation.SelectedItem as PanelDeploymentTarget;

    private bool UseOta => chkOTA.Checked;

    // ---- Deployment targets --------------------------------------------------

    private async Task LoadTargetsAsync()
    {
        if (SelectedRoute == 0)
            return;

        try
        {
            using var busy = new BusyScope(panel1);

            _targets = await NodeRepository.GetUncalibratedAsync(SelectedRoute, _cts.Token);

            if (_cts.IsCancellationRequested || IsDisposed)
                return;

            BindTargets();
        }
        catch (OperationCanceledException)
        {
        }
        catch (DataAccessException ex)
        {
            UserDialog.DataError(this, ex);
        }
    }

    /// <summary>
    /// Binds both dropdowns to the SAME list of target objects.
    ///
    /// The original bound the panel combo to the target list and the IP combo to
    /// a separate list of strings, then tried to keep them aligned by searching
    /// for a matching IP. Two duplicate IP addresses — which the database does
    /// not permit, but a stale grid can still present — made that ambiguous.
    /// Sharing the objects removes the class of bug.
    /// </summary>
    private void BindTargets()
    {
        _synchronising = true;
        try
        {
            cmbPanelLocation.DataSource = null;
            cmbIPAddress.DataSource = null;

            if (_targets.Count == 0)
            {
                ClearTargetDetails();
                btnDeploy.Enabled = false;

                label1.Text = $"Route {SelectedRoute}: no uncalibrated nodes";
                return;
            }

            cmbPanelLocation.DataSource = new List<PanelDeploymentTarget>(_targets);
            cmbPanelLocation.DisplayMember = nameof(PanelDeploymentTarget.PanelLocation);

            cmbIPAddress.DataSource = new List<PanelDeploymentTarget>(_targets);
            cmbIPAddress.DisplayMember = nameof(PanelDeploymentTarget.IPAddress);

            label1.Text = $"Route {SelectedRoute}: {_targets.Count} node(s) awaiting calibration";
        }
        finally
        {
            _synchronising = false;
        }

        cmbPanelLocation.SelectedIndex = 0;
        cmbIPAddress.SelectedIndex = 0;
        ShowTargetDetails(_targets[0]);
        UpdateDeployAvailability();
    }

    private void ClearTargetDetails()
    {
        txtDescription.Text = string.Empty;
        txtPanelSerialNumber.Text = string.Empty;
        txtNodeNumber.Text = string.Empty;
        txtTargetIP.Text = string.Empty;
    }

    private void ShowTargetDetails(PanelDeploymentTarget target)
    {
        txtDescription.Text = target.Description ?? string.Empty;
        txtPanelSerialNumber.Text = target.PanelSerialNumber.ToString(CultureInfo.InvariantCulture);
        txtNodeNumber.Text = target.NodeNumber.ToString(CultureInfo.InvariantCulture);

        // Pre-fill the OTA address from the node record so the operator does not
        // have to retype an address the application already knows.
        if (UseOta && string.IsNullOrWhiteSpace(txtTargetIP.Text))
            txtTargetIP.Text = target.IPAddress;
    }

    /// <summary>Mirrors a selection from one dropdown into the other by index.</summary>
    private void MirrorSelection(ComboBox source, ComboBox destination)
    {
        if (_synchronising)
            return;

        _synchronising = true;
        try
        {
            if (destination.SelectedIndex != source.SelectedIndex)
                destination.SelectedIndex = source.SelectedIndex;
        }
        finally
        {
            _synchronising = false;
        }
    }

    private void UpdateDeployAvailability()
    {
        var monitorOpen = _monitorPort?.IsOpen == true;

        // A deployment opens its own short-lived port, so it must not run while
        // the monitor holds the same handle.
        btnDeploy.Enabled = SelectedTarget is not null && !_deploying && (UseOta || !monitorOpen);
    }

    // ---- Handlers: selection -------------------------------------------------

    private async void cmbRoute_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_synchronising)
            return;

        await LoadTargetsAsync();
    }

    private void cmbPanelLocation_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_synchronising || SelectedTarget is null)
            return;

        MirrorSelection(cmbPanelLocation, cmbIPAddress);
        ShowTargetDetails(SelectedTarget);
        UpdateDeployAvailability();
    }

    private void cmbIPAddress_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_synchronising)
            return;

        MirrorSelection(cmbIPAddress, cmbPanelLocation);

        if (SelectedTarget is not null)
            ShowTargetDetails(SelectedTarget);

        UpdateDeployAvailability();
    }

    // ---- Handlers: transport -------------------------------------------------

    private void chkOTA_CheckedChanged(object sender, EventArgs e)
    {
        var ota = UseOta;

        label5.Visible = !ota;
        cmbSerialPort.Visible = !ota;
        btnRefresh.Visible = !ota;
        grpSerialMonitor.Visible = !ota;

        txtTargetIP.Enabled = ota;

        if (ota)
        {
            // Holding a COM port open while deploying over the network serves no
            // purpose and keeps the handle from other tools.
            CloseMonitorPort();

            if (SelectedTarget is not null && string.IsNullOrWhiteSpace(txtTargetIP.Text))
                txtTargetIP.Text = SelectedTarget.IPAddress;
        }

        UpdateDeployAvailability();
    }

    private void btnRefresh_Click(object sender, EventArgs e) => RefreshComPorts();

    private void RefreshComPorts()
    {
        var previous = cmbSerialPort.SelectedItem as string;
        var ports = DeviceConfigurationService.GetAvailablePorts();

        cmbSerialPort.BeginUpdate();
        try
        {
            cmbSerialPort.Items.Clear();
            cmbSerialPort.Items.AddRange(ports);
        }
        finally
        {
            cmbSerialPort.EndUpdate();
        }

        if (ports.Length == 0)
        {
            AppendMonitor("No serial ports detected. Connect the controller and press Refresh.\r\n");
            return;
        }

        // Keep the operator's choice across a refresh where possible; re-selecting
        // silently would be an easy way to deploy down the wrong cable.
        var index = previous is not null ? Array.IndexOf(ports, previous) : -1;
        cmbSerialPort.SelectedIndex = index >= 0 ? index : 0;
    }

    // ---- Handlers: serial monitor -------------------------------------------

    private void btnConnect_Click(object sender, EventArgs e)
    {
        if (_monitorPort?.IsOpen == true)
            return;

        if (cmbSerialPort.SelectedItem is not string portName || string.IsNullOrWhiteSpace(portName))
        {
            UserDialog.Warn(this, "Select a COM port first.", "No port selected");
            return;
        }

        var serial = AppConfig.Current.Serial;

        try
        {
            _monitorPort = new SerialPort(
                portName, serial.BaudRate, serial.ParsedParity, serial.DataBits, serial.ParsedStopBits)
            {
                ReadTimeout = serial.ReadTimeoutMs,
                WriteTimeout = serial.WriteTimeoutMs,
            };

            _monitorPort.DataReceived += OnMonitorDataReceived;
            _monitorPort.Open();

            btnConnect.Enabled = false;
            btnDisconnect.Enabled = true;
            cmbSerialPort.Enabled = false;
            btnRefresh.Enabled = false;

            AppendMonitor($"--- Connected to {portName} at {serial.BaudRate} baud ---\r\n");
            AppLogger.Information($"Serial monitor opened on {portName}.");
        }
        catch (UnauthorizedAccessException)
        {
            DisposeMonitorPort();
            UserDialog.Warn(this,
                $"{portName} is already open in another application.\n\n" +
                "Close the Arduino IDE serial monitor or any other terminal using this port, then try again.",
                "Port in use");
        }
        catch (Exception ex)
        {
            DisposeMonitorPort();
            AppLogger.Warning($"Could not open serial monitor on {portName}.", ex);
            UserDialog.Error(this, $"The COM port could not be opened.\n\n{ex.Message}", "Connection failed");
        }

        UpdateDeployAvailability();
    }

    private void btnDisconnect_Click(object sender, EventArgs e)
    {
        CloseMonitorPort();
        AppendMonitor("--- Disconnected ---\r\n");
    }

    /// <summary>
    /// Closes the monitor port and restores the UI.
    ///
    /// Order matters: the handler is detached BEFORE the close. The original
    /// closed first and unsubscribed afterwards, leaving a window in which a
    /// final DataReceived event could fire against a closing port. It also never
    /// disposed the port, so the handle leaked on every connect/disconnect cycle.
    /// </summary>
    private void CloseMonitorPort()
    {
        DisposeMonitorPort();

        btnConnect.Enabled = true;
        btnDisconnect.Enabled = false;
        cmbSerialPort.Enabled = true;
        btnRefresh.Enabled = true;

        UpdateDeployAvailability();
    }

    private void DisposeMonitorPort()
    {
        if (_monitorPort is null)
            return;

        try
        {
            _monitorPort.DataReceived -= OnMonitorDataReceived;

            if (_monitorPort.IsOpen)
                _monitorPort.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Warning("Failed to close the serial monitor port cleanly.", ex);
        }
        finally
        {
            _monitorPort.Dispose();
            _monitorPort = null;
        }
    }

    /// <summary>
    /// Serial data arrives on a background thread, so the append is marshalled to
    /// the UI thread. Guarded by <see cref="Control.IsHandleCreated"/> because a
    /// final event can arrive while the screen is being torn down, and calling
    /// Invoke on a destroyed handle throws.
    /// </summary>
    private void OnMonitorDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var port = _monitorPort;
        if (port is null || !port.IsOpen)
            return;

        string data;
        try
        {
            data = port.ReadExisting();
        }
        catch (Exception ex)
        {
            AppLogger.Warning("Failed to read from the serial monitor port.", ex);
            return;
        }

        if (string.IsNullOrEmpty(data) || IsDisposed || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke(() => AppendMonitor(data));
        }
        catch (ObjectDisposedException)
        {
            // The control went away between the checks above and the marshal.
        }
    }

    private void AppendMonitor(string text)
    {
        if (txtSerialOutput.IsDisposed)
            return;

        // Cap the buffer: a chatty controller left connected for hours would
        // otherwise grow this text box until the process ran out of memory.
        const int maxChars = 100_000;
        if (txtSerialOutput.TextLength + text.Length > maxChars)
            txtSerialOutput.Clear();

        txtSerialOutput.AppendText(text);
        txtSerialOutput.SelectionStart = txtSerialOutput.TextLength;
        txtSerialOutput.ScrollToCaret();
    }

    // ---- Handlers: calibration panel ----------------------------------------

    private void btnManualCalibrate_Click(object sender, EventArgs e)
    {
        if (!UserSession.CanCalibrate)
        {
            UserDialog.Warn(this,
                "Only an administrator may change calibration constants.",
                "Not permitted");
            return;
        }

        pnlManual.Visible = !pnlManual.Visible;

        if (pnlManual.Visible && SelectedTarget is not null)
            _ = PrefillCalibrationAsync(SelectedTarget.PanelSerialNumber);
    }

    /// <summary>
    /// Loads the stored calibration for the selected node so the operator adjusts
    /// from the current values rather than typing into empty boxes.
    /// </summary>
    private async Task PrefillCalibrationAsync(int panelSerialNumber)
    {
        try
        {
            var node = await NodeRepository.GetByPanelSerialNumberAsync(panelSerialNumber, _cts.Token);
            if (node is null || IsDisposed)
                return;

            if (string.IsNullOrWhiteSpace(txtSens1.Text))
                txtSens1.Text = node.SensitivityCs1.ToString("0.###", CultureInfo.InvariantCulture);

            if (string.IsNullOrWhiteSpace(txtSens2.Text))
                txtSens2.Text = node.SensitivityCs2.ToString("0.###", CultureInfo.InvariantCulture);

            if (string.IsNullOrWhiteSpace(txtCalib.Text))
                txtCalib.Text = node.BatteryCalibration.ToString("0.###", CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException)
        {
        }
        catch (DataAccessException ex)
        {
            AppLogger.Warning($"Could not pre-fill calibration for panel {panelSerialNumber}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads and validates the calibration panel.
    /// Returns null when the panel is hidden, meaning "send no calibration".
    /// </summary>
    private bool TryReadCalibration(out CalibrationOverride? calibration)
    {
        calibration = null;

        if (!pnlManual.Visible)
            return true;

        if (!double.TryParse(txtSens1.Text.Trim(), out var sens1))
        {
            UserDialog.ValidationError(this, "Sensitivity CS1 must be a number.", txtSens1);
            return false;
        }

        if (!double.TryParse(txtSens2.Text.Trim(), out var sens2))
        {
            UserDialog.ValidationError(this, "Sensitivity CS2 must be a number.", txtSens2);
            return false;
        }

        if (!double.TryParse(txtCalib.Text.Trim(), out var battery))
        {
            UserDialog.ValidationError(this, "Battery calibration must be a number.", txtCalib);
            return false;
        }

        var candidate = new CalibrationOverride
        {
            SensitivityCs1 = sens1,
            SensitivityCs2 = sens2,
            BatteryCalibration = battery,
        };

        if (!candidate.TryValidate(out var error))
        {
            UserDialog.ValidationError(this, error, txtSens1);
            return false;
        }

        calibration = candidate;
        return true;
    }

    // ---- Handlers: deploy ---------------------------------------------------

    private async void btnDeploy_Click(object sender, EventArgs e)
    {
        var target = SelectedTarget;
        if (target is null)
        {
            UserDialog.Info(this, "Select a panel to configure first.", "No panel selected");
            return;
        }

        if (!TryReadCalibration(out var calibration))
            return;

        string transportTarget;
        if (UseOta)
        {
            transportTarget = txtTargetIP.Text.Trim();
            if (!NetworkValidation.IsValidIPv4(transportTarget))
            {
                UserDialog.ValidationError(this,
                    "Enter the controller's IPv4 address, for example 192.168.1.105.", txtTargetIP);
                return;
            }
        }
        else
        {
            transportTarget = cmbSerialPort.SelectedItem as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(transportTarget))
            {
                UserDialog.ValidationError(this, "Select a COM port.", cmbSerialPort);
                return;
            }
        }

        // The controller restarts after accepting configuration, so this is worth
        // an explicit confirmation — especially if calibration is included.
        var calibrationNote = calibration is null
            ? "Existing calibration constants on the device will be preserved."
            : $"Calibration WILL be overwritten: CS1={calibration.SensitivityCs1}, " +
              $"CS2={calibration.SensitivityCs2}, battery={calibration.BatteryCalibration}.";

        var confirmed = UserDialog.Confirm(this,
            $"""
             Deploy configuration to panel {target.PanelLocation} on route {target.Route}?

             Panel serial   {target.PanelSerialNumber}
             Register       {target.HoldingRegister}
             Transport      {(UseOta ? $"Network ({transportTarget})" : $"USB serial ({transportTarget})")}

             {calibrationNote}

             The controller will restart after accepting the configuration.
             """,
            "Confirm deployment");

        if (!confirmed)
            return;

        var payload = new DeviceConfigurationPayload
        {
            Route = target.Route,
            PanelLocation = target.PanelLocation,
            PanelSerialNumber = target.PanelSerialNumber,
            HoldingRegister = target.HoldingRegister,
            Description = target.Description,
            LocalIP = target.IPAddress,
            SensitivityCs1 = calibration?.SensitivityCs1,
            SensitivityCs2 = calibration?.SensitivityCs2,
            BatteryCalibration = calibration?.BatteryCalibration,
        };

        var json = DeviceConfigurationService.Serialise(payload);

        _deploying = true;
        UpdateDeployAvailability();

        try
        {
            using var busy = new BusyScope();

            AppendMonitor($"--- Deploying to panel {target.PanelLocation} ---\r\n{json}");

            var result = UseOta
                ? await DeviceConfigurationService.DeployOverOtaAsync(transportTarget, json, _cts.Token)
                : await DeviceConfigurationService.DeployOverSerialAsync(transportTarget, json, _cts.Token);

            if (!string.IsNullOrWhiteSpace(result.DeviceResponse))
                AppendMonitor($"--- Device response ---\r\n{result.DeviceResponse}\r\n");

            if (!result.Success)
            {
                UserDialog.Warn(this, result.Message, "Deployment not confirmed");
                return;
            }

            // Only now, with the device having acknowledged, is the database updated.
            await PersistDeploymentAsync(target, calibration, result.Message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _deploying = false;
            UpdateDeployAvailability();
        }
    }

    private async Task PersistDeploymentAsync(
        PanelDeploymentTarget target,
        CalibrationOverride? calibration,
        string successMessage)
    {
        try
        {
            if (calibration is not null)
            {
                var (calOk, calError) = await NodeRepository.UpdateCalibrationAsync(
                    target.PanelSerialNumber, calibration, _cts.Token);

                if (!calOk)
                {
                    UserDialog.Warn(this,
                        "The controller accepted the configuration, but the calibration values could not be " +
                        $"recorded in the database.\n\n{calError}\n\n" +
                        "The device and the database now disagree. Re-run the deployment once the database is available.",
                        "Calibration not recorded");
                }
            }

            var (ok, error) = await NodeRepository.MarkCalibratedAsync(target.PanelSerialNumber, _cts.Token);

            if (!ok)
            {
                UserDialog.Warn(this,
                    $"The controller accepted the configuration, but the node could not be marked as " +
                    $"calibrated.\n\n{error}",
                    "Status not updated");
            }
            else
            {
                UserDialog.Info(this,
                    $"{successMessage}\n\n" +
                    $"Panel {target.PanelLocation} (serial {target.PanelSerialNumber}) is now marked as calibrated.",
                    "Deployment complete");
            }

            await LoadTargetsAsync();
        }
        catch (DataAccessException ex)
        {
            UserDialog.Warn(this,
                "The controller accepted the configuration, but the database could not be updated.\n\n" +
                $"{ex.Message}",
                "Database not updated");
        }
    }

    // ---- Teardown -----------------------------------------------------------

    /// <summary>
    /// Releases the serial handle and cancels in-flight queries.
    ///
    /// Called from the designer's <c>Dispose(bool)</c>, which is the reliable
    /// point: it runs when the host form replaces this screen, whereas relying on
    /// the handle being destroyed would leave the COM port open if the control
    /// were disposed without ever having been shown.
    /// </summary>
    private void ReleaseResources()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        DisposeMonitorPort();
    }
}
