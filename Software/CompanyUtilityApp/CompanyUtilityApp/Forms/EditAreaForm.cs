using System.Globalization;
using CompanyUtilityApp.Models;
using CompanyUtilityApp.UI;

namespace CompanyUtilityApp.Forms;

/// <summary>
/// Edits the two mutable fields of a panel position: its serial number and its
/// description. Route, panel location and holding register are fixed at seed
/// time and shown read-only.
///
/// Collects and validates only; the caller performs the update so the
/// uniqueness outcome can be reported against live data.
/// </summary>
public partial class EditAreaForm : Form
{
    private readonly int _originalPanelSerialNumber;

    /// <summary>The serial number as edited. Meaningful after <see cref="DialogResult.OK"/>.</summary>
    public int PanelSerialNumber { get; private set; }

    /// <summary>The description as edited, or null when cleared.</summary>
    public string? Description { get; private set; }

    /// <summary>True when the operator actually changed the serial number.</summary>
    public bool SerialNumberChanged => PanelSerialNumber != _originalPanelSerialNumber;

    public EditAreaForm(Area area)
    {
        ArgumentNullException.ThrowIfNull(area);

        InitializeComponent();

        Text = $"Edit Panel {area.PanelLocation} - Route {area.Route}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        AcceptButton = button1;
        CancelButton = button2;
        button2.DialogResult = DialogResult.Cancel;

        _originalPanelSerialNumber = area.PanelSerialNumber;
        PanelSerialNumber = area.PanelSerialNumber;

        txtRoute.Text = area.Route.ToString(CultureInfo.InvariantCulture);
        txtPanelLocation.Text = area.PanelLocation.ToString(CultureInfo.InvariantCulture);
        txtHoldingRegister.Text = area.HoldingRegister.ToString(CultureInfo.InvariantCulture);
        txtPanelSerialNumber.Text = area.PanelSerialNumber.ToString(CultureInfo.InvariantCulture);
        txtDescription.Text = area.Description ?? string.Empty;

        // Seed-time values; editing them would break the register mapping the
        // firmware and the Modbus bridge both depend on.
        txtRoute.ReadOnly = true;
        txtPanelLocation.ReadOnly = true;
        txtHoldingRegister.ReadOnly = true;

        txtDescription.MaxLength = 255;
        txtDescription.PlaceholderText = "e.g. Platform 3, Column B";
    }

    private void btnSave_Click(object sender, EventArgs e)
    {
        var raw = txtPanelSerialNumber.Text.Trim();

        if (!int.TryParse(raw, out var serial) || serial <= 0)
        {
            UserDialog.ValidationError(this,
                "Panel serial number must be a whole number greater than zero.",
                txtPanelSerialNumber);
            return;
        }

        // Changing the serial number re-points the node foreign key and the
        // physical label on site, so it is worth an explicit confirmation.
        if (serial != _originalPanelSerialNumber)
        {
            var confirmed = UserDialog.Confirm(this,
                $"Change the panel serial number from {_originalPanelSerialNumber} to {serial}?\n\n" +
                "This must match the serial number physically stencilled on the panel, " +
                "and any node assigned to this panel is linked by this value.",
                "Confirm serial number change");

            if (!confirmed)
            {
                txtPanelSerialNumber.Focus();
                txtPanelSerialNumber.SelectAll();
                return;
            }
        }

        PanelSerialNumber = serial;

        var description = txtDescription.Text.Trim();
        Description = string.IsNullOrEmpty(description) ? null : description;

        DialogResult = DialogResult.OK;
        Close();
    }
}
