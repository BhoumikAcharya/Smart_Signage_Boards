using CompanyUtilityApp.ProgramFiles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace CompanyUtilityApp
{
    public partial class AddEditNodeForm : Form
    {
        // Add mode fields, resulting node data, Existing node, Description update for Areas
        private List<Area> _availableAreas;
        private bool _isAddMode;
        public Node NodeData { get; private set; }
        private Node _existingNode;
        private Area _existingArea;
        public string AreaDescription { get; private set; }

        public AddEditNodeForm(int route)
        {
            InitializeComponent();
            chkCalibration.Checked = false;
            _isAddMode = true;
            txtRoute.Text = route.ToString();
            LoadAvailableAreas(route);
            WireUpLinkedDropdowns();
        }

        // Constructor for EDIT
        public AddEditNodeForm(Node node, Area area)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (area == null) throw new ArgumentNullException(nameof(area));

            InitializeComponent();
            _isAddMode = false;
            _existingNode = node;
            _existingArea = area;

            // Route (read‑only)
            txtRoute.Text = area.Route.ToString();

            // Panel Location combo – load only the stored value and disable
            cmbPanelLocation.Items.Clear();
            cmbPanelLocation.Items.Add(area.PanelLocation);
            cmbPanelLocation.SelectedIndex = 0;
            cmbPanelLocation.Enabled = false;

            // Panel Serial Number combo – load only the stored value and disable
            cmbPanelSerialNumber.Items.Clear();
            cmbPanelSerialNumber.Items.Add(area.PanelSerialNumber);
            cmbPanelSerialNumber.SelectedIndex = 0;
            cmbPanelSerialNumber.Enabled = false;

            // Holding Register (read‑only)
            txtHR.Text = area.HoldingRegister.ToString();
            txtHR.Enabled = false;

            // Description (editable) – load from Areas
            txtDescription.Text = area.Description ?? "";

            // Node Number (editable)
            txtNodeNumber.Text = node.NodeNumber.ToString();

            // Local IP Address (editable)
            txtIPAddress.Text = node.LocalIPAddress;

            // Calibration checkbox (editable)
            chkCalibration.Checked = node.Calibration;
        }

        private void LoadAvailableAreas(int route)
        {
            _availableAreas = NodeRepository.GetAvailableAreasForRoute(route);
            cmbPanelLocation.DataSource = null;
            cmbPanelLocation.DataSource = _availableAreas;
            cmbPanelLocation.DisplayMember = "PanelLocation";
            cmbPanelLocation.SelectedIndex = -1;

            cmbPanelSerialNumber.DataSource = null;
            cmbPanelSerialNumber.DataSource = _availableAreas;
            cmbPanelSerialNumber.DisplayMember = "PanelSerialNumber";
            cmbPanelSerialNumber.SelectedIndex = -1;
        }

        private void WireUpLinkedDropdowns()
        {
            cmbPanelLocation.SelectedIndexChanged += (s, e) =>
            {
                if (cmbPanelLocation.SelectedItem is Area selected)
                {
                    cmbPanelSerialNumber.SelectedItem = selected;
                    txtHR.Text = selected.HoldingRegister.ToString();
                }
            };

            cmbPanelSerialNumber.SelectedIndexChanged += (s, e) =>
            {
                if (cmbPanelSerialNumber.SelectedItem is Area selected)
                {
                    cmbPanelLocation.SelectedItem = selected;
                    txtHR.Text = selected.HoldingRegister.ToString();
                }
            };
        }

        private void btnSave_Click(object sender, EventArgs e)
        {

            // Validate IP
            string ip = txtIPAddress.Text.Trim();
            if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out _))
            {
                MessageBox.Show("Enter a valid IPv4 address.");
                txtIPAddress.Focus();
                return;
            }

            // Validate NodeNumber
            if (!int.TryParse(txtNodeNumber.Text.Trim(), out int nodeNum) || nodeNum <= 0)
            {
                MessageBox.Show("Node Number must be a positive integer.");
                txtNodeNumber.Focus();
                return;
            }

            if (_isAddMode)
            {
                // Check unique constraints
                int? excludeId = null; // new node
                if (NodeRepository.NodeNumberExists(nodeNum, excludeId))
                {
                    MessageBox.Show("This Node Number is already in use.");
                    txtNodeNumber.Focus();
                    return;
                }
                if (NodeRepository.LocalIPExists(ip, excludeId))
                {
                    MessageBox.Show("This IP address is already assigned.");
                    txtIPAddress.Focus();
                    return;
                }

                // Build NodeData
                var selectedArea = (Area)cmbPanelLocation.SelectedItem;
                NodeData = new Node
                {
                    NodeNumber = nodeNum,
                    PanelSerialNumber = selectedArea.PanelSerialNumber,
                    LocalIPAddress = ip
                };
                NodeData.Calibration = chkCalibration.Checked;

                // Description to be saved to Areas
                AreaDescription = string.IsNullOrEmpty(txtDescription.Text.Trim()) ? null : txtDescription.Text.Trim();
                DialogResult = DialogResult.OK;
                Close();
            }
            else // Edit mode
            {
                // Check uniqueness excluding current node
                int? excludeId = _existingNode.Id;
                if (NodeRepository.NodeNumberExists(nodeNum, excludeId))
                {
                    MessageBox.Show("This Node Number is already in use.");
                    txtNodeNumber.Focus();
                    return;
                }
                if (NodeRepository.LocalIPExists(ip, excludeId))
                {
                    MessageBox.Show("This IP address is already assigned.");
                    txtIPAddress.Focus();
                    return;
                }

                // Update existing node
                _existingNode.NodeNumber = nodeNum;
                _existingNode.LocalIPAddress = ip;
                _existingNode.Calibration = chkCalibration.Checked;
                NodeRepository.UpdateNode(_existingNode);

                // Save description to Areas
                AreaDescription = string.IsNullOrEmpty(txtDescription.Text.Trim()) ? null : txtDescription.Text.Trim();
                AreaRepository.UpdateArea(_existingArea.Id, _existingArea.PanelSerialNumber, AreaDescription);

                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }
}
