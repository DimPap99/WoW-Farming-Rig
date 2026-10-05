using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

using System.IO;

namespace PathingService.World;

/// <summary>
/// Serves the world data files the map reads (<see cref="WorldData"/>) from this instance's data
/// folder, the same folders BlazorServer serves from the bot's. Which era or client folder backs
/// each route is <see cref="DataConfig"/>'s to say.
/// </summary>
public static class WorldDataFiles
{
    public static WebApplication UseWorldData(this WebApplication app)
    {
        DataConfig data = app.Services.GetRequiredService<DataConfig>();

        // A continent whose art an earlier era supplies is served from that era's folder, and
        // its route goes first: static files run in order, so the specific route must win.
        foreach ((string continent, string folder) in data.SharedTileFolders())
        {
            if (Directory.Exists(folder))
            {
                Serve(app, folder, $"{WorldData.Tiles}/{continent}");
            }
        }

        Serve(app, data.Leaflet, WorldData.Tiles);
        Serve(app, data.ExpDbc, WorldData.Dbc);
        Serve(app, data.ExpArea, WorldData.Area);
        Serve(app, data.NpcSpawnLocations, WorldData.NpcSpawnLocations);
        Serve(app, data.MailboxLocations, WorldData.MailboxLocations);
        Serve(app, data.Path, WorldData.RecordedPaths);
        Serve(app, data.Locations, WorldData.Locations);

        return app;
    }

    // PhysicalFileProvider throws on a missing root, and these folders depend on the client: one
    // generated later (tiles downloaded after start) must still be served, so it is created now.
    private static void Serve(WebApplication app, string folder, string requestPath)
    {
        string root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(root),
            RequestPath = requestPath
        });
    }
}
