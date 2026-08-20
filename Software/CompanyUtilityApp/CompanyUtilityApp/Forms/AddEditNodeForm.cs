using System.Globalization;
using CompanyUtilityApp.Data;
using CompanyUtilityApp.Models;
using CompanyUtilityApp.UI;

namespace CompanyUtilityApp.Forms;

/// <summary>
/// Creates or edits a node.
///
/// This dialog now only VALIDATES and COLLECTS. The original wrote directly to
/// the database in edit mode while returning data to the caller in add mode, so
/// the same dialog both did and did not persist depending on how it was opened —
/// and the caller had no way to know which. Persistence belongs to the caller,
/// uniformly.
/// </summary>
public partial class AddEditNodeForm : Form
{
    private readonly bool _isAddMode;
    private readonly Node? _existingNode;
    private readonly Area? _existingArea;
    private List<Area> _availablePanels = new();

    /// <summary>Set when the operator confirms. Never null after <see cref="DialogResult.OK"/>.</summary>
    public Node? Result { get; private set; }

    /// <summary>The panel description as edited, to be written to the Areas table by the caller.</summary>
    public string? PanelDescription { get; private set; }

    /// <summary>The panel whose description may have changed.</summary>
    public int PanelSerialNumberForDescription { get; private set; }

    // ---- Construction --------------------------------------------------------

    /// <summary>Add mode: choose an unassigned panel on the given route.</summary>
    public AddEditNodeForm(int route, IReadOnlyList<Area> availablePanels)
    {
        ArgumentNullException.ThrowIfNull(availablePanels);

        InitializeComponent();
        ConfigureCommon();

        _isAddMode = true;
        Text = $"Add Node - Route {route}";

        txtRoute.Text = route.ToString(CultureInfo.InvariantCulture);
        chkCalibration.Checked = false;

        // A newly installed controller has not been configured yet, so letting
        // the operator tick "calibrated" at creation would misrepresent it. It
        // is set by a successful deployment on the Panel Settings screen.
        chkCalibration.Enabled = false;
        chkCalibration.Text = "Calibrated (set automatically after deployment)";

        _availablePanels = availablePanels.ToList();
        PopulatePanelDropdowns();
    }

    /// <summary>Edit mode: the panel assignment is fixed; identity fields are editable.</summary>
    public AddEditNodeForm(Node node, Area area)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(area);

        InitializeComponent();
        ConfigureCommon();

        _isAddMode = false;
        _existingNode = node;
        _existingArea = area;

        Text = $"Edit Node {node.NodeNumber} - Route {area.Route}";

        txtRoute.Text = area.Route.ToString(CultureInfo.InvariantCulture);

        // The panel assignment is the join key. Moving a controller to another
        // panel is a delete-and-re-add, not an edit, so both combos are locked
        // to the stored value.
        cmbPanelLocation.Items.Clear();
        cmbPanelLocation.Items.Add(area.PanelLocation);
        cmbPanelLocation.SelectedIndex = 0;
        cmbPanelLocation.Enabled = false;

        cmbPanelSerialNumber.Items.Clear();
        cmbPanelSerialNumber.Items.Add(area.PanelSerialNumber);
        cmbPanelSerialNumber.SelectedIndex = 0;
        cmbPanelSerialNumber.Enabled = false;

        txtHR.Text = area.HoldingRegister.ToString(CultureInfo.InvariantCulture);
        txtDescription.Text = area.Description ?? string.Empty;
        txtNodeNumber.Text = node.NodeNumber.ToString(CultureInfo.InvariantCulture);
        txtIPAddress.Text = node.LocalIPAddress;
        chkCalibration.Checked = node.IsCalibrated;

