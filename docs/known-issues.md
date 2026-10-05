# Known issues and findings

Things spotted while working on the code: where each lives, what it does, and when it goes
away. Phases refer to [service architecture](service-architecture.md). Add to this list
when you find something you are not fixing on the spot; move an item to "Fixed" with the
commit that fixed it.

## Open

1. **`startIndoors` never reaches the navmesh.**
   - **Where:** `Core/PPather/LocalPathingApi.FindWorldRoute` and PathingAPI's `WorldRoute2`.
   - **Cause:** both call `PPatherService.ToWorldZ` once per endpoint, and the destination's
     call sets `lastStartIndoors` back to null before `DoSearch` reads it.
   - **Effect:** the navmesh resolver never sees the bot's indoor hint. Only the geometry
     path (with game archives installed) uses it, for the start's height.
   - **Fix:** carry it through the search explicitly, when `LocalPathingApi` moves onto
     `RoutePlanner` (Phase 4). This changes what the bot walks, so it needs a live test. The
     planner mirrors today's behaviour so the parity gate holds, and the route contract has
     no `startIndoors` field until then.

2. **A map route can throw while converting back to map coordinates.**
   - **Where:** `PPatherService.ToLocal`, called by `LocalPathingApi.FindMapRoute` (no catch).
   - **Cause:** it uses `WorldMapAreaDB.GetWorldMapArea`, which throws
     `ArgumentOutOfRangeException` for a point outside every zone of the continent, and
     `InvalidOperationException` for one inside two overlapping zones when neither is the
     request's uiMap (Silithus and Feralas overlap, for example).
   - **Effect:** the route fails on the follower's thread.
   - **Status:** `RoutePlanner` keeps such a point in the request's zone. The bot side is
     fixed in Phase 4.

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
     (`RouteStatus`), and the bot's live position comes back through the Bot Manager (Phase 3).

7. **A remote pather still loads a local engine.** Even in remote mode the bot builds its own
   `PPatherService`, because `RouteGenerator` samples and floods the navmesh directly.
   - **Fix:** route generation moves server-side in Phase 4.

9. **A Forever run cannot outlive the web UI.** `ForeverRunHost --listen` stops the run when
   the host's stdio pipe closes.
   - **Fix:** the resident agent (Phase 3) replaces it.

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

17. **Leaflet's script and stylesheet come from different versions.** Both hosts load
    `leaflet.js` 1.9.4 from MapAssets and `leaflet.css` 1.7.1 from unpkg, as BlazorServer always
    has. Nothing visibly breaks; align them (a local 1.9.4 stylesheet) when the map is next
    worked on.

## Fixed

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
    and sent with every request. So the pathfinder's floor memory only matters until the bot
    has a height of its own, as at start.
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
