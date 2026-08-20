using System.Text.Json.Serialization;

namespace CompanyUtilityApp.Models;

/// <summary>
/// The JSON contract sent to the ESP32 controller, over USB serial or HTTP.
///
/// Two properties of this payload are load-bearing and must not be changed
/// casually, because the firmware depends on them:
///
/// 1. <c>NodeNumber</c> is deliberately absent. The controller derives its own
///    node identity from the holding register, and sending a second source of
///    truth would let the two disagree.
///
/// 2. The calibration fields are omitted entirely unless an administrator has
///    supplied them. The firmware only overwrites a stored calibration constant
///    when the corresponding key is present, so an absent key preserves whatever
///    was calibrated on the bench. Sending nulls or zeros would wipe it.
///
/// The controller restarts unconditionally after accepting a payload.
/// </summary>
public sealed class DeviceConfigurationPayload
{
    public int Route { get; init; }

    public int PanelLocation { get; init; }

    public int PanelSerialNumber { get; init; }

    public int HoldingRegister { get; init; }

    public string? Description { get; init; }

    public required string LocalIP { get; init; }

    // ---- Optional calibration block -----------------------------------------
    // JsonIgnore with WhenWritingNull keeps absent values out of the payload
    // instead of emitting "Sensitivity_CS1": null.

    [JsonPropertyName("Sensitivity_CS1")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SensitivityCs1 { get; init; }

    [JsonPropertyName("Sensitivity_CS2")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SensitivityCs2 { get; init; }

    [JsonPropertyName("BatteryCalibration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BatteryCalibration { get; init; }

    /// <summary>True when this payload carries a calibration override.</summary>
    [JsonIgnore]
    public bool IncludesCalibration =>
        SensitivityCs1.HasValue || SensitivityCs2.HasValue || BatteryCalibration.HasValue;
}

/// <summary>Payload that clears stored preferences and restarts the controller.</summary>
public sealed class DeviceResetPayload
{
    /// <summary>
    /// Always true. Declared as an init property rather than a computed one so it
    /// is serialised as instance data, which is what the firmware reads.
    /// </summary>
    public bool ResetConfig { get; init; } = true;
}

/// <summary>Calibration values entered by an administrator before a deployment.</summary>
public sealed class CalibrationOverride
{
    public double SensitivityCs1 { get; init; }
    public double SensitivityCs2 { get; init; }
    public double BatteryCalibration { get; init; }

    /// <summary>
    /// Sanity bounds. These are not arbitrary: the ACS712-class sensors used on
    /// these panels have sensitivities in the tens-to-hundreds of millivolts per
    /// amp, so a value outside this band means a typo or a wrong unit, and
    /// pushing it would silently corrupt every subsequent current reading.
    /// </summary>
    public const double MinSensitivity = 0.001;
    public const double MaxSensitivity = 1.0;
    public const double MinBatteryCalibration = -5.0;
    public const double MaxBatteryCalibration = 5.0;

    public bool TryValidate(out string error)
    {
        if (SensitivityCs1 is < MinSensitivity or > MaxSensitivity)
        {
            error = $"Sensitivity CS1 must be between {MinSensitivity} and {MaxSensitivity} V/A.";
            return false;
        }

        if (SensitivityCs2 is < MinSensitivity or > MaxSensitivity)
        {
            error = $"Sensitivity CS2 must be between {MinSensitivity} and {MaxSensitivity} V/A.";
            return false;
        }

        if (BatteryCalibration is < MinBatteryCalibration or > MaxBatteryCalibration)
        {
            error = $"Battery calibration must be between {MinBatteryCalibration} and {MaxBatteryCalibration} V.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
