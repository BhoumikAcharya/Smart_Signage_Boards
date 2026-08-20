#nullable disable
namespace CompanyUtilityApp.Forms
{
    partial class MainForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            panel1 = new Panel();
            PanelSettings = new Button();
            UploadDownload = new Button();
            IOActivity = new Button();
            Network = new Button();
            panelMainContent = new Panel();
            Settings = new Button();
            Tests = new Button();
            Area = new Button();
            Node = new Button();
            Home = new Button();
            panel2 = new Panel();
            openFileToolStripMenuItem1 = new ToolStripDropDownMenu();
            tempToolStripMenuItem = new ToolStripMenuItem();
            openFileToolStripMenuItem = new ToolStripMenuItem();
            saveToolStripMenuItem = new ToolStripMenuItem();
            printToolStripMenuItem = new ToolStripMenuItem();
            loginToolStripMenuItem = new ToolStripMenuItem();
            helpToolStripMenuItem = new ToolStripMenuItem();
            userManualToolStripMenuItem = new ToolStripMenuItem();
            aboutToolStripMenuItem = new ToolStripMenuItem();
            dataTransferToolStripMenuItem = new ToolStripMenuItem();
            uploadToolStripMenuItem = new ToolStripMenuItem();
            downloadToolStripMenuItem = new ToolStripMenuItem();
            logToolStripMenuItem = new ToolStripMenuItem();
            ToolStripStatusLabel = new ToolStripMenuItem();
            TSB = new ToolStripMenuItem();
            menuStrip1 = new MenuStrip();
            panel1.SuspendLayout();
            menuStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // panel1
            // 
            panel1.BackColor = SystemColors.ControlDark;
            panel1.Controls.Add(PanelSettings);
            panel1.Controls.Add(UploadDownload);
            panel1.Controls.Add(IOActivity);
            panel1.Controls.Add(Network);
            panel1.Controls.Add(panelMainContent);
            panel1.Controls.Add(Settings);
            panel1.Controls.Add(Tests);
            panel1.Controls.Add(Area);
            panel1.Controls.Add(Node);
            panel1.Controls.Add(Home);
            panel1.Dock = DockStyle.Left;
            panel1.Location = new Point(0, 28);
            panel1.Name = "panel1";
            panel1.Size = new Size(193, 594);
            panel1.TabIndex = 2;
            // 
            // PanelSettings
            // 
            PanelSettings.Dock = DockStyle.Top;
            PanelSettings.Location = new Point(0, 232);
            PanelSettings.Name = "PanelSettings";
            PanelSettings.Size = new Size(193, 29);
            PanelSettings.TabIndex = 8;
            PanelSettings.Text = "Panel Settings";
            PanelSettings.UseVisualStyleBackColor = true;
            PanelSettings.Click += PanelSettings_Click;
            // 
            // UploadDownload
            // 
            UploadDownload.Dock = DockStyle.Top;
            UploadDownload.Location = new Point(0, 203);
            UploadDownload.Name = "UploadDownload";
            UploadDownload.Size = new Size(193, 29);
            UploadDownload.TabIndex = 7;
            UploadDownload.Text = "Upload/Download";
            UploadDownload.UseVisualStyleBackColor = true;
            UploadDownload.Click += UploadDownload_Click;
            // 
            // IOActivity
            // 
            IOActivity.Dock = DockStyle.Top;
            IOActivity.Location = new Point(0, 174);
            IOActivity.Name = "IOActivity";
            IOActivity.Size = new Size(193, 29);
            IOActivity.TabIndex = 6;
            IOActivity.Text = "I/O Activity";
            IOActivity.UseVisualStyleBackColor = true;
            IOActivity.Click += IOActivity_Click;
            // 
            // Network
            // 
            Network.Dock = DockStyle.Top;
            Network.Location = new Point(0, 145);
            Network.Name = "Network";
            Network.Size = new Size(193, 29);
            Network.TabIndex = 5;
            Network.Text = "Network";
            Network.UseVisualStyleBackColor = true;
            Network.Click += Network_Click;
            // 
            // panelMainContent
            // 
            panelMainContent.Location = new Point(213, 0);
            panelMainContent.Name = "panelMainContent";
            panelMainContent.Size = new Size(503, 355);
            panelMainContent.TabIndex = 2;
            // 
            // Settings
            // 
            Settings.Dock = DockStyle.Top;
            Settings.Location = new Point(0, 116);
            Settings.Name = "Settings";
            Settings.Size = new Size(193, 29);
            Settings.TabIndex = 4;
            Settings.Text = "Settings";
            Settings.UseVisualStyleBackColor = true;
            Settings.Click += Settings_Click;
            // 
            // Tests
            // 
            Tests.Dock = DockStyle.Top;
            Tests.Location = new Point(0, 87);
            Tests.Name = "Tests";
            Tests.Size = new Size(193, 29);
            Tests.TabIndex = 3;
            Tests.Text = "Tests";
            Tests.UseVisualStyleBackColor = true;
            Tests.Click += Tests_Click;
            // 
            // Area
            // 
            Area.Dock = DockStyle.Top;
            Area.Location = new Point(0, 58);
            Area.Name = "Area";
            Area.Size = new Size(193, 29);
            Area.TabIndex = 2;
            Area.Text = "Area";
            Area.UseVisualStyleBackColor = true;
            Area.Click += Area_Click;
            // 
            // Node
            // 
            Node.Dock = DockStyle.Top;
            Node.Location = new Point(0, 29);
            Node.Name = "Node";
            Node.Size = new Size(193, 29);
            Node.TabIndex = 1;
            Node.Text = "Node";
            Node.UseVisualStyleBackColor = true;
            Node.Click += Node_Click;
            // 
            // Home
            // 
            Home.Dock = DockStyle.Top;
            Home.Location = new Point(0, 0);
            Home.Name = "Home";
            Home.Size = new Size(193, 29);
            Home.TabIndex = 0;
            Home.Text = "Home";
            Home.UseVisualStyleBackColor = true;
            Home.Click += Home_Click;
            // 
            // panel2
            // 
            panel2.BorderStyle = BorderStyle.Fixed3D;
            panel2.Dock = DockStyle.Fill;
            panel2.Location = new Point(193, 28);
            panel2.Name = "panel2";
            panel2.Size = new Size(900, 594);
            panel2.TabIndex = 3;
            // 
            // openFileToolStripMenuItem1
            // 
            openFileToolStripMenuItem1.AutoClose = false;
            openFileToolStripMenuItem1.ImageScalingSize = new Size(20, 20);
            openFileToolStripMenuItem1.Name = "openFileToolStripMenuItem1";
            openFileToolStripMenuItem1.Size = new Size(61, 4);
            // 
            // tempToolStripMenuItem
            // 
            tempToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { openFileToolStripMenuItem, saveToolStripMenuItem, printToolStripMenuItem });
            tempToolStripMenuItem.Name = "tempToolStripMenuItem";
            tempToolStripMenuItem.Size = new Size(46, 24);
            tempToolStripMenuItem.Text = "File";
            // 
            // openFileToolStripMenuItem
            // 
            openFileToolStripMenuItem.Name = "openFileToolStripMenuItem";
            openFileToolStripMenuItem.Size = new Size(149, 26);
            openFileToolStripMenuItem.Text = "New File";
            openFileToolStripMenuItem.Click += openFileToolStripMenuItem_Click;
            // 
            // saveToolStripMenuItem
            // 
            saveToolStripMenuItem.Name = "saveToolStripMenuItem";
            saveToolStripMenuItem.Size = new Size(149, 26);
            saveToolStripMenuItem.Text = "Save File";
            saveToolStripMenuItem.Click += saveToolStripMenuItem_Click;
            // 
            // printToolStripMenuItem
            // 
            printToolStripMenuItem.Name = "printToolStripMenuItem";
            printToolStripMenuItem.Size = new Size(149, 26);
            printToolStripMenuItem.Text = "Print";
            printToolStripMenuItem.Click += printToolStripMenuItem_Click;
            // 
            // loginToolStripMenuItem
            // 
            loginToolStripMenuItem.Name = "loginToolStripMenuItem";
            loginToolStripMenuItem.Size = new Size(60, 24);
            loginToolStripMenuItem.Text = "Login";
            loginToolStripMenuItem.Click += loginToolStripMenuItem_Click;
            // 
            // helpToolStripMenuItem
            // 
            helpToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { userManualToolStripMenuItem, aboutToolStripMenuItem });
            helpToolStripMenuItem.Name = "helpToolStripMenuItem";
            helpToolStripMenuItem.Size = new Size(55, 24);
            helpToolStripMenuItem.Text = "Help";
            // 
            // userManualToolStripMenuItem
            // 
            userManualToolStripMenuItem.Name = "userManualToolStripMenuItem";
            userManualToolStripMenuItem.Size = new Size(174, 26);
            userManualToolStripMenuItem.Text = "User Manual";
            userManualToolStripMenuItem.Click += userManualToolStripMenuItem_Click;
            // 
            // aboutToolStripMenuItem
            // 
            aboutToolStripMenuItem.Name = "aboutToolStripMenuItem";
            aboutToolStripMenuItem.Size = new Size(174, 26);
            aboutToolStripMenuItem.Text = "About";
            aboutToolStripMenuItem.Click += aboutToolStripMenuItem_Click;
            // 
            // dataTransferToolStripMenuItem
            // 
            dataTransferToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { uploadToolStripMenuItem, downloadToolStripMenuItem, logToolStripMenuItem });
            dataTransferToolStripMenuItem.Name = "dataTransferToolStripMenuItem";
            dataTransferToolStripMenuItem.Size = new Size(111, 24);
            dataTransferToolStripMenuItem.Text = "Data Transfer";
            // 
            // uploadToolStripMenuItem
            // 
            uploadToolStripMenuItem.Name = "uploadToolStripMenuItem";
            uploadToolStripMenuItem.Size = new Size(161, 26);
            uploadToolStripMenuItem.Text = "Upload";
            uploadToolStripMenuItem.Click += uploadToolStripMenuItem_Click;
            // 
            // downloadToolStripMenuItem
            // 
            downloadToolStripMenuItem.Name = "downloadToolStripMenuItem";
            downloadToolStripMenuItem.Size = new Size(161, 26);
            downloadToolStripMenuItem.Text = "Download";
            downloadToolStripMenuItem.Click += downloadToolStripMenuItem_Click;
            // 
            // logToolStripMenuItem
            // 
            logToolStripMenuItem.Name = "logToolStripMenuItem";
            logToolStripMenuItem.Size = new Size(161, 26);
            logToolStripMenuItem.Text = "Log";
            logToolStripMenuItem.Click += logToolStripMenuItem_Click;
            // 
            // ToolStripStatusLabel
            // 
            ToolStripStatusLabel.Alignment = ToolStripItemAlignment.Right;
            ToolStripStatusLabel.Enabled = false;
            ToolStripStatusLabel.Name = "ToolStripStatusLabel";
            ToolStripStatusLabel.Size = new Size(52, 24);
            ToolStripStatusLabel.Text = "User";
            // 
            // TSB
            // 
            TSB.Alignment = ToolStripItemAlignment.Right;
            TSB.DisplayStyle = ToolStripItemDisplayStyle.Image;
            TSB.Enabled = false;
            TSB.Image = (Image)resources.GetObject("TSB.Image");
            TSB.Name = "TSB";
            TSB.Size = new Size(34, 24);
            TSB.Text = "TSB";
            // 
            // menuStrip1
            // 
            menuStrip1.ImageScalingSize = new Size(20, 20);
            menuStrip1.Items.AddRange(new ToolStripItem[] { tempToolStripMenuItem, loginToolStripMenuItem, helpToolStripMenuItem, dataTransferToolStripMenuItem, ToolStripStatusLabel, TSB });
            menuStrip1.Location = new Point(0, 0);
            menuStrip1.Name = "menuStrip1";
            menuStrip1.RightToLeft = RightToLeft.No;
            menuStrip1.Size = new Size(1093, 28);
            menuStrip1.TabIndex = 0;
            menuStrip1.Text = "menuStrip1";
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1093, 622);
            Controls.Add(panel2);
            Controls.Add(panel1);
            Controls.Add(menuStrip1);
            MainMenuStrip = menuStrip1;
            Name = "MainForm";
            Text = "Node Calibration";
            panel1.ResumeLayout(false);
            menuStrip1.ResumeLayout(false);
            menuStrip1.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion
        private Panel panel1;
        private Button UploadDownload;
        private Button IOActivity;
        private Button Network;
        private Panel panelMainContent;
        private Button Settings;
        private Button Tests;
        private Button Area;
        private Button Node;
        private Button Home;
        private Panel panel2;
        private Button PanelSettings;
        private ToolStripDropDownMenu openFileToolStripMenuItem1;
        private ToolStripMenuItem tempToolStripMenuItem;
        private ToolStripMenuItem openFileToolStripMenuItem;
        private ToolStripMenuItem saveToolStripMenuItem;
        private ToolStripMenuItem printToolStripMenuItem;
        private ToolStripMenuItem loginToolStripMenuItem;
        private ToolStripMenuItem helpToolStripMenuItem;
        private ToolStripMenuItem userManualToolStripMenuItem;
        private ToolStripMenuItem aboutToolStripMenuItem;
        private ToolStripMenuItem dataTransferToolStripMenuItem;
        private ToolStripMenuItem uploadToolStripMenuItem;
        private ToolStripMenuItem downloadToolStripMenuItem;
        private ToolStripMenuItem logToolStripMenuItem;
        private ToolStripMenuItem ToolStripStatusLabel;
        private ToolStripMenuItem TSB;
        private MenuStrip menuStrip1;
    }
}
