namespace CompanyUtilityApp.UserControls
{
    partial class PanelSettingsControl
    {
        /// <summary> 
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseSerialPort();
                if (components != null)
                    components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            label1 = new Label();
            label3 = new Label();
            btnManualCalibrate = new Button();
            cmbIPAddress = new ComboBox();
            cmbRoute = new ComboBox();
            label2 = new Label();
            label4 = new Label();
            cmbPanelLocation = new ComboBox();
            txtDescription = new TextBox();
            label9 = new Label();
            txtPanelSerialNumber = new TextBox();
            btnDeploy = new Button();
            label5 = new Label();
            cmbSerialPort = new ComboBox();
            chkOTA = new CheckBox();
            btnRefresh = new Button();
            panel1 = new Panel();
            txtTargetIP = new TextBox();
            label10 = new Label();
            pnlAction = new Panel();
            grpSerialMonitor = new GroupBox();
            panel5 = new Panel();
            txtSerialOutput = new TextBox();
            panel4 = new Panel();
            btnDisconnect = new Button();
            btnConnect = new Button();
            pnlManual = new Panel();
            txtSens1 = new TextBox();
            txtCalib = new TextBox();
            label8 = new Label();
            label6 = new Label();
            txtSens2 = new TextBox();
            label7 = new Label();
            panel1.SuspendLayout();
            pnlAction.SuspendLayout();
            grpSerialMonitor.SuspendLayout();
            panel5.SuspendLayout();
            panel4.SuspendLayout();
            pnlManual.SuspendLayout();
            SuspendLayout();
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(43, 44);
            label1.Name = "label1";
            label1.Size = new Size(51, 20);
            label1.TabIndex = 2;
            label1.Text = "Route:";
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new Point(295, 44);
            label3.Name = "label3";
            label3.Size = new Size(81, 20);
            label3.TabIndex = 4;
            label3.Text = "IP Address:";
            // 
            // btnManualCalibrate
            // 
            btnManualCalibrate.Location = new Point(74, 173);
            btnManualCalibrate.Name = "btnManualCalibrate";
            btnManualCalibrate.Size = new Size(147, 29);
            btnManualCalibrate.TabIndex = 10;
            btnManualCalibrate.Text = "ManualCalibrate";
            btnManualCalibrate.UseVisualStyleBackColor = true;
            btnManualCalibrate.Click += btnManualCalibrate_Click;
            // 
            // cmbIPAddress
            // 
            cmbIPAddress.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbIPAddress.FormattingEnabled = true;
            cmbIPAddress.Location = new Point(382, 41);
            cmbIPAddress.Name = "cmbIPAddress";
            cmbIPAddress.Size = new Size(151, 28);
            cmbIPAddress.TabIndex = 7;
            cmbIPAddress.SelectedIndexChanged += cmbIPAddress_SelectedIndexChanged;
            // 
            // cmbRoute
            // 
            cmbRoute.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbRoute.Location = new Point(100, 41);
            cmbRoute.Name = "cmbRoute";
            cmbRoute.Size = new Size(121, 28);
            cmbRoute.TabIndex = 13;
            cmbRoute.SelectedIndexChanged += cmbRoute_SelectedIndexChanged;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(268, 92);
            label2.Name = "label2";
            label2.Size = new Size(108, 20);
            label2.TabIndex = 3;
            label2.Text = "Panel Location:";
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(565, 44);
            label4.Name = "label4";
            label4.Size = new Size(88, 20);
            label4.TabIndex = 5;
            label4.Text = "Description:";
            // 
            // cmbPanelLocation
            // 
            cmbPanelLocation.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPanelLocation.FormattingEnabled = true;
            cmbPanelLocation.Location = new Point(382, 89);
            cmbPanelLocation.Name = "cmbPanelLocation";
            cmbPanelLocation.Size = new Size(151, 28);
            cmbPanelLocation.TabIndex = 6;
            cmbPanelLocation.SelectedIndexChanged += cmbPanelLocation_SelectedIndexChanged;
            // 
            // txtDescription
            // 
            txtDescription.Location = new Point(659, 41);
            txtDescription.Multiline = true;
            txtDescription.Name = "txtDescription";
            txtDescription.ReadOnly = true;
            txtDescription.Size = new Size(422, 57);
            txtDescription.TabIndex = 8;
            // 
            // label9
            // 
            label9.AutoSize = true;
            label9.Location = new Point(614, 122);
            label9.Name = "label9";
            label9.Size = new Size(39, 20);
            label9.TabIndex = 11;
            label9.Text = "PSN:";
            // 
            // txtPanelSerialNumber
            // 
            txtPanelSerialNumber.Location = new Point(659, 119);
            txtPanelSerialNumber.Name = "txtPanelSerialNumber";
            txtPanelSerialNumber.ReadOnly = true;
            txtPanelSerialNumber.Size = new Size(125, 27);
            txtPanelSerialNumber.TabIndex = 12;
            // 
            // btnDeploy
            // 
            btnDeploy.Location = new Point(659, 173);
            btnDeploy.Name = "btnDeploy";
            btnDeploy.Size = new Size(94, 29);
            btnDeploy.TabIndex = 21;
            btnDeploy.Text = "Deploy";
            btnDeploy.UseVisualStyleBackColor = true;
            btnDeploy.Click += btnDeploy_Click;
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Location = new Point(279, 28);
            label5.Name = "label5";
            label5.Size = new Size(79, 20);
            label5.TabIndex = 20;
            label5.Text = "Serial Port:";
            // 
            // cmbSerialPort
            // 
            cmbSerialPort.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSerialPort.FormattingEnabled = true;
            cmbSerialPort.Location = new Point(364, 25);
            cmbSerialPort.Name = "cmbSerialPort";
            cmbSerialPort.Size = new Size(151, 28);
            cmbSerialPort.TabIndex = 22;
            // 
            // chkOTA
            // 
            chkOTA.AutoSize = true;
            chkOTA.Location = new Point(1124, 50);
            chkOTA.Name = "chkOTA";
            chkOTA.Size = new Size(86, 24);
            chkOTA.TabIndex = 14;
            chkOTA.Text = "Ethernet";
            chkOTA.UseVisualStyleBackColor = true;
            chkOTA.CheckedChanged += chkOTA_CheckedChanged;
            // 
            // btnRefresh
            // 
            btnRefresh.Location = new Point(521, 24);
            btnRefresh.Name = "btnRefresh";
            btnRefresh.Size = new Size(28, 29);
            btnRefresh.TabIndex = 23;
            btnRefresh.Text = "⟳";
            btnRefresh.UseVisualStyleBackColor = true;
            btnRefresh.Click += btnRefresh_Click;
            // 
            // panel1
            // 
            panel1.Controls.Add(txtTargetIP);
            panel1.Controls.Add(label10);
            panel1.Controls.Add(chkOTA);
            panel1.Controls.Add(btnDeploy);
            panel1.Controls.Add(txtPanelSerialNumber);
            panel1.Controls.Add(label9);
            panel1.Controls.Add(txtDescription);
            panel1.Controls.Add(cmbPanelLocation);
            panel1.Controls.Add(label4);
            panel1.Controls.Add(label2);
            panel1.Controls.Add(cmbRoute);
            panel1.Controls.Add(cmbIPAddress);
            panel1.Controls.Add(btnManualCalibrate);
            panel1.Controls.Add(label3);
            panel1.Controls.Add(label1);
            panel1.Dock = DockStyle.Top;
            panel1.Location = new Point(0, 0);
            panel1.Name = "panel1";
            panel1.Size = new Size(1400, 239);
            panel1.TabIndex = 3;
            // 
            // txtTargetIP
            // 
            txtTargetIP.Enabled = false;
            txtTargetIP.Location = new Point(1254, 85);
            txtTargetIP.Name = "txtTargetIP";
            txtTargetIP.Size = new Size(125, 27);
            txtTargetIP.TabIndex = 25;
            // 
            // label10
            // 
            label10.AutoSize = true;
            label10.Location = new Point(1124, 88);
            label10.Name = "label10";
            label10.Size = new Size(124, 20);
            label10.TabIndex = 24;
            label10.Text = "Esp32 IP Address:";
            // 
            // pnlAction
            // 
            pnlAction.Controls.Add(grpSerialMonitor);
            pnlAction.Controls.Add(pnlManual);
            pnlAction.Dock = DockStyle.Fill;
            pnlAction.Location = new Point(0, 239);
            pnlAction.Name = "pnlAction";
            pnlAction.Size = new Size(1400, 561);
            pnlAction.TabIndex = 4;
            // 
            // grpSerialMonitor
            // 
            grpSerialMonitor.Controls.Add(panel5);
            grpSerialMonitor.Controls.Add(panel4);
            grpSerialMonitor.Dock = DockStyle.Fill;
            grpSerialMonitor.Location = new Point(376, 0);
            grpSerialMonitor.Name = "grpSerialMonitor";
            grpSerialMonitor.Size = new Size(1024, 561);
            grpSerialMonitor.TabIndex = 22;
            grpSerialMonitor.TabStop = false;
            grpSerialMonitor.Text = "Serial Monitor";
            // 
            // panel5
            // 
            panel5.Controls.Add(txtSerialOutput);
            panel5.Dock = DockStyle.Fill;
            panel5.Location = new Point(3, 100);
            panel5.Name = "panel5";
            panel5.Size = new Size(1018, 458);
            panel5.TabIndex = 4;
            // 
            // txtSerialOutput
            // 
            txtSerialOutput.Dock = DockStyle.Fill;
            txtSerialOutput.Location = new Point(0, 0);
            txtSerialOutput.Multiline = true;
            txtSerialOutput.Name = "txtSerialOutput";
            txtSerialOutput.ReadOnly = true;
            txtSerialOutput.ScrollBars = ScrollBars.Vertical;
            txtSerialOutput.Size = new Size(1018, 458);
            txtSerialOutput.TabIndex = 2;
            // 
            // panel4
            // 
            panel4.Controls.Add(btnDisconnect);
            panel4.Controls.Add(btnConnect);
            panel4.Controls.Add(btnRefresh);
            panel4.Controls.Add(cmbSerialPort);
            panel4.Controls.Add(label5);
            panel4.Dock = DockStyle.Top;
            panel4.Location = new Point(3, 23);
            panel4.Name = "panel4";
            panel4.Size = new Size(1018, 77);
            panel4.TabIndex = 3;
            // 
            // btnDisconnect
            // 
            btnDisconnect.Enabled = false;
            btnDisconnect.Location = new Point(140, 24);
            btnDisconnect.Name = "btnDisconnect";
            btnDisconnect.Size = new Size(94, 29);
            btnDisconnect.TabIndex = 1;
            btnDisconnect.Text = "Disconnect";
            btnDisconnect.UseVisualStyleBackColor = true;
            btnDisconnect.Click += btnDisconnect_Click;
            // 
            // btnConnect
            // 
            btnConnect.Location = new Point(14, 24);
            btnConnect.Name = "btnConnect";
            btnConnect.Size = new Size(94, 29);
            btnConnect.TabIndex = 0;
            btnConnect.Text = "Connect";
            btnConnect.UseVisualStyleBackColor = true;
            btnConnect.Click += btnConnect_Click;
            // 
            // pnlManual
            // 
            pnlManual.Controls.Add(txtSens1);
            pnlManual.Controls.Add(txtCalib);
            pnlManual.Controls.Add(label8);
            pnlManual.Controls.Add(label6);
            pnlManual.Controls.Add(txtSens2);
            pnlManual.Controls.Add(label7);
            pnlManual.Dock = DockStyle.Left;
            pnlManual.Location = new Point(0, 0);
            pnlManual.Name = "pnlManual";
            pnlManual.Size = new Size(376, 561);
            pnlManual.TabIndex = 0;
            pnlManual.Visible = false;
            // 
            // txtSens1
            // 
            txtSens1.Location = new Point(210, 49);
            txtSens1.Name = "txtSens1";
            txtSens1.Size = new Size(125, 27);
            txtSens1.TabIndex = 21;
            // 
            // txtCalib
            // 
            txtCalib.Location = new Point(210, 136);
            txtCalib.Name = "txtCalib";
            txtCalib.Size = new Size(125, 27);
            txtCalib.TabIndex = 23;
            // 
            // label8
            // 
            label8.AutoSize = true;
            label8.Location = new Point(43, 52);
            label8.Name = "label8";
            label8.Size = new Size(161, 20);
            label8.TabIndex = 18;
            label8.Text = "ACS Sensitivity Value 1:";
            // 
            // label6
            // 
            label6.AutoSize = true;
            label6.Location = new Point(68, 139);
            label6.Name = "label6";
            label6.Size = new Size(136, 20);
            label6.TabIndex = 20;
            label6.Text = "Battery Calibration:";
            // 
            // txtSens2
            // 
            txtSens2.Location = new Point(210, 93);
            txtSens2.Name = "txtSens2";
            txtSens2.Size = new Size(125, 27);
            txtSens2.TabIndex = 22;
            // 
            // label7
            // 
            label7.AutoSize = true;
            label7.Location = new Point(43, 96);
            label7.Name = "label7";
            label7.Size = new Size(161, 20);
            label7.TabIndex = 19;
            label7.Text = "ACS Sensitivity Value 2:";
            // 
            // PanelSettingsControl
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            Controls.Add(pnlAction);
            Controls.Add(panel1);
            Name = "PanelSettingsControl";
            Size = new Size(1400, 800);
            panel1.ResumeLayout(false);
            panel1.PerformLayout();
            pnlAction.ResumeLayout(false);
            grpSerialMonitor.ResumeLayout(false);
            panel5.ResumeLayout(false);
            panel5.PerformLayout();
            panel4.ResumeLayout(false);
            panel4.PerformLayout();
            pnlManual.ResumeLayout(false);
            pnlManual.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private Label label1;
        private Label label3;
        private Button btnManualCalibrate;
        private ComboBox cmbIPAddress;
        private ComboBox cmbRoute;
        private Label label2;
        private Label label4;
        private ComboBox cmbPanelLocation;
        private TextBox txtDescription;
        private Label label9;
        private TextBox txtPanelSerialNumber;
        private Button btnDeploy;
        private Label label5;
        private ComboBox cmbSerialPort;
        private CheckBox chkOTA;
        private Button btnRefresh;
        private Panel panel1;
        private Panel pnlAction;
        private GroupBox grpSerialMonitor;
        private Panel panel5;
        private TextBox txtSerialOutput;
        private Panel panel4;
        private Button btnDisconnect;
        private Button btnConnect;
        private Panel pnlManual;
        private TextBox txtSens1;
        private TextBox txtCalib;
        private Label label8;
        private Label label6;
        private TextBox txtSens2;
        private Label label7;
        private TextBox txtTargetIP;
        private Label label10;
    }
}
