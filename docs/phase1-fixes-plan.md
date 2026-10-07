# Phase 1 review - the fixes: plan and handoff

**Status (2026-10-07):** step 1 is done and merged into `main` (0ec0be56): 1.1 to 1.8, 1.8
without the writer's side, and every independent item (42, 36, the rest of 46, the planner's half
of 2, 45's text); see "Step 1 as built". Step 2, the review of Phase 2, is done too: it found
items 47 to 56 of known-issues and corrected items 12, 14, 19, 35, 38 and 41 (see "Step 2 as
run"). The user decided 4 to 7 the same day, and the Phase 2 review's items are fixed on
`fix/phase2-review` (not committed yet): see "Step 3, the Phase 2 items, as built". Next: the rest
of step 3. A max-level
review of Phase 1
(84c93499..7ef44187, the Pathing Service, the planner, the contracts, the pathing client and
`ServiceTests`), checked against the code as of e71c5781, found what is now items 32 to 46 of
[known-issues.md](known-issues.md). It also corrected items 2 and 12 and the floor-memory design
note there. This file holds the order of work agreed with the user and, for each fix, where it
lives, the fix, its test and the traps. The review's full report (every verdict, probe and
location) is outside the repository, at `~/RiderProjects/WoW-Farming-Rig-review/phase1/phase1-review.md`
on the machine it ran on. Line numbers below are as of e71c5781.

## The order (agreed 2026-10-06)

1. **Step 1: the urgent fixes that stand alone** (below). They lose data or wedge the service
   today, and nothing in Phase 2 changes how they are fixed.
2. **Step 2: review Phase 2**, scoped to the code that survives.
3. **Step 3: the structural fixes**, with both reviews in hand. They sit in the code Phase 2
   extended (`CloseLanes`, `BakeAdmin`, the watch hub), so they are designed once.

## How to work

- Branch from `main` (`fix/phase1-review`), one commit per fix. Write messages like the log's
  ("Routes say when they stop short, ..."), with the co-author line.
- Every fix gets a test in `ServiceTests` (plain `net10.0`, runs on Linux):
  `dotnet test --project ServiceTests`. Tests that need the baked precata navmesh skip themselves
  without it. Build the solution before pushing (`dotnet build WoWFarmingRig.sln`).
- **A test for a hang must time out, not hang.** Give each call a short timeout and assert that
  it came back. Otherwise a regression wedges the test run, and the spinning thread stays in the
  test process.
- Limits are `public const` fields in the class that owns them (root `CLAUDE.md`).
- `RoutePlannerParityTests` must stay green: for its 14 routes the planner returns exactly the
  bot's points. Anything that changes what the bot walks needs a live test, and is not in step 1.
- `PPather` changes reach the bot too (`NavmeshTileCache`, `NavmeshPathfinder`). Say in the commit
  what the bot sees.
- When a fix lands, move its known-issues item to "Fixed" with the commit, as that file asks. If
  a fix covers only part of an item, rewrite the item to say what is left.
- Don't patch DotRecast (the submodule) for item 32: bound the inputs at our own boundary.

## Step 1 - the fixes

### 1.1 One request hangs a continent's lane for good (item 32) - done

- **Reach, checked:**
  - `GET /area` (`PathEndpoints.cs:44-53`) goes to `RoutePlanner.GetAreaAndHeight` (`:489-515`),
    then `NavmeshPathfinder.TryGetHeight`, then DotRecast's `QueryPolygons`.
  - `WatchDrawing.ToWorld` (`:67`) makes that call once per point of a POST body.
  - `edgeMarginYards` reaches it inside `FindPath` (`TryPushFromEdge`, `TrySnap`, `FindNearestPoly`).
  - Route endpoints are safe: `Find` answers NotBaked from `lane.HasTile` before it searches.
  - `jitterYards` does not hang, but 100000 cost 35.8 s on one route while holding the lane.
