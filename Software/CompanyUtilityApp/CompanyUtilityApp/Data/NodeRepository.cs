using System.Globalization;
using System.Data;
using CompanyUtilityApp.Infrastructure;
using CompanyUtilityApp.Models;
using Microsoft.Data.SqlClient;

namespace CompanyUtilityApp.Data;

/// <summary>
/// Reads and writes the <c>Nodes</c> table — the ESP32 controllers installed at
/// panel positions.
///
/// Two methods from the original implementation were removed rather than ported,
/// because both referenced columns that do not exist and would have thrown a
/// <see cref="SqlException"/> the first time they ran:
///
///   * <c>IpAddressExists</c> queried <c>Nodes.IPAddress</c>; the column is
///     <c>LocalIPAddress</c>. <see cref="LocalIpExistsAsync"/> replaces it.
///   * <c>PanelLocationExistsForRoute</c> queried <c>Nodes.Route</c> and
///     <c>Nodes.PanelLocation</c>; neither exists on that table — they live on
///     <c>Areas</c>. The constraint it was trying to express,
///     one node per panel, is already guaranteed by the unique
///     <c>Nodes.PanelSerialNumber</c> column, so no replacement is needed.
/// </summary>
public static class NodeRepository
{
    private const string NodeColumns =
        """
        Id, NodeNumber, PanelSerialNumber, LocalIPAddress,
        Zerovolt_CS1, Zerovolt_CS2, Sensitivity_CS1, Sensitivity_CS2,
        Threshold_CS1, Threshold_CS2, BatteryCalibration,
        BatterySagCompensation, PSUThreshold, Calibration
        """;

    // ---- Queries -------------------------------------------------------------

    /// <summary>
    /// Nodes installed on a route, joined to their panel position.
    ///
    /// INNER JOIN is intentional: it yields only panels that actually have a
    /// controller, rather than all pre-seeded positions.
    /// </summary>
    public static async Task<List<NodeListItem>> GetByRouteAsync(
        int route,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                """
                SELECT A.Route, A.PanelLocation, A.PanelSerialNumber,
                       N.NodeNumber, N.LocalIPAddress, A.Description, N.Calibration
                FROM Areas A
                INNER JOIN Nodes N ON A.PanelSerialNumber = N.PanelSerialNumber
                WHERE A.Route = @route
                ORDER BY A.PanelLocation ASC
                """, connection);

            command.Parameters.Add("@route", SqlDbType.Int).Value = route;

