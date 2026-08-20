namespace CompanyUtilityApp.UserControls;

/// <summary>
/// Live I/O activity view.
///
/// Not yet implemented. Intended to poll the per-node diagnostic register block
/// through <c>ModbusDiagnosticsService</c> and present it as a live table.
/// </summary>
public partial class IOActivityControl : UserControl
{
    public IOActivityControl()
    {
        InitializeComponent();

        var notice = new Label
        {
            AutoSize = true,
            Location = new Point(24, 24),
            Text =
                "I/O Activity - not available in this build.\r\n\r\n" +
                "Planned: a live view of each node's status, power, current channels,\r\n" +
                "battery level and relay state, polled over Modbus TCP.",
        };

        Controls.Add(notice);
    }
}