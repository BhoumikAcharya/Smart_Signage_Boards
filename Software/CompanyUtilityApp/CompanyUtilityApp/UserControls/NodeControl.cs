using CompanyUtilityApp.Configuration;
using CompanyUtilityApp.Data;
using CompanyUtilityApp.Forms;
using CompanyUtilityApp.Models;
using CompanyUtilityApp.UI;

namespace CompanyUtilityApp.UserControls;

/// <summary>
/// Node management: lists the controllers installed on a route and provides
/// add, edit and delete.
///
/// All database work is asynchronous. The original ran every query on the UI
/// thread, so the whole window froze for the duration of each call — and a
/// dropped connection took the process down with it.
/// </summary>
public partial class NodeControl : UserControl
{
    private readonly CancellationTokenSource _cts = new();
    private bool _loading;

    public NodeControl()
    {
        InitializeComponent();
        ConfigureGrid();

        cmbRoute.Items.Clear();
        foreach (var route in AppConfig.Current.Routes.RouteNumbers)
            cmbRoute.Items.Add(route);

        cmbRoute.DropDownStyle = ComboBoxStyle.DropDownList;
        if (cmbRoute.Items.Count > 0)
            cmbRoute.SelectedIndex = 0;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        if (!DesignMode)
            _ = ReloadAsync();
    }

    private int SelectedRoute => cmbRoute.SelectedItem is int route ? route : 0;

    // ---- Grid ----------------------------------------------------------------

    /// <summary>
    /// Sets grid behaviour once, in code, rather than relying on the columns
    /// existing. Column headers are applied after binding, when they do.
    /// </summary>
    private void ConfigureGrid()
    {
        dgvNodes.AutoGenerateColumns = true;
        dgvNodes.ReadOnly = true;
        dgvNodes.AllowUserToAddRows = false;
        dgvNodes.AllowUserToDeleteRows = false;
        dgvNodes.AllowUserToResizeRows = false;
        dgvNodes.MultiSelect = false;
        dgvNodes.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        dgvNodes.RowHeadersVisible = false;
        dgvNodes.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

        // Double-click a row to edit: the obvious gesture, and it was missing.
        dgvNodes.CellDoubleClick += async (_, args) =>
        {
            if (args.RowIndex >= 0)
                await EditSelectedAsync();
        };
    }

    private void ApplyColumnLayout()
    {
        if (dgvNodes.Columns.Count == 0)
            return;

        void Configure(string name, string header, int displayIndex, int? width = null, bool fill = false)
        {
            var column = dgvNodes.Columns[name];
            if (column is null)
                return;

            column.HeaderText = header;
            column.DisplayIndex = displayIndex;

            if (fill)
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            else if (width.HasValue)
                column.Width = width.Value;
        }

        Configure(nameof(NodeListItem.Route), "Route", 0, 70);
        Configure(nameof(NodeListItem.PanelLocation), "Panel", 1, 70);
        Configure(nameof(NodeListItem.PanelSerialNumber), "Panel Serial No.", 2, 130);
        Configure(nameof(NodeListItem.NodeNumber), "Node No.", 3, 90);
        Configure(nameof(NodeListItem.LocalIPAddress), "Local IP Address", 4, 140);
        Configure(nameof(NodeListItem.IsCalibrated), "Calibrated", 5, 90);
        Configure(nameof(NodeListItem.Description), "Description", 6, fill: true);
    }

