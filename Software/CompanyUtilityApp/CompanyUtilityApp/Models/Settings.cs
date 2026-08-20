using System.ComponentModel.DataAnnotations;

namespace CompanyUtilityApp.Models;

/// <summary>
/// Technician and notification preferences, persisted to <c>settings.json</c> in
/// the per-user application data folder.
/// </summary>
public sealed class TechnicianSettings
{
    public string TechnicianName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool MaintenanceNotifications { get; set; }
    public string NotificationFrequency { get; set; } = "Monthly";

    public static readonly string[] FrequencyOptions =
    {
        "Daily", "Weekly", "Fortnightly", "Monthly", "Quarterly",
    };

    /// <summary>
    /// Validates the fields that are actually used to contact someone. Blank is
    /// accepted throughout — this screen is informational and a half-filled form
    /// should not block a technician mid-commissioning — but a value that IS
    /// present has to be usable.
    /// </summary>
    public bool TryValidate(out string error)
    {
        if (!string.IsNullOrWhiteSpace(Email) && !new EmailAddressAttribute().IsValid(Email))
        {
            error = "Email address is not in a recognisable format.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Phone) && Phone.Any(c => !char.IsDigit(c) && !"+-() ".Contains(c)))
        {
            error = "Phone number may only contain digits and the characters + - ( ) and spaces.";
            return false;
        }

        if (MaintenanceNotifications && string.IsNullOrWhiteSpace(Email))
        {
            error = "An email address is required when maintenance notifications are enabled.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Ethernet and Wi-Fi parameters intended for the controllers, persisted
/// alongside the technician settings.
/// </summary>
public sealed class NetworkSettings
{
    public string EthernetIPAddress { get; set; } = string.Empty;
    public string EthernetSubnetMask { get; set; } = string.Empty;
    public string EthernetGateway { get; set; } = string.Empty;

    public bool WifiEnabled { get; set; }
    public string WifiSsid { get; set; } = string.Empty;
    public string WifiPassword { get; set; } = string.Empty;
    public string WifiIPAddress { get; set; } = string.Empty;
    public string WifiSubnetMask { get; set; } = string.Empty;
    public string WifiGateway { get; set; } = string.Empty;

    /// <summary>
    /// Validates every populated address field. Misconfigured network settings
    /// are one of the more expensive faults on this system — a controller that
    /// comes up on the wrong subnet has to be recovered over USB on site — so
    /// they are checked before they can be saved.
    /// </summary>
    public bool TryValidate(out string error)
    {
        foreach (var (label, value) in new[]
                 {
                     ("Ethernet IP address", EthernetIPAddress),
                     ("Ethernet subnet mask", EthernetSubnetMask),
                     ("Ethernet gateway", EthernetGateway),
                 })
        {
            if (!IsBlankOrValidIPv4(value))
            {
                error = $"{label} is not a valid IPv4 address.";
                return false;
            }
        }

        if (!WifiEnabled)
        {
            error = string.Empty;
            return true;
        }

        foreach (var (label, value) in new[]
                 {
                     ("Wi-Fi IP address", WifiIPAddress),
                     ("Wi-Fi subnet mask", WifiSubnetMask),
                     ("Wi-Fi gateway", WifiGateway),
                 })
        {
            if (!IsBlankOrValidIPv4(value))
            {
                error = $"{label} is not a valid IPv4 address.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(WifiSsid))
        {
            error = "An SSID is required when Wi-Fi is enabled.";
            return false;
        }

        // WPA2 personal minimum. Rejecting a shorter key here saves a failed
        // association that would otherwise only show up on the device console.
        if (WifiPassword.Length is > 0 and < 8)
        {
            error = "Wi-Fi password must be at least 8 characters (WPA2 minimum).";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsBlankOrValidIPv4(string value) =>
        string.IsNullOrWhiteSpace(value) || NetworkValidation.IsValidIPv4(value);
}

/// <summary>Shared IPv4 parsing used by both the settings screens and node entry.</summary>
public static class NetworkValidation
{
    /// <summary>
    /// True for a dotted-quad IPv4 literal.
    ///
    /// <c>IPAddress.TryParse</c> alone is not enough: it accepts "10" and
    /// "192.168.1" by padding them out, which would let a technician save a
    /// half-typed address that then fails to match anything on site.
    /// </summary>
    public static bool IsValidIPv4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Trim().Split('.');
        if (parts.Length != 4)
            return false;

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3)
                return false;

            if (!part.All(char.IsAsciiDigit))
                return false;

            if (!int.TryParse(part, out var octet) || octet is < 0 or > 255)
                return false;

            // Reject leading zeros: "192.168.01.1" is ambiguous across parsers.
            if (part.Length > 1 && part[0] == '0')
                return false;
        }

        return true;
    }
}