- **Fix:**
  - A world bound: |x| and |y| at most `ChunkReader.ZEROPOINT` (17,066.67 yards, the edge of the
    world grid; no tile exists beyond it). Check world points, after any map conversion:
    - `GetAreaAndHeight` answers zeros beyond the bound and never queries;
    - `/area` and `WatchDrawing.Problem` answer 400, so callers learn.
  - Caps on the tuning, as `public const` fields on `NavmeshQueryTuning`
    (`PPather/Navmesh/NavmeshQueryMemory.cs`), so `RouteHandler` and the planner share them.
    - Suggested: 10 yards each. Jitter defaults to 0 and the edge margin to 1.5
      (`Navmesh:Query:EdgeMargin`); the tests use 3 at most; no Control Center page sends either.
    - `RouteHandler.Problem` answers 400 above a cap, and `RoutePlanner.Find` refuses it too, so a
      direct caller cannot get past it.
- **Test:** each of these answers 400, with a short timeout on the call:
  - `/area?mapId=0&x=0&y=3e11`;
  - a route with `edgeMarginYards` 1e12, and one with `jitterYards` 1e5;
  - a drawing with a point at 3e11.
- **Trap:** the hang needs a lane (baked tiles) to reproduce; the regression test does not, since
  it asserts the refusal, which comes first.

### 1.2 The path store edits the class profiles' path groups (item 34) - done

- **Facts:**
  - `Json/path` holds 546 point arrays and 8 group files: `1-8_*.json`, objects with
    `Requirements`, `Generate` and `SideActivityRequirements`, with `//` comments.
    `1-5_Gnome.json` is a real path.
  - `RecordedPathStore.Get` (`:70`) reads with Newtonsoft as `Vector3[]`, which skips unknown
    members, so a group comes back as (0,0,0) points.
