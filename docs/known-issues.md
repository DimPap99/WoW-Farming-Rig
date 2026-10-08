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

2. **The bot's map route can throw, or quietly change frame, while converting back to map coordinates.**
   - **Where:** `PPatherService.ToLocal`, called by `LocalPathingApi.FindMapRoute` (no catch).
     `RoutePlanner.WorldToMap` did the same until the Phase 1 review (see "Fixed").
   - **Cause:** it uses `WorldMapAreaDB.GetWorldMapArea`, which returns the one map that holds
     a point and throws when it finds none or several. The continent maps hold points too
     (1415 Eastern Kingdoms covers the gaps between its zones), so:
     - a point inside a zone other than the request's is in two or more maps, which throws
       `InvalidOperationException` (Silithus and Feralas overlap, for example);
     - a point that no zone holds, only a continent map, does not throw: it comes back in that
       map's 0-100 frame, mixed in with the zone's. (-7500, 500) on map 0 is one; the Phase 1
       review (2026-10-06) counted 3,514 of 10,590 baked Azeroth tile centres held by 1415 alone;
     - `ArgumentOutOfRangeException` is left for a point outside every map of the continent.
   - **Effect:** the bot's route fails on the follower's thread, or a gap point jumps on the zone
     map with no error.
   - **Fix:** convert every point in the request's zone (`WorldMapAreaDB.ToMap_FlipXY(world,
     area)`), as the planner does now, on the bot in Phase 4. `RoutePlannerParityTests` compares
     map routes with `ToLocal`; its corpus stays in its zones, where the two agree.

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
    - **A Forever run too** (Phase 3 review): a stop between `ForeverRun`'s first frame and the
      controller's first readable class cannot end it, since the stop cancels the only thread that brings
      frames and the constructor's wait takes no token. The run ends Abandoned with exit code 75.
    - **Fix:** the constructor's wait reads its token, and each runtime ties the run's stop to that
      token while the controller is built, and only then, so a stop still releases input before
      anything is torn down.

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
    - The manager's half comes from `BotManagerStatus`'s 2 s poll on the retrying client, which the
      fleet feed's own connection makes redundant (Phase 3 review; the plan's step 3).

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
      `TilesChanged` (a bake's end, a cache clear) wait with no limit.
    - **Cause:** the endpoints are synchronous, and the request holding the gate blocks in
      `Task.WaitAll` on the tile workers, which are async and need pool threads too.
    - **Seen:** a model of a lane on Kestrel (12 CPUs). 64 concurrent requests needing about
      4.5 s of serial work: 30 answered, 34 got 503 Busy, and `/health` stalled 19 s.
      200 requests: 168 Busy and a 29 s stall.
    - **Effect:** besides the Busy answers, `/area` gives up after its 10 s and answers height 0,
      the same as "not baked", and keeps the thread even after its caller has gone (it takes no
      cancellation).
    - **Fix (not started):** `WaitAsync` behind async endpoints, and `/area` answering 503 when
      busy, as routes do. The watch drawings, which waited once per point, are gone (item 55).

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

40. **The planner does not plan Forever the way the Forever bot does.** (The cost-zone half is fixed:
    see below.)
    - **Where:** `RoutePlanner` with `--exp=forever` (`WithSurfaceHeight`, the lane's
      `Navmesh:Query` settings), against `Core/Forever/ForeverNavmesh.OpenDiskOnly` and
      `ForeverNavigator`.
    - **Cause:** the planner replaces a height of 0 with the surface nearest height 0
      (`TryGetHeight`), so the resolver's column logic never runs. The bot passes 0 to
      `TryResolveColumn`, which takes the highest surface under the point
      (`PreferGroundUnderPoint = true`).
    - **Effect:** on a bridge, an upper floor or a hillside over a mine, the Control Center plans
      Forever legs from the lower surface while the bot walks from the higher one.
    - **Fix (not started):** resolve Forever endpoints the way the bot does.
    - **Cost zones, fixed 2026-10-07:** a Forever run plans with the era's authored zones when its
      route file says `"CostZones": "Apply"` (`ForeverRoute.ApplyCostZones`, loaded by
      `ForeverNavmesh.CostZonesOf` as the planner reads them), and without them otherwise, as
      before; the Control Center plans a Forever route's legs under the same choice
      (`RouteRequest.CostZones`), the route designer and the Forever routes page alike. A run reads
      the zones when it opens its continent's navmesh, so an edit reaches the next run, which the
      agent's data sync brings it to anyway.

44. **Height lookups read the navmesh without the tile lock.**
    - **Where:** `NavmeshPathfinder.TryGetHeight`, which `RoutePlanner` uses for `/area` and for
      route endpoints at height 0.
    - **Cause:** it runs `FindNearestPoly` without the tile cache's read lock, while workers still
      loading an earlier query's corridor add and evict tiles under the write lock, and it does
      not wait for a tile that is already loading.
    - **Effect:** a point on a tile still loading gets height 0, or a route between baked points
      answers NoPath. Rarely, a tile read while it is evicted has its data cleared under it: a
      500. Not seen; from the code (`TryLocatePoly` takes the lock).
    - **Fix (not started):** take the read lock and wait for the point's own tile, as `FindPath`
      does for its corridor.

45. **Parity holds only for disk-only Classic installs.**
    - **Where:** `service-architecture.md`, rule 4, and `RoutePlannerParityTests`.
    - **Cause:** with game archives the bot seeds a missing height from the geometry
      (`Search.CreateWorldLocation`: canopy, terrain, `startIndoors`), while the planner always
      takes the navmesh surface, and the parity test skips when the bot side has archives.
      Forever is item 40.
    - **Effect:** a preview can differ from what a bot with archives walks. Rule 4 says so since
      the Phase 1 review, which also corrected the endpoint table's capabilities and reload rows.
    - **Fix (not started):** a parity test for installs with archives, or planning as they do;
      until then rule 4 claims disk-only Classic installs only.

46. **Smaller findings.** (The null cost-zone entry, the continent names and their one rule, `%2F`
    in path names and the planner's exception per map point, with item 2, are fixed: see "Fixed".)
    - A cost-zone save's uiMap id is checked only for being not negative, not against the
      continent's maps, so the number of zone files in a continent, which every load reads, has no
      bound. A check would have to keep uiMap 0, the legacy single file's.
    - A lane is built under the planner's global lock (`RoutePlanner.GetLane`): 15-60 ms,
      measured, in which every continent's queries and `/health` wait. Once per continent since
      the Phase 2 review: a bake or a clear no longer closes the lane.
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
    - The Phase 3 review found two halves left: `AssignmentRules` still accepts the names `PathName`
      now refuses (item 68), and the Bot Manager's client still repeats a GET the manager answered 500
      or timed out, and counts the manager's own 504 as a failure (item 37 changed the pathing client's
      copy only). Both go with the plan's step 3.

Items 47 to 56 come from the review of Phase 2 (7ef44187..4e65fd27, 2026-10-07), checked against
0ec0be56. The report is outside the repository, at
`~/RiderProjects/WoW-Farming-Rig-review/phase2/phase2-review.md` on the machine it ran on. They were
fixed the same day on `fix/phase2-review` (see "Fixed"); what is left:

47. **The Control Center has no logins.** (The forwarders' half is fixed: see "Fixed".)
    - **Where:** the Control Center's pages, which call the Pathing Service and the Bot Manager from
      the server with their keys; its proxy forwards to another machine reads only.
    - **Effect:** whoever reaches the Control Center can delete tiles, bake, edit paths and zones,
      and start or stop bots. Nearly harmless while it binds localhost and one person uses it: a web
      page can still reach it through DNS rebinding, and the hubs through a WebSocket (item 75).
    - **Fix (not started; with central hosting):** logins, with an admin role for what changes every
      bot (deleting tiles, bakes and area grids, stopping bots) and users for the rest, checked by
      the pages and the proxy; with cookie logins, anti-forgery on top of the cross-site rule.

48. **Two pages editing one zone map overwrite each other.** (Deleting zones after a failed load is
    fixed: see "Fixed".)
    - **Where:** both zone pages save a zone map's whole road and danger lists
      (`PUT costzones/{continent}/roads|danger/{uiMapId}`), as they hold them.
    - **Effect:** an edit made in the Control Center after the bot's map loaded is overwritten by
      that map's next save of the same zone map, and the other way round.
    - **Fix (deferred to the database the authored data is to move to):** a save names the version
      it was made from, and the service refuses a stale one.

56. **Left from the Phase 2 review's smaller findings.**
    - The agent host still stops on a malformed `PathingService:Url` (or `BotManager:Url`), and is
      built again into the same error: kept, as an agent that cannot sync must not run a bot on stale
      copies. BlazorServer no longer stops for it. (Corrected by the Phase 3 review: `ResidentHost`
      builds the host again in the same process every 3 s, with no backoff; `run-agent.ps1` is not
      involved.)
    - BlazorServer's own map (Frontend) still skips a shared-era tile folder that is missing when it
      starts (Cataclysm only); the Pathing Service serves it since the review.
    - The report's unverified cleanups are not done: magic strings in `ApiReference.razor` and
      `Error.razor`, about 500 lines of `PPatherService` only PathingAPI used, `ControlCenterLogging`
      copying `PathingLogging`, `WorldDataFiles` re-implementing Frontend's static files, the
      MessagePack 3.1.8 pin nothing references, and the benchmark's copy of the test routes.

59. **Forever's world data is QuestieDB's, with its gaps** (2026-10-07).
    - **Where:** `scripts/extract-questiedb.py`, into `Json/dbc/forever/creatures.json`,
      `npcspawnlocations`, `area`, `mailboxlocations` and `quests/forever`; read by the Pathing
      Service's layers and quests and by a run's `Visit` and `Quest` steps.
    - **Gaps:** Forever's new NPCs have no role and no creature type in QuestieDB yet, so a new vendor
      is no vendor and a new beast not skinnable here; about 490 of Forever's ~1,240 new quests have
      objectives. The data follows a game in beta: regenerate it now and then (`source.json` names
      the QuestieDB commit). QuestieDB declares no license.
    - **No hit boxes:** Forever's `WorldMapArea.json` has none, so a zone's NPC and node layers take
      everything inside its map's rectangle - Elwynn Forest's vendors include Stormwind's. (A zone's
      quests go by the quest log, not the rectangle.) The area grid, once baked for Forever, could
      give them.
    - **The islands:** Zephras Isle (map 2991) and the Darkspear Islands (2997) are zones of the map
      data but no continent of the world view, so their layers and quests are not drawn.
    - **Not synced:** an agent's data sync carries routes and cost zones, not this data; the game
      PC's checkout of the repository has it. It is to move to the Pathing Service's API.

60. **A `Visit` goes there; it does not talk yet** (2026-10-07).
    - **Where:** `Core/Forever/Tasks/ForeverVisit.cs`, `ForeverQuestArea.cs`.
    - **Effect:** a `Visit` walks to the NPC's spawn and is done on arrival: accepting or handing in
      a quest, selling, repairing and training are still the player's. A `Quest` roams the camp of
      what the quest asks for and takes any eligible unit there, not only the quest's, and ends by
      kills, minutes or a level, not by the quest log. An NPC that stands where the baked mesh does
      not reach (behind a counter) fails its visit at the start-up check, with why.
    - **Needs:** the NPC clicked and its frames answered (`ForeverPointAndClick`); an NPC id per
      nameplate; quest-log and bag telemetry from the add-on ("Quests" in `docs/forever-port.md`).
    - **Also:** without Forever's area grid baked, a point's zone is guessed from the maps, which can
      name the neighbour (the Valley of Trials as The Barrens) in logs and in the Control Center;
      the place itself is exact.

61. **A line walked exactly is not checked against the navmesh** (2026-10-07).
    - **Where:** the route designer's exact legs (`ServiceClients/Routes/RoutePoints`), written as
      `Via` points 10 yards apart, and `ForeverTravel.TryResolve`, the run's start-up check.
    - **Effect:** the designer draws an exact leg as its line whether or not the ground under it is
      walkable, and a run refuses a route whose `Via` lies more than about 8 yards from the baked
      mesh ("outside the baked mesh"), as across a mountain's unwalkable face; the walk between
      points goes round small obstacles on its own. Where there are tiles, the world view shows the
      walkable polygons when zoomed in, and Compare draws the replanned way beside the line.
    - **Fix (not started):** a line check in the planner (heights along the line, the stretches off
      the mesh), drawn by the designer; and a start-up check that passes a `Via` off the mesh by, as
      the walk passes by one it cannot get past, instead of refusing the route - a change to how the
      bot walks, so with a live test.

62. **Each mode of the World workspace keeps its own layers** (2026-10-07).
    - **Where:** `ControlCenter/Components/Pages/WorldWorkspace` and the `WorldLayers` each mode
      (Explore, Route, the Forever routes page) holds.
    - **Effect:** a zone, layers or a quest chosen in Explore are not those of Route's layers panel;
      each mode keeps its own while the workspace lives. Leaving the workspace (the fleet, the
      Forever routes page) and coming back starts every mode afresh, an unsaved route included.
    - **Fix (not started):** the layers' choices in a scoped session every `WorldLayers` reads and
      tells, and the route being designed in one, so they outlive the page too.

Items 63 to 77 come from the review of Phase 3 (its eleven commits in 4e65fd27..e71c5781,
2026-10-08), checked against ef51cc7d. The report is outside the repository, at
`~/RiderProjects/WoW-Farming-Rig-review/phase3/phase3-review.md` on the machine it ran on; the
plan to fix them, and the design findings, are [phase3-fixes-plan.md](phase3-fixes-plan.md).

68. **A path override that does not load leaves the bot idle under "Running".**
    - **Where:** `HeadlessServer/Runtimes/ClassicRuntime` (`TryCreate`, `Run`), `Core/BotController`
      (`LoadPathProfile`, `CreateSession`), `ServiceClients/Bots/AssignmentRules`.
    - **Cause:** `LoadPathProfile` returns nothing, and its failure is only logged. The override is
      parsed when the session resolves its `GoapAgent`, after `CreateSession` disposed the old session,
      so `GoapAgent` still points at the disposed one, and turning the bot on sets that one active.
      `TryCreate` checks only that the override exists, and the profile's own paths not at all: a
      missing one is found after the run attached and pressed the flush key, though the factory
      promises that a refusal never touched the game. `AssignmentRules` accepts names the service
      refuses (`_forever/x.json`, `%`, a segment ending in a dot).
    - **Effect:** a malformed override (a truncated local copy used while the service is down, a
      Forever route given as one) gives a run that says Running "profile X on path" while the
      character stands still until someone stops it.
    - **Fix:** `TryCreate` reads every path the run needs before it attaches and refuses one that
      does not load; `LoadPathProfile` answers whether it loaded, and `CreateSession` replaces the
      session only once the new one is built; one name rule for every name (the plan's step 3).

70. **One malformed route file takes the route library offline.**
    - **Where:** `SharedLib/Forever/ForeverRouteFile.TryParse`, `PathingService/Forever/ForeverRouteLibrary`,
      `PathingService/Sync/SyncLibrary`.
    - **Cause:** Newtonsoft reads `"Steps":[null]`, `[a,,b]` and `[,a]` as null steps without a
      `JsonException`, and `TryStep` dereferences the step: a `NullReferenceException`, which `TryParse`
      does not catch. The library describes every file in one loop.
    - **Effect:** a step deleted by hand that leaves its comma makes `GET /api/v1/forever/routes` answer
      500: the Forever routes page, the assignment form and the designer show no route at all, and the
      file cannot be opened from the page to fix it. An agent assigned the route reports Failed, with
      the exception's text, instead of Refused.
    - **Also:** a name ending in `.JSON` is accepted, but the library and the sync list `*.json`,
      case-sensitively on Linux: such a route answers "saved" and is never listed or synced.
    - **Fix:** an empty step is refused with its number; the library shows a file it cannot read as
      refused instead of failing the list; a name ends in `.json`, exactly.

71. **Names that differ only in case are one file on a Windows bot.**
    - **Where:** `BotAgent/Sync/DataSync` (its manifest is case-insensitive on Windows); the Pathing
      Service's `ForeverRouteLibrary.Save` and `RecordedPathStore.Save`; the route designer
      (`ForeverLibrary.razor`).
    - **Cause:** a Linux Pathing Service can hold `Route.json` and `route.json`; on Windows they are one
      file, and the manifest keeps the first in ordinal order. Nothing refuses a name that differs from
      another only in case, and the designer, which finds an existing route ignoring case, saves the
      name as typed.
    - **Effect:** a Windows bot assigned `route.json` pulls `Route.json`'s bytes into
      `_forever\route.json` (a correct local copy is kept aside, then overwritten) and runs the other
      route, while the assignment, the console and the history say `route.json`.
    - **Fix:** the service refuses a name that another file of its folder has in another case; the sync
      refuses a run whose named file has such a twin on the service and leaves other twins alone; the
      designer replaces a route under its stored name.

72. **The Bot Manager's registry can lose its bots.**
    - **Where:** `BotManager/Registry/JsonBotRegistry`, `BotManager/Bots/BotFleet`,
      `ServiceClients/Bots/AssignmentRules.IsAgentId`.
    - **Cause:** `Read` takes any I/O error for a damaged file: a sharing lock at startup (on Windows:
      OneDrive, a backup tool, an editor) empties the registry, the move aside fails the same way and is
      swallowed, and the first registration rewrites `bots.json` with one bot. `Save` and `Remove`
      change memory before they write, and a failed write is caught nowhere, inside the fleet's lock.
      Run reports are delivered at least once, and `AddRun` keeps a repeat. Agent text is stored and
      logged as sent.
    - **Effect:** a start that met a lock loses every other bot and assignment. A write that fails (disk
      full, a read-only folder) answers "not saved" for an assignment the manager then runs, keeps every
      agent from registering, and leaves a disconnected bot shown online. A report whose acknowledgement
      was lost is in the history twice. An agent id such as `CON` is a device on a Windows manager, and
      a run message with a line break forges a line in the manager's log.
    - **Fix:** only unreadable JSON is kept aside, and an I/O error stops the start. The write comes
      first, memory after: a registration or a disconnect whose write fails is logged and goes on, and an
      assignment answers that it was not saved and keeps the old one. One report per run. Device names
      are not agent ids; agent text is capped, and its line breaks escaped in the manager's log.

73. **One stalled Control Center freezes every agent.**
    - **Where:** `BotManager/Bots/BotFleet` (every `fleet.Clients.All.SendAsync`).
    - **Cause:** each agent hub method awaits the send to every fleet listener, and SignalR reads an
      agent's next message, the answer to a command included, only once the current method returned.
    - **Effect:** a second Control Center that sleeps, sits at a breakpoint or is on a slow link stalls
      every agent's snapshots, logs and registrations for about 10 s, and Start and Stop answer 504;
      again at each reconnect. Two changes of one bot can also reach the listeners reversed, as the
      bot's info is built outside the lock.
    - **Fix:** one send loop for the fleet hub, fed in order under the fleet's lock, as the watch hub's
      broadcaster does.

74. **Two agents with one id hide one of them.**
    - **Where:** `BotManager/Bots/BotFleet.RegisterAsync`; `BotAgent/AgentIdentity` and HeadlessServer's
      start.
    - **Cause:** "the newest wins": a second connection registering an id that is online takes it over
      silently; the old connection is neither closed nor told, and its snapshots and reports are
      acknowledged and dropped. Nothing keeps two agents with one id from running: a HeadlessServer
      folder copied to another PC (`agent_identity.json` comes with it), or two processes from one
      folder. One connection may also register a second id, leaving the first online for good, and a
      registration without a client overwrites the client last seen.
    - **Effect:** the displaced bot plays on unseen and cannot be stopped, and its run history is lost.
    - **Fix (decided 2026-10-08):** an id online from another machine is refused, naming that machine; a
      reconnect from the same machine replaces its old connection, which is closed; calls on a connection
      that is no longer the bot's fail, so the agent keeps its reports; one connection is one agent; one
      agent per id runs on a PC; a hello without a client keeps the last one seen.

75. **The key rules trust loopback too far.**
    - **Where:** `ServiceClients/Security/ServiceKeyPolicy` and `ForwardPolicy`, both services' guards,
      both forwarders; `AllowedHosts: "*"` in the four hosts' settings.
    - **Cause:** "this machine" is decided from the TCP peer alone, whatever the `Host` header says. The
      cross-site rule leaves GET alone, and a WebSocket upgrade is a GET; SignalR accepts a WebSocket from
      any origin, without negotiate.
    - **Effect:**
      - Any web page in a browser on a service's machine can open `/fleetHub` (the whole fleet's
        stream) or `/agentHub` (register as any bot, and take its commands) without the key.
      - DNS rebinding makes a page of another site "this machine" and same-origin. A probe read the
        bots, set an assignment, started a run and deleted a continent's tiles; through a forwarder,
        which adds the key, anything. Chrome 142+ and Firefox 153+ stop it today, Safari and older
        browsers do not.
      - Behind a reverse proxy or a tunnel on the same machine, or with forwarded headers turned on,
        every remote caller is 127.0.0.1.
    - **Fix:** loopback is trusted only when the `Host` names this machine (localhost, a loopback
      address, the machine's own name); a WebSocket upgrade from another site is refused as a write is;
      `ServiceKey:TrustLoopback=false` serves no caller without the key, for a service behind a proxy.

76. **The agent's run host has small races and leaks.**
    - Telemetry read a disposed runtime between a run's end and its report, so a Classic report lost its
      kills and deaths.
    - A run's report could be lost when the host stopped (the run's end was signalled before the report
      was raised), or sent twice (Stopped, then Abandoned) when the run ended as the abandon fired.
    - A Start that arrived while the host stopped was accepted, and the process exited under it.
    - `Agent:StopGraceMs` had no upper bound, and the host's 30 s shutdown timeout did not follow it:
      from about 19 s of grace the Abandoned report and the restart code were lost, from about 30 s the
      forced release too.
    - A host built again after a fault started the command line's profile again, unasked; a standalone
      run that ended before its id was recorded left the process idle.
    - An identity file that could not be read for an I/O error was replaced by a new id.
    - Each host built again resent up to 2,000 log lines, which a console then showed out of order.
    - **Fix:** in `BotRuntimeHost`, `TelemetryPublisher`, `AgentLogSink`, `HeadlessHost`,
      `StartFromCommandLine` and `AgentIdentity`; see the plan.

77. **Smaller findings of the Phase 3 review.**
    - **Agent and HeadlessServer:** `--Section:Key value` dropped a value starting with `-` (item 22);
      a standalone run started by a double-click closed its window, and the reason with it; a Forever
      runtime whose map data could not be read was Failed instead of Refused; the logon task ran the
      agent below normal priority, and its threads below WoW's; `run-agent.ps1` ended its loop when its
      log could not be written, and timed runs in local time (a DST change misjudged one backoff);
      `RunOptions`' profile is no longer `Required`, so a missing one prints one line instead of the
      usage; `headless_appsettings.Development.json` overrides a logger name nothing uses.
    - **Bot Manager and Control Center:** `BotManagerStatus` took any JSON at `/health` for the
      manager's (item 50's twin); Stop was disabled while a Start was in flight; a console's first read of
      the log or the runs that failed was never retried; a run's end left the card and the console
      showing it for up to a second; the assignment form built its draft after its first awaits, so a
      choice made then was replaced; text typed on the Forever routes page during a save was marked
      saved; the "no bots yet" hint printed the Control Center's own address of the manager; the run
      history kept every run ever shown; a bot's zone was named from the Pathing Service's client,
      whatever client the bot played.
    - **Sync:** answers were taken with no size limit, and bytes were never checked against the
      manifest's size and hash; a manifest with a null entry threw.

## Fixed

69. **A repeating route with nothing left to do spun for ever** (Phase 3 review, 2026-10-08). The task
    activity passed the tasks already done with `while (current is met) Finish(current)`, and with
    `"Repeat": true` the agenda wrapped to tasks met again at once (a `Level` the character has), so
    the bot's thread spun at full speed, wrote about four lines a lap and outlived the run.
    `ForeverAgenda.PassDone` now completes the done tasks once round at most, and a repeating agenda
    whose every task is done ends (`ForeverAgenda.End`): the run ends Complete, saying "every step of
    the route is already done". Also: `Until.Minutes` must be a finite number above 0 (NaN, Infinity
    and 1e400 passed, and the step never ended); and `ForeverRun` reads how the goal stood before it
    turns the bot off, which failed a repeating route that reached its bound with no kill ("Controller
    stopped"). Tests: `ForeverAgendaTests` (the old loop misses the deadline), `ForeverRouteFileTests`.
    The time bound's outcome needs a live run: a repeating route that reaches its bound with no kill
    ends Complete.

67. **A client probe that threw stopped the run under way** (Phase 3 review, 2026-10-08). The probe
    caught a fixed list of exceptions, and `TelemetryPublisher`, a `BackgroundService`, caught none:
    WMI failing, or WoW ending during the probe or running as administrator (`ExecutablePath` answered
    "" and `ReadInfo` then read `Wow.exe` in the working directory) faulted it, and on .NET 10 that
    stopped the host and the run, again every few seconds while the cause lasted. Now `WowGameProbe`
    answers no client for any failure and logs why once; the publisher logs a tick that fails, once
    until one succeeds, and goes on, and measures its probe interval on the monotonic clock (a wall
    clock set back held the probe); `WowProcess.ReadInfo` says it could not read the path, and why it
    may not have. Tests: `TelemetryPublisherTests` (both fail on the old publisher). Windows
    checklist: as administrator.

66. **An unattended Classic start could delete the addon's frame configuration** (Phase 3 review,
    2026-10-08). `AddWoWProcess`'s probe deleted `frame_config.json` on any mismatch with the client
    (a minimized window's client area is 0 by 0, a resized one, another addon version), and the
    addon's config and folder when the addon could not be read. BlazorServer needs that to open its
    configuration page; the agent ran it on every remote start, so one start while WoW was minimized
    refused every later Classic run on that PC until someone configured the addon again.
    `AddWoWProcess(log, keepConfiguration, out isForever, out problem)` now answers what does not match
    in words a person can act on (`FrameConfig.Mismatch`: "the WoW window is minimized: restore it",
    the two sizes, the two addon versions), and deletes only without `keepConfiguration`, which
    HeadlessServer always passes; a Classic run is refused with the reason. `--loadonly` refuses
    without both files before it looks for a client (`ClassicRuntime.MissingConfiguration`), as it
    did before Phase 3. Tests: `FrameMismatchTests`. Windows checklist: minimized.

65. **Only Ctrl+C stopped the agent cleanly** (Phase 3 review, 2026-10-08). The Generic Host's
    console lifetime handles Ctrl+C, Ctrl+Break and SIGTERM. Closing the window (CTRL_CLOSE, .NET's
    SIGHUP) was not handled, a process that loaded user32 hears no logoff, and `dotnet run` put the
    agent in a job that killed it as soon as `dotnet.exe` ended: closing the window or signing out
    left a movement key held in a game that kept running, with no report.
    - `StopOnSignals` (HeadlessServer) stops the host on SIGHUP and, on Windows, on the session ending
      (`SystemEvents.SessionEnding`), and holds the signal until the host has stopped, at most 4.5 s,
      since Windows ends the process once the handler returns.
    - `run-agent.ps1`, `run.bat` and `rundev.bat` run the newest Release build's `HeadlessServer.dll`
      with `dotnet`, never through `dotnet run`.
    - `run-agent.ps1` no longer ends its loop when its log cannot be written and times runs with a
      stopwatch; the logon task runs at normal priority. Task Scheduler's End terminates, and the
      scripts and guides say so.
    - Tests: `StopOnSignalsTests` (the hold and its bound; the real agent, sent SIGHUP on Linux, exits
      0 where it was ended with 129). Live under pwsh 7 on Linux: `kill -9` restarts the agent 5 s
      later, SIGTERM and SIGHUP end it with 0 and the loop with it. Windows checklist: the window
      closes, a logoff.

64. **A click into the agent's window froze the bot with a key held** (Phase 3 review, 2026-10-08).
    Serilog's console sink wrote synchronously, and in the Windows 10 console a click starts a
    selection that blocks every write until it ends: every thread that logged stopped at its next
    line, the bot mid-action and a Stop from the Control Center alike. The console sink is now behind
    a queue of its own (`HeadlessLogging.Queued`, `Serilog.Sinks.Async`, 10,000 lines, dropping when
    full); the file, the debugger and the agent's stream are written as before
    (`HeadlessLoggingTests`). Windows checklist: QuickEdit.

63. **A Classic run that failed to start crashed the agent, and nothing logged why** (Phase 3 review,
    2026-10-08). `WowProcess` polled on a foreground thread waiting on the handle of the token source
    it was given, which only `BotController.Dispose` cancelled: a Classic run whose controller failed
    to build (DXGI on the lock screen, at a UAC prompt, in a disconnected RDP session) disposed the
    source uncancelled, and the thread's next wait threw and ended the process, with no log line since
    the old `Program`'s crash logger went in Phase 3. `WowProcess` now polls on a `PollThread` (a
    background thread on a linked token source of its own, which `Dispose` cancels and joins), and a
    poll that throws marks the client not running; the container that built it disposes it, as do
    `AddWoWProcess`'s probe and `ForeverRun`. HeadlessServer logs an unhandled exception as Fatal
    again (`PollThreadTests`; with the old wait the test host dies). Windows checklist: a locked PC.

57. **A continent with nothing baked answered its relief with 500** (2026-10-07, found with the
    layers' browser check). `GET navmesh/{continent}/overview` built the tile graph of a folder that
    did not exist: `TileSetStamp.Of` and `NavmeshTileGraph.Build` listed it and threw. The route
    designer offered such continents ("(no tiles)") already; the layers made it common, as they show
    on continents without tiles. A missing folder is now no tiles (`TileSetStamp.FilesOf`): an empty
    relief, a graph kept in memory but not saved, and tiles that appear later count as before
    (`NavmeshApiTests.A_continent_with_nothing_baked_has_an_empty_relief`).

58. **Forever's sub-areas lay in no zone** (2026-10-07). Forever's `WorldMapArea.json` has no
    sub-area rows, so `WorldMapAreaDB.TryGetZoneOfArea` knew no town or valley: `/api/v1/area` would
    have answered uiMap 0 for one, and a quest of Northshire Valley belonged to no zone. It now walks
    the client's `AreaTable.json` where the map file has no row (`AreaTableFile`;
    `ZoneMathTests.A_forever_area_lies_in_its_zone_by_the_area_table`).

52. **A lane kept a tile missing that was on disk** (Phase 2 review, 2026-10-07; item 12's fix one
    layer too high). A disk-only `NavmeshTileCache` kept a tile it did not find in `emptyTiles` for
    its life. Item 12 answered with a lane lifecycle that only `BakeAdmin` triggered, and a cancelled
    bake closed the lanes before its tile workers stopped, so a route asked in those seconds kept the
    late tiles empty; each entry point also treated a closed lane its own way (`/area` height 0, a
    continent route Busy at once).
    - Now the cache itself looks again: a tile it did not find is checked on disk again a second
      later (`MissingTileRecheck`, one stat), whoever wrote it. The bot's own cache does the same,
      so tiles downloaded while a bot runs are used.
    - Lanes are never closed any more (`CloseLanes`, `Lane.Closed` and the retries are gone). What
      deletes or replaces tiles calls `RoutePlanner.TilesChanged`: the lane lets go of its tiles
      between queries; `BakeAdmin` calls it after a clear, and after a bake once its workers stopped.
    - The tile graph is compared with the tiles at most every 30 s, and built again once changed
      tiles have been left alone for 10 s.
    - Tests: tiles that appear count a moment later; a lane told lets go of deleted tiles; a clear
      through the API makes the planner let go (`NavmeshApiTests`).

38. **A planner lane never saw tiles that others wrote** (Phase 1 review; fixed with item 52,
    2026-10-07). Tiles from `download-navmesh.ps1`, a bot baking into the shared folder or BakeTool
    count a second after they appear.

47. **The forwarders opened the Pathing Service to whoever reached them** (Phase 2 review,
    2026-10-07; the Control Center's logins are still open, item 47). BlazorServer listens on
    `*:5000`, forwarded all of `/api/v1` from 127.0.0.1 and added the service's key, so any device
    that could open the dashboard could delete a continent's tiles, bake, or overwrite paths, zones
    and Forever routes, which the service refuses from other machines; the body-less admin POSTs
    could even come from a web page the user visited.
    - Each forwarder now applies the rules to its own caller first (`ServiceClients/Security/
      ForwardPolicy`): its machine gets anything; another machine reads, plus what the host's pages
      author (BlazorServer: paths, roads and danger zones), and anything else only with the key
      (403); a request it forwarded itself is refused (508), which stops one pointed at itself. It
      checks the browser's origin headers itself and takes them off, and adds a hop mark.
    - Both services refuse a write that a page on another site made a browser send
      (`ServiceKeyPolicy.CrossSiteRefusal`: `Sec-Fetch-Site`, else `Origin`; 403).
    - The Control Center forwards only what its browser fetches: `/api/v1`, `/swagger`, `/tiles`
      and `/path`; no longer `/watchHub` and the rest of the world data.
    - Tests: `ForwardPolicyTests`, `ControlCenterForwardTests` (a listener that echoes the headers it
      got), `ServiceKeyTests`.

48. **The bot's map could delete a zone map's zones** (Phase 2 review, 2026-10-07; two pages editing
    one zone map is still open, item 48). A save sends a zone map's whole lists, and a load that
    failed (the service down when the page opened) left none loaded, silently: drawing one circle
    then deleted the shipped roads of that zone map and replaced its danger zones, answering 204.
    Now a page that has not read the zones reads them before it saves, and refuses with a notice
    when it cannot; the read is shared between saves and dropped for a continent the map has left.

49. **The bot's map showed no refusal, failed save or "offline"** (Phase 2 review, 2026-10-07).
    `mapNotice` wrote to the console and to a toast only the Control Center had, and since 64aa9c22
    no host showed them, so a refused road stayed drawn as if saved. It now writes to the map's own
    notice line (bottom left), kept until closed; "offline" goes away when the service answers
    again, and says why (a malformed `PathingService:Url` included). Area and height lookups that
    fail say so; the teleport layer falls back to the zone's bounds, and a custom point placed while
    the service is down says its height is unknown.

50. **A health answer the Control Center did not expect ended every session** (Phase 2 review,
    2026-10-07). The client reads a member a body lacks as null, and the status bar read
    `health.Coverage`, `Bake` and `Lanes` unchecked: `PathingService:Url` set to the Bot Manager, or a
    service on an older build, ended every page at its first render. `PathingStatus` now takes only
    this version's Pathing Service health, and the API version from the capabilities; anything else
    is the service offline with why. Tested with the Bot Manager's body and a Phase 1 body. The
    Phase 3 review found the twin left open: `BotManagerStatus` takes any JSON at the manager's
    `/health` (item 77).

51. **The Control Center kept the service's facts until the service came back** (Phase 2 review,
    2026-10-07). `/world` (tile counts) and `/capabilities` (game files) were read once, so a
    continent baked from its own Navmesh page stayed out of the zone pickers until a restart. Now
    `/health` carries `Instance`, new at every start, and pages read again (`Reconnected`) when it
    changes, when the continents with tiles change, and at the next poll after a read of the world
    failed (`ServiceStatus.ReadAgain`); capabilities are read again every 30 s; offline, the status
    bar shows nothing from before; a world read that missed its places is not kept.

53. **The bake admin could wedge, and remembered a failed source** (Phase 2 review, 2026-10-07).
    `StartBake` handed the bake's token to `Task.Run`, so a cancel before the task started skipped
    the bake and its end, and every job answered 409 until a restart; one engine lived as long as
    the process and never forgot a continent whose archives failed to open once; `--bake` with
    `--bake-area` dropped the area grid; a clear that failed part-way answered 500 and skipped the
    lanes. Now a job is under way until its task has ended, however it ended; the cancel is checked
    inside the bake; each job has its own engine, let go at its end; startup jobs run in turn; a
    clear tries once more after a moment, and the planner lets go of the tiles whatever it managed.
    An area grid that skipped a continent no longer logs "extracted".

54. **A Forever service without CASC baked from another client's archives** (Phase 2 review,
    2026-10-07). `HasGameFiles` counted any archive in `Json/MPQ`, one folder for every client, and
    Forever's continents open from them, so a bake wrote 3.3.5a geometry into Forever's tiles. A
    Forever instance has game files only through `ForeverCasc` now.

55. **The watch hub kept a scene nobody viewed** (Phase 2 review, 2026-10-07). Since 64aa9c22
    nothing drew it, yet a drawing looked up every point's height on a lane and `Terrain` took the
    bake admin's one job slot. Deleted: `WatchDrawing`, `POST /watch/paths` and `/markers`,
    `Replay`, `Terrain`, the broadcaster's scene, `NavmeshTileMesh`, `TerrainMesh`,
    `BakeAdmin.TryExtractTerrain`, the planner's tile events and the map's `leaflet-signalr.js`.
    Route watchers, `POST /watch/clear` and `clear` stay.

56. **Smaller findings of the Phase 2 review** (2026-10-07; what is left is item 56 above).
    - A Busy answer reached no `RoutePlanned` watcher; it does now, from a continent route too.
    - The bot's map wrote into whatever client the service served; a write to a service of another
      era is refused with why.
    - The bot's `CostZoneWatcher` was null when `Json/road/<era>` was missing at start; it creates
      the folder and watches it.
    - NPC routes: an answer for an earlier zone or type no longer wins, and a Find click sent as
      the zone changes no longer ends the circuit.
    - A component's `Token` read first after it went away is cancelled; `MainLayout` catches a
      script that fails.
    - `LocationCatalog` and `WorldCatalog` keep no failed read; the stackalloc is out of the loop.
    - A continent map's NPCs and spawns use its own bounds (Eastern Kingdoms has Booty Bay again).
    - `PathEditor` keeps its selection through Halve.
    - The Pathing Service serves a shared-era tile folder created after it started.
    - Docs: the retry comment, navigation.md's bake endpoints, technical-challenges.md's LZ4 and
      3D view, `NavmeshTile`'s axes.

46. **The continent rule disagreed with itself** (item 46's last finding; Phase 2 review,
    2026-10-07). The cost-zone store took a continent in any case, while the bake admin and the
    navmesh routes compared ordinally: `PUT costzones/azeroth/...` answered 204 and
    `DELETE navmesh/azeroth` 400. One rule now, `ClientContinents`: the client's world continents,
    any case, answered and stored in the client's own.

2. **The planner's map routes changed frame at a gap** (the planner's half; Phase 1 review,
   2026-10-06). `RoutePlanner.WorldToMap` took each point in the map that held it, as the bot's
   `ToLocal` does, and caught the throw for a point in two maps (any point in another zone, since
   the continent's map holds it too) to keep it in the request's zone, an exception per point.
   A point no zone held, only the continent's map, came back in that map's 0-100 frame among the
   zone's. Every point is now converted in the request's zone, with no exception, so a map route
   turns back into the world route it was planned as. Tested with a route from the Eastern
   Plaguelands to a coast cliff that only Eastern Kingdoms' map holds. The bot's half is item 2.

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
    failed; `POST /costzones/reload` names it too). Both zone pages show a 409 as saved, not in
    use yet (BlazorServer's map only on the console until item 49). Reloads of a lane run one at a
    time from their read to their swap, so the one that read last applies last.

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
    - **Corrected by the Phase 3 review:** on Windows only Ctrl+C and Ctrl+Break stopped it
      cleanly; closing the window and a logoff do since item 65's fix, and the task's End terminates.

22. **Configuration keys on HeadlessServer's command line failed the parse** (Phase 3).
    `HeadlessServer/CLAUDE.md` said `--Pathing:Engine=Navmesh` reached the configuration, but
    CommandLineParser parsed the same arguments and refused the unknown option
    (`UnknownOptionError`). `HeadlessArguments` now gives `--Section:Key` arguments to the
    configuration only and the rest to the parser. The Phase 3 review found a hole: a value that
    starts with `-` (`--Navmesh:Query:EdgeMargin -1`) was dropped, or took the next key as its value
    (item 77).

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
    - The Phase 2 review found a hole: a cancelled bake closed the lanes before its last tiles
      were written. Fixed again, one layer down, in the tile cache: item 52, which replaced
      `CloseLanes`.

13. **File names were not checked** (Phase 2, 32ce9d46). `Frontend/Controllers` are deleted: the
    map authors through the Pathing Service, which refuses such names (`PathName`) and takes only
    plain continent names.
    - The old `PathController.SavePath` joined any name onto `Json/path`, so `../` wrote
      anywhere; and since its listing returned `/path/<name>`, saving a path the map had loaded
      wrote to `Json/path/path/<name>` (or failed for want of the folder).

14. **Cost zones were not validated on save** (Phase 2, 32ce9d46). The service validates with
    `CostZoneLoader.Problem` and answers 400 with the reason, which the map shows on its notice
    line (it reached only the console from 64aa9c22 until item 49).

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
    The forward opened the service's whole API to whoever reached the dashboard until item 47.

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
