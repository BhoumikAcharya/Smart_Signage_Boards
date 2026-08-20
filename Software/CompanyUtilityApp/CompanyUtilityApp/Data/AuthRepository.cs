using CompanyUtilityApp.Infrastructure;
using CompanyUtilityApp.Models;
using CompanyUtilityApp.Security;
using Microsoft.Data.SqlClient;

namespace CompanyUtilityApp.Data;

/// <summary>Outcome of a sign-in attempt.</summary>
public enum AuthenticationResult
{
    Success,
    InvalidCredentials,
    Failed,
}

/// <summary>Authenticates operators against the <c>Users</c> table.</summary>
public static class AuthRepository
{
    /// <summary>
    /// Validates credentials.
    ///
    /// On success against a legacy unsalted SHA-256 row, the stored hash is
    /// transparently upgraded to PBKDF2. This is the only moment the plaintext
    /// password is available, so it is the only moment the upgrade can happen.
    /// A failure to upgrade is logged but never blocks the sign-in.
    /// </summary>
    public static async Task<(AuthenticationResult Result, User? User, string? Error)> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return (AuthenticationResult.InvalidCredentials, null, "Enter both a username and a password.");

        username = username.Trim();

        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);

            int id;
            string storedUsername, fullName, storedHash;
            DateTime createdAt;

            await using (var command = Db.CreateCommand(
                """
                SELECT Id, Username, FullName, PasswordHash, CreatedAt
                FROM Users
                WHERE Username = @username
                """, connection))
            {
                command.Parameters.Add("@username", System.Data.SqlDbType.NVarChar, 50).Value = username;

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Same message and broadly the same cost whether the user
                    // exists or not, so the screen cannot be used to enumerate
                    // valid usernames.
                    AppLogger.Warning($"Failed sign-in attempt for unknown username '{username}'.");
                    return (AuthenticationResult.InvalidCredentials, null, "Incorrect username or password.");
                }

                id = reader.GetInt32(0);
                storedUsername = reader.GetString(1);
                fullName = reader.GetString(2);
                storedHash = reader.GetString(3);
                createdAt = reader.IsDBNull(4) ? DateTime.MinValue : reader.GetDateTime(4);
            }

            if (!PasswordHasher.Verify(password, storedHash))
            {
                AppLogger.Warning($"Failed sign-in attempt for '{username}': incorrect password.");
                return (AuthenticationResult.InvalidCredentials, null, "Incorrect username or password.");
            }

            if (PasswordHasher.NeedsUpgrade(storedHash))
                await TryUpgradeHashAsync(connection, id, password, cancellationToken).ConfigureAwait(false);

            var user = new User
            {
                Id = id,
                Username = storedUsername,
                FullName = fullName,
                CreatedAt = createdAt,
            };

            return (AuthenticationResult.Success, user, null);
        }
        catch (DataAccessException ex)
        {
            return (AuthenticationResult.Failed, null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var wrapped = Db.Translate(ex, "verify the sign-in credentials");
            return (AuthenticationResult.Failed, null, wrapped.Message);
        }
    }

    private static async Task TryUpgradeHashAsync(
        SqlConnection connection,
        int userId,
        string password,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = Db.CreateCommand(
                "UPDATE Users SET PasswordHash = @hash WHERE Id = @id", connection);

            command.Parameters.Add("@hash", System.Data.SqlDbType.NVarChar, 255).Value = PasswordHasher.Hash(password);
            command.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = userId;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            AppLogger.Information($"Upgraded stored password hash for user id {userId} from SHA-256 to PBKDF2.");
        }
        catch (Exception ex)
        {
            // The operator is already authenticated; a failed upgrade is a
            // maintenance concern, not a sign-in failure.
            AppLogger.Warning($"Could not upgrade the password hash for user id {userId}.", ex);
        }
    }

    /// <summary>
    /// Changes a password, verifying the current one first. Provided so the
    /// PBKDF2 path is reachable without waiting for a legacy sign-in.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> ChangePasswordAsync(
        string username,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            return (false, "The new password must be at least 8 characters long.");

        var (result, user, error) = await AuthenticateAsync(username, currentPassword, cancellationToken)
            .ConfigureAwait(false);

        if (result != AuthenticationResult.Success || user is null)
            return (false, error ?? "The current password is incorrect.");

        try
        {
            await using var connection = await Db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Db.CreateCommand(
                "UPDATE Users SET PasswordHash = @hash WHERE Id = @id", connection);

            command.Parameters.Add("@hash", System.Data.SqlDbType.NVarChar, 255).Value = PasswordHasher.Hash(newPassword);
            command.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = user.Id;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            AppLogger.Information($"Password changed for '{user.Username}'.");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, Db.Translate(ex, "change the password").Message);
        }
    }
}
