namespace ControlCenter.Components.Shared;

/// <summary>
/// The names the pages draw under in the 3D view and the map. A name is the drawing's id, so a
/// later drawing with the same name replaces the earlier one; planned routes are the service's
/// own <c>path</c>.
/// </summary>
public static class WatchNames
{
    /// <summary>A straight line between two places, drawn without planning.</summary>
    public const string Line = "route";

    /// <summary>A recorded path, drawn as recorded.</summary>
    public const string Recorded = "recorded";

    /// <summary>The start a page picked, as a marker.</summary>
    public const string Start = "startSphere";
}
