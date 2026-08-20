using System.Data;
using CompanyUtilityApp.Infrastructure;
using CompanyUtilityApp.Models;

namespace CompanyUtilityApp.Data;

/// <summary>
/// Reads and updates the <c>Areas</c> table — the fixed grid of panel positions.
///
/// The table is seeded once with every route/panel combination and is only ever
/// updated, never inserted into or deleted from, so this repository exposes no
/// Add or Delete.
/// </summary>
public static class AreaRepository
{
    private const string SelectColumns =
        "Id, Route, PanelLocation, HoldingRegister, PanelSerialNumber, Description";

    public static async Task<List<Area>> GetByRouteAsync(
        int route,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                $"""
                 SELECT {SelectColumns}
                 FROM Areas
                 WHERE Route = @route
                 ORDER BY PanelLocation ASC
                 """, connection);

            command.Parameters.Add("@route", SqlDbType.Int).Value = route;

            var results = new List<Area>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results.Add(Map(reader));

            return results;
        }
        catch (Exception ex) when (ex is not DataAccessException and not OperationCanceledException)
        {
            throw Db.Translate(ex, $"load the panel list for route {route}");
        }
    }

    public static async Task<Area?> GetByPanelSerialNumberAsync(
        int panelSerialNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                $"SELECT {SelectColumns} FROM Areas WHERE PanelSerialNumber = @psn", connection);

            command.Parameters.Add("@psn", SqlDbType.Int).Value = panelSerialNumber;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
        }
        catch (Exception ex) when (ex is not DataAccessException and not OperationCanceledException)
        {
            throw Db.Translate(ex, $"load the panel with serial number {panelSerialNumber}");
        }
    }

    /// <summary>
    /// Updates a panel's serial number and description.
    ///
    /// Uniqueness is enforced by the database, not just pre-checked here: a
    /// check-then-write leaves a window in which another workstation can take the
    /// value, so the unique-violation path is treated as a normal outcome rather
    /// than an exception to show the operator.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> UpdateAsync(
        int areaId,
        int newPanelSerialNumber,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (newPanelSerialNumber <= 0)
            return (false, "Panel serial number must be a positive whole number.");

        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                """
                UPDATE Areas
                SET PanelSerialNumber = @psn,
                    Description       = @description
                WHERE Id = @id
                """, connection);

            command.Parameters.Add("@psn", SqlDbType.Int).Value = newPanelSerialNumber;
            command.Parameters.Add("@description", SqlDbType.NVarChar, 255).Value = description.ToDbValue();
            command.Parameters.Add("@id", SqlDbType.Int).Value = areaId;

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
                return (false, "The panel record no longer exists. Refresh and try again.");

            AppLogger.Information(
                $"Area {areaId} updated: PSN={newPanelSerialNumber}, description='{description}'.");
            return (true, null);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2627 or 2601)
        {
            AppLogger.Warning($"Rejected duplicate panel serial number {newPanelSerialNumber} for area {areaId}.");
            return (false, $"Panel serial number {newPanelSerialNumber} is already assigned to another panel.");
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 547)
        {
            // A node row points at the old serial number via the foreign key.
            AppLogger.Warning(
                $"Rejected serial number change for area {areaId}: a node still references the current value.");
            return (false,
                "This panel's serial number cannot change while a node is assigned to it. " +
                "Delete the node first, then change the serial number.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Db.Translate(ex, $"update the panel with id {areaId}");
        }
    }

    /// <summary>Updates only the description, leaving the serial number untouched.</summary>
    public static async Task<(bool Ok, string? Error)> UpdateDescriptionAsync(
        int panelSerialNumber,
        string? description,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                "UPDATE Areas SET Description = @description WHERE PanelSerialNumber = @psn", connection);

            command.Parameters.Add("@description", SqlDbType.NVarChar, 255).Value = description.ToDbValue();
            command.Parameters.Add("@psn", SqlDbType.Int).Value = panelSerialNumber;

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return affected > 0
                ? (true, null)
                : (false, $"No panel found with serial number {panelSerialNumber}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Db.Translate(ex, $"update the description for panel {panelSerialNumber}");
        }
    }

    private static Area Map(Microsoft.Data.SqlClient.SqlDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Route = reader.GetInt32(1),
        PanelLocation = reader.GetInt32(2),
        HoldingRegister = reader.GetInt32(3),
        PanelSerialNumber = reader.GetInt32(4),
        Description = reader.GetNullableString(5),
    };
}
