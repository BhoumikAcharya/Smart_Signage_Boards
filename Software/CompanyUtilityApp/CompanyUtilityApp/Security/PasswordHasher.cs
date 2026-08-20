using System.Security.Cryptography;
using System.Text;

namespace CompanyUtilityApp.Security;

/// <summary>
/// Password hashing and verification.
///
/// The original implementation stored a bare, unsalted SHA-256 of the password.
/// That is fast to compute, which is exactly the problem: an attacker with a copy
/// of the Users table can test billions of candidates per second on commodity
/// hardware, and because there is no salt, identical passwords produce identical
/// hashes and a single precomputed table cracks every account at once.
///
/// New passwords are stored as PBKDF2-HMAC-SHA256 with a per-password random salt
/// and a deliberately high iteration count.
///
/// Existing rows are NOT orphaned. <see cref="Verify"/> recognises the legacy
/// 64-character hex format, and <see cref="NeedsUpgrade"/> lets the caller
/// transparently re-hash a legacy password to PBKDF2 on the next successful
/// sign-in. No user has to be told to reset anything.
/// </summary>
public static class PasswordHasher
{
    /// <summary>
    /// Iteration count for new hashes. OWASP's 2023 guidance for
    /// PBKDF2-HMAC-SHA256 is 600,000; that costs a few hundred milliseconds,
    /// which is imperceptible on a sign-in screen and expensive in bulk.
    /// </summary>
    private const int Iterations = 600_000;

    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    /// <summary>Marker that distinguishes the new format from the legacy hex hash.</summary>
    private const string Prefix = "PBKDF2$";

    private const int LegacyHashLength = 64; // SHA-256 as lowercase hex

    /// <summary>Produces a storable hash string for a new or changed password.</summary>
    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);

        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verifies a password against either storage format.
    /// Returns false rather than throwing on a malformed stored value, so a
    /// corrupted row denies access instead of crashing the sign-in screen.
    /// </summary>
    public static bool Verify(string password, string? storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        return storedHash.StartsWith(Prefix, StringComparison.Ordinal)
            ? VerifyPbkdf2(password, storedHash)
            : VerifyLegacySha256(password, storedHash);
    }

    /// <summary>
    /// True when the stored value is in the legacy format and should be re-hashed
    /// after a successful sign-in.
    /// </summary>
    public static bool NeedsUpgrade(string? storedHash) =>
        !string.IsNullOrWhiteSpace(storedHash)
        && !storedHash.StartsWith(Prefix, StringComparison.Ordinal);

    private static bool VerifyPbkdf2(string password, string storedHash)
    {
        // Format: PBKDF2$<iterations>$<base64 salt>$<base64 hash>
        var parts = storedHash.Split('$');
        if (parts.Length != 4)
            return false;

        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool VerifyLegacySha256(string password, string storedHash)
    {
        var trimmed = storedHash.Trim();
        if (trimmed.Length != LegacyHashLength)
            return false;

        var computed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

        // Fixed-time comparison, and case-insensitive because the original code
        // wrote lowercase hex while Convert.ToHexString emits uppercase.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(trimmed.ToUpperInvariant()));
    }
}