- **Fix (decided):** the store recognises a file that is not a recorded path, by its content,
  and leaves it alone.
  - A cheap sniff: read with a `JsonTextReader` (comments allowed) up to the first property name
    of the first element. `X`, `Y` or `Z` (any case) means a path; anything else, or not an array,
    means not a path.
  - `List` skips such a file, and `Get` answers Unreadable ("... is a PathSettings group, not a
    recorded path"). `Save` and `Delete` refuse when the existing file is not a path.
    - As built: `Get` answers 400 (Invalid), like a `_forever/` name, rather than Unreadable's
      500, which the client retries.
  - The route library and the assignment form list through the store, so they stop offering
    the groups.
- **Alternative (not chosen):** move the groups into a folder of their own, as `_forever` is.
  That touches `ClassConfiguration`, every 1-10 profile's `PathsFilenames` and
  `docs/class-profiles.md`.
- **Test (`RecordedPathStoreTests`):** use a copy of a real group file, comments included.
  - It is not listed, `Get` answers Unreadable, and `Save` and `Delete` refuse.
  - The file stays byte-identical, and a normal path still round-trips.
- **Trap:** `List` runs on every refresh of the library. Read only the start of each of the 546
  files, not the whole file.

### 1.3 A cost-zone file can pass validation and hang every load (item 33) - done

- **Facts:**
  - `CostZoneLoader.Problem` (`:156`) caps width (64), points per segment (50,000), coordinates
    (`CoordLimit`, ±40,000) and a shape's size (`ExtentLimit`, 5,000 yards). It does not cap a
    leg's length, or how many segments and shapes a file holds.
  - `CostZones.MarkLeg` (`:213`) visits every 16.67-yard cell of the leg's bounding box, grown by
    the influence (the larger of `Width × 33.33` and `8 + 33.33`). `RasterizeBox` does the same
    for circles and rectangles.
- **Measured:**
  - The 67 zone files in `Json/road` rasterize 592,395 cells in all.
  - The largest file is 31,525 cells (`precata/Northrend/116.json`); the worst single leg is 550.
  - The hostile file in the item is about 1.15e12.
- **Fix:**
  - In `Problem`, count the file's cells exactly as the rasterizer walks them: per leg,
    `(floor(w / step) + 1) × (floor(h / step) + 1)` with the influence added; per shape likewise.
  - Refuse a file above a `public const` cap. Suggested: 2,000,000 cells, 63 times today's
    largest file and a fraction of a second.
  - The loader validates with the same `Problem`, so a file already on disk over the cap is
    skipped with a warning instead of hanging the lane, the watcher and the bot.
- **Test:**
  - A two-point road from (-40000, -40000) to (40000, 40000) of width 64 is refused, with a
    reason naming the road.
  - Every file under `Json/road` still passes: a test that walks the folder.
- **Trap:** the bot loads the same files through the same loader, so the cap must stay far above
  anything real.

### 1.4 The pathing client retries a busy service (item 37) - done

- **Where:** `PathingClientRegistration` (`:57`). `BotManagerClientRegistration` (`:43`) calls
  `DisableForUnsafeHttpMethods`, but that would also stop retrying a route POST that never
  reached the service, which is safe to repeat.
- **Fix:**
  - Set `options.Retry.ShouldHandle` to retry only what a retry can fix: the request did not
    reach the service (`HttpRequestException`), or 502 or 504.
  - Not 500: it is deterministic (Unreadable, a planner exception).
  - Not 503: the lane already waited 10 s.
  - Not the attempt timeout: it would run a long search twice.
- **Also:** in `RoutePlanner.Find`, check the token right after taking the gate, and release it
  and return when the caller has gone. Then an attempt the client gave up on does not keep the
  lane. The endpoint already passes the request's token
  (`RouteEndpoints` → `RouteHandler.Find` → `planner.Find(query, token)`).
- **Test:** a client from `AddPathingClient` over a stub handler
  (`ConfigurePrimaryHttpMessageHandler`), counting attempts: a 503 is tried once, a 500 once,
  and an `HttpRequestException` 3 times.
- As built: the circuit breaker takes the same rule (plus attempt timeouts), so a busy continent
  no longer opens it for every call; tested with 120 503s and then a route that is found.
- Item 30 (the log noise) shrinks with this, but stays its own item.

### 1.5 A cost-zone save answers 204 when the reload failed (item 41) - done

- **Where:** `CostZoneEndpoints.Saved`, `RoutePlanner.ReloadCostZones` (`:522-555`).
- **Fix:**
  - Reload only the saved continent (today every open lane reloads on every save). Return what
    failed: the loader knows the file and the problem, but now only logs them.
  - `Saved` answers 409 with a detail that says the file was saved, but the continent's zones did
    not reload because `<file>: <problem>` (decided).
    - Alternative (not chosen): check before writing that the continent would load, and refuse
      with 400. A save then never lands half applied, but one old bad file blocks every edit
      there (by name) until it is fixed.
  - Overlapping reloads: serialize them per lane with a lock taken before `TryLoad`, so the
    reload that read last applies last.
- **Test:** a continent folder with one invalid road file (width 100). A valid PUT for another
  map answers 409 naming that file; once the file is fixed, the next save applies.
- **Trap:** two pages save zones: the world view's `ControlCenter/Components/Pages/CostZones.razor`
  and BlazorServer's map (`MapAssets/wwwroot/script/leaflet-zones.js`). Make sure both show a 409's
  detail.

### 1.6 Windows lets `_forever./x.json` past the guard (item 43) - done

- **Where:** `PathName.IsPlainSegment` (`:110`). `Trim()` already refuses surrounding spaces.
- **Fix:** refuse a segment that ends in `.`. Windows drops a single trailing period from any
  segment, and trailing periods and spaces from the last one.
- **Test (runs on Linux):** `_forever./x.json`, `a./b.json` and `x.json.` are refused;
  `a.b/c.json` is accepted.

### 1.7 A null cost-zone element answers 500 (item 46, first bullet) - done

- **Where:** `CostZoneMapping.ToModel` (roads `:36-58`, and danger zones) and `UnknownMode`
  (`:115-133`).
- **Fix:** a null element is a 400 ("roads[3] is missing"), checked before mapping.
- **Test:** PUT `{"roads":[null]}` and `{"circles":[null],"rectangles":[]}`; both answer 400.
- As built: a request the service cannot bind (a null point, broken JSON, `x=abc`) answered 500
  too, in Development, where `run.sh` runs it; the exception handler now answers its own 400.

### 1.8 A disk-only tile cache deletes a tile it cannot read (item 39) - done, but the writer's side

- **Where:** `NavmeshTileCache.LoadOrBake` (`:482-499`) deletes the file. The caller (`:417-424`)
  then records the tile in `emptyTiles` for the cache's life. The bot shares this code.
- **Fix:** delete only when the cache can bake (`world != null`). A disk-only cache logs, and
  answers "unreadable", distinct from "absent". The caller does not add an unreadable tile to
  `emptyTiles`, so a later request reads it again once its writer is done.
- **Test:** a disk-only cache over a folder with a truncated `.dnm`. After a request the file is
  still there; once a good copy replaces it, a later request loads it.
- **Optional, the writer's side:**
  - bakes write `<tile>.tmp` and rename;
  - `download-navmesh.ps1` extracts to a temporary folder and moves it into place.
  - The bytes don't change, but run the bake gate if the bake's writer changes
    (`PPather/CLAUDE.md`).
  - As built: left for step 3. The gate needs game files, which the Linux machine this ran on
    does not have; known issue 39 stays open for it.

### Also independent: in step 1 if it goes quickly, else in step 3

- **Item 36, the client misreads failures.** Done.
  - `ServiceHttp.TryReadAsync` buffers the body once (`LoadIntoBufferAsync`), so the error path
    can read it again.
  - `FindRouteAsync` takes a `RouteResponse` only from `application/json` (never
    `problem+json`), with a defined status, and with points when it is Found or Partial. Anything
    else is mapped by status code.
  - Non-finite points are a BadRequest before sending.
  - Tests (stub handler):
    - a ProblemDetails 500 or 401 gives Unavailable with its detail, never a route;
    - a 200 with an unknown status gives a failure with the body's text, not "could not be
      reached".
- **Item 42, the floor-isolation test cannot fail.** Do this one before step 3 reworks the
  lanes, because it is that rework's guard. Done: the test reads the lane's `OwnMemory`.
  - Make the test able to fail. For example: give `ServiceTests` access to PPather's internals
    (there is no `InternalsVisibleTo` yet), expose the lane pathfinder's own memory, and assert
    that planner queries leave it untouched.
  - Prove it by mutation: with `Find` calling `nav.FindPath(from, to, null)`, the test must fail.
  - Fix `PathingService/CLAUDE.md`'s pointer to these tests.
- **Item 2, the planner's map frame.** Done; the parity corpus stays in its zones, so it holds.
  - `RoutePlanner.WorldToMap` (`:840-851`) converts every point in the request's zone
    (`WorldMapAreaDB.ToMap_FlipXY(world, area)`), which is what its catch already does. That also
    ends the exception thrown for each point (item 46).
  - Trap: `RoutePlannerParityTests` compares Map-space routes with the bot's `ToLocal` (`:79`). A
    corpus route with a point in a gap between zones would now differ there, where the bot is the
    one that is wrong; the bot side is Phase 4.
- **Item 46, the rest:** done.
  - check a continent name against the client's continents (case-insensitive, answering with
    the canonical case) rather than letters and digits only;
  - refuse `%` in a path name (no file in `Json/path` has one).
