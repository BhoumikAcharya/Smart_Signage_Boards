using CompanyUtilityApp.Services;

namespace CompanyUtilityApp.UserControls;

/// <summary>
/// Diagnostics and endurance testing over Modbus.
///
/// Not yet implemented. The transport it depends on is complete and disposable
/// (<see cref="ModbusDiagnosticsService"/>), so the remaining work is the grid and
/// the test sequencing rather than any plumbing.
///
/// The screen states that plainly instead of presenting controls that do nothing,
/// which is what an unfinished screen owes its operator.
/// </summary>
public partial class TestsControl : UserControl
{
    public TestsControl()
    {
        InitializeComponent();

        label1.Text =
            "Tests - not available in this build.\r\n\r\n" +
            "Planned: an operation test that reads each node's diagnostic registers\r\n" +
            "(status, power, both current channels, battery and relay state) and an\r\n" +
            "endurance test that cycles relays over a set period.\r\n\r\n" +
            "The Modbus transport these depend on is already in place; the gateway\r\n" +
            "address is configured under \"Modbus\" in appsettings.json.";

        label1.AutoSize = true;
    }
}