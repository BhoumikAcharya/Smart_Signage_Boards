using CompanyUtilityApp.Data;
using CompanyUtilityApp.Infrastructure;

namespace CompanyUtilityApp.UI;

/// <summary>
/// Consistent dialogs and a busy-cursor scope.
///
/// Message boxes were previously created ad hoc with inconsistent captions,
/// icons and button sets. Routing them through one place keeps the wording
/// uniform and gives a single seam for logging what the operator was told.
/// </summary>
public static class UserDialog
{
    private const string AppName = "Node Calibration";

    public static void Info(IWin32Window? owner, string message, string caption = AppName) =>
        Show(owner, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Warn(IWin32Window? owner, string message, string caption = AppName)
    {
        AppLogger.Warning($"Shown to operator: {message}");
        Show(owner, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public static void Error(IWin32Window? owner, string message, string caption = AppName)
    {
        AppLogger.Error($"Shown to operator: {message}");
        Show(owner, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>Shows a validation message and returns focus to the offending control.</summary>
    public static void ValidationError(IWin32Window? owner, string message, Control? focusTarget = null)
    {
        Show(owner, message, "Check the entered values", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        focusTarget?.Focus();
    }

    /// <summary>Yes/No confirmation. Defaults to No so Enter cannot confirm by accident.</summary>
    public static bool Confirm(IWin32Window? owner, string message, string caption = "Confirm")
    {
        var result = MessageBox.Show(
            owner, message, caption,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

        return result == DialogResult.Yes;
    }

    /// <summary>
    /// Confirmation for an irreversible action, with the consequence spelled out
    /// and the default on No.
    /// </summary>
    public static bool ConfirmDestructive(IWin32Window? owner, string action, string consequence)
    {
        var result = MessageBox.Show(
            owner,
            $"{action}\n\n{consequence}\n\nThis cannot be undone. Continue?",
            "Confirm",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        return result == DialogResult.Yes;
    }

    /// <summary>Reports a data-layer failure in operator-facing terms.</summary>
    public static void DataError(IWin32Window? owner, DataAccessException exception) =>
        Show(owner, exception.Message, "Database", MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static void Show(
        IWin32Window? owner, string message, string caption,
        MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        if (owner is not null)
            MessageBox.Show(owner, message, caption, buttons, icon);
        else
            MessageBox.Show(message, caption, buttons, icon);
    }
}

/// <summary>
/// Shows the wait cursor and optionally disables a control for the duration of an
/// operation, restoring both on dispose even if the operation throws.
///
/// Every long-running handler previously toggled state by hand, which meant an
/// early return or an exception could leave a button permanently disabled.
/// </summary>
public sealed class BusyScope : IDisposable
{
    private readonly Control? _control;
    private readonly bool _wasEnabled;
    private readonly Cursor? _previousCursor;
    private bool _disposed;

    public BusyScope(Control? control = null)
    {
        _control = control;

        // Cursor.Current can legitimately be null, meaning "no override in force".
        _previousCursor = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor;

        if (_control is not null)
        {
            _wasEnabled = _control.Enabled;
            _control.Enabled = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Cursor.Current = _previousCursor ?? Cursors.Default;

        if (_control is not null && !_control.IsDisposed)
            _control.Enabled = _wasEnabled;

        _disposed = true;
    }
}