- **Item 45, the docs:** done; the item stays open for a parity test with archives.
  - the endpoint table: `BakedContinents` and `GameFiles` (reload's 204 or 409 went in with 1.5);
  - narrow rule 4 to what holds today, disk-only Classic installs, until step 3.

## Step 1 as built (2026-10-07)

One commit per fix on `fix/phase1-review`, after the plan's own (0f4d61b8): 1.1 262a0efc, 1.2
b2a83a72, 1.3 d30a3348, 1.4 0cd22047, 1.5 34acba4d, 1.6 48435013, 1.7 ecd69a42, 1.8 478333b2;
item 42 d831c02d, 36 cbea7ac9, 46 c6d2f9d1, 2 e9e61919, 45 1a3c8cac. `ServiceTests` grew from 301
to 351 tests, all passing with the precata tiles present; `AgentTests` (50) and the solution
build pass. Every new test was run once against the old code (or with the guard taken out) and
failed there; the tests for hangs did so by their deadline, not by hanging.

Beyond the plan, found on the way:
- In Development, where every `run.sh` runs its service, minimal APIs throw on a body or a query
  they cannot bind, and `UseExceptionHandler` made that a 500, which clients took for the
  service's own failure. The Pathing Service answers 400 now (with 1.7); the Bot Manager has the
  same handler (item 46).
- `PathingClient.GetAreaAsync` wrote 3e11 as `3E+11`, whose `+` a query reads as a space (with 1.1).
- The circuit breaker counted a busy continent as the service failing; it takes the retry's rule
  now (with 1.4).
- `Get` of a path group answers 400, like a `_forever/` name, not Unreadable's 500 (1.2).
- Both zone pages took a 409 as "not saved"; they show it as saved, not in use yet (1.5).
- The old map-frame conversion only went wrong at gaps, where the continent's map alone holds a
  point: a point in another zone is in two maps, threw, and was caught into the right frame (2).

Moved to step 3: the tile writers (item 39, open); item 46's uiMap bound and the Bot Manager's
500.

## Step 2 - review Phase 2 (the handoff)

To start, in a new session: "Read docs/phase1-fixes-plan.md, Step 2, and run it." The review is
`/code-review max 7ef44187..4e65fd27`. Step 1 is merged: `main` is at 0ec0be56 on origin.

**Phase 2 is three commits of 2026-10-05:**
- 31698e77, Pathing Service: world data, catalog, bake admin and the watch hub (5,083 lines added).
- 32ce9d46, Control Center replaces PathingAPI; the map's assets move to `MapAssets` (3,749 added,
  9,581 deleted).
