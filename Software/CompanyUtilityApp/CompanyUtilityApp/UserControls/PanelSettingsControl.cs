using CompanyUtilityApp.ProgramFiles;   // contains NodeRepository, PanelSettingsModel, etc.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.ComponentModel.Design.ObjectSelectorEditor;

namespace CompanyUtilityApp.UserControls
{
    public partial class PanelSettingsControl : UserControl
    {
        private SerialPort _serialPort = null;
        private bool _updating = false;                    // prevents recursive dropdown events
        private List<PanelSettingsModel> _assignedNodes;   // all uncalibrated nodes for current route

        [System.ComponentModel.DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsAdmin
        {
            get => btnManualCalibrate.Visible;
            set
            {
                btnManualCalibrate.Visible = value;
                btnManualCalibrate.Enabled = value;
            }
        }

        // ------------------ Initialization of Constructors. 
        public PanelSettingsControl()
        {
            InitializeComponent();

            // Route dropdown initialisation
            cmbRoute.Items.AddRange(new object[] { 1, 2, 3, 4 });
            cmbRoute.SelectedIndex = 0;

            // OTA-WIFI Components
            chkOTA.Visible = true;   // visible to everyone
            txtTargetIP.Enabled = false;

            // Manual calibration panel hidden by default
            pnlManual.Visible = false;

            // Serial monitor UI state
            btnConnect.Enabled = true;
            btnDisconnect.Enabled = false;
            btnDeploy.Enabled = true;          // ← CHANGED from false to true
            LoadComPorts();
        }

        // ------------------ Com Ports functions. 
        private void LoadComPorts()
        {
            // Remember the currently selected port (if any) to reselect after refresh
            string previous = cmbSerialPort.Text;
            cmbSerialPort.Items.Clear();
            cmbSerialPort.Items.AddRange(SerialPort.GetPortNames());
            if (cmbSerialPort.Items.Count > 0)
            {
                if (!string.IsNullOrEmpty(previous) && cmbSerialPort.Items.Contains(previous))
                    cmbSerialPort.SelectedItem = previous;
                else
                    cmbSerialPort.SelectedIndex = 0;
            }
        }

        private void CloseSerialPort()
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
                _serialPort.DataReceived -= SerialPort_DataReceived;
                _serialPort = null;
            }
        }

