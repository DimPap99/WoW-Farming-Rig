# Known issues and findings

Things spotted while working on the code: where each lives, what it does, and when it goes
away. Phases refer to [service architecture](service-architecture.md). Add to this list
when you find something you are not fixing on the spot; move an item to "Fixed" with the
commit that fixed it.

## Open

1. **`startIndoors` never reaches the navmesh.**
   - **Where:** `Core/PPather/LocalPathingApi.FindWorldRoute`; PathingAPI's `WorldRoute2` had
     it too, until Phase 2.
   - **Cause:** it calls `PPatherService.ToWorldZ` once per endpoint, and the destination's
     call sets `lastStartIndoors` back to null before `DoSearch` reads it.
   - **Effect:** the navmesh resolver never sees the bot's indoor hint. Only the geometry
     path (with game archives installed) uses it, for the start's height.
   - **Fix:** carry it through the search explicitly, when `LocalPathingApi` moves onto
     `RoutePlanner` (Phase 4). This changes what the bot walks, so it needs a live test. The
     planner mirrors today's behaviour so the parity gate holds, and the route contract has
     no `startIndoors` field until then.

2. **A map route can throw, or quietly change frame, while converting back to map coordinates.**
   - **Where:** `PPatherService.ToLocal`, called by `LocalPathingApi.FindMapRoute` (no catch),
     and `RoutePlanner.WorldToMap`.
   - **Cause:** both use `WorldMapAreaDB.GetWorldMapArea`, which returns the one map that holds
     a point and throws when it finds none or several. The continent maps hold points too
     (1415 Eastern Kingdoms covers the gaps between its zones), so:
     - a point inside a zone other than the request's is in two or more maps, which throws
       `InvalidOperationException` (Silithus and Feralas overlap, for example);
     - a point that no zone holds, only a continent map, does not throw: it comes back in that
       map's 0-100 frame, mixed in with the zone's. (-7500, 500) on map 0 is one; the Phase 1
       review (2026-10-06) counted 3,514 of 10,590 baked Azeroth tile centres held by 1415 alone;
     - `ArgumentOutOfRangeException` is left for a point outside every map of the continent.
   - **Effect:** the bot's route fails on the follower's thread. `RoutePlanner` keeps a throwing
     point in the request's zone, but a gap point jumps on the zone map with no error.
   - **Fix:** convert every point in the request's zone (`WorldMapAreaDB.ToMap_FlipXY(world,
     area)`, what the planner's catch does), in the planner now and on the bot in Phase 4.

3. **Two threads can share one `NavmeshPathfinder`.**
   - **Where:** `PPatherService.GetQueryNavmesh`.
   - **Cause:** it hands out the live search's pathfinder for the active continent. Route
     generation (bot thread) and `Navigation` (pathfinder thread) can then query it at the
     same time, and `DtNavMeshQuery` is not thread-safe.
   - **Fix:** goes with the two-call search in Phase 4.

4. **`ContinentDB` is static and not thread-safe.**
   - **Cause:** it is filled by whatever loads first (`TryAdd`, first client wins), from
     every `PPatherService` and `RoutePlanner`.
   - **Effect:** one Pathing Service cannot serve two clients (Classic and Forever), and
     `ServiceTests` runs serially because of it.
   - **Fix:** make it instance state passed to whoever needs it.

5. **`Vector3Converter` cannot read what its upper-case variant writes.**
   - **Cause:** `Read` matches only `x`/`y`/`z`, while `new Vector3Converter(true)` writes
     `X`/`Y`/`Z` (recorded paths, subzone bounds).
   - **Effect:** System.Text.Json with this converter would read such a file as zeros, with no
     error. Nothing reads that way today: path files are read with Newtonsoft.
   - **Fix:** compare the names case-insensitively.

6. **The legacy remote pathing clients have no server.**
   - PathingAPI is gone (Phase 2), and with it `api/PPather/*` and the AnTCP listener. A bot set
     to `RemoteV1` or `RemoteV3`, or with `Pathing:PathVisualizer` on, finds nothing at startup
     and falls back to Local (or no visualizer) with only a log line. The shipped configs use
     Local.
   - RemoteV1 also turned a 429 or any error into an empty route, which looks exactly like
     "no path".
   - **Fix:** the clients are removed in Phase 4. The new contract types the failure
     (`RouteStatus`). The bot's live position now comes back through the Bot Manager
     (`BotSnapshot.Position`, Phase 3); drawing it on the 3D view is not done.

7. **A remote pather still loads a local engine.** Even in remote mode the bot builds its own
   `PPatherService`, because `RouteGenerator` samples and floods the navmesh directly.
   - **Fix:** route generation moves server-side in Phase 4.

10. **Duplicated hash code.** `ForeverNavmesh.CacheDirectory` computes the settings hash
    itself instead of calling `NavmeshSettings.CacheDirectory`.
    - **Fix:** Phase 4 cleanup.

15. **BlazorServer's map does not survive a script error at start.**
    - **Where:** `Frontend/Pages/LeafletComponent.razor` awaits the map's `init` in
      `OnAfterRenderAsync` without catching a `JSException`.
    - **Effect:** a script error there ends the bot UI's Blazor circuit; the page stops
      updating until reloaded. The Control Center's map page catches it, and since Phase 2 the
      script itself stops cleanly when its zone data cannot be read, which was the case that
      failed.
    - **Fix:** the same guard as `ControlCenter/Components/Pages/Map.razor`; needs a check on
      Windows, where BlazorServer runs.

16. **A full page load of an unknown Control Center URL is a bare 404.** The `NotFound` page
    renders for in-app navigation only. Status-code re-execution would also rewrite the 404s the
    forwarded API answers, so it is left off.

17. **Leaflet's script and stylesheet come from different versions.** BlazorServer loads
    `leaflet.js` 1.9.4 from MapAssets and `leaflet.css` 1.7.1 from unpkg, as it always has (the
    Control Center no longer loads Leaflet since 2026-10-05). Nothing visibly breaks; align them
    (a local 1.9.4 stylesheet) when the map is next worked on.

23. **A Classic run waits for the addon without listening to its stop.**
    - **Where:** `Core/BotController`'s constructor, reached from `HeadlessServer/Runtimes/ClassicRuntime`.
    - **Cause:** it loops on `wait.Update(5000)` until the addon reads the player's class, and checks
      no token.
    - **Effect:** a Classic run started while the addon's data is not on screen cannot be stopped by
      its token. After the grace period the agent releases the keys (none are held yet), reports the
      run `Abandoned` and restarts (exit code 75). Standalone HeadlessServer always waited the same
      way, forever. Found by reading the code, not observed.
    - **Fix:** a stop-aware wait for a readable frame before `BotController` is built, as `ForeverRun`
      waits for its first frame.

24. **BlazorServer's "Forever runs" page still starts runs as a child process.**
    - **Where:** `Frontend/ForeverRunHost`, `ForeverRunFiles`, `ForeverRouteCatalog`,
      `ForeverRunOutput`, `Pages/ForeverRuns.razor`.
    - **Effect:** a run it starts stops when BlazorServer goes (what item 9 was), and it can start a
      run on a PC whose agent runs one too. The trial runner it starts is now a command line over
      `ForeverRun`, so the run itself is the agent's.
    - **Fix:** delete the page and its host once the agent has passed the Windows checks (Phase 3's
      decision 7); the Control Center's console replaces it.

25. **Forever leaves bodies unlooted when the mob died close beside the player.**
    - **Where:** `Core/Forever/ForeverLoot.cs` (`Plan`), `ForeverCorpses.Sight`,
      `ForeverLootStep.Approach`.
    - **Seen:** a 9-minute crossroads trip on 2026-10-04, started from BlazorServer's Forever runs
      page: 14 bodies, 9 looted, 5 left. The Interact Key never showed three of the five.
    - **Cause, read from the code** (the run's snapshots come only every 2 s): a body's place is a
      stretch along the mob's last bearing, and the loot round looks round only at the stretch's
      far end. A mob that came at the Hunter died 8 to 12 yards out, beside the player at the
      stretch's near end, so the round walked away from it. The step closer walks along the facing,
      not toward the body, and after losing a body the round goes on to the far end instead of
      looking where it was seen. Two bodies lying together can be mixed up. Minimap tracking placed
      none of the 14 bodies. Not ruled out: a body with nothing to loot or skin may never show for
      the Interact Key.
    - **Fix (proposed, not started):** look round at the near end too, and where the key last
      showed a pending body; log each body's stretch at death and every dead unit the key shows,
      so a run tells "walked past it" from "nothing to loot"; check the tracking's dot matching
      against a live capture.

28. **Routes swim along the sea floor.**
    - **Where:** `PPather/Navmesh/WowQueryFilter` (`CostWater` = 1.3), and the bake, which keeps
      the sea floor under water as walkable (`AreaWater`).
    - **Seen:** Durotar to Tanaris across the continent (2026-10-05) runs down the east coast:
      2,378 of its 10,338 yards on the sea floor, down to height -47. The band search's partial
      route did the same between Ratchet and Theramore.
    - **Effect:** swimming is priced barely above walking, so a long coast swim beats any land
      detour; the bot would swim it at the surface, with fatigue a risk far from the shore.
    - **Fix (a decision, not started):** price swimming higher, for both the bot and the
      planner, or per request. It changes routes the bot walks, so it needs live runs.

29. **The world view's relief paints land below sea level as sea.**
    - **Where:** `ControlCenter/wwwroot/js/world-view.js` (`RAMP`, the relief picture), from
      `navmesh/{continent}/overview`, which gives one walkable height per tile and nothing else.
    - **Seen:** Thousand Needles and Un'Goro Crater read as lakes (2026-10-06); the flat tiles of
      the old relief did the same.
    - **Cause:** the ramp turns blue below height 0, the only hint of water there is. A tile's
      polygons know their water (`NavmeshTileGeometry.Water`), so zoomed in it is right.
    - **Fix (not started):** carry a water share per tile in the overview (the tile graph builds
      it) and colour water by that, height only for land.

30. **The Control Center's log fills while the Bot Manager is down.**
    - **Where:** `ServiceClients/Bots/BotManagerClientRegistration` (`AddStandardResilienceHandler`),
      polled every 2 s by `BotManagerStatus`; the Pathing Service's client is built the same way.
    - **Seen:** 11 minutes with the manager down (2026-10-06): 12,497 lines, 1.6 MB; 829 retry
      events from Polly, each with the connection's stack trace, the last try of each poll at Error.
      `appsettings.json` keeps Polly at Warning, which these are.
    - **Effect:** the daily log file grows by megabytes an hour, and real errors drown. The status
      bar and the pages already say the manager is offline.
    - **Fix (not started):** a poll that fails is the poller's news, not an error: quieten the
      resilience pipeline's logging for the pollers (or give them a client without retries), and
      log the state change once, as `ServiceStatus` does.
    - The agent's data sync uses the same client and was quieted in its own settings
      (`headless_appsettings.json`: `System.Net.Http.HttpClient` at Warning, `Polly` at Fatal, since
      Polly logs the last failed attempt at Error); the Control Center's pollers are not.
    - The pathing client no longer repeats a 500 or a 503 (item 37), which takes those out of the
      noise; a service that is down is still retried and logged on every poll.

31. **A file deleted centrally stays on the bot PCs.**
    - **Where:** `BotAgent/Sync/DataSync`. The sync never deletes (Phase 3, decision 5): it brings a
      run's files up to date and keeps a local change aside before replacing it, but a file the
      Pathing Service no longer has is left as it is.
    - **Effect:** a road or danger zone removed in the Control Center keeps shaping routes on a bot
      PC that synced it before. A cost-zone file is deleted when its zone map's last zone goes, and
      the bot's `CostZoneWatcher` reads whatever is in the folder. A recorded path or a route deleted
      centrally stays usable there too, which matters less: it runs only when named.
    - **Fix (not started):** for cost zones, mirror an era's folder whole, keeping aside what it
      removes, or have the service list what was deleted. Either changes how a bot walks, so the
      bot PC should say what it removed.

Items 32 to 46 come from the review of Phase 1 (84c93499..7ef44187, 2026-10-06), checked
against the code as of e71c5781. The plan to fix them is [phase1-fixes-plan.md](phase1-fixes-plan.md).

35. **The planner blocks request threads.**
    - **Where:** `RoutePlanner`. Route queries, `/area` and each leg of a continent route wait on
      the lane's `SemaphoreSlim` synchronously for up to 10 s; cost-zone reloads and
      `CloseLanes` wait with no limit.
    - **Cause:** the endpoints are synchronous, and the request holding the gate blocks in
      `Task.WaitAll` on the tile workers, which are async and need pool threads too.
    - **Seen:** a model of a lane on Kestrel (12 CPUs). 64 concurrent requests needing about
      4.5 s of serial work: 30 answered, 34 got 503 Busy, and `/health` stalled 19 s.
      200 requests: 168 Busy and a 29 s stall.
    - **Effect:** besides the Busy answers, `/area` gives up after its 10 s and answers height 0,
      the same as "not baked", and keeps the thread even after its caller has gone (it takes no
      cancellation).
    - **Fix (not started):** `WaitAsync` behind async endpoints, and `/area` answering 503 when
      busy, as routes do.

38. **A planner lane never sees tiles that others write.** Item 12 fixed this for the service's
    own bakes.
    - **Where:** `RoutePlanner`'s lanes (a disk-only `NavmeshTileCache` keeps a missing tile as
      empty); `CloseLanes` is called only by `BakeAdmin`.
    - **Effect:** tiles from `download-navmesh.ps1`, a bot baking into the shared folder or
      BakeTool stay empty to a running service: routes over them answer NoPath or Partial, or
      detour, until a restart. The comment over `RoutePlanner.GetLane` and `PathingService/CLAUDE.md`
      say downloads are picked up; `RoutePlannerWatchTests` asserts that they are not.
    - **Fix (not started):** watch the tile folder (or its newest write, as the coverage cache
      does) and close the lanes it touches.

39. **Tiles are written in place.**
    - **Where:** bakes (`NavmeshTileCache.LoadOrBake`, `File.Create`) and `download-navmesh.ps1`
      (extracting into the folder).
    - **Effect:** another process can read a tile while it is written. A disk-only cache used to
      delete such a tile and count it empty for good; since the Phase 1 review it keeps it and
      reads it again on a later request (see "Fixed"), so what is left is one request that routes
      as if the tile were missing.
    - **Fix (not started):** write each tile under a temporary name and rename it; the download
      script extracts into a temporary folder and moves the tiles in. The bytes do not change, but
      run the bake gate (`PPather/CLAUDE.md`) when the bake's writer changes.

40. **The planner does not plan Forever the way the Forever bot does.**
    - **Where:** `RoutePlanner` with `--exp=forever` (`WithSurfaceHeight`, the lane's
      `Navmesh:Query` settings, cost zones), against `Core/Forever/ForeverNavmesh.OpenDiskOnly`
      and `ForeverNavigator`.
    - **Cause:** the planner replaces a height of 0 with the surface nearest height 0
      (`TryGetHeight`), so the resolver's column logic never runs, and it applies
      `Json/road/forever`. The bot passes 0 to `TryResolveColumn`, which takes the highest
      surface under the point (`PreferGroundUnderPoint = true`), and loads no zones.
    - **Effect:** on a bridge, an upper floor or a hillside over a mine, the Control Center's
      Forever routes page plans legs from the lower surface while the bot walks from the higher
      one. Forever zones bend the preview and never the bot, though the data sync ships them.
    - **Fix (not started):** resolve Forever endpoints the way the bot does, and decide whether
      Forever has cost zones at all.

44. **Height lookups read the navmesh without the tile lock.**
    - **Where:** `NavmeshPathfinder.TryGetHeight`, which `RoutePlanner` uses for `/area` and for
      route endpoints at height 0.
    - **Cause:** it runs `FindNearestPoly` without the tile cache's read lock, while workers still
      loading an earlier query's corridor add and evict tiles under the write lock, and it does
      not wait for a tile that is already loading.
    - **Effect:** a point on a tile still loading gets height 0, or a route between baked points
      answers NoPath. Rarely, a tile read while it is evicted has its data cleared under it: a
      500. Not seen; from the code (`ResidentTiles` and `TryLocatePoly` take the lock).
    - **Fix (not started):** take the read lock and wait for the point's own tile, as `FindPath`
      does for its corridor.

45. **Docs that promise more than the code does.**
    - `service-architecture.md`, rule 4: "a parity test enforces" that a preview is the bot's
      path, but `RoutePlannerParityTests` skips when the bot side has game archives. With them
      the bot seeds a missing height from the geometry (`Search.CreateWorldLocation`: canopy,
      terrain, `startIndoors`), while the planner always takes the navmesh surface. Parity holds
      for disk-only Classic installs; Forever is item 40.
    - `service-architecture.md`, the endpoint table: capabilities carries `BakedContinents` and
      `GameFiles`, not `Continents`.
    - **Fix:** correct the text; rule 4 needs the parity test, or the claim, to cover installs
      with archives.

46. **Smaller findings.** (The null cost-zone entry, the continent names and `%2F` in path names
    are fixed: see "Fixed".)
    - A cost-zone save's uiMap id is checked only for being not negative, not against the
      continent's maps, so the number of zone files in a continent, which every load reads, has no
      bound. A check would have to keep uiMap 0, the legacy single file's.
    - A lane is built under the planner's global lock (`RoutePlanner.GetLane`): 15-60 ms,
      measured, in which every continent's queries and `/health` wait, again after each bake or
      cache clear.
    - `RoutePlanner.WorldToMap` throws and catches an exception for each point outside the
      request's zone.
    - An endpoint on a tile with no geometry (open sea) answers NotBaked, "download or bake",
      rather than NoPath: a bake writes no file for an empty tile.
    - `GET /costzones/{continent}` lists numeric file names only, while the loader applies every
      `*.json` in the folder; a legacy `{continent}.json` is listed but never applied, and one
      unreadable file fails the whole GET.
    - The Bot Manager answers 500 to a request it cannot read (a body that is not the contract's
      JSON, a query that is not a number) when it runs in Development, as `run.sh` runs it: minimal
      APIs throw there, and `UseExceptionHandler` makes it a 500. Its client does not repeat a
      POST, but a GET is repeated. The Pathing Service answers 400 since the Phase 1 review
      (`StatusCodeSelector` in `PathingServiceRegistration`); the manager needs the same lines.

## Fixed

46. **Cost-zone continents and path names were checked too loosely** (item 46's second and third
    findings; Phase 1 review, 2026-10-06). The store took any name of letters and digits as a
    continent, so on Linux `azeroth` saved with 204 into a folder nothing read, which the data sync
    then wrote into `Azeroth\` on a Windows bot; and it refused the client's own `Zul'gurub` or
    `Azjol_LowerCity`. It now takes the client's continents only (`WorldMapAreaDB`), in any case,
    and saves under the client's own. `PathName` refuses `%`, so a name whose `/` came as `%2F`
    (Swagger UI's "Try it out" escapes the whole name) is no longer saved as one flat file.

36. **The pathing client misread failures** (Phase 1 review, 2026-10-06). It read any JSON body
    as a `RouteResponse`: a ProblemDetails (500, 400, 401) became `Status = (RouteStatus)500` with
    no points, and a JSON error without `status` read as `Found` with none. A status it did not
    know threw, and the error path then read the disposed body, which turned a 200 into "could not
    be reached"; a NaN point threw in its own serializer and came back as Unavailable. Now
    `FindRouteAsync` takes a route only from `application/json` with a status it knows and points
    (at least one when found or partial), maps anything else by its status code with the service's
    words, says "answered a route this client cannot read" with the body's start for a 2xx it cannot
    read, and refuses a point or tuning that is not finite before sending. `ServiceHttp.TryReadAsync`
    buffers a body before reading it, so it can be read again.

42. **The test of fresh pathfinder memory per request could not fail** (Phase 1 review,
    2026-10-06). `A_query_does_not_inherit_the_previous_callers_floor` compared answers, but the
    planner gives a start without a height the surface under it before `FindPath`, and the floor
    memory applies only to a start at height 0, so making `Find` share the lane's own memory still
    passed every test. PPather's internals are now visible to `ServiceTests`, and
    `Queries_leave_the_lanes_own_memory_as_they_found_it` runs the corpus and a continent route,
    then reads each lane pathfinder's own memory (`OwnMemory`): no floor, no sticky band, no stall.
    With `Find` calling `nav.FindPath(from, to, null)`, it fails, while the parity tests still
    pass. `PathingService/CLAUDE.md` points to it.

39. **A disk-only tile cache deleted a tile it failed to read** (Phase 1 review, 2026-10-06). A
    tile that did not parse was deleted to be baked again, even by a cache with no world, which
    can never bake it, and then counted empty for the cache's life. On Linux a planner lane that
    read a tile mid-write unlinked it from the shared folder. A disk-only cache now keeps the file,
    warns once, does not count the tile as empty and reads it again on a later request; a cache
    with game files still deletes and rebakes it. The bot without game files does the same. Left:
    writers still write in place (item 39, open).

46. **A request the Pathing Service could not read answered 500** (item 46's first finding; Phase
    1 review, 2026-10-06). A null entry in a cost-zone array (`{"roads":[null]}`) threw in
    `CostZoneMapping`, and in Development, where `run.sh` runs the service, minimal APIs throw on a
    body or query they cannot bind, which `UseExceptionHandler` turned into a 500. A client takes a
    500 for the service's own failure (and repeated it, until item 37). A null entry is now refused
    first ("roads[3] is missing"), and a `BadHttpRequestException` answers its own status, 400,
    with its message as the detail. The Bot Manager has the same handler: see item 46.

43. **On Windows, `_forever./x.json` got past the Forever guard** (Phase 1 review, 2026-10-06).
    The guard compared the raw first segment with `_forever`, and segments could end in a dot,
    which Windows drops, so a PUT to `/api/v1/paths` could overwrite a Forever route file with a
    point array, and a DELETE remove it. `PathName` refuses a segment that ends in a dot (spaces
    were already refused) and checks the guard again on the resolved path. Tested on Linux by the
    names; the rewrite itself happens only on Windows.

41. **A cost-zone save answered 204 when the reload failed** (Phase 1 review, 2026-10-06). The
    reload is all or nothing per continent, and the save dropped its result, so with one invalid
    file in the continent (an old road of width 100, say) every valid save answered 204 and changed
    nothing until the lane reopened. A save now reloads only its own continent and answers 409,
    saved but not in use, naming the file in the way and why (`CostZoneLoader.TryLoad` says what
    failed; `POST /costzones/reload` names it too). Both zone pages, the world view's and
    BlazorServer's map, show a 409 as saved, not in use yet. Reloads of a lane run one at a time
    from their read to their swap, so the one that read last applies last.

37. **The pathing client retried a busy service** (Phase 1 review, 2026-10-06). The standard
    resilience handler retried every 5xx, POST included: a 503 Busy (the lane had already waited
    10 s) was tried 3 times, so the caller learned after about 35 s and the lane saw 3 times the
    waiters, and a 500 was retried for nothing. One breaker served every call, so a busy continent
    could shut out path, cost-zone and health calls too. The client now retries only what a retry
    can fix (`PathingClientRegistration.IsWorthRetrying`): a request that did not reach the service,
    or a gateway's 502 or 504; not a 500, a 503 or an attempt that timed out, which would run its
    search again. The breaker opens on those and on timeouts, not on a busy continent or a bad
    file. `RoutePlanner` checks the caller's token once it holds the lane, so a caller that gave up
    while it waited does not get a search. Left: a search that has started runs to its end after
    its caller has gone, since the pathfinder takes no token; a continent route stops at its next
    leg.

33. **A cost-zone file could pass validation and still hang every load** (Phase 1 review,
    2026-10-06). `CostZoneLoader.Problem` capped width, points, coordinates and each shape's size,
    but not the rasterizer's work: `CostZones.MarkLeg` walks each leg's whole bounding box in
    16.7-yard steps, so a road zig-zagging corner to corner (about 1.15e12 steps) was accepted and
    hung the save, the watcher, every lane open and the bot's own load. `Problem` now counts the
    cells as the rasterizer walks them (`CostZones.RasterCells`, beside its loops) and refuses a
    file over `CostZoneLoader.MaxRasterCells` (2,000,000), naming the road or shape that costs the
    most. The repository's largest file takes 31,525 and all 67 together 592,395. The loader
    applies the same check, so a file already on disk over the cap is skipped with a warning, here
    and on the bot. Left: the cap is per file, and a uiMap id is any number (item 46), so a
    continent's file count is not bounded.

34. **The path store listed, overwrote and deleted the class profiles' path groups** (Phase 1
    review, 2026-10-06). `Json/path` also holds the eight `PathSettings` groups (`1-8_*.json`)
    that every 1-10 profile names in `PathsFilenames`. Newtonsoft read one as a path of (0,0,0)
    points, so the route library opened it as 3 points, and saving or deleting it made every
    1-10 profile throw on load. The store now knows a recorded path by how the file starts (a list
    whose first entry starts with X, Y or Z, comments skipped) and leaves anything else alone: it is
    not listed, and a read, save or delete by its name answers 400 with why. The route library and
    the assignment form list through the store, so they no longer offer the groups; the data sync
    still serves them.

32. **One request could hang a continent's planner lane for good** (Phase 1 review, 2026-10-06).
    - **Cause:** only finiteness was checked. DotRecast's `CalcTileLoc` casts with
      `(int)MathF.Floor`, which saturates far outside the world grid, and its loop over the tiles
      then never ends. An `/area` point or a watch drawing beyond about 2.86e11 yards, or an
      `edgeMarginYards` that pushed a point that far, held the lane's gate, a core and every
      cost-zone save until a restart. `jitterYards` had no ceiling either: 100,000 took 36 s on one
      route.
    - **Fix:** a world point must lie within `NavmeshCoords.WorldEdge` (17,066.67 yards, the edge
      of the world grid) on each axis. `/area` and the watch drawings answer 400 beyond it, and
      `RoutePlanner.GetAreaAndHeight` and `NavmeshPathfinder.TryGetHeight` answer nothing there
      without asking Detour. Jitter and edge margin are capped at 10 yards each
      (`NavmeshQueryTuning`): the route endpoint answers 400 and the planner `Refused`. The tests
      run against a deadline; with the guard taken out, the planner's test times out on the real
      hang.

27. **A route that stopped short was reported as found** (2026-10-05). The band search loads
    tiles only near the straight line; Durotar to Tanaris stopped 2,837 yards short in southern
    Dustwallow, and the service answered `Found` with that half route, which the Control Center
    showed in green. The pathfinder measured the gap (`EndGapYd`) but nothing read it. A route
    ending more than 5 yards from the resolved destination is now `Partial`, with where it stops
    and how far short; `RouteSearch.Continent` searches the whole continent along its tile graph.

9. **A Forever run could not outlive the web UI** (Phase 3). `ForeverRunHost --listen` stopped the
   run when the host's stdio pipe closed. The resident agent runs Forever in process through
   `ForeverRun` and keeps running while the Bot Manager and the Control Center are down. The
   BlazorServer page that still starts child runs is item 24.

21. **HeadlessServer had no clean stop** (Phase 3).
    - **Cause:** it started the bot and blocked on `Console.ReadKey`; the bot's foreground threads
      outlived `Main`, nothing handled Ctrl+C, and the `ServiceProvider` was never disposed.
      `AddWoWProcess` also left its probe's `WowProcess` polling on a foreground thread whose token
      nobody cancelled, which alone kept the process alive.
    - **Fix:** HeadlessServer runs on the Generic Host. Ctrl+C, a logoff or a stop cancels the run's
      token: the bot is turned off, which releases its input, and only then is the run's composition
      disposed. `AddWoWProcess` cancels its probe's token, and `Main` ends with `Environment.Exit`, so
      a run that could not be torn down cannot keep the process alive. Checked on Linux (SIGTERM ends
      an agent cleanly); on Windows with a game it is on the Phase 3 checklist.

22. **Configuration keys on HeadlessServer's command line failed the parse** (Phase 3).
    `HeadlessServer/CLAUDE.md` said `--Pathing:Engine=Navmesh` reached the configuration, but
    CommandLineParser parsed the same arguments and refused the unknown option
    (`UnknownOptionError`). `HeadlessArguments` now gives `--Section:Key` arguments to the
    configuration only and the rest to the parser.

26. **The Control Center's toggles never looked pressed** (Phase 3, step 5). Blazor writes a
    `bool` attribute as present or absent, `true` as an empty value, so `aria-pressed="@navmesh"`
    came out as `aria-pressed=""`: the 3D view's toggles never matched the pressed style
    (`[aria-pressed="true"]`), and screen readers got an invalid state; `aria-expanded` on the
    rail and the panels likewise. Found by the fleet pages' browser checks; `Aria.State`
    (`Components/Shared`) writes "true" or "false".

8. **The dashboard needed a running game** (Phase 2, 32ce9d46). The Control Center is the central
   UI and needs no client. `BlazorServer` still cannot start without one (`AddWoWProcess`
   throws); it stays the PC's setup UI.

11. **Everything twice in the two hosts** (Phase 2, 32ce9d46). PathingAPI is deleted. The map's
    scripts, libraries and styles live once, in `MapAssets`, served by BlazorServer and the
    Control Center. `teleport_locations.txt` lives once, in `Json/locations`.

12. **A planner lane never noticed tiles baked after it looked** (Phase 2, 31698e77).
    - **Cause:** a disk-only `NavmeshTileCache` records a tile that is not on disk as empty
      (`emptyTiles`) for the cache's lifetime and never looks at it again.
    - **Fix:** `RoutePlanner.CloseLanes` takes a lane out of service (a query waiting on it moves
      to a fresh one). The Pathing Service closes the lanes a bake touched when it ends, and a
      continent's lane when its tiles are deleted. `ServiceTests` walls a route off with
      missing tiles, copies them in, and checks that the route arrives only once the lane closed.
    - Fixed only for the service's own bakes and deletes. Tiles that anything else writes into
      the folder are still not seen: item 38.

13. **File names were not checked** (Phase 2, 32ce9d46). `Frontend/Controllers` are deleted: the
    map authors through the Pathing Service, which refuses such names (`PathName`) and takes only
    plain continent names.
    - The old `PathController.SavePath` joined any name onto `Json/path`, so `../` wrote
      anywhere; and since its listing returned `/path/<name>`, saving a path the map had loaded
      wrote to `Json/path/path/<name>` (or failed for want of the folder).

14. **Cost zones were not validated on save** (Phase 2, 32ce9d46). The service validates with
    `CostZoneLoader.Problem` and answers 400 with the reason, which the map shows.

18. **Routes and markers from the watch hub did not draw** (Phase 2, 31698e77). MessagePack
    writes a `System.Numerics.Vector3` as a map of its fields (`{X, Y, Z}`), while the 3D view
    and the map read a point as `[x, y, z]`. PathingAPI's broadcaster sent `Vector3`s, so its
    routes and markers drew at the origin in 3D and threw on the map; only navmesh tiles, sent as
    flat float arrays, worked. The Pathing Service sends points as float arrays, and
    `ServiceTests` checks the shape.
    - PathingAPI also asked for LZ4 compression with a call whose result it dropped, so the
      setting never applied. Applying it would break the browser: its MessagePack reader does
      not know MessagePack-CSharp's compression extension.

19. **BlazorServer's bake buttons called endpoints only PathingAPI had** (Phase 2, 32ce9d46).
    `api/PPather/Bake*` answered 404 on the bot's dashboard. BlazorServer now forwards `/api/v1`
    to the Pathing Service, which bakes when its host has the game files and says so otherwise.

20. **`teleport_locations.txt` was loaded as a script** (Phase 2, 32ce9d46). BlazorServer's
    `App.razor` had a `<script>` tag for it, and Frontend removed it from its static assets
    (`Content Remove`) while the map fetched it from `_content/Frontend/`. It now lives in
    `Json/locations`, served at `/locations` by both hosts and read by the Pathing Service's
    location catalog.

## Design notes

**The pathfinder's per-walker memory.** `NavmeshPathfinder` remembers three things about its
caller between queries (now in `NavmeshQueryMemory`). Two jobs depend on them:

- **Which floor (correctness).** A start without a height takes the floor the walker was last
  resolved onto, if it is within 80 yards. No client gives the bot its height: Classic's addon
  reads only the map X/Y (`C_Map.GetPlayerMapPosition`), and Forever's `UnitPosition` returns
  a Z that is always 0. What differs is who remembers the floor.
  - **Classic remembers it in the bot.** `PlayerReader.WorldPosZ` ("MapZ not exists") is set
    from the heights of the route points `Navigation` walks and from known spawn positions,
    and sent with every request. Where it has none yet, as at start, `PPatherService.DoSearch`
    fills the height from the geometry or the navmesh surface before the pathfinder sees it,
    and the floor memory applies only to a start at height 0 (`ContinuityHint`). So Classic
    reaches the memory only when that fill finds no surface.
  - **Forever leaves it to the pathfinder.** The navigator asks for paths without a height on
    purpose, and the kept floor stops it flipping between a mountain top and the ground below
    ([forever-movement.md](forever-movement.md), "Which floor", tested live). When it walks a
    route, `ForeverWalker` passes the route's own heights instead.
    - When it chases a mob or a body, `WalkToward` passes `UnitPosition`'s Z of 0 to the path
      request and to the steering checks (`TryWalkStraight`). Both use and update the same
      memory, so they agree on the floor.
    - Nothing in the API prevents carrying the floor in the bot, as Classic does. Take it from
      each answer (the first point of a returned path is the floor the pathfinder chose) and
      drop it after a jump in position, as the 80-yard rule does.
    - Stateless remote planning needs exactly that (Phase 4). It changes how the Forever bot
      walks, so it waits for a live test.
- **Repeat requests (speed).** The follower asks for the same destination every few seconds.
  - The sticky corridor keeps a band it already widened, so the tiles it loaded are reused.
  - The stall memo skips re-walking a dead end's whole escalation for 30 seconds or
    150 yards.
  - Re-deriving both from scratch cost 2.7-4.0 s per request in a Wetlands run, per the
    pathfinder's comments.

So the memory stays.
- **Local mode:** one walker per process, so the instance's own memory is exactly right.
- **Shared service, UI queries:** these are one-off, so each request gets fresh memory, which
  is what the planner does now.
- **Shared service, remote bots - preferred direction:** keep the service stateless. The
  request carries the bot's last floor (its last resolved height), the way Classic's
  `WorldPosZ` already does. The speed memos then apply only within one request.
- **Fallback:** if repeat requests turn out to be too slow without the speed memos, add a
  small per-client cache keyed by the client id the user plans to introduce.