- 4e65fd27, the docs for both.

**Review the code that survives at HEAD.** Lines still from Phase 2 by `git blame`, of the file's
length now:
- The Pathing Service, the heart of it:
  - `Navmesh/`: `BakeAdmin` (412 of 412), `StartupBakes` (70/70), `ForeverGameFiles` (63/63),
    `NavmeshEndpoints` (87/128).
  - `Watch/`: `WatchBroadcaster` (328/408), `WatchHub`, `WatchEndpoints`, `WatchDrawing`,
    `NavmeshTileMesh`, `TerrainMesh`.
  - `World/`: `WorldCatalog` (162/324), `WorldDataFiles`, `WorldEndpoints`; `Catalog/`:
    `LocationCatalog`, `TestRoutes`, `CatalogEndpoints`; `Status/CoverageCache`.
  - `PPather/Planning/RoutePlanner` (253/965): `CloseLanes`, a lane's `Closed`, the
    `RoutePlanned`, `TileLoaded` and `TileUnloaded` events, `ResidentTiles`.
  - Smaller parts: `PPatherService` (+32), `DataConfig` (+26), and the contracts
    (`WatchContracts`, `WorldContracts`, `NavmeshContracts`, `WorldData`, `PathingApi`).
- The Control Center's plumbing from 32ce9d46: `Hosting/ServiceProxy` (forwards with the key),
  `ControlCenterRegistration`, `Pathing/PathingStatus`, `Pathing/WorldCache`,
  `Layout/StatusBar.razor`, `Pages/NpcRoutes.razor` (189/330), `ServiceClients/Pathing/ZoneMath`,
  the client methods it added, and BlazorServer's forward of `/api/v1` (`BlazorServer/Program.cs`,
  `MapForwarder`).
- The deletion of PathingAPI and `Frontend/Controllers`: did anything still used go with them (a
  route, a script's call, a setting, a doc)?

**Skip:** about 1,200 lines of Control Center pages the world view has replaced (`PathEditorPage`,
`PathLibrary`, `SearchPanel`, `Viewport3D`, `Map.razor`, `WatchNames.cs`), about 600 lines of map
scripts that only moved to `MapAssets`, and the docs except where they promise behaviour. Read the
tests and `Benchmarks/PathingServiceBenchmark.cs` (496 lines) only as far as they guard the code.

**Check every finding at HEAD.** 39 commits came after 4e65fd27, and 18 of them touch these files
(Phase 3, the world view, step 1). Step 1 changed Phase 2 code itself:
- `WatchDrawing` and `WatchEndpoints` refuse a point beyond the world (1.1);
- a lane reloads its zones per continent and one reload at a time (1.5);
- `WorldToMap` converts in the request's zone (item 2);
- an unreadable request answers 400 (1.7);
- the client reads and retries differently (36, 1.4).

**Already known: cite, don't report again.**
- Open: 35 (blocking waits: `WatchDrawing` asks once per point, `BakeAdmin`, `CloseLanes` with no
  limit), 38 (only `BakeAdmin` closes lanes; tiles others write), 39 (bakes write tiles in place),
  44 (height lookups without the tile lock, which `/area` and `WatchDrawing` use), 46 (a lane built
  under the global lock, NotBaked on an empty tile, `GET /costzones`), 30 (the log).
- Fixed by Phase 2 itself: 8, 11, 12, 13, 14, 18, 19, 20. A finding against one is a regression.

**How to run it, from what the Phase 1 run taught:**
- Reuse its setup in `~/RiderProjects/WoW-Farming-Rig-review/phase1/`. `COMMON.md` gives every
  agent the range, a snapshot of the tree at the range's end and the rules (no changes to the
  repository, no builds, scratch only); `VERIFY.md` gives a verifier one candidate and a fixed
  block to return (verdict at the target, HEAD status, known-issues item, severity). Copy both to
  `.../phase2/`, and change the range to 7ef44187..4e65fd27, the snapshot to 4e65fd27, HEAD to
  the current `main` (0ec0be56 for the code), the known issues to 1 to 46, and the scratch path to
  the new session's.
