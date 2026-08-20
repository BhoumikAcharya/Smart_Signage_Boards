using CompanyUtilityApp.Models;
using CompanyUtilityApp.Services;
using CompanyUtilityApp.UI;

namespace CompanyUtilityApp.UserControls;

/// <summary>
/// Ethernet and Wi-Fi parameters intended for the controllers.
///
/// As with the technician settings, this screen previously had no persistence at
/// all — three empty label handlers and an empty checkbox handler, so the Wi-Fi
/// fields never even hid. Both behaviours are implemented here.
///
/// Designer control names are textBox1..8, checkBox1 and label5..9; they are
/// aliased below rather than renamed, so the generated layout stays intact.
/// </summary>
public partial class NetworkControl : UserControl
{
    private readonly SettingsService _settingsService = new();
    private Button? _saveButton;

    // Ethernet group.
    private TextBox EthernetIpBox => textBox1;
    private TextBox EthernetSubnetBox => textBox2;
    private TextBox EthernetGatewayBox => textBox3;

    // Wi-Fi group.
    private TextBox WifiSsidBox => textBox4;
    private TextBox WifiPasswordBox => textBox5;
    private TextBox WifiIpBox => textBox6;
    private TextBox WifiSubnetBox => textBox7;
    private TextBox WifiGatewayBox => textBox8;

    private CheckBox WifiEnabledBox => checkBox1;

    /// <summary>Controls that only apply when Wi-Fi is enabled.</summary>
    private Control[] WifiControls =>
    [
        WifiSsidBox, WifiPasswordBox, WifiIpBox, WifiSubnetBox, WifiGatewayBox,
        label5, label6, label7, label8, label9,
    ];

    public NetworkControl()
    {
        InitializeComponent();

        EthernetIpBox.PlaceholderText = "192.168.1.50";
        EthernetSubnetBox.PlaceholderText = "255.255.255.0";
        EthernetGatewayBox.PlaceholderText = "192.168.1.1";
        WifiIpBox.PlaceholderText = "192.168.1.60";
        WifiSubnetBox.PlaceholderText = "255.255.255.0";
        WifiGatewayBox.PlaceholderText = "192.168.1.1";
        WifiSsidBox.PlaceholderText = "Network name";

        // A visible pre-shared key on a shared workstation is a real exposure.
        WifiPasswordBox.UseSystemPasswordChar = true;
        WifiPasswordBox.PlaceholderText = "At least 8 characters";

        foreach (var box in new[]
                 {
                     EthernetIpBox, EthernetSubnetBox, EthernetGatewayBox,
                     WifiIpBox, WifiSubnetBox, WifiGatewayBox,
                 })
        {
            box.MaxLength = 15;
        }

        WifiSsidBox.MaxLength = 32;    // 802.11 SSID limit
        WifiPasswordBox.MaxLength = 63; // WPA2 pre-shared key limit

        AddSaveButton();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        if (!DesignMode)
            LoadSettings();
    }

    private void AddSaveButton()
    {
        _saveButton = new Button
        {
            Text = "Save Network Settings",
            Width = 170,
            Height = 30,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };

        var bottom = Controls.Cast<Control>()
            .Where(c => c.Visible)
            .Select(c => c.Bottom)
            .DefaultIfEmpty(0)
            .Max();

        _saveButton.Location = new Point(EthernetIpBox.Left, bottom + 16);
        _saveButton.Click += OnSaveClick;

        Controls.Add(_saveButton);
    }

    private void LoadSettings()
    {
        var settings = _settingsService.LoadNetworkSettings();

        EthernetIpBox.Text = settings.EthernetIPAddress;
        EthernetSubnetBox.Text = settings.EthernetSubnetMask;
        EthernetGatewayBox.Text = settings.EthernetGateway;

        WifiEnabledBox.Checked = settings.WifiEnabled;
        WifiSsidBox.Text = settings.WifiSsid;
        WifiPasswordBox.Text = settings.WifiPassword;
        WifiIpBox.Text = settings.WifiIPAddress;
        WifiSubnetBox.Text = settings.WifiSubnetMask;
        WifiGatewayBox.Text = settings.WifiGateway;

        ApplyWifiVisibility();
    }

    private void checkBox1_CheckedChanged(object sender, EventArgs e) => ApplyWifiVisibility();

    /// <summary>Shows the Wi-Fi group only when Wi-Fi is enabled.</summary>
    private void ApplyWifiVisibility()
    {
        var enabled = WifiEnabledBox.Checked;

        foreach (var control in WifiControls)
            control.Visible = enabled;
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        var settings = new NetworkSettings
        {
            EthernetIPAddress = EthernetIpBox.Text.Trim(),
            EthernetSubnetMask = EthernetSubnetBox.Text.Trim(),
            EthernetGateway = EthernetGatewayBox.Text.Trim(),
            WifiEnabled = WifiEnabledBox.Checked,
            WifiSsid = WifiSsidBox.Text.Trim(),
            WifiPassword = WifiPasswordBox.Text,
            WifiIPAddress = WifiIpBox.Text.Trim(),
            WifiSubnetMask = WifiSubnetBox.Text.Trim(),
            WifiGateway = WifiGatewayBox.Text.Trim(),
        };

        using var busy = new BusyScope(_saveButton);

        var (ok, error) = _settingsService.SaveNetworkSettings(settings);

        if (ok)
        {
            UserDialog.Info(this,
                $"Network settings saved.\n\nStored in:\n{_settingsService.FilePath}\n\n" +
                "These values are recorded for reference. Applying them to a controller is done " +
                "from the Panel Settings screen.",
                "Network");
            return;
        }

        UserDialog.ValidationError(this, error ?? "The network settings could not be saved.");
    }
}
