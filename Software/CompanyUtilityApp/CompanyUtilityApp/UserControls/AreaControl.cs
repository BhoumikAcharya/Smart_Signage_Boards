using CompanyUtilityApp.ProgramFiles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace CompanyUtilityApp
{
    public partial class AreaControl : UserControl
    {
        public AreaControl()
        {
            InitializeComponent();
            cmbRoute.Items.AddRange(new object[] { 1, 2, 3, 4 });
            cmbRoute.SelectedIndex = 0;
            LoadAreas();
        }

        private void LoadAreas()
        {
            int route = (int)cmbRoute.SelectedItem;
            dgvAreas.DataSource = null;
            dgvAreas.DataSource = AreaRepository.GetAreasByRoute(route);
            ConfigureGridColumns();
        }

        private void ConfigureGridColumns()
        {
            if (dgvAreas.Columns.Count == 0) return;

            // Hide the internal Id column
            dgvAreas.Columns["Id"].Visible = false;

            // Set column headers
            dgvAreas.Columns["Route"].HeaderText = "Route";
            dgvAreas.Columns["PanelLocation"].HeaderText = "Panel Location";
            dgvAreas.Columns["HoldingRegister"].HeaderText = "Holding Register";
            dgvAreas.Columns["PanelSerialNumber"].HeaderText = "Panel Serial Number";
            dgvAreas.Columns["Description"].HeaderText = "Panel Location Description";

            // Optional widths
            dgvAreas.Columns["Description"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        }

        private void cmbRoute_SelectedIndexChanged(object sender, EventArgs e)
        {
            LoadAreas();
        }

        private void btnEdit_Click(object sender, EventArgs e)
        {
            if (dgvAreas.SelectedRows.Count == 0) return;
            Area selected = (Area)dgvAreas.SelectedRows[0].DataBoundItem;
            if (selected == null) return;

            using (var dialog = new EditAreaForm(selected))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    // Update the database
                    bool success = AreaRepository.UpdateArea(selected.Id, dialog.PanelSerialNumber, dialog.Description);
                    if (!success)
                    {
                        MessageBox.Show("The entered Panel Serial Number is already in use.", "Duplicate Serial",
                                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    LoadAreas(); // Refresh grid
                }
            }
        }
    }
}
