namespace CompanyUtilityApp.UserControls;

/// <summary>
/// CSV import and export of node and panel records.
///
/// Not yet implemented. The repository methods it needs are in place
/// (<c>NodeRepository</c> and <c>AreaRepository</c> expose the required reads and
/// writes), so what remains is the file parsing, the validation report and the
/// per-row error log.
///
/// Import in particular must not be shipped half-finished: a bulk write that
/// partially applies would leave node identity inconsistent with what is
/// installed on site, which is expensive to unpick.
/// </summary>
public partial class TransferControl : UserControl
{
    public TransferControl()
    {
        InitializeComponent();
    }

    private void TransferControl_Load(object sender, EventArgs e)
    {
        if (DesignMode)
            return;

        label1.Text =
            "Upload / Download - not available in this build.\r\n\r\n" +
            "Planned: export nodes and panel positions to CSV, and import a reviewed\r\n" +
            "CSV back with a per-row validation report.\r\n\r\n" +
            "Import is deliberately withheld until the validation and rollback\r\n" +
            "behaviour is complete, because a partially applied bulk write would\r\n" +
            "leave node identity inconsistent with the installed hardware.";

        label1.AutoSize = true;
    }
}