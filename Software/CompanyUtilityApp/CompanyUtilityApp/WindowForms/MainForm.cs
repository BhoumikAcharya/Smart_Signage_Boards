using CompanyUtilityApp.ProgramFiles;
using CompanyUtilityApp.UserControls;
using System.Drawing.Printing;

namespace CompanyUtilityApp
{
    public partial class MainForm : Form
    {
        // Initial variables
        //private bool _isLoggedIn = false;
        //private string _loggedInUser = string.Empty;
        private User _currentUser;



        // All methods.
        // This helper method handles the view switching logic
        private void LoadUserControl(UserControl controlToLoad)
        {
            panel2.Controls.Clear();
            controlToLoad.Dock = DockStyle.Fill;
            panel2.Controls.Add(controlToLoad);
        }

        //private void UpdateLoginUI()
        //{
        //    if (_isLoggedIn)
        //    {
        //        ToolStripStatusLabel.Text = _loggedInUser;
        //        TSB.Visible = true;
        //        loginToolStripMenuItem.Text = "Logout";
        //    }
        //    else
        //    {
        //        ToolStripStatusLabel.Text = "Not Logged In";
        //        TSB.Visible = false;
        //        loginToolStripMenuItem.Text = "Login";
        //    }
        //}

        private void UpdateUIForLoginState()
        {
            bool loggedIn = (_currentUser != null);

            // MenuStrip user display
            ToolStripStatusLabel.Text = loggedIn ? _currentUser.FullName : "Not Logged In";
            TSB.Visible = loggedIn;
            loginToolStripMenuItem.Text = loggedIn ? "Logout" : "Login";

            //// Enable/disable protected features
            //Node.Enabled = loggedIn;
            //Area.Enabled = loggedIn;
            //Tests.Enabled = loggedIn;
            //Settings.Enabled = loggedIn;
            //Network.Enabled = loggedIn;
            //IOActivity.Enabled = loggedIn;
            //UploadDownload.Enabled = loggedIn;
            //PanelSettings.Enabled = loggedIn;

            //// File menu
            //tempToolStripMenuItem.Enabled = loggedIn;
            //// Data Transfer menu
            //dataTransferToolStripMenuItem.Enabled = loggedIn;
        }

        // Main
        public MainForm()
        {
            InitializeComponent();
            UpdateUIForLoginState();
            LoadUserControl(new HomeControl());


        }

        //this is form load.
        private void Form1_Load(object sender, EventArgs e)
        {

        }

        // The Big Threes: Memuscript, Panel1, Central_Panel.
        private void menuStrip1_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void panel2_Paint(object sender, PaintEventArgs e)
        {

        }
        //

        // Menu Script File: all
        private void tempToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void newFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            MessageBox.Show("New File created.");
        }

        private void openFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "PDF Files|*.pdf|All Files|*.*";
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                // TODO: Load file logic
            }
        }

        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }
        private void printToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Logic to print the current active view
            PrintDialog printDialog = new PrintDialog();
            printDialog.ShowDialog();
        }
        // 

        // Login menu script 
        private void loginToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_currentUser == null)
            {
                using (LoginForm login = new LoginForm())
                {
                    if (login.ShowDialog() == DialogResult.OK)
                    {
                        _currentUser = login.LoggedInUser;
                        UpdateUIForLoginState();
                    }
                }
            }
            else
            {
                var result = MessageBox.Show("Are you sure you want to logout?",
                    "Confirm Logout", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.Yes)
                {
                    _currentUser = null;
                    UpdateUIForLoginState();
                    LoadUserControl(new HomeControl());

                }
            }
        }

        // Help menu: About, User_manual
        private void helpToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Node Calibration v1.0\nCopyright © I2STechnologies 2026", "About");

        }

        private void userManualToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string manualPath = Path.Combine(Application.StartupPath, "DocumentFiles", "Sinage_Project_Node_Calibration_2.pdf");

            if (File.Exists(manualPath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = manualPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open the user manual.\nError: {ex.Message}",
                                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                MessageBox.Show("User manual not found.\nPlease ensure 'UserManual.pdf' is in the DocumentFiles folder.",
                                "Manual Missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        //

        // data transfer manuscript
        private void dataTransferToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void uploadToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void downloadToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }
        private void logToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }
        //
        // End of all menu scripts

        // All other functions
        private void TSB_Click(object sender, EventArgs e)
        {

        }

        private void ToolStripStatusLabel_Click(object sender, EventArgs e)
        {
            // Assuming your label is named toolStripLabelUser
            ToolStripStatusLabel.Text = "_currentUser.FullName";
        }

        private void Node_Click(object sender, EventArgs e)
        {
            LoadUserControl(new NodeControl());
        }

        private void Area_Click(object sender, EventArgs e)
        {
            LoadUserControl(new AreaControl());
        }

        private void Settings_Click(object sender, EventArgs e)
        {
            LoadUserControl(new SettingsControl());
        }

        private void Network_Click(object sender, EventArgs e)
        {
            LoadUserControl(new NetworkControl());
        }

        private void Home_Click(object sender, EventArgs e)
        {
            LoadUserControl(new HomeControl());
        }

        private void Tests_Click(object sender, EventArgs e)
        {
            LoadUserControl(new TestsControl());
        }

        private void IOActivity_Click(object sender, EventArgs e)
        {

        }

        private void PanelSettings_Click(object sender, EventArgs e)
        {

            var panelSettings = new PanelSettingsControl();
            panelSettings.IsAdmin = (_currentUser != null &&
                                     _currentUser.Username.Equals("admin", StringComparison.OrdinalIgnoreCase));
            LoadUserControl(panelSettings);

        }
    }
}