    /// <summary>
    /// Highlights uncalibrated rows so the outstanding commissioning work is
    /// visible at a glance instead of requiring a column-by-column read.
    /// </summary>
    private void ApplyRowStyling()
    {
        foreach (DataGridViewRow row in dgvNodes.Rows)
        {
            if (row.DataBoundItem is not NodeListItem item)
                continue;

            if (!item.IsCalibrated)
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 249, 219);
                row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(214, 190, 96);
            }
        }
    }

    // ---- Loading -------------------------------------------------------------

    private async Task ReloadAsync()
    {
        if (_loading || SelectedRoute == 0)
            return;

        _loading = true;

        try
        {
            using var busy = new BusyScope(panel1);

            var nodes = await NodeRepository.GetByRouteAsync(SelectedRoute, _cts.Token);

            if (_cts.IsCancellationRequested || IsDisposed)
                return;

            dgvNodes.DataSource = null;
            dgvNodes.DataSource = nodes;

            ApplyColumnLayout();
            ApplyRowStyling();
            UpdateActionAvailability();

            label1.Text = nodes.Count == 0
                ? $"Route {SelectedRoute}: no nodes assigned yet"
                : $"Route {SelectedRoute}: {nodes.Count} node(s), " +
                  $"{nodes.Count(n => !n.IsCalibrated)} awaiting calibration";
        }
        catch (OperationCanceledException)
        {
            // The control was closed mid-load; nothing to report.
        }
        catch (DataAccessException ex)
        {
            UserDialog.DataError(this, ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateActionAvailability()
    {
        var hasSelection = dgvNodes.CurrentRow?.DataBoundItem is NodeListItem;
        btnEdit.Enabled = hasSelection;
        btnDelete.Enabled = hasSelection;
    }

    private NodeListItem? SelectedItem => dgvNodes.CurrentRow?.DataBoundItem as NodeListItem;

    // ---- Handlers ------------------------------------------------------------

    private async void cmbRoute_SelectedIndexChanged(object sender, EventArgs e) => await ReloadAsync();

    private async void btnAdd_Click(object sender, EventArgs e)
    {
        if (SelectedRoute == 0)
            return;

        try
        {
            List<Area> availablePanels;
            using (new BusyScope(btnAdd))
                availablePanels = await NodeRepository.GetUnassignedPanelsAsync(SelectedRoute, _cts.Token);

            if (availablePanels.Count == 0)
            {
                UserDialog.Info(this,
                    $"Every panel on route {SelectedRoute} already has a node assigned.\n\n" +
                    "Delete an existing node first, or choose another route.",
                    "No panels available");
                return;
            }

            using var dialog = new AddEditNodeForm(SelectedRoute, availablePanels);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null)
                return;

            var (ok, error) = await NodeRepository.AddAsync(dialog.Result, _cts.Token);
            if (!ok)
            {
                UserDialog.Warn(this, error ?? "The node could not be saved.", "Node not created");
                await ReloadAsync();
                return;
            }

            await SavePanelDescriptionAsync(dialog);
            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (DataAccessException ex)
        {
            UserDialog.DataError(this, ex);
        }
    }

    private async void btnEdit_Click(object sender, EventArgs e) => await EditSelectedAsync();

    private async Task EditSelectedAsync()
    {
        var selected = SelectedItem;
        if (selected is null)
        {
            UserDialog.Info(this, "Select a node in the list first.", "No node selected");
            return;
        }

        try
        {
            Node? node;
            Area? area;

            using (new BusyScope(btnEdit))
            {
                // Re-read both records: the grid is a snapshot and another
                // workstation may have changed or removed the node since it loaded.
                node = await NodeRepository.GetByPanelSerialNumberAsync(selected.PanelSerialNumber, _cts.Token);
                area = await AreaRepository.GetByPanelSerialNumberAsync(selected.PanelSerialNumber, _cts.Token);
            }

            if (node is null || area is null)
            {
                UserDialog.Warn(this,
                    "This node no longer exists. It may have been deleted from another workstation.",
                    "Node not found");
                await ReloadAsync();
                return;
            }

            using var dialog = new AddEditNodeForm(node, area);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null)
                return;

            var (ok, error) = await NodeRepository.UpdateAsync(dialog.Result, _cts.Token);
            if (!ok)
            {
                UserDialog.Warn(this, error ?? "The node could not be updated.", "Node not updated");
                await ReloadAsync();
                return;
            }

            await SavePanelDescriptionAsync(dialog);
            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (DataAccessException ex)
        {
            UserDialog.DataError(this, ex);
        }
    }

    private async void btnDelete_Click(object sender, EventArgs e)
    {
        var selected = SelectedItem;
        if (selected is null)
        {
            UserDialog.Info(this, "Select a node in the list first.", "No node selected");
            return;
        }

        var confirmed = UserDialog.ConfirmDestructive(this,
            $"Delete node {selected.NodeNumber} at panel {selected.PanelLocation} " +
            $"(serial {selected.PanelSerialNumber}, {selected.LocalIPAddress})?",
            "The node record and its stored calibration values will be removed. " +
            "The panel position itself is kept, and the physical controller is not changed — " +
            "it keeps running its current configuration until it is reconfigured.");

        if (!confirmed)
            return;

        try
        {
            Node? node;
            using (new BusyScope(btnDelete))
                node = await NodeRepository.GetByPanelSerialNumberAsync(selected.PanelSerialNumber, _cts.Token);

            if (node is null)
            {
                UserDialog.Info(this, "That node has already been removed.", "Already deleted");
                await ReloadAsync();
                return;
            }

            var (ok, error) = await NodeRepository.DeleteAsync(node.Id, _cts.Token);
            if (!ok)
                UserDialog.Warn(this, error ?? "The node could not be deleted.", "Node not deleted");

            await ReloadAsync();
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
    /// Writes the panel description edited in the dialog.
    ///
    /// A failure here is reported but not treated as fatal: the node itself has
    /// already been saved, and rolling that back over a cosmetic field would be
    /// worse than leaving the description stale.
    /// </summary>
    private async Task SavePanelDescriptionAsync(AddEditNodeForm dialog)
    {
        if (dialog.PanelSerialNumberForDescription <= 0)
            return;

        var (ok, error) = await AreaRepository.UpdateDescriptionAsync(
            dialog.PanelSerialNumberForDescription, dialog.PanelDescription, _cts.Token);

        if (!ok)
        {
            UserDialog.Warn(this,
                $"The node was saved, but its panel description was not updated.\n\n{error}",
                "Description not saved");
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        // The Designer file owns Dispose(bool) for this control, so cleanup hooks
        // in here instead of overriding it. Cancelling the token stops any query
        // still in flight from touching a disposed control when it returns.
        _cts.Cancel();
        base.OnHandleDestroyed(e);
    }
}
