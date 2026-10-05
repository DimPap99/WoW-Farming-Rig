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

6. **The legacy remote pathing clients.**
   - RemoteV1 turns a 429 or any error into an empty route, which looks exactly like "no path".
   - Both remote modes are probed once at startup and fall back to Local with only a log line.
   - **Fix:** removed in Phase 4. The new contract types the failure (`RouteStatus`).

7. **A remote pather still loads a local engine.** Even in remote mode the bot builds its own
   `PPatherService`, because `RouteGenerator` samples and floods the navmesh directly.
   - **Fix:** route generation moves server-side in Phase 4.

8. **The dashboard needs a running game.** `BlazorServer` cannot start without a WoW client:
   `AddWoWProcess` throws.
   - **Fix:** the Control Center (Phase 2) is the central UI and needs no client. BlazorServer
     stays as the PC's setup UI.

9. **A Forever run cannot outlive the web UI.** `ForeverRunHost --listen` stops the run when
   the host's stdio pipe closes.
   - **Fix:** the resident agent (Phase 3) replaces it.

10. **Duplicated hash code.** `ForeverNavmesh.CacheDirectory` computes the settings hash
    itself instead of calling `NavmeshSettings.CacheDirectory`.
    - **Fix:** Phase 4 cleanup.

11. **Everything twice in the two hosts.** `teleport_locations.txt`, the map page and its CSS
    exist in both BlazorServer/Frontend and PathingAPI.
    - **Fix:** deleted in Phase 2 with PathingAPI.

## Fixed in the Pathing Service, still in the old code

These remain in `Frontend/Controllers` (mounted by BlazorServer and PathingAPI) until Phase 2
deletes them.

12. **File names are not checked.**
    - `PathController.SavePath` joins any file name onto `Json/path`, so `../` writes anywhere.
    - `RoadController` and `DangerZoneController` take any continent string as a folder name.
    - **In the service:** `PathingService` refuses such names (`PathName`) and takes only plain
      continent names.

13. **Cost zones are not validated on save.**
    - The road and danger-zone controllers write whatever they receive. `CostZoneLoader`
      later skips an invalid file with only a warning, so a bad save quietly removes zones.
    - **In the service:** `PathingService` validates with `CostZoneLoader.Problem` and answers
      400 with the reason.

## Design notes

**The pathfinder's per-walker memory.** `NavmeshPathfinder` remembers three things about its
caller between queries (now in `NavmeshQueryMemory`). Two jobs depend on them:

- **Which floor (correctness).** A start without a height takes the floor the walker was last
  resolved onto, if it is within 80 yards.
  - Forever depends on it: the client reports Z = 0, so the navigator asks for paths without
    a height on purpose and the kept floor stops it flipping between a mountain top and the
    ground below ([forever-movement.md](forever-movement.md), "Which floor", tested live).
  - Classic passes the player's real height, so it never uses this.
- **Repeat requests (speed).** The follower asks for the same destination every few seconds.
  - The sticky corridor keeps a band it already widened, so the tiles it loaded are reused.
  - The stall memo skips re-walking a dead end's whole escalation for 30 seconds or
    150 yards.
  - Re-deriving both from scratch cost 2.7-4.0 s per request in a Wetlands run, per the
    pathfinder's comments.

So the memory stays.
- **Local mode:** one walker per process, so the instance's own memory is exactly right.
- **Shared service:** memory must belong to the caller. One-off UI queries get fresh memory
  per request (what the planner does now). A bot planning remotely will get memory keyed by
  its client id once clients have one.
- **Stateless alternative:** the request could carry the floor hint itself (the last
  resolved height), leaving only the speed part tied to a client.
