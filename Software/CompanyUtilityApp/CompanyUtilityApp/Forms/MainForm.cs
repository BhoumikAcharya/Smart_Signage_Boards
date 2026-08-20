using System.Diagnostics;
using CompanyUtilityApp.Data;
using CompanyUtilityApp.Infrastructure;
using CompanyUtilityApp.UI;
using CompanyUtilityApp.UserControls;

namespace CompanyUtilityApp.Forms;

/// <summary>
/// Application shell: navigation, sign-in state and the hosted content area.
///
/// Two behavioural corrections from the original implementation:
///
/// 1. The privileged screens are actually gated now. The enable/disable code was
///    present but commented out, so every screen — including node creation and
///    device deployment — was reachable without signing in.
///
/// 2. Hosted controls are disposed when replaced. The previous
///    <c>panel2.Controls.Clear()</c> detached each control without disposing it,
///    so a screen holding a serial port or a Modbus socket kept it open for the
///    life of the process.
/// </summary>
public partial class MainForm : Form
{
    private const string UserManualFileName = "Sinage_Project_Node_Calibration_2.pdf";

    public MainForm()
    {
        InitializeComponent();

        Text = "Node Calibration - I2ST Technologies Pvt. Ltd.";

        UserSession.StateChanged += OnSessionStateChanged;

        ApplySessionState();
        Navigate(new HomeControl());

        // Report database reachability once, in the background, so a
        // misconfiguration is visible immediately rather than on first use.
        _ = VerifyDatabaseAsync();
    }

    // ---- Navigation ----------------------------------------------------------

    /// <summary>
    /// Replaces the hosted screen, disposing the one it replaces.
    /// </summary>
    private void Navigate(UserControl screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var previous = panel2.Controls.Count > 0 ? panel2.Controls[0] : null;

        panel2.SuspendLayout();
        try
        {
            panel2.Controls.Clear();
            screen.Dock = DockStyle.Fill;
            panel2.Controls.Add(screen);
        }
        finally
        {
            panel2.ResumeLayout(performLayout: true);
        }

        // Disposal is what releases COM ports and sockets held by the old screen.
        previous?.Dispose();

        AppLogger.Trace($"Navigated to {screen.GetType().Name}.");
    }

    /// <summary>
    /// Opens a screen that requires a signed-in operator.
    /// </summary>
    private void NavigateProtected(Func<UserControl> factory, string screenName)
    {
        if (!UserSession.IsAuthenticated)
        {
            UserDialog.Info(this,
                $"Sign in to use {screenName}.\n\nUse Login on the menu bar.",
                "Sign-in required");
            return;
        }

        Navigate(factory());
    }

    // ---- Session state -------------------------------------------------------

    private void OnSessionStateChanged(object? sender, EventArgs e) => ApplySessionState();

    /// <summary>
    /// Enables or disables everything that depends on being signed in, and
    /// reflects the current operator in the menu bar.
    /// </summary>
    private void ApplySessionState()
    {
        var signedIn = UserSession.IsAuthenticated;

        ToolStripStatusLabel.Text = signedIn ? UserSession.DisplayName : "Not signed in";
        TSB.Visible = signedIn;
        loginToolStripMenuItem.Text = signedIn ? "Logout" : "Login";

        // Left navigation. Home stays available so there is always somewhere to be.
        Node.Enabled = signedIn;
        Area.Enabled = signedIn;
        PanelSettings.Enabled = signedIn;
        Tests.Enabled = signedIn;
        Settings.Enabled = signedIn;
        Network.Enabled = signedIn;
        IOActivity.Enabled = signedIn;
        UploadDownload.Enabled = signedIn;

        // Menus that act on data.
        tempToolStripMenuItem.Enabled = signedIn;
        dataTransferToolStripMenuItem.Enabled = signedIn;
    }

    private async Task VerifyDatabaseAsync()
    {
        var (ok, error) = await Db.CheckHealthAsync();

        if (ok)
        {
            AppLogger.Information("Database health check passed.");
            return;
        }

        AppLogger.Warning($"Database health check failed: {error}");

        if (IsDisposed)
            return;

        UserDialog.Warn(this,
            "The node database could not be reached.\n\n" +
            $"{error}\n\n" +
            "Screens that read or write data will not work until this is resolved. " +
            "Check the connection string in appsettings.json.",
            "Database unavailable");
    }

    // ---- Sign in / out -------------------------------------------------------

    private void loginToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (UserSession.IsAuthenticated)
        {
            if (!UserDialog.Confirm(this, "Sign out of Node Calibration?", "Confirm sign-out"))
                return;

            UserSession.SignOut();
            Navigate(new HomeControl());
            return;
        }

