using CompanyUtilityApp.Configuration;
using CompanyUtilityApp.Data;
using CompanyUtilityApp.Forms;
using CompanyUtilityApp.Models;
using CompanyUtilityApp.UI;

namespace CompanyUtilityApp.UserControls;

/// <summary>
/// Area management: lists every panel position on a route and allows the serial
/// number and description to be corrected.
///
/// The Areas table is seeded with all route/panel combinations and is never
/// inserted into or deleted from, so this screen offers edit only.
/// </summary>
public partial class AreaControl : UserControl
{
    private readonly CancellationTokenSource _cts = new();
    private bool _loading;

    public AreaControl()
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

    private Area? SelectedItem => dgvAreas.CurrentRow?.DataBoundItem as Area;

    private void ConfigureGrid()
    {
        dgvAreas.AutoGenerateColumns = true;
        dgvAreas.ReadOnly = true;
        dgvAreas.AllowUserToAddRows = false;
        dgvAreas.AllowUserToDeleteRows = false;
        dgvAreas.AllowUserToResizeRows = false;
        dgvAreas.MultiSelect = false;
        dgvAreas.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        dgvAreas.RowHeadersVisible = false;

        dgvAreas.CellDoubleClick += async (_, args) =>
        {
            if (args.RowIndex >= 0)
                await EditSelectedAsync();
        };
    }

    private void ApplyColumnLayout()
    {
        if (dgvAreas.Columns.Count == 0)
            return;

        // Surrogate key: meaningless to the operator.
        if (dgvAreas.Columns[nameof(Area.Id)] is { } idColumn)
            idColumn.Visible = false;

        // Computed display helper, not a stored field.
        if (dgvAreas.Columns[nameof(Area.DisplayLabel)] is { } labelColumn)
            labelColumn.Visible = false;

        void Configure(string name, string header, int displayIndex, int? width = null, bool fill = false)
        {
            var column = dgvAreas.Columns[name];
            if (column is null)
                return;

            column.HeaderText = header;
            column.DisplayIndex = displayIndex;

            if (fill)
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            else if (width.HasValue)
                column.Width = width.Value;
        }

        Configure(nameof(Area.Route), "Route", 0, 70);
        Configure(nameof(Area.PanelLocation), "Panel Location", 1, 110);
        Configure(nameof(Area.HoldingRegister), "Holding Register", 2, 130);
        Configure(nameof(Area.PanelSerialNumber), "Panel Serial No.", 3, 130);
        Configure(nameof(Area.Description), "Panel Location Description", 4, fill: true);
    }

    private async Task ReloadAsync()
    {
        if (_loading || SelectedRoute == 0)
            return;

        _loading = true;

        try
        {
            using var busy = new BusyScope(panel1);

            var areas = await AreaRepository.GetByRouteAsync(SelectedRoute, _cts.Token);

            if (_cts.IsCancellationRequested || IsDisposed)
                return;

            dgvAreas.DataSource = null;
            dgvAreas.DataSource = areas;

            ApplyColumnLayout();

            btnEdit.Enabled = areas.Count > 0;
            label1.Text = $"Route {SelectedRoute}: {areas.Count} panel position(s), " +
                          $"{areas.Count(a => !string.IsNullOrWhiteSpace(a.Description))} described";
        }
        catch (OperationCanceledException)
        {
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

    private async void cmbRoute_SelectedIndexChanged(object sender, EventArgs e) => await ReloadAsync();

    private async void btnEdit_Click(object sender, EventArgs e) => await EditSelectedAsync();

    private async Task EditSelectedAsync()
    {
        var selected = SelectedItem;
        if (selected is null)
        {
            UserDialog.Info(this, "Select a panel in the list first.", "No panel selected");
            return;
        }

        using var dialog = new EditAreaForm(selected);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var (ok, error) = await AreaRepository.UpdateAsync(
                selected.Id, dialog.PanelSerialNumber, dialog.Description, _cts.Token);

            if (!ok)
                UserDialog.Warn(this, error ?? "The panel could not be updated.", "Panel not updated");

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

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _cts.Cancel();
        base.OnHandleDestroyed(e);
    }
}
