# PathingService - the pathing and authoring API

The central service from `docs/service-architecture.md`. It plans routes over the baked navmesh
for many callers at once, and it is where navigation data is authored: recorded paths and road
and danger-zone costs, with bakes, world data and a Forever route library to follow in Phase 2.
Bots keep planning in their own process; this service owns the data and plans for the UI.

Plain `net10.0`, so it runs on Linux and macOS. It references `Contracts`, `PPather`,
`DataConfig` and `SharedLib` only. **Never add a reference to `Core`, `Game`, `WinAPI` or
`Frontend`** - that would drag the bot (and Windows) into a service.

## Run

```bash
PathingService/run.sh              # som (Classic) on http://localhost:5010, Swagger UI at /swagger
PathingService/run.sh --exp=forever
```

`run.sh` changes into this folder first, because `data_config.json` is read from the working
directory. The port is `Contracts.Pathing.PathingApi.DefaultPort` (5010), which stays apart from
PathingAPI's 5001 while both exist. `--urls` overrides it, and `--LogFile=` (empty) turns the
log file off.

## Layout

```
Program.cs                         host: validation on build, default URL, logging, the API
Hosting/PathingServiceRegistration AddPathingService / UsePathingService - the composition root
Hosting/PathingLogging.cs          Serilog to console + daily file (LogFile)
Hosting/StoreResult.cs             store outcomes and their one mapping to HTTP
Hosting/AtomicFile.cs              write-then-rename, so readers never see half a file
Routes/RouteHandler.cs             RouteRequest -> RoutePlanner -> RouteResponse (the only mapping)
Routes/RouteEndpoints.cs           POST /api/v1/routes/find
Paths/PathName.cs                  the one check every path name passes
Paths/RecordedPathStore.cs         Json/path library, formats exactly as before
Paths/PathEndpoints.cs             /api/v1/paths, /api/v1/area
CostZones/CostZoneStore.cs         Json/road/<era>: roads and dangerzone/ per uiMap
CostZones/CostZoneMapping.cs       wire shapes <-> PPather's RoadData / DangerZoneData
CostZones/CostZoneEndpoints.cs     /api/v1/costzones
Status/StatusEndpoints.cs          /api/v1/capabilities, /api/v1/health
```

The planning itself is `PPather/Planning/RoutePlanner` - one implementation, meant to be shared
with the bot's local mode in Phase 4.

## Things that will bite you

**One client per instance.** `--exp` picks the client (default `som`). A request naming
another client answers `BadRequest`. Serving two clients from one process needs `ContinentDB`
to stop being static first: it is filled by whoever loads first.

**A route that does not exist is an answer, not an error.** `Found`, `NoPath` and `NotBaked`
are HTTP 200. Only `BadRequest` (400) and `Unavailable` (503, a continent busy past the
planner's wait) are errors, and every one of them carries a `RouteResponse`. Never answer 429:
that is how the old V1 client turned "busy" into "no path".

**Each request gets fresh pathfinder memory.** `NavmeshPathfinder` remembers the last floor it
resolved, its corridor band and recent stalls. That is right for one walker and wrong for a
shared service, so the planner passes a new `NavmeshQueryMemory` per request (see the parity
and concurrency tests in `ServiceTests/Planning`). The memory is worth having: it keeps
Forever's height-less walker on its floor and makes repeat requests cheap (see
`docs/known-issues.md`, "Design notes"). When bots plan here, the preferred way is for each
request to carry the bot's last floor, so the service stays stateless. A cache keyed by
client id comes only if repeat requests turn out too slow.

**The planner never bakes.** An endpoint on a tile with no `.dnm` answers `NotBaked`, naming
the point. A continent whose cache directory appears later (downloaded or baked) gets a lane
on its next query, without a restart.

**Formats on disk are the old ones, byte for byte.**
- Recorded paths are written as `PathController.SavePath` wrote them (System.Text.Json with
  an upper-case `Vector3Converter`) and read as the bot reads them (Newtonsoft,
  case-insensitive).
- Cost zones are written as `UIMapJsonFiles` wrote them (Newtonsoft, indented, the engine's
  models).
- `ServiceTests/Stores` pins all three. Change a format and BlazorServer, PathingAPI and the
  bot stop agreeing with this service.

**Names are checked once, in `PathName`.** Relative, `/` between folders, plain segments, ends
in `.json`, and never under `_forever/` (Forever route files, a different format). The old
controller joined any name onto the folder. Continent names in the cost-zone routes are letters
and digits only, for the same reason.

**A cost-zone save reloads the planner's zones at once.** The `CostZoneWatcher` would reload
too, half a second later; the second reload is harmless. A save is validated with
`CostZoneLoader.Problem`, the same rules the loader applies, so a bad save is refused (400)
instead of silently dropping zones at the next load.

**Tests run it in memory.** `ServiceTests/Api/PathingServiceFactory` swaps the `DataConfig`
for a temporary root and turns the log file off. Tests that need tiles skip when the precata
navmesh is not downloaded.
