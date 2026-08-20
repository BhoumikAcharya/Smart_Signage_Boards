using CompanyUtilityApp.Data;
using CompanyUtilityApp.Models;
using CompanyUtilityApp.UI;

namespace CompanyUtilityApp.Forms;

/// <summary>
/// Credential prompt. Returns the authenticated <see cref="Models.User"/> to the
/// caller and never exposes the password hash.
/// </summary>
public partial class LoginForm : Form
{
    /// <summary>
    /// Consecutive failures in this dialog. After a handful, a short delay is
    /// imposed: it costs a legitimate operator who mistyped almost nothing, and
    /// makes an interactive guessing attempt through this screen impractical.
    /// </summary>
    private int _failedAttempts;

    private const int AttemptsBeforeThrottle = 3;
    private const int ThrottleMilliseconds = 2000;

    /// <summary>The signed-in operator. Null unless <see cref="DialogResult.OK"/> was returned.</summary>
    public User? AuthenticatedUser { get; private set; }

    public LoginForm()
    {
        InitializeComponent();

        AcceptButton = button1;
        CancelButton = button2;

        Text = "Sign In - Node Calibration";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        // The designer leaves this as a normal text box, which would show the
        // password in clear text on screen.
        txtPassword.UseSystemPasswordChar = true;

        button1.Text = "Sign In";
        button2.Text = "Cancel";
        button2.DialogResult = DialogResult.Cancel;
    }

    private async void button1_Click(object sender, EventArgs e) => await AttemptSignInAsync();

    private async Task AttemptSignInAsync()
    {
        var username = txtUsername.Text.Trim();
        var password = txtPassword.Text;

        if (string.IsNullOrEmpty(username))
        {
            UserDialog.ValidationError(this, "Enter your username.", txtUsername);
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            UserDialog.ValidationError(this, "Enter your password.", txtPassword);
            return;
        }

        using (new BusyScope(button1))
        {
            var (result, user, error) = await AuthRepository.AuthenticateAsync(username, password);

            if (result == AuthenticationResult.Success && user is not null)
            {
                AuthenticatedUser = user;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            _failedAttempts++;

            if (result == AuthenticationResult.Failed)
            {
                // A database problem is not a credential problem, and telling the
                // operator otherwise sends them hunting for the wrong fault.
                UserDialog.Error(this, error ?? "The sign-in could not be completed.", "Sign-in unavailable");
            }
            else
            {
                UserDialog.Warn(this, error ?? "Incorrect username or password.", "Sign-in failed");
            }
        }

        txtPassword.Clear();
        txtPassword.Focus();

        if (_failedAttempts >= AttemptsBeforeThrottle)
        {
            using (new BusyScope(this))
                await Task.Delay(ThrottleMilliseconds);
        }
    }

    private void button2_Click(object sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }
}
