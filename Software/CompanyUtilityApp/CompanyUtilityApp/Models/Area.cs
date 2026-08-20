namespace CompanyUtilityApp.Models;

/// <summary>
/// A physical panel position on a route. Mirrors one row of the <c>Areas</c>
/// table, which is pre-populated with every route/panel combination the site
/// supports (4 routes x 100 panels by default) and is never inserted into or
/// deleted from at runtime — only updated.
/// </summary>
public sealed class Area
{
    public int Id { get; init; }

    public int Route { get; init; }

    public int PanelLocation { get; init; }

    /// <summary>
    /// Modbus holding register that carries this panel's state.
    /// Derived at seed time as <c>40000 + (Route-1)*100 + PanelLocation</c> and
    /// treated as immutable thereafter.
    /// </summary>
    public int HoldingRegister { get; init; }

    /// <summary>
    /// Serial number stencilled on the physical panel. Unique across the site
    /// and used as the join key to <see cref="Node"/>.
    /// </summary>
    public int PanelSerialNumber { get; set; }

    /// <summary>Free-text location note, e.g. "Platform 3, Column B".</summary>
    public string? Description { get; set; }

    /// <summary>Label used in dropdowns and confirmation prompts.</summary>
    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(Description)
            ? $"Panel {PanelLocation} (PSN {PanelSerialNumber})"
            : $"Panel {PanelLocation} (PSN {PanelSerialNumber}) - {Description}";
}
