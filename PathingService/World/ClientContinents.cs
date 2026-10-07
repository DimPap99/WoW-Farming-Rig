using PPather;

using SharedLib;
using SharedLib.Data;

using System;
using System.Collections.Generic;

namespace PathingService.World;

/// <summary>
/// The one rule for a continent a request names, wherever it names one (bakes, the navmesh routes,
/// cost zones): one of the client's world continents, the ones it bakes and plans on, in any case,
/// and from then on in the client's own case. Continent names become folder names, so nothing else
/// is taken: the bot reads <c>Azeroth\</c>, and on Linux the service would not read <c>azeroth/</c>.
/// </summary>
public sealed class ClientContinents
{
    private readonly string client;

    public ClientContinents(DataConfig data, WorldMapAreaDB maps)
    {
        client = data.Exp;
        ContinentDB.Init(maps.Values);
        Names = [.. PPatherService.KnownWorldContinents()];
    }

    /// <summary>The client's world continents, in its own case.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The client's continent <paramref name="name"/> names in any case, in the client's own case; null for none.</summary>
    public string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (string continent in Names)
        {
            if (continent.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return continent;
            }
        }

        return null;
    }

    /// <summary>Why a request naming <paramref name="name"/> is refused, when <see cref="Resolve"/> finds no continent.</summary>
    public string Refusal(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? "a continent name is required"
            : $"'{name}' is not a continent of {client}; use one of {string.Join(", ", Names)}";
    }
}
