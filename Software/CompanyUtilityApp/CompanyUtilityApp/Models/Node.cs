namespace CompanyUtilityApp.Models;

/// <summary>
/// An ESP32 controller installed at a panel position. Mirrors one row of the
/// <c>Nodes</c> table.
///
/// The calibration constants are per-device physical characteristics, not
/// preferences: they translate raw ADC counts into amps and volts, so a wrong
/// value silently corrupts every reading the node publishes. The defaults below
/// match the database defaults and the firmware's own fallbacks.
/// </summary>
public sealed class Node
{
    /// <summary>Surrogate key. Zero for a node that has not been persisted yet.</summary>
    public int Id { get; set; }

    /// <summary>Site-wide node identifier, unique across all routes.</summary>
    public int NodeNumber { get; set; }

    /// <summary>Foreign key to <see cref="Area.PanelSerialNumber"/>.</summary>
    public int PanelSerialNumber { get; set; }

    /// <summary>Static IPv4 address assigned to the controller.</summary>
    public required string LocalIPAddress { get; set; }

    // ---- Current-sensor calibration -----------------------------------------

    /// <summary>Zero-current bias voltage of sensor 1, in volts.</summary>
    public double ZeroVoltCs1 { get; set; } = 2.40;

    /// <summary>Zero-current bias voltage of sensor 2, in volts.</summary>
    public double ZeroVoltCs2 { get; set; } = 2.40;

    /// <summary>Volts per amp for sensor 1.</summary>
    public double SensitivityCs1 { get; set; } = 0.105;

    /// <summary>Volts per amp for sensor 2.</summary>
    public double SensitivityCs2 { get; set; } = 0.105;

    /// <summary>Current in amps below which channel 1 is considered off.</summary>
    public double ThresholdCs1 { get; set; } = 0.100;

    /// <summary>Current in amps below which channel 2 is considered off.</summary>
    public double ThresholdCs2 { get; set; } = 0.120;

    // ---- Power calibration ---------------------------------------------------

    /// <summary>Additive correction applied to the measured battery voltage.</summary>
    public double BatteryCalibration { get; set; }

    /// <summary>Correction for the voltage dip seen while the relays energise.</summary>
    public double BatterySagCompensation { get; set; }

    /// <summary>Raw ADC count below which the PSU is treated as failed.</summary>
    public int PsuThreshold { get; set; } = 1800;

    /// <summary>
    /// True once a configuration payload has been accepted by the controller.
    /// Uncalibrated nodes are the ones offered on the Panel Settings screen.
    /// </summary>
    public bool IsCalibrated { get; set; }
}
