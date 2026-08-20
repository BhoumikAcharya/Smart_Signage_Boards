namespace CompanyUtilityApp.Models;

/// <summary>
/// An operator of the calibration tool. Mirrors the <c>Users</c> table minus the
/// password hash, which is never carried outside the authentication repository.
/// </summary>
public sealed class User
{
    public int Id { get; init; }

    public required string Username { get; init; }

    public required string FullName { get; init; }

    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Administrators may push calibration constants. Membership is decided by
    /// username because the schema carries no role column; introducing one is
    /// the correct long-term fix and is noted in the build documentation.
    /// </summary>
    public bool IsAdministrator =>
        string.Equals(Username, "admin", StringComparison.OrdinalIgnoreCase);
}
