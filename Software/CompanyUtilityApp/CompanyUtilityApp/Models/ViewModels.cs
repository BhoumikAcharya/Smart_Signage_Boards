namespace CompanyUtilityApp.Models;

/// <summary>
/// Flattened Areas-joined-Nodes row for the node management grid. Read-only
/// projection: the grid never writes through it.
/// </summary>
public sealed class NodeListItem
{
    public int Route { get; init; }
    public int PanelLocation { get; init; }
    public int PanelSerialNumber { get; init; }
    public int NodeNumber { get; init; }
    public required string LocalIPAddress { get; init; }
    public string? Description { get; init; }
    public bool IsCalibrated { get; init; }
}

/// <summary>
/// An uncalibrated node offered for deployment on the Panel Settings screen,
/// carrying the identity fields the controller needs.
/// </summary>
public sealed class PanelDeploymentTarget
{
    public int Route { get; init; }
    public int PanelLocation { get; init; }
    public int PanelSerialNumber { get; init; }
    public int HoldingRegister { get; init; }
    public int NodeNumber { get; init; }
    public required string IPAddress { get; init; }
    public string? Description { get; init; }

    /// <summary>Label shown in the panel-selection dropdown.</summary>
    public override string ToString() =>
        $"Panel {PanelLocation}  ·  PSN {PanelSerialNumber}  ·  {IPAddress}";
}
