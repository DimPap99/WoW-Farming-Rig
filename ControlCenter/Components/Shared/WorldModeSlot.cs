using ControlCenter.World;

using System;

namespace ControlCenter.Components.Shared;

/// <summary>
/// One mode's hold on the World page's world view: the page cascades a slot to each mode it hosts, the
/// mode's <see cref="WorldModeView"/> binds its scene, tools and events here, and the page shows the bound
/// view's while the mode is the one shown.
/// </summary>
public sealed class WorldModeSlot(string mode, Func<WorldView?> view, Action<WorldModeSlot> changed)
{
    public string Mode { get; } = mode;

    /// <summary>What the mode shows and hears now; null before its first render and after it went.</summary>
    public WorldModeView? Bound { get; private set; }

    /// <summary>The page's one world view.</summary>
    public WorldView? View => view();

    /// <summary>The scene of this mode the page last showed.</summary>
    public WorldScene? LastShown { get; set; }

    public void Bind(WorldModeView bound)
    {
        Bound = bound;
        changed(this);
    }

    public void Unbind(WorldModeView bound)
    {
        if (ReferenceEquals(Bound, bound))
        {
            Bound = null;
        }
    }
}
