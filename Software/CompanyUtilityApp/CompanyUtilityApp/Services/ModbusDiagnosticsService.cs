using System.Net.Sockets;
using CompanyUtilityApp.Configuration;
using CompanyUtilityApp.Infrastructure;
using Modbus.Device;

namespace CompanyUtilityApp.Services;

/// <summary>One node's diagnostic snapshot, decoded from its 6-register block.</summary>
public sealed record NodeDiagnostics(
    int NodeNumber,
    bool IsOnline,
    bool PowerOk,
    bool Current1Ok,
    bool Current2Ok,
    int BatteryPercent,
    int RelayStatus)
{
    /// <summary>Relay status is a 2-bit field: bit 0 = relay 1, bit 1 = relay 2.</summary>
    public bool Relay1On => (RelayStatus & 0b01) != 0;

    public bool Relay2On => (RelayStatus & 0b10) != 0;

    public string StatusText => IsOnline ? "ONLINE" : "OFFLINE";
}

/// <summary>
/// Reads node telemetry and writes control registers through the Modbus TCP
/// gateway on the Raspberry Pi.
///
/// Replaces the former static <c>ModbusHelper</c>, which returned an
/// <see cref="IModbusMaster"/> while keeping the underlying
/// <see cref="TcpClient"/> private. Callers therefore had no way to close the
/// socket, so every connection attempt leaked one — and a static readonly host
/// address meant the gateway could not be moved without a rebuild.
///
/// This type owns both objects and is disposable, so a caller can wrap it in
/// <c>using</c> and be sure the socket is released.
/// </summary>
public sealed class ModbusDiagnosticsService : IDisposable
{
    private readonly AppConfig.ModbusOptions _options = AppConfig.Current.Modbus;
    private TcpClient? _client;

    // Concrete type rather than IModbusMaster: the factory only ever returns
    // ModbusIpMaster here, and naming it avoids an interface dispatch on every
    // register read (CA1859).
    private ModbusIpMaster? _master;
    private bool _disposed;

    public bool IsConnected => _client?.Connected == true && _master is not null;

