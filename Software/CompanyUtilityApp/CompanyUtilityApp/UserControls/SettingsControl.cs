using CompanyUtilityApp.Models;
using CompanyUtilityApp.Services;
using CompanyUtilityApp.UI;

namespace CompanyUtilityApp.UserControls;

/// <summary>
/// Technician and notification preferences.
///
/// Previously this screen was inert: the designer laid out the fields and the
/// code-behind contained nothing but two empty label handlers, so anything typed
/// was discarded when the screen was replaced. It now loads and saves through
/// <see cref="SettingsService"/>.
///
/// The designer named the controls textBox1..4, checkBox1 and comboBox1. Renaming
/// them would mean regenerating the layout, so they are aliased to meaningful
/// names here instead.
/// </summary>
public partial class SettingsControl : UserControl
{
    private readonly SettingsService _settingsService = new();
    private Button? _saveButton;

    // Aliases for the designer-generated control names.
    private TextBox TechnicianNameBox => textBox1;
    private TextBox PhoneBox => textBox2;
    private TextBox EmailBox => textBox3;
    private TextBox AddressBox => textBox4;
    private CheckBox NotificationsBox => checkBox1;
    private ComboBox FrequencyBox => comboBox1;

    public SettingsControl()
    {
        InitializeComponent();

        FrequencyBox.DropDownStyle = ComboBoxStyle.DropDownList;
        FrequencyBox.Items.Clear();
        FrequencyBox.Items.AddRange(TechnicianSettings.FrequencyOptions);

        TechnicianNameBox.MaxLength = 100;
        PhoneBox.MaxLength = 20;
        EmailBox.MaxLength = 150;
        AddressBox.MaxLength = 250;

        PhoneBox.PlaceholderText = "+91 98765 43210";
        EmailBox.PlaceholderText = "technician@i2st.example";

        NotificationsBox.CheckedChanged += (_, _) => ApplyNotificationState();

        AddSaveButton();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        if (!DesignMode)
            LoadSettings();
    }

    /// <summary>
    /// Adds the Save button in code, because the designer never gave this screen
    /// one — which is the reason nothing could be persisted.
    /// </summary>
    private void AddSaveButton()
    {
        _saveButton = new Button
        {
            Text = "Save Settings",
            Width = 130,
            Height = 30,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };

        // Place it below the lowest existing control so it cannot overlap the layout.
        var bottom = Controls.Cast<Control>()
            .Where(c => c.Visible)
            .Select(c => c.Bottom)
            .DefaultIfEmpty(0)
            .Max();

        var left = Controls.Cast<Control>()
            .OfType<TextBox>()
            .Select(c => c.Left)
            .DefaultIfEmpty(20)
            .Min();

        _saveButton.Location = new Point(left, bottom + 16);
        _saveButton.Click += OnSaveClick;

        Controls.Add(_saveButton);
    }

    private void LoadSettings()
    {
        var settings = _settingsService.LoadTechnicianSettings();

        TechnicianNameBox.Text = settings.TechnicianName;
        PhoneBox.Text = settings.Phone;
        EmailBox.Text = settings.Email;
        AddressBox.Text = settings.Address;
        NotificationsBox.Checked = settings.MaintenanceNotifications;

        var index = FrequencyBox.Items.IndexOf(settings.NotificationFrequency);
        FrequencyBox.SelectedIndex = index >= 0 ? index : FrequencyBox.Items.IndexOf("Monthly");

        ApplyNotificationState();
    }

    /// <summary>
    /// A notification frequency only means something when notifications are on,
    /// so the dropdown follows the checkbox.
    /// </summary>
    private void ApplyNotificationState() =>
        FrequencyBox.Enabled = NotificationsBox.Checked;

    private void OnSaveClick(object? sender, EventArgs e)
    {
        var settings = new TechnicianSettings
        {
            TechnicianName = TechnicianNameBox.Text.Trim(),
            Phone = PhoneBox.Text.Trim(),
            Email = EmailBox.Text.Trim(),
            Address = AddressBox.Text.Trim(),
            MaintenanceNotifications = NotificationsBox.Checked,
            NotificationFrequency = FrequencyBox.SelectedItem as string ?? "Monthly",
        };

        using var busy = new BusyScope(_saveButton);

        var (ok, error) = _settingsService.SaveTechnicianSettings(settings);

        if (ok)
        {
            UserDialog.Info(this,
                $"Settings saved.\n\nStored in:\n{_settingsService.FilePath}",
                "Settings");
            return;
        }

        UserDialog.ValidationError(this, error ?? "The settings could not be saved.");
    }
}