- At most 5 verifier agents at a time: 23 at once hit API errors.
- Every agent writes its verdict to `phase2/parts/` as it goes: a usage limit cut the Phase 1 run
  with 18 verifiers unfinished.
- One report, `phase2/phase2-review.md`: live findings first, most severe first, then those fixed
  since; line numbers at 4e65fd27 unless marked HEAD.

**Afterwards:**
- Add the live findings to known-issues from 47, in its style (Where, Cause, Effect, Fix), and
  correct any item the review shows to be wrong.
- Update step 3 here with what the review adds, and add any decisions for the user.
- Commit when the user asks. A fix waits for step 3 unless it is urgent and stands alone, as
  step 1's did.

## Step 2 as run (2026-10-07)

- **The run:** `/code-review max 7ef44187..4e65fd27`, with ten finder angles and a coordinator's
  pass of its own: 48 deduplicated candidates (V1-V48), verified at most 5 agents at a time.
  - A usage limit stopped the first run at 01:23 with four verify batches done; the rest ran after
    the reset.
  - Every agent's notes and verdicts are in `~/RiderProjects/WoW-Farming-Rig-review/phase2/parts/`.
  - The report is `phase2/phase2-review.md` beside them: 39 live findings, most severe first, the
    design findings, the HEAD-only and moot ones, and the refuted ones.
- **Severity:** nothing high. Five medium:
  - BlazorServer's forward opens the Pathing Service's whole API to the LAN, with the key since
    Phase 3 (item 47);
  - the bot's map deletes a zone map's zones when it saves after a failed load (item 48);
  - the bot's map shows no refusal, failed save or "offline" (item 49);
  - a health body the Control Center does not expect ends every session (item 50);
  - the Control Center keeps tile counts until the service comes back (item 51).
- **The rest is low.** The bake admin's wedge and its memo (53), a cancelled bake's late tiles (52),
  Forever baking from `Json/MPQ` (54), and the smaller findings (56).
- **What the deletions took:** the bot's own cost-zone reload and its own area lookup, and
  `--bake` with `--bake-area` together. No script call broke; the docs kept two stale names.
- **Design findings** (the altitude angle), which shape step 3:
  - item 12's fix belongs in the tile cache, not in a lane lifecycle (item 52);
  - `BakeAdmin` wants a job object (item 53);
  - the forwarders need the key rule themselves (item 47);
  - the Control Center should read what changes from `/health` (item 51);
  - the watch hub's scene half has had no viewer since 64aa9c22 and can be deleted (item 55);
  - the continent rule exists three times (item 46).

## Step 3, the Phase 2 items, as built (2026-10-07)

On `fix/phase2-review`, from `main` at 0ec0be56; `dotnet test` 436 passed (401 before). Each item
is in known-issues' "Fixed" with what changed; in short:

- **Item 55 (decision 5):** the watch hub's scene is deleted, the route watchers kept. That took
  `WatchDrawing`'s share of items 35 and 44, `BakeAdmin`'s terrain job and the planner's tile events
  with it, and the map's `leaflet-signalr.js`.
