using CompanyUtilityApp.Models;

namespace CompanyUtilityApp.Infrastructure;

/// <summary>
/// Holds the identity of the signed-in operator for the lifetime of the process.
///
/// Previously the logged-in user lived in a private field on <c>MainForm</c> and
/// the privileged-feature checks were commented out, so every screen was
/// reachable without signing in and the admin-only calibration panel was gated
/// by a string comparison performed in the form. Centralising it here means one
/// place decides what an operator may do.
/// </summary>
public static class UserSession
{
    /// <summary>Raised whenever sign-in state changes so the shell can refresh.</summary>
    public static event EventHandler? StateChanged;

    public static User? CurrentUser { get; private set; }

    public static bool IsAuthenticated => CurrentUser is not null;

    /// <summary>
    /// True when the operator may push calibration constants to a controller.
    /// Calibration changes the meaning of every current reading the node
    /// reports, so it is deliberately restricted.
    /// </summary>
    public static bool CanCalibrate => CurrentUser?.IsAdministrator == true;

    public static string DisplayName => CurrentUser?.FullName ?? "Not signed in";

    public static void SignIn(User user)
    {
        CurrentUser = user ?? throw new ArgumentNullException(nameof(user));
        AppLogger.Information($"Sign-in: {user.Username} ({user.FullName}), administrator={user.IsAdministrator}");
        StateChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void SignOut()
    {
        if (CurrentUser is not null)
            AppLogger.Information($"Sign-out: {CurrentUser.Username}");

        CurrentUser = null;
        StateChanged?.Invoke(null, EventArgs.Empty);
    }
}