            var results = new List<NodeListItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new NodeListItem
                {
                    Route = reader.GetInt32(0),
                    PanelLocation = reader.GetInt32(1),
                    PanelSerialNumber = reader.GetInt32(2),
                    NodeNumber = reader.GetInt32(3),
                    LocalIPAddress = reader.GetString(4),
                    Description = reader.GetNullableString(5),
                    IsCalibrated = reader.GetBoolean(6),
                });
            }

            return results;
        }
        catch (Exception ex) when (ex is not DataAccessException and not OperationCanceledException)
        {
            throw Db.Translate(ex, $"load the nodes for route {route}");
        }
    }

    /// <summary>Panel positions on a route that do not yet have a controller assigned.</summary>
    public static async Task<List<Area>> GetUnassignedPanelsAsync(
        int route,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                """
                SELECT A.Id, A.Route, A.PanelLocation, A.HoldingRegister,
                       A.PanelSerialNumber, A.Description
                FROM Areas A
                WHERE A.Route = @route
                  AND NOT EXISTS (
                      SELECT 1 FROM Nodes N
                      WHERE N.PanelSerialNumber = A.PanelSerialNumber)
                ORDER BY A.PanelLocation ASC
                """, connection);

            command.Parameters.Add("@route", SqlDbType.Int).Value = route;

            var results = new List<Area>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new Area
                {
                    Id = reader.GetInt32(0),
                    Route = reader.GetInt32(1),
                    PanelLocation = reader.GetInt32(2),
                    HoldingRegister = reader.GetInt32(3),
                    PanelSerialNumber = reader.GetInt32(4),
                    Description = reader.GetNullableString(5),
                });
            }

            return results;
        }
        catch (Exception ex) when (ex is not DataAccessException and not OperationCanceledException)
        {
            throw Db.Translate(ex, $"load the available panels for route {route}");
        }
    }

    /// <summary>Uncalibrated nodes on a route, as deployment targets.</summary>
    public static async Task<List<PanelDeploymentTarget>> GetUncalibratedAsync(
        int route,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                """
                SELECT A.Route, A.PanelLocation, A.PanelSerialNumber,
                       N.LocalIPAddress, A.Description, A.HoldingRegister, N.NodeNumber
                FROM Areas A
                INNER JOIN Nodes N ON A.PanelSerialNumber = N.PanelSerialNumber
                WHERE A.Route = @route AND N.Calibration = 0
                ORDER BY A.PanelLocation ASC
                """, connection);

            command.Parameters.Add("@route", SqlDbType.Int).Value = route;

            var results = new List<PanelDeploymentTarget>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new PanelDeploymentTarget
                {
                    Route = reader.GetInt32(0),
                    PanelLocation = reader.GetInt32(1),
                    PanelSerialNumber = reader.GetInt32(2),
                    IPAddress = reader.GetString(3),
                    Description = reader.GetNullableString(4),
                    HoldingRegister = reader.GetInt32(5),
                    NodeNumber = reader.GetInt32(6),
                });
            }

            return results;
        }
        catch (Exception ex) when (ex is not DataAccessException and not OperationCanceledException)
        {
            throw Db.Translate(ex, $"load the uncalibrated nodes for route {route}");
        }
    }

    public static async Task<Node?> GetByPanelSerialNumberAsync(
        int panelSerialNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                $"SELECT {NodeColumns} FROM Nodes WHERE PanelSerialNumber = @psn", connection);

            command.Parameters.Add("@psn", SqlDbType.Int).Value = panelSerialNumber;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
        }
        catch (Exception ex) when (ex is not DataAccessException and not OperationCanceledException)
        {
            throw Db.Translate(ex, $"load the node for panel {panelSerialNumber}");
        }
    }

    // ---- Uniqueness checks ---------------------------------------------------
    // These give a friendly message before the write is attempted. They are a
    // convenience, not the guarantee: the database's unique constraints are, and
    // the insert/update paths below handle a violation as a normal outcome.

    public static Task<bool> NodeNumberExistsAsync(
        int nodeNumber, int? excludeNodeId = null, CancellationToken cancellationToken = default) =>
        ExistsAsync("NodeNumber = @value", SqlDbType.Int, nodeNumber, excludeNodeId, cancellationToken);

    public static Task<bool> LocalIpExistsAsync(
        string ipAddress, int? excludeNodeId = null, CancellationToken cancellationToken = default) =>
        ExistsAsync("LocalIPAddress = @value", SqlDbType.NVarChar, ipAddress.Trim(), excludeNodeId, cancellationToken);

    public static Task<bool> PanelSerialNumberAssignedAsync(
        int panelSerialNumber, int? excludeNodeId = null, CancellationToken cancellationToken = default) =>
        ExistsAsync("PanelSerialNumber = @value", SqlDbType.Int, panelSerialNumber, excludeNodeId, cancellationToken);

    private static async Task<bool> ExistsAsync(
        string predicate,
        SqlDbType type,
        object value,
        int? excludeNodeId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);

            var sql = $"SELECT COUNT(1) FROM Nodes WHERE {predicate}";
            if (excludeNodeId.HasValue)
                sql += " AND Id <> @excludeId";

            await using var command = Db.CreateCommand(sql, connection);
            command.Parameters.Add("@value", type).Value = value;
            if (excludeNodeId.HasValue)
                command.Parameters.Add("@excludeId", SqlDbType.Int).Value = excludeNodeId.Value;

            var count = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            return count > 0;
        }
        catch (Exception ex) when (ex is not DataAccessException and not OperationCanceledException)
        {
            throw Db.Translate(ex, "check whether that value is already in use");
        }
    }

    // ---- Writes --------------------------------------------------------------

    /// <summary>
    /// Inserts a node and returns its new identity.
    ///
    /// Calibration constants are left to the database defaults: they are physical
    /// characteristics measured per device, and inventing values here would give
    /// a false impression that the node had been calibrated.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> AddAsync(
        Node node,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);

        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                """
                INSERT INTO Nodes (NodeNumber, PanelSerialNumber, LocalIPAddress, Calibration)
                VALUES (@nodeNumber, @psn, @ip, @calibration);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """, connection);

            command.Parameters.Add("@nodeNumber", SqlDbType.Int).Value = node.NodeNumber;
            command.Parameters.Add("@psn", SqlDbType.Int).Value = node.PanelSerialNumber;
            command.Parameters.Add("@ip", SqlDbType.NVarChar, 15).Value = node.LocalIPAddress.Trim();
            command.Parameters.Add("@calibration", SqlDbType.Bit).Value = node.IsCalibrated;

            var newId = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            node.Id = Convert.ToInt32(newId, CultureInfo.InvariantCulture);

            AppLogger.Information(
                $"Node created: id={node.Id}, number={node.NodeNumber}, PSN={node.PanelSerialNumber}, ip={node.LocalIPAddress}.");
            return (true, null);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            AppLogger.Warning($"Rejected duplicate node insert (number={node.NodeNumber}, ip={node.LocalIPAddress}).");
            return (false, DescribeDuplicate(ex, node));
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            return (false,
                $"Panel serial number {node.PanelSerialNumber} does not exist in the Areas table. " +
                "Assign the serial number on the Area screen first.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Db.Translate(ex, "save the new node");
        }
    }

    /// <summary>
    /// Updates the editable identity fields of a node.
    ///
    /// The panel serial number is deliberately not updatable: it is the join key
    /// to the panel position, and moving a controller to a different panel is a
    /// delete-and-re-add, not an edit.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> UpdateAsync(
        Node node,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);

        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                """
                UPDATE Nodes
                SET NodeNumber     = @nodeNumber,
                    LocalIPAddress = @ip,
                    Calibration    = @calibration
                WHERE Id = @id
                """, connection);

            command.Parameters.Add("@nodeNumber", SqlDbType.Int).Value = node.NodeNumber;
            command.Parameters.Add("@ip", SqlDbType.NVarChar, 15).Value = node.LocalIPAddress.Trim();
            command.Parameters.Add("@calibration", SqlDbType.Bit).Value = node.IsCalibrated;
            command.Parameters.Add("@id", SqlDbType.Int).Value = node.Id;

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
                return (false, "The node record no longer exists. Refresh the list and try again.");

            AppLogger.Information(
                $"Node updated: id={node.Id}, number={node.NodeNumber}, ip={node.LocalIPAddress}, calibrated={node.IsCalibrated}.");
            return (true, null);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            AppLogger.Warning($"Rejected duplicate node update (id={node.Id}).");
            return (false, DescribeDuplicate(ex, node));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Db.Translate(ex, $"update node {node.NodeNumber}");
        }
    }

    public static async Task<(bool Ok, string? Error)> DeleteAsync(
        int nodeId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand("DELETE FROM Nodes WHERE Id = @id", connection);
            command.Parameters.Add("@id", SqlDbType.Int).Value = nodeId;

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
                return (false, "The node record no longer exists. The list has been refreshed.");

            AppLogger.Information($"Node deleted: id={nodeId}.");
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Db.Translate(ex, $"delete node {nodeId}");
        }
    }

    /// <summary>
    /// Records that a controller has accepted its configuration.
    ///
    /// Called only after the device has acknowledged the payload — never merely
    /// because bytes were written to a port.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> MarkCalibratedAsync(
        int panelSerialNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                "UPDATE Nodes SET Calibration = 1 WHERE PanelSerialNumber = @psn", connection);

            command.Parameters.Add("@psn", SqlDbType.Int).Value = panelSerialNumber;

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
                return (false, $"No node is assigned to panel serial number {panelSerialNumber}.");

            AppLogger.Information($"Node for panel {panelSerialNumber} marked as calibrated.");
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Db.Translate(ex, $"mark panel {panelSerialNumber} as calibrated");
        }
    }

    /// <summary>
    /// Stores calibration constants that were pushed to a controller, so the
    /// database reflects what the device is actually running.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> UpdateCalibrationAsync(
        int panelSerialNumber,
        CalibrationOverride calibration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calibration);

        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                """
                UPDATE Nodes
                SET Sensitivity_CS1   = @sens1,
                    Sensitivity_CS2   = @sens2,
                    BatteryCalibration = @battery
                WHERE PanelSerialNumber = @psn
                """, connection);

            command.Parameters.Add("@sens1", SqlDbType.Float).Value = calibration.SensitivityCs1;
            command.Parameters.Add("@sens2", SqlDbType.Float).Value = calibration.SensitivityCs2;
            command.Parameters.Add("@battery", SqlDbType.Float).Value = calibration.BatteryCalibration;
            command.Parameters.Add("@psn", SqlDbType.Int).Value = panelSerialNumber;

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
                return (false, $"No node is assigned to panel serial number {panelSerialNumber}.");

            AppLogger.Information(
                $"Calibration stored for panel {panelSerialNumber}: " +
                $"CS1={calibration.SensitivityCs1}, CS2={calibration.SensitivityCs2}, battery={calibration.BatteryCalibration}.");
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Db.Translate(ex, $"store the calibration for panel {panelSerialNumber}");
        }
    }

    // ---- Helpers -------------------------------------------------------------

    /// <summary>
    /// Turns a unique-constraint violation into a message naming the field.
    /// The SQL Server message text carries the index name, which is the only
    /// reliable way to tell which column collided.
    /// </summary>
    private static string DescribeDuplicate(SqlException ex, Node node)
    {
        var text = ex.Message;

        if (text.Contains("NodeNumber", StringComparison.OrdinalIgnoreCase))
            return $"Node number {node.NodeNumber} is already assigned to another node.";

        if (text.Contains("LocalIPAddress", StringComparison.OrdinalIgnoreCase)
            || text.Contains("IPAddress", StringComparison.OrdinalIgnoreCase))
            return $"IP address {node.LocalIPAddress} is already assigned to another node.";

        if (text.Contains("PanelSerialNumber", StringComparison.OrdinalIgnoreCase))
            return $"Panel serial number {node.PanelSerialNumber} already has a node assigned.";

        return "Node number, IP address and panel serial number must each be unique. " +
               "One of these values is already in use.";
    }

    private static Node Map(SqlDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        NodeNumber = reader.GetInt32(1),
        PanelSerialNumber = reader.GetInt32(2),
        LocalIPAddress = reader.GetString(3),
        ZeroVoltCs1 = reader.GetDouble(4),
        ZeroVoltCs2 = reader.GetDouble(5),
        SensitivityCs1 = reader.GetDouble(6),
        SensitivityCs2 = reader.GetDouble(7),
        ThresholdCs1 = reader.GetDouble(8),
        ThresholdCs2 = reader.GetDouble(9),
        BatteryCalibration = reader.GetDouble(10),
        BatterySagCompensation = reader.GetDouble(11),
        PsuThreshold = reader.GetInt32(12),
        IsCalibrated = reader.GetBoolean(13),
    };
}