- **Items 38 and 52 (decision 6), in `NavmeshTileCache`:** a disk-only cache looks again for a tile it
  did not find a second later (one stat); `Reset` lets go of everything, and a tile read before it is
  not added after it. `CloseLanes`, `Lane.Closed`, the retries and the closed-lane checks are gone:
  `RoutePlanner.TilesChanged` resets a lane's cache under its gate, and drops the tile graph, which
  is also compared with the tiles every 30 s. The bot uses tiles downloaded while it runs.
- **Item 53:** `BakeAdmin`'s jobs (bake, area grid, clear), one at a time, each on an engine of its
  own; `StartBake` no longer hands its token to `Task.Run`; startup jobs in turn; a clear retries
  once and always tells the planner. **Item 54:** Forever has game files only through `ForeverCasc`.
  **Item 46's continent rule:** `ClientContinents`, for the store, the navmesh routes and the admin.
- **Item 47 (decision 4):** `ServiceClients/Security/ForwardPolicy`, applied by BlazorServer's forward
  and the Control Center's proxy to their own callers, and `ServiceKeyPolicy.CrossSiteRefusal` in
  both services and both forwarders. Not built: logins on the Control Center (item 47, open).
- **Items 48 to 50 (decision 7):** the bot's map saves no zone map it could not read, shows every
  notice on its own notice line, and refuses writes to a service of another era; the Control Center
  takes only the Pathing Service's own health. The lost update between two pages waits for the
  database (item 48, open).
- **Item 51:** `PathingHealth.Instance`; pages read again on a restart, on a change of the continents
  with tiles, and after a failed world read (`ServiceStatus.ReadAgain`); capabilities every 30 s.
- **Item 56:** each smaller finding, with a test where one can fail; what is left is item 56, open.

## Step 3 - what is left

- **Item 35, blocking waits:**
  - `WaitAsync` behind async endpoints;
  - `/area` answers 503 when busy;
  - bounded waits in `TilesChanged` and the reloads.
- **Item 44, height lookups:** take the read lock, and wait for the point's own tile.
- **Item 40, Forever parity:** first a decision for the user (below). Then resolve Forever
  endpoints the way the bot does (`TryResolveColumn`, `PreferGroundUnderPoint`).
- **The rest of item 45:** rule 4 needs a parity test for installs with archives, or keeps the
  narrowed claim.
- **The rest of item 46:** NotBaked on an empty tile, `GET /costzones` against what the loader
  applies, a lane built under the global lock, a save's uiMap checked against the continent's
  maps (keeping uiMap 0), and the Bot Manager's 500 for a request it cannot read.
- **Writers (item 39):** bakes and the download script write tiles under a temporary name and
  rename them; 1.8 left this out. Run the bake gate, which needs game files.
- **With the central hosting plan:** logins and roles on the Control Center (item 47), and versioned
  saves of authored data once it moves to a database (item 48).

## Decisions for the user

1. **Item 34:** the content check in the store, or a folder of their own for the path groups.
   **Decided (2026-10-06): the content check.**
2. **Item 41:** answer 409 after the write, or refuse a save whose continent would not load.
   **Decided (2026-10-06): 409 after the write.**
3. **Item 40:** do Forever routes get cost zones at all? Today the planner applies them and the
   bot does not. Open; it can wait until step 3.
4. **Item 47:** may the bot's map, from the LAN, reach the Pathing Service's admin calls (delete
   tiles, bake), or only what it authors (paths, zones)? Or must every LAN caller of a forwarder
   carry the key, as the services ask? **Decided (2026-10-07): only what it authors.** The
   dashboards run on every PC for now; later the Control Center is hosted centrally and everyone
   connects to it, with an admin role for what can do damage and users for the rest, and only the
   input simulation runs on the PCs. Not built yet.
5. **Item 55:** delete the watch hub's scene half, which nothing has viewed since 64aa9c22, or keep
   it for a viewer to come? **Decided (2026-10-07): delete it; the route watchers stay.**
6. **Item 52:** fix item 12 in the tile cache (a missing tile is looked at again once its file
   appears), which the bot shares, instead of the lanes' lifecycle. **Decided (2026-10-07): in the
   cache.** The bot uses tiles downloaded while it runs.
7. **Items 48 to 50:** fix them now, each standing alone as step 1's did, or with step 3?
   **Decided (2026-10-07): fixed now, with the rest of the review's items.** Item 48's lost update
   waits for the database the authored data is to move to.
