using System.Runtime.InteropServices;
using CompanyUtilityApp.Forms;
using CompanyUtilityApp.Infrastructure;

namespace CompanyUtilityApp;

internal static class Program
{
    /// <summary>
    /// Application entry point.
    ///
    /// Two things happen here that did not before:
    ///
    /// 1. Global exception handlers are installed. Previously any unhandled
    ///    exception — a dropped database connection was enough — terminated the
    ///    process with the Windows crash dialog and no record of what happened.
    ///    Now it is logged and reported, and a UI-thread fault leaves the
    ///    application usable.
    ///
    /// 2. A single-instance guard prevents two copies running at once. Two
    ///    instances would compete for the same COM port and could deploy
    ///    conflicting configuration to the same controller.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(initiallyOwned: true, "I2ST.NodeCalibration.SingleInstance", out var isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Node Calibration is already running.\n\n" +
                "Only one copy may run at a time, because two instances would compete for the same " +
                "serial port and could send conflicting configuration to a controller.",
                "Already Running",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AppLogger.Initialise();

        // Route thread exceptions to our handler rather than letting the runtime
        // show its own dialog and quit.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnUiThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            AppLogger.Error("The application failed during start-up.", ex);
            ReportFatal(ex);
        }
        finally
        {
            AppLogger.Information("=== Session ended ===");
        }
    }

    /// <summary>
    /// Handles an exception raised on the UI thread. The application keeps
    /// running: losing a grid refresh should not discard whatever else the
    /// operator has open.
    /// </summary>
    private static void OnUiThreadException(object sender, ThreadExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled exception on the UI thread.", e.Exception);

        var message =
            "Something went wrong while completing that action.\n\n" +
            $"{e.Exception.Message}\n\n" +
            "The application is still running and your other work is unaffected. " +
            $"Technical details have been written to:\n{AppLogger.LogDirectory}";

        MessageBox.Show(message, "Unexpected Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// Handles an exception from a background thread. These are not recoverable —
    /// the runtime tears the process down afterwards — so the goal is to leave a
    /// usable record before it goes.
    /// </summary>
    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        AppLogger.Error($"Unhandled exception on a background thread (terminating={e.IsTerminating}).", exception);

        if (e.IsTerminating && exception is not null)
            ReportFatal(exception);
    }

    /// <summary>
    /// Catches faulted tasks nobody awaited. Logged rather than shown: by the
    /// time this fires the operator has usually already seen the real symptom.
    /// </summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("A background task faulted without being observed.", e.Exception);
        e.SetObserved();
    }

    private static void ReportFatal(Exception exception)
    {
        var message =
            "Node Calibration has to close because of an unexpected error.\n\n" +
            $"{exception.Message}\n\n" +
            $"A diagnostic log has been written to:\n{AppLogger.LogDirectory}\n\n" +
            "Please send that log to the maintainer.";

        MessageBox.Show(message, "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Stop);
    }
}
