# PathingService - the pathing and authoring API

The central service from `docs/service-architecture.md`. It plans routes over the baked navmesh
for many callers at once, and it is where navigation data is authored and administered:
recorded paths, road and danger-zone costs, bakes and area grids. It serves the map's world
data and streams what it plans to the watch hub. Bots keep planning in their own process; this
service owns the data and plans for the UI (the Control Center).

Plain `net10.0`, so it runs on Linux and macOS. It references `Contracts`, `PPather`,
`DataConfig`, `SharedLib`, `ServiceClients` (for the shared key rules) and `Utilities/ForeverCasc` only. **Never add a reference to `Core`,
`Game`, `WinAPI` or `Frontend`** - that would drag the bot (and Windows) into a service.

## Run

```bash
PathingService/run.sh              # som (Classic) on http://localhost:5010, Swagger UI at /swagger
PathingService/run.sh --exp=forever
PathingService/run.sh --bake=Azeroth          # also start a bake (or --bake=all, --bake-area=...)
```

`run.sh` changes into this folder first, because `data_config.json` is read from the working
directory. The port is `Contracts.Pathing.PathingApi.DefaultPort` (5010). `--urls` overrides it,
and `--LogFile=` (empty) turns the log file off. The Control Center (`ControlCenter/run.sh`, 5020)
is its UI.

## Layout

```
Program.cs                         host: validation on build, default URL, logging, the API
Hosting/PathingServiceRegistration AddPathingService / UsePathingService - the composition root
Hosting/PathingLogging.cs          Serilog to console + daily file (LogFile)
Hosting/StoreResult.cs             store outcomes (incl. Conflict) and their one mapping to HTTP
Hosting/AtomicFile.cs              write-then-rename, so readers never see half a file
Hosting/ServiceKeyGuard.cs         the key (ServiceClients' ServiceKeyPolicy): other machines need ApiKey
Routes/RouteHandler.cs             RouteRequest -> RoutePlanner -> RouteResponse (the only mapping)
Routes/RouteEndpoints.cs           POST /api/v1/routes/find
Paths/PathName.cs                  the one check every path name passes
Paths/RecordedPathStore.cs         Json/path library, formats exactly as before
Paths/PathEndpoints.cs             /api/v1/paths, /api/v1/area
Forever/ForeverRouteLibrary.cs     Json/path/_forever: Forever route files, checked as a run loads them
Forever/ForeverRouteEndpoints.cs   /api/v1/forever/routes
CostZones/CostZoneStore.cs         Json/road/<era>: roads and dangerzone/ per uiMap
CostZones/CostZoneMapping.cs       wire shapes <-> PPather's RoadData / DangerZoneData
CostZones/CostZoneEndpoints.cs     /api/v1/costzones
World/WorldDataFiles.cs            the map's static data routes (Contracts' WorldData)
World/WorldCatalog.cs              zones with bounds, continents, NPC types, NPCs by zone
World/WorldEndpoints.cs            /api/v1/world, /api/v1/world/npcs
Catalog/LocationCatalog.cs         curated spots + Json/locations/teleport_locations.txt
Catalog/TestRoutes.cs              known-trouble routes as RouteRequests
Catalog/CatalogEndpoints.cs        /api/v1/catalog/locations, /testroutes
Navmesh/BakeAdmin.cs               the admin-only engine: bake, clear, area grids, terrain
Navmesh/ForeverGameFiles.cs        ForeverCasc:* -> Forever's CASC geometry
Navmesh/NavmeshEndpoints.cs        /api/v1/navmesh, /api/v1/bake, /api/v1/areagrids
Navmesh/StartupBakes.cs            --bake= / --bake-area=
Watch/WatchHub.cs                  /watchHub (MessagePack): viewers, Replay, Terrain
Watch/WatchBroadcaster.cs          planner events and drawings -> the hub, one send loop
Watch/NavmeshTileMesh.cs           a Detour tile as triangles
Watch/TerrainMesh.cs               the game geometry under a navmesh tile, by kind
Watch/WatchDrawing.cs              /api/v1/watch/*: checks, map -> world with navmesh heights
Watch/WatchEndpoints.cs            the hub and the draw endpoints
Status/StatusEndpoints.cs          /api/v1/capabilities, /api/v1/health
Status/CoverageCache.cs            baked tile counts, at most every 15 s
```

The planning itself is `PPather/Planning/RoutePlanner` - one implementation, meant to be shared
with the bot's local mode in Phase 4.

## Things that will bite you

**One client per instance.** `--exp` picks the client (default `som`). A request naming
another client answers `BadRequest`. Serving two clients from one process needs `ContinentDB`
to stop being static first: it is filled by whoever loads first.

**Other machines need the key.** A request from this machine (loopback, or no remote address, as the
in-memory test server has) is always served; one from another machine only with `ApiKey` in the
`X-Api-Key` header, and not at all while no key is configured. Bound beyond localhost without a key
(`--urls=http://0.0.0.0:5010`), the service stops right after it starts. The check runs before the
API, the hub and the world data. The Control Center and BlazorServer forward the browser's calls with
the key, so a browser never holds it.

