using System.Globalization;
using System.Diagnostics;
using System.Text;
using CompanyUtilityApp.Configuration;

namespace CompanyUtilityApp.Infrastructure;

public enum LogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Minimal rolling file logger.
///
/// The application previously had no diagnostics at all: failures surfaced as a
/// message box and then vanished, which makes a field fault impossible to
/// reconstruct after the fact. Every repository call, device deployment and
/// unhandled exception now leaves a dated trail under
/// <c>%LOCALAPPDATA%\I2ST\NodeCalibration\logs</c>.
///
/// Deliberately dependency-free — adding a logging framework to a single-user
/// desktop tool is not worth the deployment weight.
/// </summary>
public static class AppLogger
{
    private static readonly object _gate = new();
    private static readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "I2ST", "NodeCalibration", "logs");

    private static LogLevel _minimumLevel = LogLevel.Information;
    private static bool _initialised;

    public static string LogDirectory => _logDirectory;

    /// <summary>Prepares the log directory and prunes old files. Safe to call twice.</summary>
    public static void Initialise()
    {
        lock (_gate)
        {
            if (_initialised)
                return;

            _minimumLevel = Enum.TryParse<LogLevel>(
                AppConfig.Current.Logging.MinimumLevel, ignoreCase: true, out var level)
                ? level
                : LogLevel.Information;

            try
            {
                Directory.CreateDirectory(_logDirectory);
                PruneOldFiles(AppConfig.Current.Logging.RetainedFileCount);
            }
            catch (Exception ex)
            {
                // Logging must never be the reason the app fails to start.
                Debug.WriteLine($"Log directory unavailable: {ex.Message}");
            }

            _initialised = true;
        }

        Information($"=== Session started · v{Application.ProductVersion} · {Environment.UserName}@{Environment.MachineName} ===");

        foreach (var warning in AppConfig.Current.LoadWarnings)
            Warning($"Configuration: {warning}");
    }

    public static void Trace(string message) => Write(LogLevel.Debug, message, null);

    public static void Information(string message) => Write(LogLevel.Information, message, null);

    public static void Warning(string message, Exception? exception = null) =>
        Write(LogLevel.Warning, message, exception);

    public static void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, message, exception);

    private static void Write(LogLevel level, string message, Exception? exception)
    {
        if (level < _minimumLevel)
            return;

        var builder = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(level.ToString().ToUpperInvariant().PadRight(11)).Append("] ")
            .Append(message);

        if (exception is not null)
        {
            builder.AppendLine()
                   .Append("    ").Append(exception.GetType().FullName).Append(": ").Append(exception.Message);

            if (exception.StackTrace is not null)
                builder.AppendLine().Append(exception.StackTrace);

            var inner = exception.InnerException;
            while (inner is not null)
            {
                builder.AppendLine()
                       .Append("    --> ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message);
                inner = inner.InnerException;
            }
        }

        var line = builder.ToString();
        Debug.WriteLine(line);

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_logDirectory);
                File.AppendAllText(CurrentFilePath(), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // A failed write must not cascade. The Debug output above still carries it.
        }
    }

    private static string CurrentFilePath() =>
        Path.Combine(_logDirectory, $"nodecalibration-{DateTime.Now:yyyy-MM-dd}.log");

    private static void PruneOldFiles(int retain)
    {
        if (retain <= 0)
            return;

        var files = new DirectoryInfo(_logDirectory)
            .GetFiles("nodecalibration-*.log")
            .OrderByDescending(f => f.Name)
            .Skip(retain);

        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch
            {
                // A locked or missing file is not worth reporting.
            }
        }
    }
}
