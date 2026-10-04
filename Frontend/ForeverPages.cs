using BlazorBootstrap;

using Microsoft.AspNetCore.Components.Routing;

namespace Frontend;

/// <summary>
/// The pages the web UI offers on WoW: Forever. The others read Classic-only
/// readers or services that are not registered there, so the layout neither
/// lists them nor renders them.
/// </summary>
public static class ForeverPages
{
    public static List<NavItem> NavItems() =>
    [
        new() { Id = "0", Href = "/", Text = "Forever status", IconName = IconName.Activity, Match = NavLinkMatch.All },
        new() { Id = "4", Href = "/ForeverRuns", Text = "Forever runs", IconName = IconName.PlayCircle },
        new() { Id = "1", Href = "/Screenshot", Text = "Screenshot", IconName = IconName.Camera },
        new() { Id = "2", Href = "/Log", Text = "Log", IconName = IconName.Terminal },
        new() { Id = "3", Href = "/RestartServer", Text = "Restart", IconName = IconName.ArrowClockwise }
    ];

    /// <summary>Whether the page at <paramref name="baseRelativeUri"/> is one of <see cref="NavItems"/>.</summary>
    public static bool Includes(string baseRelativeUri)
    {
        string path = "/" + baseRelativeUri.Split('?', '#')[0];
        return NavItems().Exists(item => string.Equals(item.Href, path, StringComparison.OrdinalIgnoreCase));
    }
}
