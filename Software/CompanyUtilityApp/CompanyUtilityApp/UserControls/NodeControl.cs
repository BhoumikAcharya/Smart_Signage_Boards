using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using CompanyUtilityApp.ProgramFiles;

namespace CompanyUtilityApp
{
    public partial class NodeControl : UserControl
    {
        public NodeControl()
        {
            InitializeComponent();
            cmbRoute.Items.AddRange(new object[] { 1, 2, 3, 4 });
            cmbRoute.SelectedIndex = 0;
            LoadData();
        }

        // Refresh the DataGridView
        private void LoadData()
        {
            int route = (int)cmbRoute.SelectedItem;
            dgvNodes.DataSource = null;
            dgvNodes.DataSource = NodeRepository.GetAllNodesForRoute(route);
            ConfigureGridColumns();
        }

        private void ConfigureGridColumns()
        {
            if (dgvNodes.Columns.Count == 0) return;

            dgvNodes.Columns["Route"].HeaderText = "Route";
            dgvNodes.Columns["PanelLocation"].HeaderText = "Panel Location";
            dgvNodes.Columns["PanelSerialNumber"].HeaderText = "Panel Serial Number";
            dgvNodes.Columns["NodeNumber"].HeaderText = "Node Number";
            dgvNodes.Columns["LocalIPAddress"].HeaderText = "Local IP Address";
            dgvNodes.Columns["Description"].HeaderText = "Description";
            dgvNodes.Columns["Description"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            // Add these lines for the calibration checkbox column
            if (dgvNodes.Columns["Calibration"] != null)
            {
                dgvNodes.Columns["Calibration"].HeaderText = "Calibrated";
                dgvNodes.Columns["Calibration"].ReadOnly = true;   // prevents direct editing in the grid
            }

            // === Set column order ===
            dgvNodes.Columns["Route"].DisplayIndex = 0;
            dgvNodes.Columns["PanelLocation"].DisplayIndex = 1;
            dgvNodes.Columns["PanelSerialNumber"].DisplayIndex = 2;
            dgvNodes.Columns["NodeNumber"].DisplayIndex = 3;
            dgvNodes.Columns["LocalIPAddress"].DisplayIndex = 4;
            dgvNodes.Columns["Calibration"].DisplayIndex = 5;
            dgvNodes.Columns["Description"].DisplayIndex = 6;
        }

        private void cmbRoute_SelectedIndexChanged(object sender, EventArgs e)
        {
            LoadData();
        }

        private void btnAdd_Click(object sender, EventArgs e)
        {
            int route = (int)cmbRoute.SelectedItem;
            using (var form = new AddEditNodeForm(route))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    // Insert node
                    NodeRepository.AddNode(form.NodeData);

                    // Update Areas description if provided
                    if (!string.IsNullOrEmpty(form.AreaDescription))
                    {
                        // Find the Area ID by PanelSerialNumber
                        var area = AreaRepository.GetAreasByRoute(route)
                                    .FirstOrDefault(a => a.PanelSerialNumber == form.NodeData.PanelSerialNumber);
                        if (area != null)
                            AreaRepository.UpdateArea(area.Id, area.PanelSerialNumber, form.AreaDescription);
                    }
                    LoadData();
                }
            }
        }

        private void btnEdit_Click(object sender, EventArgs e)
        {
            if (dgvNodes.SelectedRows.Count == 0)
                return;

            NodeDisplayItem item = (NodeDisplayItem)dgvNodes.SelectedRows[0].DataBoundItem;
            if (item == null)
                return;

            // Fetch the full Node and Area records
            Node node = NodeRepository.GetNodeByPanelSerialNumber(item.PanelSerialNumber);
            Area area = AreaRepository.GetAreaByPanelSerialNumber(item.PanelSerialNumber);

            if (node == null || area == null)
            {
                MessageBox.Show("Could not load the node or area details. The record may have been deleted.",
                                "Edit Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LoadData(); // refresh the grid to remove stale entries
                return;
            }

            using (var form = new AddEditNodeForm(node, area))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    LoadData();
                }
            }
        }

        private void btnDelete_Click(object sender, EventArgs e)
        {
            if (dgvNodes.SelectedRows.Count == 0) return;
            NodeDisplayItem item = (NodeDisplayItem)dgvNodes.SelectedRows[0].DataBoundItem;

            var confirm = MessageBox.Show($"Delete node {item.NodeNumber}?", "Confirm Delete",
                                          MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            Node node = NodeRepository.GetNodeByPanelSerialNumber(item.PanelSerialNumber);
            if (node != null)
                NodeRepository.DeleteNode(node.Id);
            LoadData();
        }
    }
}
