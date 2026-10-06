# Phase 1 review - the fixes: plan and handoff

**Status (2026-10-06):** step 1 under way on `fix/phase1-review`; done: 1.1 to 1.8 (1.8 without the writer's side, now in step 3), and of the independent items, 42. A max-level review of Phase 1
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

- **Item 36, the client misreads failures.**
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
- **Item 2, the planner's map frame.**
  - `RoutePlanner.WorldToMap` (`:840-851`) converts every point in the request's zone
    (`WorldMapAreaDB.ToMap_FlipXY(world, area)`), which is what its catch already does. That also
    ends the exception thrown for each point (item 46).
  - Trap: `RoutePlannerParityTests` compares Map-space routes with the bot's `ToLocal` (`:79`). A
    corpus route with a point in a gap between zones would now differ there, where the bot is the
    one that is wrong; the bot side is Phase 4.
- **Item 46, the rest:**
  - check a continent name against the client's continents (case-insensitive, answering with
    the canonical case) rather than letters and digits only;
  - refuse `%` in a path name (no file in `Json/path` has one).
- **Item 45, the docs:**
  - the endpoint table: `BakedContinents` and `GameFiles` (reload's 204 or 409 went in with 1.5);
  - narrow rule 4 to what holds today, disk-only Classic installs, until step 3.

## Step 2 - review Phase 2

- **Run** `/code-review max 7ef44187..4e65fd27` (31698e77, 32ce9d46, 4e65fd27).
- **Scope:** about 7,100 lines of code were added.
  - About 1,200 were Control Center pages the world view has since replaced: `PathEditorPage`,
    `PathLibrary`, `SearchPanel`, `Viewport3D`, `Map.razor`, `WatchNames.cs`.
  - About 600 are the map scripts moving to `MapAssets`.
  - Point the review at what survives: 31698e77 (`BakeAdmin`, world data, the catalog, the watch
    hub and `WatchBroadcaster`, `CloseLanes` and the planner's events), and the PathingAPI
    deletion (did anything still used go with it?).
- **Check each finding** against the code after step 1, and against known issues 1 to 46.
- **What the Phase 1 run taught:**
  - Run at most 5 verifier agents at a time: 23 at once hit API errors.
  - Have every agent write its verdict to a file as it goes: a usage limit cut the first run.
  - Keep the reviewer's single report file as the one record.
- **Afterwards:** add the findings to known-issues, and update step 3 here.

## Step 3 - the structural fixes, after step 2

- **Item 35, blocking waits:**
  - `WaitAsync` behind async endpoints;
  - `/area` answers 503 when busy;
  - bounded waits in `CloseLanes` and the reloads.
  - It touches the watch hub (`WatchDrawing`, which asks once per point) and `BakeAdmin`, from
    Phase 2.
- **Item 38, tiles that others write:**
  - watch the tile folder, or its newest write as `CoverageCache` does, and close the lanes it
    touches;
  - correct the comment over `RoutePlanner.GetLane`, `PathingService/CLAUDE.md`, and
    `RoutePlannerWatchTests`' assertion.
- **Item 44, height lookups:** take the read lock, and wait for the point's own tile.
- **Item 40, Forever parity:** first a decision for the user (below). Then resolve Forever
  endpoints the way the bot does (`TryResolveColumn`, `PreferGroundUnderPoint`).
- **The rest of item 45:** rule 4 needs a parity test for installs with archives, or keeps the
  narrowed claim.
- **The rest of item 46:** NotBaked on an empty tile, and `GET /costzones` against what the
  loader applies.
- **Writers:** bakes and the download script write tiles under a temporary name and rename them,
  if 1.8 left that out.

## Decisions for the user

1. **Item 34:** the content check in the store, or a folder of their own for the path groups.
   **Decided (2026-10-06): the content check.**
2. **Item 41:** answer 409 after the write, or refuse a save whose continent would not load.
   **Decided (2026-10-06): 409 after the write.**
3. **Item 40:** do Forever routes get cost zones at all? Today the planner applies them and the
   bot does not. Open; it can wait until step 3.