    /// <summary>
    /// Opens the gateway connection, applying a connect timeout.
    ///
    /// <see cref="TcpClient"/> has no connect timeout of its own, so an
    /// unreachable gateway would otherwise block for the OS default (roughly 20
    /// seconds on Windows) and freeze whatever called it.
    /// </summary>
    public async Task<(bool Ok, string? Error)> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsConnected)
            return (true, null);

        Cleanup();

        try
        {
            _client = new TcpClient
            {
                ReceiveTimeout = _options.ReadWriteTimeoutMs,
                SendTimeout = _options.ReadWriteTimeoutMs,
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeoutMs);

            await _client.ConnectAsync(_options.GatewayHost, _options.Port, timeout.Token).ConfigureAwait(false);

            _master = ModbusIpMaster.CreateIp(_client);
            _master.Transport.ReadTimeout = _options.ReadWriteTimeoutMs;
            _master.Transport.WriteTimeout = _options.ReadWriteTimeoutMs;

            AppLogger.Information($"Modbus gateway connected: {_options.GatewayHost}:{_options.Port}");
            return (true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Cleanup();
            var message = $"The Modbus gateway at {_options.GatewayHost}:{_options.Port} did not answer within " +
                          $"{_options.ConnectTimeoutMs} ms.";
            AppLogger.Warning(message);
            return (false, message);
        }
        catch (Exception ex)
        {
            Cleanup();
            AppLogger.Warning($"Could not connect to the Modbus gateway at {_options.GatewayHost}:{_options.Port}.", ex);
            return (false,
                $"Could not connect to the Modbus gateway at {_options.GatewayHost}:{_options.Port}. " +
                "Confirm the bridge service is running and the address in appsettings.json is correct.");
        }
    }

    /// <summary>
    /// Reads and decodes one node's diagnostic block.
    ///
    /// Address layout, zero-based: node N starts at 5000 + (N-1)*6, then
    /// +0 status, +1 power, +2 current 1, +3 current 2, +4 battery %, +5 relays.
    /// </summary>
    public async Task<NodeDiagnostics?> ReadNodeAsync(
        int nodeNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeNumber, 1);

        if (!IsConnected)
        {
            var (ok, _) = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!ok)
                return null;
        }

        var start = (ushort)(AppConfig.ModbusOptions.DiagnosticsBaseAddress
                             + (nodeNumber - 1) * AppConfig.ModbusOptions.RegistersPerNode);

        try
        {
            var registers = await Task.Run(
                () => _master!.ReadHoldingRegisters(
                    _options.SlaveId, start, AppConfig.ModbusOptions.RegistersPerNode),
                cancellationToken).ConfigureAwait(false);

            if (registers.Length < AppConfig.ModbusOptions.RegistersPerNode)
            {
                AppLogger.Warning($"Short Modbus read for node {nodeNumber}: {registers.Length} registers.");
                return null;
            }

            return new NodeDiagnostics(
                NodeNumber: nodeNumber,
                IsOnline: registers[0] == 1,
                PowerOk: registers[1] == 1,
                Current1Ok: registers[2] == 1,
                Current2Ok: registers[3] == 1,
                BatteryPercent: registers[4],
                RelayStatus: registers[5]);
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Modbus read failed for node {nodeNumber} at register {start}.", ex);
            Cleanup();   // Force a reconnect on the next attempt.
            return null;
        }
    }

    /// <summary>Reads a contiguous range of nodes, skipping any that fail.</summary>
    public async Task<List<NodeDiagnostics>> ReadNodeRangeAsync(
        int firstNode,
        int count,
        CancellationToken cancellationToken = default)
    {
        var results = new List<NodeDiagnostics>(count);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var diagnostics = await ReadNodeAsync(firstNode + i, cancellationToken).ConfigureAwait(false);
            if (diagnostics is not null)
                results.Add(diagnostics);
        }

        return results;
    }

    /// <summary>Sets the MUX register (Modbus 43001).</summary>
    public Task<bool> WriteMuxAsync(ushort value, CancellationToken cancellationToken = default) =>
        WriteRegisterAsync(AppConfig.ModbusOptions.MuxRegisterAddress, value, "MUX", cancellationToken);

    /// <summary>Sets a manual relay register (Modbus 41001 upwards) for one node.</summary>
    public Task<bool> WriteManualRelayAsync(
        int nodeNumber, ushort value, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeNumber, 1);

        var address = (ushort)(AppConfig.ModbusOptions.ManualRelayBaseAddress + (nodeNumber - 1));
        return WriteRegisterAsync(address, value, $"manual relay for node {nodeNumber}", cancellationToken);
    }

    /// <summary>Sets a SCADA register (Modbus 42001 upwards) for one node.</summary>
    public Task<bool> WriteScadaAsync(
        int nodeNumber, ushort value, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeNumber, 1);

        var address = (ushort)(AppConfig.ModbusOptions.ScadaBaseAddress + (nodeNumber - 1));
        return WriteRegisterAsync(address, value, $"SCADA register for node {nodeNumber}", cancellationToken);
    }

    private async Task<bool> WriteRegisterAsync(
        ushort address,
        ushort value,
        string description,
        CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            var (ok, _) = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!ok)
                return false;
        }

        try
        {
            await Task.Run(
                () => _master!.WriteSingleRegister(_options.SlaveId, address, value),
                cancellationToken).ConfigureAwait(false);

            AppLogger.Information($"Modbus write: {description} (register {address}) = {value}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Modbus write failed: {description} (register {address}).", ex);
            Cleanup();
            return false;
        }
    }

    private void Cleanup()
    {
        try
        {
            _master?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Warning("Failed to dispose the Modbus master cleanly.", ex);
        }

        try
        {
            _client?.Close();
            _client?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Warning("Failed to close the Modbus socket cleanly.", ex);
        }

        _master = null;
        _client = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Cleanup();
        _disposed = true;
    }
}