        using var login = new LoginForm();
        if (login.ShowDialog(this) == DialogResult.OK && login.AuthenticatedUser is not null)
        {
            UserSession.SignIn(login.AuthenticatedUser);
            Navigate(new HomeControl());
        }
    }

    // ---- Left navigation -----------------------------------------------------

    private void Home_Click(object sender, EventArgs e) => Navigate(new HomeControl());

    private void Node_Click(object sender, EventArgs e) =>
        NavigateProtected(() => new NodeControl(), "Node Management");

    private void Area_Click(object sender, EventArgs e) =>
        NavigateProtected(() => new AreaControl(), "Area Management");

    private void PanelSettings_Click(object sender, EventArgs e) =>
        NavigateProtected(() => new PanelSettingsControl(), "Panel Settings");

    private void Tests_Click(object sender, EventArgs e) =>
        NavigateProtected(() => new TestsControl(), "Tests");

    private void Settings_Click(object sender, EventArgs e) =>
        NavigateProtected(() => new SettingsControl(), "Settings");

    private void Network_Click(object sender, EventArgs e) =>
        NavigateProtected(() => new NetworkControl(), "Network Configuration");

    private void IOActivity_Click(object sender, EventArgs e) =>
        NavigateProtected(() => new IOActivityControl(), "I/O Activity");

    private void UploadDownload_Click(object sender, EventArgs e) =>
        NavigateProtected(() => new TransferControl(), "Upload / Download");

    // ---- File menu -----------------------------------------------------------

    private void openFileToolStripMenuItem_Click(object sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open a document",
            Filter = "PDF documents (*.pdf)|*.pdf|CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        OpenWithShell(dialog.FileName, "the selected file");
    }

    private void saveToolStripMenuItem_Click(object sender, EventArgs e) =>
        UserDialog.Info(this,
            "Saving is handled per screen.\n\n" +
            "Node and panel changes are written to the database as you confirm each dialog. " +
            "Technician and network preferences are saved with the Save button on their own screens.",
            "Save");

    private void printToolStripMenuItem_Click(object sender, EventArgs e)
    {
        // The original opened a PrintDialog and discarded the result, which
        // showed a dialog that could not print anything. Saying so is more
        // honest than pretending.
        UserDialog.Info(this,
            "Printing is not available in this version.\n\n" +
            "Use the Upload / Download screen to export data, then print from the exported file.",
            "Print");
    }

    // ---- Data Transfer menu --------------------------------------------------

    private void uploadToolStripMenuItem_Click(object sender, EventArgs e) =>
        NavigateProtected(() => new TransferControl(), "Upload / Download");

    private void downloadToolStripMenuItem_Click(object sender, EventArgs e) =>
        NavigateProtected(() => new TransferControl(), "Upload / Download");

    /// <summary>Opens the log folder, which is where the diagnostic trail lives.</summary>
    private void logToolStripMenuItem_Click(object sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLogger.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppLogger.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warning("Could not open the log folder.", ex);
            UserDialog.Warn(this, $"The log folder could not be opened.\n\n{AppLogger.LogDirectory}");
        }
    }

    // ---- Help menu -----------------------------------------------------------

    private void userManualToolStripMenuItem_Click(object sender, EventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "DocumentFiles", UserManualFileName);

        if (!File.Exists(path))
        {
            UserDialog.Warn(this,
                $"The user manual could not be found.\n\nExpected at:\n{path}",
                "Manual not found");
            return;
        }

        OpenWithShell(path, "the user manual");
    }

    private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
    {
        var version = Application.ProductVersion;

        UserDialog.Info(this,
            $"""
             Node Calibration for the Smart Signage System

             Version   {version}
             Company   I2ST Technologies Pvt. Ltd.

             Configures and calibrates ESP32 signage controllers over
             USB serial and Ethernet, and manages node identity in
             SQL Server.

             Logs: {AppLogger.LogDirectory}
             """,
            "About Node Calibration");
    }

    // ---- Helpers -------------------------------------------------------------

    private void OpenWithShell(string path, string description)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Could not open {description} at '{path}'.", ex);
            UserDialog.Error(this,
                $"Windows could not open {description}.\n\n{ex.Message}\n\n" +
                "Check that an application is associated with this file type.");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UserSession.StateChanged -= OnSessionStateChanged;

        // Give the hosted screen a chance to release its hardware handles.
        if (panel2.Controls.Count > 0)
            panel2.Controls[0].Dispose();

        base.OnFormClosed(e);
    }
}
