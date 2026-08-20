using System.IO.Ports;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CompanyUtilityApp.Configuration;
using CompanyUtilityApp.Infrastructure;
using CompanyUtilityApp.Models;

namespace CompanyUtilityApp.Services;

/// <summary>Outcome of pushing configuration to a controller.</summary>
public sealed record DeploymentResult(bool Success, string Message, string? DeviceResponse = null)
{
    public static DeploymentResult Ok(string message, string? response = null) =>
        new(true, message, response);

    public static DeploymentResult Fail(string message, string? response = null) =>
        new(false, message, response);
}

/// <summary>
/// Pushes configuration payloads to ESP32 controllers over USB serial or HTTP.
///
/// The single most important behavioural change from the original code: a
/// deployment is only reported as successful once the controller has
/// ACKNOWLEDGED it. The previous serial path called
/// <c>NodeRepository.MarkCalibrated</c> immediately after
/// <c>SerialPort.Write</c>, but a successful write only means the bytes reached
/// the local UART buffer. An unplugged cable, a device in bootloader mode, or a
/// controller that rejected the JSON all produced a successful write and a node
/// permanently flagged as calibrated in the database while running stale
/// configuration.
/// </summary>
public static class DeviceConfigurationService
{
    /// <summary>
    /// Shared HttpClient. Creating one per request exhausts sockets under
    /// repeated use because the OS holds each closed connection in TIME_WAIT.
    /// </summary>
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(AppConfig.Current.Ota.RequestTimeoutSeconds),
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Substrings that indicate the controller accepted the payload. Matched
    /// case-insensitively against whatever it prints back on the serial console.
    /// </summary>
    private static readonly string[] AckTokens =
    {
        "config saved", "configuration saved", "config ok", "saved", "restarting", "reboot",
    };

    private static readonly string[] NakTokens =
    {
        "error", "invalid", "failed", "bad json", "parse",
    };

    /// <summary>Serialises a payload to the newline-terminated form the firmware expects.</summary>
    public static string Serialise<T>(T payload) =>
        JsonSerializer.Serialize(payload, _jsonOptions) + "\n";

    // ---- Serial (USB) --------------------------------------------------------

    /// <summary>
    /// Sends a payload over a COM port and waits for the controller to answer.
    ///
    /// Opens its own short-lived port so a deployment cannot fight the serial
    /// monitor for the same handle.
    /// </summary>
    public static async Task<DeploymentResult> DeployOverSerialAsync(
        string portName,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(portName))
            return DeploymentResult.Fail("Select a COM port before deploying.");

        var serial = AppConfig.Current.Serial;
        SerialPort? port = null;

        try
        {
            port = new SerialPort(portName, serial.BaudRate, serial.ParsedParity, serial.DataBits, serial.ParsedStopBits)
            {
                ReadTimeout = serial.ReadTimeoutMs,
                WriteTimeout = serial.WriteTimeoutMs,
                NewLine = "\n",
            };

            port.Open();

            // Clear anything the device printed before we arrived, so the reply
            // we evaluate is genuinely a response to this payload.
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            AppLogger.Information($"Serial deploy to {portName}: {payloadJson.Trim()}");
            await port.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(payloadJson), cancellationToken)
                .ConfigureAwait(false);
            await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            var response = await ReadDeviceReplyAsync(port, serial.DeviceAckTimeoutMs, cancellationToken)
                .ConfigureAwait(false);

