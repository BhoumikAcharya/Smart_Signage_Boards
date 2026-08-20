using System.IO.Ports;
using System.Text.Json;

namespace CompanyUtilityApp.Configuration;

/// <summary>
/// Strongly typed application configuration, loaded once from
/// <c>appsettings.json</c> beside the executable.
///
/// Replaces the hardcoded connection string and Raspberry&#160;Pi IP address that
/// previously lived in <c>DatabaseHelper</c> and <c>ModbusHelper</c>. Site
/// engineers can now retarget a deployment by editing a text file instead of
/// rebuilding the application.
/// </summary>
public sealed class AppConfig
{
    private const string FileName = "appsettings.json";

    private static readonly Lazy<AppConfig> _instance = new(Load, isThreadSafe: true);

    /// <summary>Reused rather than constructed per call (CA1869).</summary>
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The configuration in force for this process.</summary>
    public static AppConfig Current => _instance.Value;

    public DatabaseOptions Database { get; init; } = new();
    public ModbusOptions Modbus { get; init; } = new();
    public SerialOptions Serial { get; init; } = new();
    public OtaOptions Ota { get; init; } = new();
    public RouteOptions Routes { get; init; } = new();
    public LoggingOptions Logging { get; init; } = new();

    /// <summary>
    /// Problems found while loading. The application still starts on built-in
    /// defaults so a malformed file cannot strand a technician on site, but the
    /// reason is surfaced in the log and on the status bar.
    /// </summary>
    public IReadOnlyList<string> LoadWarnings { get; private init; } = Array.Empty<string>();

    private static AppConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);

        if (!File.Exists(path))
        {
            return new AppConfig
            {
                LoadWarnings = new[]
                {
                    $"'{FileName}' was not found next to the executable; built-in defaults are in use.",
                },
            };
        }

        try
        {
            var json = File.ReadAllText(path);

            var loaded = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions);
            if (loaded is null)
            {
                return new AppConfig
                {
                    LoadWarnings = new[] { $"'{FileName}' was empty; built-in defaults are in use." },
                };
            }

            var warnings = new List<string>();
            loaded.Validate(warnings);
            return new AppConfig
            {
                Database = loaded.Database,
                Modbus = loaded.Modbus,
                Serial = loaded.Serial,
                Ota = loaded.Ota,
                Routes = loaded.Routes,
                Logging = loaded.Logging,
                LoadWarnings = warnings,
            };
        }
        catch (Exception ex)
        {
            return new AppConfig
            {
                LoadWarnings = new[]
                {
                    $"'{FileName}' could not be read ({ex.Message}); built-in defaults are in use.",
                },
            };
        }
    }

    private void Validate(List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(Database.ConnectionString))
            warnings.Add("Database.ConnectionString is empty; the application cannot reach the database.");

        if (Routes.Count <= 0)
            warnings.Add($"Routes.Count must be positive; falling back to {RouteOptions.DefaultCount}.");

        if (Routes.PanelsPerRoute <= 0)
            warnings.Add($"Routes.PanelsPerRoute must be positive; falling back to {RouteOptions.DefaultPanelsPerRoute}.");

        if (Modbus.Port is < 1 or > 65535)
            warnings.Add("Modbus.Port is outside 1-65535; falling back to 502.");
    }

    public sealed class DatabaseOptions
    {
        public string ConnectionString { get; init; } =
            @"Server=(localdb)\MSSQLLocalDB;Database=NodeManagementDB;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=15";

        public int CommandTimeoutSeconds { get; init; } = 30;
    }

    public sealed class ModbusOptions
    {
        public string GatewayHost { get; init; } = "192.168.1.10";
        public int Port { get; init; } = 502;
        public byte SlaveId { get; init; } = 1;
        public int ConnectTimeoutMs { get; init; } = 3000;
        public int ReadWriteTimeoutMs { get; init; } = 3000;

        /// <summary>Zero-based base address of the per-node diagnostic block.</summary>
        public const ushort DiagnosticsBaseAddress = 5000;

        /// <summary>Registers occupied by each node's diagnostic block.</summary>
        public const ushort RegistersPerNode = 6;

        /// <summary>Zero-based address of the MUX register (Modbus 43001).</summary>
        public const ushort MuxRegisterAddress = 3000;

        /// <summary>Zero-based base address of the manual relay block (Modbus 41001).</summary>
        public const ushort ManualRelayBaseAddress = 1000;

        /// <summary>Zero-based base address of the SCADA block (Modbus 42001).</summary>
        public const ushort ScadaBaseAddress = 2000;
    }

    public sealed class SerialOptions
    {
        public int BaudRate { get; init; } = 115200;
        public int DataBits { get; init; } = 8;
        public string Parity { get; init; } = "None";
        public string StopBits { get; init; } = "One";
        public int ReadTimeoutMs { get; init; } = 2000;
        public int WriteTimeoutMs { get; init; } = 2000;

        /// <summary>
        /// How long to wait for the device to acknowledge a configuration push
        /// before reporting failure. Writing bytes to a COM port proves nothing
        /// about the controller having accepted them, so the deployment is only
        /// treated as successful once the device answers.
        /// </summary>
        public int DeviceAckTimeoutMs { get; init; } = 8000;

        // Fully qualified because the string properties above shadow the
        // System.IO.Ports type names in this scope.
        public System.IO.Ports.Parity ParsedParity =>
            Enum.TryParse<System.IO.Ports.Parity>(Parity, ignoreCase: true, out var value)
                ? value
                : System.IO.Ports.Parity.None;

        public System.IO.Ports.StopBits ParsedStopBits =>
            Enum.TryParse<System.IO.Ports.StopBits>(StopBits, ignoreCase: true, out var value)
                ? value
                : System.IO.Ports.StopBits.One;
    }

    public sealed class OtaOptions
    {
        public int RequestTimeoutSeconds { get; init; } = 5;
        public string ConfigEndpoint { get; init; } = "/config";
    }

    public sealed class RouteOptions
    {
        public const int DefaultCount = 4;
        public const int DefaultPanelsPerRoute = 100;

        public int Count { get; init; } = DefaultCount;
        public int PanelsPerRoute { get; init; } = DefaultPanelsPerRoute;

        /// <summary>Route numbers to offer in the UI, e.g. 1..4.</summary>
        public IReadOnlyList<int> RouteNumbers =>
            Enumerable.Range(1, Count > 0 ? Count : DefaultCount).ToList();
    }

    public sealed class LoggingOptions
    {
        public string MinimumLevel { get; init; } = "Information";
        public int RetainedFileCount { get; init; } = 14;
    }
}