        // An operator may need to clear the flag to force a re-deployment.
        chkCalibration.Enabled = true;
        chkCalibration.Text = "Calibrated";
    }

    private void ConfigureCommon()
    {
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        AcceptButton = btnSave;
        CancelButton = btnCancel;
        btnCancel.DialogResult = DialogResult.Cancel;

        // Read-only throughout: derived from the panel record, never typed.
        txtRoute.ReadOnly = true;
        txtHR.ReadOnly = true;

        txtIPAddress.PlaceholderText = "192.168.1.105";
        txtNodeNumber.PlaceholderText = "e.g. 101";
        txtDescription.MaxLength = 255;
        txtIPAddress.MaxLength = 15;
    }

    // ---- Panel selection -----------------------------------------------------

    /// <summary>
    /// Fills the linked panel dropdowns.
    ///
    /// Both combos are bound to the same <see cref="Area"/> instances rather than
    /// to two parallel lists, so keeping them in step is a matter of assigning
    /// the same object instead of matching values across collections.
    /// </summary>
    private void PopulatePanelDropdowns()
    {
        cmbPanelLocation.SelectedIndexChanged -= OnPanelLocationChanged;
        cmbPanelSerialNumber.SelectedIndexChanged -= OnPanelSerialNumberChanged;

        cmbPanelLocation.DataSource = null;
        cmbPanelSerialNumber.DataSource = null;

        if (_availablePanels.Count == 0)
        {
            txtHR.Text = string.Empty;
            btnSave.Enabled = false;

            UserDialog.Info(this,
                "Every panel on this route already has a node assigned.\n\n" +
                "Delete an existing node first, or choose another route.",
                "No panels available");
            return;
        }

        cmbPanelLocation.DataSource = new List<Area>(_availablePanels);
        cmbPanelLocation.DisplayMember = nameof(Area.PanelLocation);

        cmbPanelSerialNumber.DataSource = new List<Area>(_availablePanels);
        cmbPanelSerialNumber.DisplayMember = nameof(Area.PanelSerialNumber);

        cmbPanelLocation.SelectedIndexChanged += OnPanelLocationChanged;
        cmbPanelSerialNumber.SelectedIndexChanged += OnPanelSerialNumberChanged;

        cmbPanelLocation.SelectedIndex = 0;
        SyncFromSelectedPanel((Area)cmbPanelLocation.SelectedItem!);
    }

    private void OnPanelLocationChanged(object? sender, EventArgs e)
    {
        if (cmbPanelLocation.SelectedItem is not Area selected)
            return;

        // Assigning by index avoids the recursive event storm that assigning
        // SelectedItem on the partner combo would cause.
        var index = _availablePanels.FindIndex(a => a.Id == selected.Id);
        if (index >= 0 && cmbPanelSerialNumber.SelectedIndex != index)
            cmbPanelSerialNumber.SelectedIndex = index;

        SyncFromSelectedPanel(selected);
    }

    private void OnPanelSerialNumberChanged(object? sender, EventArgs e)
    {
        if (cmbPanelSerialNumber.SelectedItem is not Area selected)
            return;

        var index = _availablePanels.FindIndex(a => a.Id == selected.Id);
        if (index >= 0 && cmbPanelLocation.SelectedIndex != index)
            cmbPanelLocation.SelectedIndex = index;

        SyncFromSelectedPanel(selected);
    }

    private void SyncFromSelectedPanel(Area panel)
    {
        txtHR.Text = panel.HoldingRegister.ToString(CultureInfo.InvariantCulture);

        // Show the stored description for the chosen panel, but do not clobber
        // something the operator has already typed.
        if (string.IsNullOrWhiteSpace(txtDescription.Text))
            txtDescription.Text = panel.Description ?? string.Empty;
    }

    // ---- Save ----------------------------------------------------------------

    private async void btnSave_Click(object sender, EventArgs e)
    {
        using var busy = new BusyScope(btnSave);

        if (!TryReadIpAddress(out var ipAddress))
            return;

        if (!TryReadNodeNumber(out var nodeNumber))
            return;

        var panelSerialNumber = _isAddMode
            ? (cmbPanelSerialNumber.SelectedItem as Area)?.PanelSerialNumber ?? 0
            : _existingArea!.PanelSerialNumber;

        if (panelSerialNumber <= 0)
        {
            UserDialog.ValidationError(this, "Select a panel location.", cmbPanelLocation);
            return;
        }

        var excludeNodeId = _isAddMode ? null : (int?)_existingNode!.Id;

        try
        {
            if (await NodeRepository.NodeNumberExistsAsync(nodeNumber, excludeNodeId))
            {
                UserDialog.ValidationError(this,
                    $"Node number {nodeNumber} is already assigned to another node.", txtNodeNumber);
                return;
            }

            if (await NodeRepository.LocalIpExistsAsync(ipAddress, excludeNodeId))
            {
                UserDialog.ValidationError(this,
                    $"IP address {ipAddress} is already assigned to another node.", txtIPAddress);
                return;
            }

            if (_isAddMode && await NodeRepository.PanelSerialNumberAssignedAsync(panelSerialNumber))
            {
                UserDialog.ValidationError(this,
                    $"Panel serial number {panelSerialNumber} already has a node assigned. " +
                    "Refresh the list and choose another panel.", cmbPanelLocation);
                return;
            }
        }
        catch (DataAccessException ex)
        {
            UserDialog.DataError(this, ex);
            return;
        }

        var description = txtDescription.Text.Trim();
        PanelDescription = string.IsNullOrEmpty(description) ? null : description;
        PanelSerialNumberForDescription = panelSerialNumber;

        Result = _isAddMode
            ? new Node
            {
                NodeNumber = nodeNumber,
                PanelSerialNumber = panelSerialNumber,
                LocalIPAddress = ipAddress,
                IsCalibrated = false,
            }
            : new Node
            {
                Id = _existingNode!.Id,
                NodeNumber = nodeNumber,
                PanelSerialNumber = panelSerialNumber,
                LocalIPAddress = ipAddress,
                IsCalibrated = chkCalibration.Checked,

                // Carry the stored calibration through untouched: this dialog
                // does not edit it, and dropping it would reset the record to
                // model defaults on the next write.
                ZeroVoltCs1 = _existingNode.ZeroVoltCs1,
                ZeroVoltCs2 = _existingNode.ZeroVoltCs2,
                SensitivityCs1 = _existingNode.SensitivityCs1,
                SensitivityCs2 = _existingNode.SensitivityCs2,
                ThresholdCs1 = _existingNode.ThresholdCs1,
                ThresholdCs2 = _existingNode.ThresholdCs2,
                BatteryCalibration = _existingNode.BatteryCalibration,
                BatterySagCompensation = _existingNode.BatterySagCompensation,
                PsuThreshold = _existingNode.PsuThreshold,
            };

        DialogResult = DialogResult.OK;
        Close();
    }

    private bool TryReadIpAddress(out string ipAddress)
    {
        ipAddress = txtIPAddress.Text.Trim();

        if (!NetworkValidation.IsValidIPv4(ipAddress))
        {
            UserDialog.ValidationError(this,
                "Enter a complete IPv4 address in dotted-quad form, for example 192.168.1.105.",
                txtIPAddress);
            return false;
        }

        return true;
    }

    private bool TryReadNodeNumber(out int nodeNumber)
    {
        if (!int.TryParse(txtNodeNumber.Text.Trim(), out nodeNumber) || nodeNumber <= 0)
        {
            UserDialog.ValidationError(this,
                "Node number must be a whole number greater than zero.", txtNodeNumber);
            return false;
        }

        return true;
    }
}