            return EvaluateReply(response, $"COM port {portName}");
        }
        catch (OperationCanceledException)
        {
            return DeploymentResult.Fail("The deployment was cancelled.");
        }
        catch (UnauthorizedAccessException)
        {
            AppLogger.Warning($"Serial port {portName} is in use by another application.");
            return DeploymentResult.Fail(
                $"{portName} is already open in another application. Close the serial monitor or Arduino IDE and try again.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Serial deployment to {portName} failed.", ex);
            return DeploymentResult.Fail($"Serial communication failed: {ex.Message}");
        }
        finally
        {
            SafeClose(port);
        }
    }

    /// <summary>
    /// Collects the controller's reply until it falls quiet or the timeout expires.
    /// The firmware prints several lines and then restarts, so a fixed single
    /// read would truncate the interesting part.
    /// </summary>
    private static async Task<string> ReadDeviceReplyAsync(
        SerialPort port,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var lastDataAt = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (port.BytesToRead > 0)
            {
                builder.Append(port.ReadExisting());
                lastDataAt = DateTime.UtcNow;

                // Stop early once we have a verdict; no reason to wait out the
                // full timeout after the device has clearly answered.
                var soFar = builder.ToString();
                if (ContainsAny(soFar, AckTokens) || ContainsAny(soFar, NakTokens))
                {
                    // Give it a beat to finish the line, then stop.
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                    if (port.BytesToRead > 0)
                        builder.Append(port.ReadExisting());
                    break;
                }
            }
            else if (builder.Length > 0 && (DateTime.UtcNow - lastDataAt).TotalMilliseconds > 750)
            {
                // It said something, then went quiet. Treat that as the reply.
                break;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return builder.ToString().Trim();
    }

    private static DeploymentResult EvaluateReply(string response, string transport)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            AppLogger.Warning($"No acknowledgement from the controller on {transport}.");
            return DeploymentResult.Fail(
                $"The configuration was sent to {transport}, but the controller did not acknowledge it. " +
                "The node has NOT been marked as calibrated. Check the cable and that the device is running the " +
                "signage firmware, then try again.");
        }

        AppLogger.Information($"Controller reply via {transport}: {response}");

        if (ContainsAny(response, NakTokens))
        {
            return DeploymentResult.Fail(
                "The controller rejected the configuration. The node has NOT been marked as calibrated.",
                response);
        }

        if (ContainsAny(response, AckTokens))
            return DeploymentResult.Ok("The controller accepted the configuration and is restarting.", response);

        // It answered with something unrecognised. Report it rather than guessing.
        return DeploymentResult.Fail(
            "The controller replied, but not with a recognised confirmation. The node has NOT been marked as " +
            "calibrated. Review the device output below and retry if appropriate.",
            response);
    }

    private static bool ContainsAny(string text, IEnumerable<string> tokens) =>
        tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static void SafeClose(SerialPort? port)
    {
        if (port is null)
            return;

        try
        {
            if (port.IsOpen)
                port.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Warning("Failed to close the serial port cleanly.", ex);
        }
        finally
        {
            port.Dispose();
        }
    }

    // ---- OTA (HTTP) ----------------------------------------------------------

    /// <summary>
    /// POSTs a payload to the controller's HTTP configuration endpoint.
    /// Here the HTTP status code IS the acknowledgement, so no separate wait is
    /// needed.
    /// </summary>
    public static async Task<DeploymentResult> DeployOverOtaAsync(
        string ipAddress,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        if (!NetworkValidation.IsValidIPv4(ipAddress))
            return DeploymentResult.Fail("Enter a valid IPv4 address for the controller.");

        var url = $"http://{ipAddress.Trim()}{AppConfig.Current.Ota.ConfigEndpoint}";

        try
        {
            AppLogger.Information($"OTA deploy to {url}: {payloadJson.Trim()}");

            using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            AppLogger.Information($"OTA response {(int)response.StatusCode}: {body}");

            if (response.IsSuccessStatusCode)
            {
                return DeploymentResult.Ok(
                    "The controller accepted the configuration over the network and is restarting.", body);
            }

            return DeploymentResult.Fail(
                $"The controller responded with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                "The node has NOT been marked as calibrated.", body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DeploymentResult.Fail("The deployment was cancelled.");
        }
        catch (OperationCanceledException)
        {
            // HttpClient surfaces its own timeout as OperationCanceledException.
            AppLogger.Warning($"OTA request to {url} timed out.");
            return DeploymentResult.Fail(
                $"The controller at {ipAddress} did not respond within " +
                $"{AppConfig.Current.Ota.RequestTimeoutSeconds} seconds. Check the IP address and the network path.");
        }
        catch (HttpRequestException ex)
        {
            AppLogger.Warning($"OTA request to {url} failed.", ex);
            return DeploymentResult.Fail(
                $"Could not reach the controller at {ipAddress}. Check that it is powered, on the network, " +
                "and reachable from this machine.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Unexpected OTA failure for {url}.", ex);
            return DeploymentResult.Fail($"The network deployment failed: {ex.Message}");
        }
    }

    // ---- Reset --------------------------------------------------------------

    /// <summary>
    /// Sends the reset payload, which clears the controller's stored preferences
    /// and restarts it. Destructive: the caller is expected to have confirmed.
    /// </summary>
    public static Task<DeploymentResult> ResetConfigurationAsync(
        bool useOta,
        string target,
        CancellationToken cancellationToken = default)
    {
        var json = Serialise(new DeviceResetPayload());
        AppLogger.Warning($"Configuration reset requested for {(useOta ? "IP" : "port")} {target}.");

        return useOta
            ? DeployOverOtaAsync(target, json, cancellationToken)
            : DeployOverSerialAsync(target, json, cancellationToken);
    }

    /// <summary>Available COM ports, sorted numerically rather than as text.</summary>
    public static string[] GetAvailablePorts()
    {
        try
        {
            return SerialPort.GetPortNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => int.TryParse(new string(p.Where(char.IsAsciiDigit).ToArray()), out var n) ? n : int.MaxValue)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            AppLogger.Warning("Could not enumerate the serial ports.", ex);
            return Array.Empty<string>();
        }
    }
}
