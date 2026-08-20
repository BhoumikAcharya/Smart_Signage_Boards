using System.Text.Json;
using CompanyUtilityApp.Infrastructure;
using CompanyUtilityApp.Models;

namespace CompanyUtilityApp.Services;

/// <summary>
/// Persists technician and network preferences to <c>settings.json</c>.
///
/// The file lives under <c>%LOCALAPPDATA%</c> rather than beside the executable.
/// A program-files installation directory is not writable by a standard user, so
/// saving next to the binary either fails or silently triggers UAC
/// virtualisation, which is why per-user application data is the correct location.
///
/// Writes are atomic: the payload goes to a temporary file which then replaces
/// the original, so an interrupted save cannot leave a half-written file that
/// fails to parse on next launch.
/// </summary>
public sealed class SettingsService
{
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
    };

    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "I2ST", "NodeCalibration");

    private readonly object _gate = new();

    public string FilePath => Path.Combine(_directory, FileName);

    /// <summary>The persisted document, holding both settings groups.</summary>
    private sealed class SettingsDocument
    {
        public TechnicianSettings Technician { get; set; } = new();
        public NetworkSettings Network { get; set; } = new();
    }

    public TechnicianSettings LoadTechnicianSettings() => Load().Technician;

    public NetworkSettings LoadNetworkSettings() => Load().Network;

    public (bool Ok, string? Error) SaveTechnicianSettings(TechnicianSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.TryValidate(out var error))
            return (false, error);

        var document = Load();
        document.Technician = settings;
        return Save(document, "technician settings");
    }

    public (bool Ok, string? Error) SaveNetworkSettings(NetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.TryValidate(out var error))
            return (false, error);

        var document = Load();
        document.Network = settings;
        return Save(document, "network settings");
    }

    private SettingsDocument Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new SettingsDocument();

                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<SettingsDocument>(json, _options) ?? new SettingsDocument();
            }
            catch (JsonException ex)
            {
                // A corrupt file should not stop the screen from opening; the
                // operator sees empty fields and can re-save.
                AppLogger.Warning($"'{FilePath}' is not valid JSON; defaults are in use.", ex);
                return new SettingsDocument();
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"Could not read '{FilePath}'; defaults are in use.", ex);
                return new SettingsDocument();
            }
        }
    }

    private (bool Ok, string? Error) Save(SettingsDocument document, string description)
    {
        lock (_gate)
        {
            var temporaryPath = FilePath + ".tmp";

            try
            {
                Directory.CreateDirectory(_directory);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, _options));

                // File.Move with overwrite is atomic enough for our purposes and
                // avoids the window where neither file exists.
                File.Move(temporaryPath, FilePath, overwrite: true);

                AppLogger.Information($"Saved {description} to '{FilePath}'.");
                return (true, null);
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogger.Error($"Access denied saving {description} to '{FilePath}'.", ex);
                return (false, $"Access was denied when writing to {FilePath}.");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to save {description} to '{FilePath}'.", ex);
                return (false, $"The settings could not be saved: {ex.Message}");
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A stray .tmp file is harmless.
        }
    }
}