**A route that does not exist is an answer, not an error.** `Found`, `Partial`, `NoPath` and
`NotBaked` are HTTP 200. Only `BadRequest` (400) and `Unavailable` (503, a continent busy past the
planner's wait) are errors, and every one of them carries a `RouteResponse`. Never answer 429:
that is how the old V1 client turned "busy" into "no path".

**A route that stops short says so.** The band search (`RouteSearch.Band`, the default and the
bot's own) loads tiles only within about 270 yards of the straight line, 530 when it widens; a
route that must stray further ends at a dead end. That used to come back as `Found`. Now a route
ending more than `RoutePlanner.ArrivalToleranceYd` (5 yd) from the resolved destination is
`Partial`, with its points and a detail naming where it stops and how far short.

**`RouteSearch.Continent` searches the whole continent.** The way across is found on the
continent's `NavmeshTileGraph` (a node per connected part of each tile), cut into legs that stay
within 100 yards of it, and each leg is an ordinary band search. The graph is built from the tile
files once per bake (about 10 s for Kalimdor) and kept beside them as `tilegraph.v1.bin`, stamped
with the tiles' count, size and newest write; closing a lane drops it from memory, and a changed
stamp rebuilds it. After that a continent route takes well under a second. It plans what the
navmesh connects: swimming costs 1.3 times walking, so Durotar to Tanaris runs down the east coast
on the sea floor.

**Each request gets fresh pathfinder memory.** `NavmeshPathfinder` remembers the last floor it
resolved, its corridor band and recent stalls. That is right for one walker and wrong for a
shared service, so the planner passes a new `NavmeshQueryMemory` per request (see the parity
and concurrency tests in `ServiceTests/Planning`). The memory is worth having: it keeps
Forever's height-less walker on its floor and makes repeat requests cheap (see
`docs/known-issues.md`, "Design notes"). When bots plan here, the preferred way is for each
request to carry the bot's last floor, so the service stays stateless. A cache keyed by
client id comes only if repeat requests turn out too slow.

**The planner never bakes; `BakeAdmin` does, on its own engine.** An endpoint on a tile with no
`.dnm` answers `NotBaked`, naming the point. Bakes, area grids and terrain run on one
`PPatherService` that never answers a route, one job at a time (they share its continent and
geometry); a second job is refused with 409 and what is running. Without game files
(`Json/MPQ`, or `ForeverCasc:Install` for Forever) they are refused up front.

**Whatever changes tiles on disk closes lanes.** A lane remembers a tile that was missing as
empty for its whole life (known issue 12). A bake closes the lanes of what it baked when it
ends, a cache clear the continent's lane after deleting; the next route reopens a lane on
what is on disk. A query waiting on a closed lane moves to the fresh one.

**The watch hub sends points as `[x, y, z]`.** Both views read a point that way. MessagePack
writes a `System.Numerics.Vector3` as a map of its fields, so send `float[]`s
(`WatchBroadcaster.Wire`), never a `Vector3`. And no LZ4: the browser's MessagePack reader does
not know MessagePack-CSharp's compression extension.

**Opening a view resets nothing.** A late viewer calls `Replay` and gets the lanes' resident
tiles and the last five routes, sent to it alone. PathingAPI reset its single engine for every
new 3D page; a service planning for others must not.

**Terrain is extracted, sent and dropped.** The Terrain toggle sends up to 25 tile indices;
`BakeAdmin.TryExtractTerrain` loads those ADTs, sends each tile's mesh to that viewer only and
resets the engine, so a continent's triangle cache (gigabytes) never stays resident.

**Formats on disk are the old ones, byte for byte.**
- Recorded paths are written as `PathController.SavePath` wrote them (System.Text.Json with
  an upper-case `Vector3Converter`) and read as the bot reads them (Newtonsoft,
  case-insensitive).
- Cost zones are written as `UIMapJsonFiles` wrote them (Newtonsoft, indented, the engine's
  models).
- `ServiceTests/Stores` pins all three. Change a format and BlazorServer and the bot stop
  agreeing with this service.

**Names are checked once, in `PathName`.** Relative, `/` between folders, plain segments, ends
in `.json`, and never under `_forever/` (Forever route files, a different format). The old
controller joined any name onto the folder. Continent names in the cost-zone and navmesh routes
are the client's own continents only, for the same reason.

**Forever route files are text, checked against Forever's map data.** The library reads and writes
them as text, so the `//` comments that document a route survive a save, and it checks each with
`ForeverRouteFile.TryParse` (SharedLib), the loader a run uses, against `dbc/forever` whatever
`--exp` the instance serves (`WorldMapAreaDB` is per instance; `ContinentDB` is not involved). A
route a run would refuse is never written. Names are one plain file name: no subfolders.

**A cost-zone save reloads the planner's zones at once.** The `CostZoneWatcher` would reload
too, half a second later; the second reload is harmless. A save is validated with
`CostZoneLoader.Problem`, the same rules the loader applies, so a bad save is refused (400)
instead of silently dropping zones at the next load.

**The world data routes create their folders.** `PhysicalFileProvider` throws on a missing
root, and which folder backs a route depends on the client, so `UseWorldData` creates it. The
shared-art tile routes (`DataConfig.SharedTileFolders`) go first: static files run in order.

**Tests run it in memory.** `ServiceTests/Api/PathingServiceFactory` swaps the `DataConfig`
for a temporary root and turns the log file off. Tests that need tiles skip when the precata
navmesh is not downloaded; the hub tests connect a MessagePack client over the test server.