        // ------------------ Serial Monitor and Button Methods.
        private void btnRefresh_Click(object sender, EventArgs e)
        {
            LoadComPorts();
            // IMPORTANT: Do NOT change any button enable states here
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            if (_serialPort != null && _serialPort.IsOpen) return;

            string port = cmbSerialPort.Text;
            if (string.IsNullOrEmpty(port))
            {
                MessageBox.Show("Select a COM port.", "No Port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _serialPort = new SerialPort(port, 115200);
                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.Open();

                btnConnect.Enabled = false;
                btnDisconnect.Enabled = true;
                cmbSerialPort.Enabled = false;
                btnDeploy.Enabled = false;          // ← disable deploy while connected

                AppendToSerialOutput($"Connected to {port}\n");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open COM port: {ex.Message}", "Connection Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _serialPort = null;
            }
        }

        private void btnDisconnect_Click(object sender, EventArgs e)
        {
            CloseSerialPort();

            btnConnect.Enabled = true;
            btnDisconnect.Enabled = false;
            cmbSerialPort.Enabled = true;
            btnDeploy.Enabled = true;               // ← re‑enable deploy

            AppendToSerialOutput("Disconnected.\n");
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;
            string data = _serialPort.ReadExisting();
            if (!string.IsNullOrEmpty(data))
            {
                // Must update UI on the main thread
                this.Invoke((MethodInvoker)(() =>
                {
                    txtSerialOutput.AppendText(data);
                    txtSerialOutput.SelectionStart = txtSerialOutput.TextLength;
                    txtSerialOutput.ScrollToCaret();
                }));
            }
        }

        private void AppendToSerialOutput(string text)
        {
            txtSerialOutput.AppendText(text);
            txtSerialOutput.SelectionStart = txtSerialOutput.TextLength;
            txtSerialOutput.ScrollToCaret();
        }

        // ------------------ Route and Panel Logic.
        private void cmbRoute_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_updating) return;
            int route = (int)cmbRoute.SelectedItem;
            LoadAssignedNodes(route);
        }

        private void LoadAssignedNodes(int route)
        {
            _assignedNodes = NodeRepository.GetUncalibratedNodesForRoute(route);
            Debug.WriteLine($"Uncalibrated nodes for route {route}: {_assignedNodes.Count}");

            // Populate panel location dropdown
            cmbPanelLocation.DataSource = null;
            cmbPanelLocation.DataSource = _assignedNodes;
            cmbPanelLocation.DisplayMember = "PanelLocation";

            // Populate IP address dropdown
            cmbIPAddress.DataSource = null;
            cmbIPAddress.DataSource = _assignedNodes.Select(n => n.IPAddress).ToList();

            if (_assignedNodes.Count == 0)
            {
                cmbPanelLocation.Text = "";
                cmbIPAddress.Text = "";
                txtDescription.Text = "";
                txtPanelSerialNumber.Text = "";
                return;
            }

            // Select the first item – this will trigger cmbPanelLocation_SelectedIndexChanged
            cmbPanelLocation.SelectedIndex = 0;
        }

        private void cmbPanelLocation_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_updating) return;
            if (cmbPanelLocation.SelectedItem == null) return;

            var selected = (PanelSettingsModel)cmbPanelLocation.SelectedItem;
            UpdateUIFromSelectedNode(selected);
        }

        private void cmbIPAddress_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_updating) return;
            if (cmbIPAddress.SelectedItem == null) return;

            string ip = cmbIPAddress.SelectedItem.ToString();
            var match = _assignedNodes.FirstOrDefault(n => n.IPAddress == ip);
            if (match != null)
            {
                _updating = true;
                cmbPanelLocation.SelectedItem = match;
                UpdateUIFromSelectedNode(match);
                _updating = false;
            }
        }

        private void UpdateUIFromSelectedNode(PanelSettingsModel node)
        {
            _updating = true;
            cmbIPAddress.Text = node.IPAddress;         // keep the IP combo text in sync
            txtDescription.Text = node.Description ?? "";
            txtPanelSerialNumber.Text = node.PanelSerialNumber.ToString();
            _updating = false;
        }

        // ------------------ Deploy btn and send data method.
        private void btnManualCalibrate_Click(object sender, EventArgs e)
        {
            pnlManual.Visible = !pnlManual.Visible;
        }

        private async void btnDeploy_Click(object sender, EventArgs e)
        {
            // Get the currently selected node from the panel location dropdown
            if (cmbPanelLocation.SelectedItem == null)
            {
                MessageBox.Show("Please select a node first.", "No Node", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedNode = (PanelSettingsModel)cmbPanelLocation.SelectedItem;

            // ---- Calibration values (optional) ----
            decimal? sens1 = null;
            decimal? sens2 = null;
            decimal? battCal = null;

            if (pnlManual.Visible)
            {
                // Validate all three fields
                if (!decimal.TryParse(txtSens1.Text, out decimal val1) ||
                    !decimal.TryParse(txtSens2.Text, out decimal val2) ||
                    !decimal.TryParse(txtCalib.Text, out decimal val3))
                {
                    MessageBox.Show("Please enter valid numeric calibration values.",
                        "Invalid Calibration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                sens1 = val1;
                sens2 = val2;
                battCal = val3;
            }

            // ---- Build JSON payload ----
            var payload = new
            {
                Route = selectedNode.Route,
                PanelLocation = selectedNode.PanelLocation,
                IPAddress = selectedNode.IPAddress,
                Description = selectedNode.Description,
                PanelSerialNumber = selectedNode.PanelSerialNumber,
                SENSITIVITY_1 = sens1,
                SENSITIVITY_2 = sens2,
                Battery_Calibration = battCal,
                Restart = true
            };

            var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            string json = JsonSerializer.Serialize(payload, options) + "\n";

            if (chkOTA.Checked)
            {
                await SendOverWiFi(json, selectedNode);
            }
            else
            {
                SendData(json, selectedNode);   // existing serial method
            }
        }

        private void SendData(string data, PanelSettingsModel selectedNode)
        {
            // Use the existing serial port if available, otherwise open a temporary one
            bool needTemporary = (_serialPort == null || !_serialPort.IsOpen);
            SerialPort port = null;
            bool tempPort = false;
            try
            {
                if (needTemporary)
                {
                    if (string.IsNullOrEmpty(cmbSerialPort.Text))
                    {
                        MessageBox.Show("Select a COM port.", "No Port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    port = new SerialPort(cmbSerialPort.Text, 115200);
                    port.Open();
                    tempPort = true;
                }
                else
                {
                    port = _serialPort;
                }

                port.Write(data);
                MessageBox.Show("Configuration sent successfully.", "Deploy",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

                // Mark the node as calibrated in the database
                NodeRepository.MarkCalibrated(selectedNode.PanelSerialNumber);

                // Refresh the dropdown to remove the now‑calibrated node
                int route = (int)cmbRoute.SelectedItem;
                LoadAssignedNodes(route);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Serial communication error: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (tempPort && port != null && port.IsOpen)
                    port.Close();
            }
        }



        // ------------------ This method: Dispose is handled in the Designer file
        //protected override void Dispose(bool disposing) { }

        // ------------------ OTA-wifi Methods

        private void chkOTA_CheckedChanged(object sender, EventArgs e)
        {
            bool ota = chkOTA.Checked;
            label5.Visible = !ota;          // disable serial controls when OTA active
            cmbSerialPort.Visible = !ota;
            btnRefresh.Visible = !ota;
            grpSerialMonitor.Visible = !ota;

            txtTargetIP.Enabled = ota;
            if (ota)
            {
                // Disconnect any active serial connection
                if (_serialPort != null && _serialPort.IsOpen)
                    btnDisconnect_Click(null, EventArgs.Empty);
            }
        }

        private async Task SendOverWiFi(string json, PanelSettingsModel selectedNode)
        {
            string ip = txtTargetIP.Text.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                MessageBox.Show("Enter the ESP32 IP address.", "OTA", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                try
                {
                    string url = $"http://{ip}/config";
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    HttpResponseMessage response = await client.PostAsync(url, content);
                    if (response.IsSuccessStatusCode)
                    {
                        MessageBox.Show("OTA configuration sent successfully.", "OTA Deploy", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        NodeRepository.MarkCalibrated(selectedNode.PanelSerialNumber);
                        int route = (int)cmbRoute.SelectedItem;
                        LoadAssignedNodes(route);
                    }
                    else
                    {
                        MessageBox.Show($"ESP32 responded with status: {response.StatusCode}", "OTA Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                catch (TaskCanceledException)
                {
                    MessageBox.Show("OTA request timed out. Check IP address and network.", "OTA Timeout", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"OTA error: {ex.Message}", "OTA Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

    }
}