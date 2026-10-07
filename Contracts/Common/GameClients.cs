using System;

namespace Contracts;

/// <summary>The client data names (<c>--exp</c>) that services, bots and pages tell clients apart by.</summary>
public static class GameClients
{
    /// <summary>WoW: Forever, whose bot reads the screen and walks route files.</summary>
    public const string Forever = "forever";

    public static bool IsForever(string? client)
    {
        return string.Equals(client, Forever, StringComparison.OrdinalIgnoreCase);
    }
}
