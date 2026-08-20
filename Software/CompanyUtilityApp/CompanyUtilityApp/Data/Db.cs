using System.Globalization;
using CompanyUtilityApp.Configuration;
using CompanyUtilityApp.Infrastructure;
using Microsoft.Data.SqlClient;

namespace CompanyUtilityApp.Data;

/// <summary>
/// Raised when a database operation fails, carrying a message that is safe and
/// meaningful to show an operator.
///
/// The repositories previously let raw <see cref="SqlException"/> escape to the
/// UI, where an unhandled instance terminated the process. Wrapping gives the
/// screens one exception type to catch and keeps server details out of dialogs.
/// </summary>
public sealed class DataAccessException : Exception
{
    public DataAccessException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Creates database connections and centralises error translation.
///
/// Replaces the former <c>DatabaseHelper</c>, which exposed a hardcoded
/// connection string as a public constant.
/// </summary>
public static class Db
{
    /// <summary>Well-known SQL Server error numbers worth explaining specifically.</summary>
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;
    private const int ForeignKeyViolation = 547;
    private const int LoginFailed = 18456;
    private const int CannotOpenDatabase = 4060;

    public static string ConnectionString => AppConfig.Current.Database.ConnectionString;

    /// <summary>Opens a connection asynchronously, translating connectivity faults.</summary>
    public static async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw Translate(ex, "open a connection to the node database");
        }
    }

    /// <summary>Creates a command with the configured timeout applied.</summary>
    public static SqlCommand CreateCommand(string sql, SqlConnection connection)
    {
        var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = AppConfig.Current.Database.CommandTimeoutSeconds,
        };
        return command;
    }

    /// <summary>
    /// Converts a provider exception into a <see cref="DataAccessException"/>
    /// whose message an operator can act on.
    /// </summary>
    public static DataAccessException Translate(Exception ex, string operation)
    {
        AppLogger.Error($"Database failure while trying to {operation}.", ex);

        if (ex is SqlException sql)
        {
            var message = sql.Number switch
            {
                UniqueConstraintViolation or UniqueIndexViolation =>
                    "That value is already used by another record. Node numbers, IP addresses and panel serial numbers must each be unique.",
                ForeignKeyViolation =>
                    "The record is still referenced elsewhere, or the panel serial number does not exist in the Areas table.",
                LoginFailed =>
                    "The database rejected the sign-in. Check that this Windows account has access to NodeManagementDB.",
                CannotOpenDatabase =>
                    "The NodeManagementDB database could not be opened. Confirm it exists on the configured server.",
                -2 =>
                    "The database did not respond in time. Check the network connection to the server.",
                _ =>
                    $"The database reported an error while trying to {operation}.",
            };

            return new DataAccessException(message, sql);
        }

        return new DataAccessException(
            $"An unexpected error occurred while trying to {operation}.", ex);
    }

    /// <summary>Reads a nullable string column without tripping over DBNull.</summary>
    public static string? GetNullableString(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Boxes a nullable string for a parameter value, mapping empty to NULL.</summary>
    public static object ToDbValue(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    /// <summary>
    /// Verifies the database is reachable and the expected tables exist.
    /// Called once at start-up so a misconfiguration is reported on the status
    /// bar rather than as a stack trace the first time someone opens a grid.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = CreateCommand(
                """
                SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_NAME IN ('Users', 'Areas', 'Nodes')
                """, connection);

            var found = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);

            return found == 3
                ? (true, null)
                : (false, $"Connected, but only {found} of the 3 expected tables (Users, Areas, Nodes) were found.");
        }
        catch (DataAccessException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Database health check failed.", ex);
            return (false, ex.Message);
        }
    }
}
