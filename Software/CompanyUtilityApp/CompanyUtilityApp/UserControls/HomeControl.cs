using CompanyUtilityApp.Infrastructure;

namespace CompanyUtilityApp.UserControls;

/// <summary>
/// Landing screen. Shows the company mark and reflects who is signed in, so an
/// operator can tell at a glance whether the protected screens are available.
/// </summary>
public partial class HomeControl : UserControl
{
    public HomeControl()
    {
        InitializeComponent();

        pictureBox1.SizeMode = PictureBoxSizeMode.Zoom;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        if (DesignMode)
            return;

        label1.Text = UserSession.IsAuthenticated
            ? $"Signed in as {UserSession.DisplayName}"
            : "Sign in from the Login menu to manage nodes and deploy configuration.";
    }
}