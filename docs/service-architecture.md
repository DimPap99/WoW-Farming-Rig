# Service architecture - design

**Status:** agreed 2026-10-05. Phase 0 is done and Phase 1 (the Pathing Service) is built; see
"Phase 1 as built".
**Scope:** how the parts of the rig talk to each other, what each one serves, and where things
are viewed. The bot's play logic (goals, GOAP, readers, input, navigation, the Forever
activities) is deliberately out of scope and does not change.

## Why

Today each WoW client gets one Windows process that is both bot and UI (`BlazorServer` or
`HeadlessServer`). Forever runs through `Utilities/ForeverControllerTrial`, which
`Frontend/ForeverRunHost` starts as a stdio child. `PathingAPI` is a second web app with its
own copy of the map pages and the authoring endpoints. That has costs:

- **The UI depends on the game.** `AddWoWProcess` throws when no client is running, so the
  dashboard cannot even start without one.
- **PathingAPI serves one caller at a time.** It answers HTTP 429 when busy, and switching
  continent disposes the loaded engine.
- **Everything exists twice.** Two hosts, two layouts, two map pages, two copies of
  `teleport_locations.txt`, and the road, danger-zone and path controllers mounted in both.
- **A run's lifetime is tied to its host.** `ForeverRunHost --listen` stops the run when the
  host's pipe closes.

The aim is a product that is more robust and easier to work in: services that each own clear
data, one central place to watch and author, and bots that only play and keep playing while
the management side is down.

## Decisions

1. **One bot per PC.** This matches the code: Forever steers with the mouse and needs the
   foreground window. Each game PC, or later each VM, runs exactly one resident **Bot Agent**.
2. **Bots plan and steer locally.** With one bot per machine nothing is duplicated, and a warm
   navmesh query costs about 1-8 ms in process against a network hop. Navigation also keeps
   working when every service is down. Local mode stays the default.
3. **The Pathing Service is the authoring API.** It holds paths, route files, cost zones, bakes
   and tiles, and it plans routes for the UI. Remote planning for a bot is optional, for
   example an agent without local tiles (Phase 4).
4. **Three central services**, one instance each, on any OS: Pathing Service, Bot Manager and
   Control Center. They are separate processes so one being down never stops another.

## Components

```
 Browser
    │
 ┌──▼────────────────────────────────────────────┐
 │ Control Center (Blazor)                        │   central: one instance
 │ fleet · per-bot console · map · route editor · │   each, any OS
 │ library · bakes                                │
 └──┬───────────────────────────────┬────────────┘
    │ REST + SignalR                │ REST + SignalR (+ tiles/data proxied)
 ┌──▼──────────────────────┐   ┌────▼──────────────────────────┐
 │ Bot Manager             │   │ Pathing Service               │
 │ registry · assignments ·│   │ authoring API: paths, routes, │
 │ commands · live state   │   │ cost zones, bakes, tiles ·    │
 └──▲──────────────────────┘   │ planning for the UI           │
    │ SignalR, agent dials out └────▲──────────────────────────┘
    │                               │ REST: pull assigned routes, cost
    │                               │ zones, tiles (cached locally)
 ┌──┴───────────────────────────────┴──┐
 │ Bot Agent: one per game PC (or VM), │   resident, starts at logon;
 │ capture · input · GOAP · in-process │   plans and steers locally
 │ planning + steering on local tiles  │
 └─────────────────────────────────────┘
```

| Component | Owns | Runs on | Project |
|---|---|---|---|
| Pathing Service | recorded paths, route files, cost zones, bakes, tiles; planning for the UI | central, any OS | `PathingService` (Phase 1) |
| Bot Manager | bots (PC/agent ↔ account, character, client), assignments, commands, live-state cache, run history | central, any OS | `BotManager` (Phase 3) |
| Bot Agent | the PC's WoW client: capture, input, GOAP, planning, steering; one run at a time | each game PC/VM, Windows | `HeadlessServer` (Phase 3) |
| Control Center | nothing; it is a client of the other two | central, any OS | `ControlCenter` (Phase 2) |

### Rules

1. **Agents are resident and outlive the manager.**
   - An agent starts at user logon from a Task Scheduler task. It has to run in the interactive
     session to capture the screen and send input, which a Windows Service in session 0 cannot
     do.
   - It dials out to the manager over SignalR, reconnects with backoff, and re-registers. A run
     keeps going while the manager is down.
   - A crash restarts it, through a restart loop like `BlazorServer/Program.Main` plus the
     task's restart-on-failure. With no manager configured it works standalone, as the CLI
     does today.
2. **Stop is a command, not a kill.** The agent's `BotRuntimeHost` stops a run by releasing
   input first, as `Game/CLAUDE.md` requires, and tears it down after a grace period (the
   `StopGraceMs` logic from `ForeverRunHost`).
3. **Navigation is local; data is central.** The agent pulls what it needs (an assigned route,
   cost zones, tiles) and caches it in its local data folder. A sync that rewrites cost-zone
   files takes effect without a restart, because `PPatherService` already watches them.
4. **One planner implementation.** `PPather/Planning/RoutePlanner` serves the Pathing Service
   now and the agent's local mode later. A path previewed in the Control Center is the path
   the bot will compute, and a parity test enforces that.
5. **Failures are typed.** A route request returns `Found`, `NoPath`, `NotBaked`, `Unavailable`
   or `BadRequest`. "Service down" must never read as "no path", which is what the RemoteV1
   client does today with a 429.
6. **The Pathing Service never calls bots; bots ask it.** An assignment goes from manager to
   agent ("run profile X with route R"), and the agent fetches route R by id.
7. **Nothing depends on the Control Center.** A panel whose service is down says "offline";
   everything else keeps working.

## Communication

- **REST** for request/response: ASP.NET Core minimal APIs and System.Text.Json under
  `/api/v1`, with OpenAPI and Swagger UI on each service.
- **SignalR** (MessagePack, already in use) for streams: agent↔manager control and telemetry,
  manager→Control Center fan-out, and the pathing watch stream.
- **Contracts** are one shared project. Every DTO, route and hub method name lives there as a
  type or a `public const`, so both ends are checked by the compiler with no codegen. gRPC was
  considered: it would add a protobuf toolchain and a second RPC style for nothing SignalR
  does not already do, and Blazor Server speaks SignalR natively.
- **Direction.** Agents dial out to the manager, so an agent needs no inbound port and a
  restarted manager simply sees agents register again. The Control Center calls the two
  services. Nothing calls the agents except over the connection they opened.
- **Security.** Everything binds to localhost by default. Once agents connect from other PCs,
  every manager and pathing endpoint takes an API-key header.
- **Versioning.** Routes carry `/api/v1`, and agents send a protocol version when they
  register.

## Data ownership

| Data | Owner | Others |
|---|---|---|
| Recorded paths `Json/path`, Forever routes `Json/path/_forever`, cost zones `Json/road/<era>`, navmesh tiles, area grids | Pathing Service | Agents keep synced local copies (Phase 3). Formats on disk stay as they are. |
| Bots, assignments, run history | Bot Manager | JSON file store behind `IBotRegistry`; SQLite only if it becomes necessary |
| Live state: vitals, goal, position, kills, log | Agent | streamed to the manager, always also logged to file |
| Committed static data: `Json/dbc`, `Json/area`, spells, items, class profiles | the repository | read locally by whatever needs it |

Until the agent's data sync exists, run the Pathing Service against the same `Json` folder the
bot uses, either on the same PC or with changes committed to the repository.

## Pathing contracts (v1)

`Contracts/Pathing`, served by `PathingService`:

| Endpoint | Body / query | Answer |
|---|---|---|
| `POST /api/v1/routes/find` | `RouteRequest(Client, UiMapId, Space: World\|Map, From, To, Tuning)` | `RouteResponse(Status, Points, Smoothed, ElapsedMs, Detail)` |
| `GET /api/v1/capabilities` | | `PathingCapabilities(ApiVersion, Client, Era, PathsAreSmoothed, Continents)` |
| `GET /api/v1/health` | | baked tiles per continent and resident lanes |
| `GET /api/v1/paths?filter=` | | `RecordedPathInfo[]` |
| `GET/PUT/DELETE /api/v1/paths/{name}` | `Point3[]` on PUT | `RecordedPath` / 204 / 404 |
| `GET /api/v1/area?mapId&x&y` | | `AreaAtPoint(AreaId, Z)` |
| `GET /api/v1/costzones/{continent}` | | `CostZoneSet` (roads and danger zones by uiMap) |
| `PUT /api/v1/costzones/{continent}/roads/{uiMapId}` | `RoadZones` | 204, or 400 with the reason |
| `PUT /api/v1/costzones/{continent}/danger/{uiMapId}` | `DangerZones` | 204, or 400 with the reason |
| `POST /api/v1/costzones/reload` | | 204 |

Planning happens in **continent lanes**. Each lane is a disk-only `NavmeshPathfinder` with its
cost zones and a lock. Lanes never evict each other, so mixed-continent callers no longer
thrash. Every request gets fresh query memory (see "Findings"), a lane busy beyond its wait
budget answers `Unavailable` rather than 429, and an endpoint on an unbaked tile answers
`NotBaked`.

## Code organization

| Project | TFM | References | Role |
|---|---|---|---|
| `Contracts` | `net10.0` | nothing | DTOs, route and hub constants |
| `ServiceClients` | `net10.0` | Contracts | typed clients for the Control Center and the agent |
| `PathingService` | `net10.0`, web | Contracts, PPather, DataConfig, SharedLib | the Pathing Service |
| `ServiceTests` | `net10.0` | the above | xUnit, runs on Linux |
| `BotManager`, `ControlCenter` | `net10.0`, web | Contracts, ServiceClients | Phases 2 and 3 |

Dependency rules:
- Services never reference `Core`, `Game`, `WinAPI` or `Frontend`.
- The Control Center never references `PPather` or `Core`. It proxies tiles, data and the watch
  hub to the Pathing Service with YARP, so the map's JS keeps same-origin URLs.

The heavy lifting lives in reusable classes:

| Class | Replaces / serves |
|---|---|
| `PPather/Planning/RoutePlanner` | the one route planner: Pathing Service now, agent local mode in Phase 4 |
| `PPather/Navmesh/NavmeshQueryMemory` | the per-walker state that `NavmeshPathfinder` kept in fields |
| `PPather/Navmesh/CostZoneWatcher`, `NavmeshSettings.CacheDirectory` | extracted from `PPatherService`; also used by the planner |
| `PathingService/Paths/RecordedPathStore`, `CostZones/CostZoneStore` | the formats `Frontend/Controllers` writes today, in one place |
| agent `BotRuntimeHost` + `IBotRuntimeFactory` | `ForeverRunHost` / `ForeverRunOutput`; builds a Classic or Forever runtime per run (composition only) |

## Phases

0. **Own the repository** - done 2026-10-05. The repo is standalone rather than a GitHub fork,
   and the DotRecast submodule points at `DimPap99/DotRecast`.
1. **Pathing Service: planning and authoring API** - built 2026-10-05 (see below).
   - Delivers `Contracts`, the planner, `PathingService`, the pathing client and `ServiceTests`.
   - The bot is untouched.
2. **Control Center.**
   - The map, route editor (reusing `Frontend/Helper/PathEditor`), path library, live route
     view and route search, all as clients of the Pathing Service.
   - The Pathing Service gains bake and admin, world data, the watch hub and a Forever route
     library. Forever's route file model and loader move out of `Core/Forever` first, with
     no behaviour change.
   - Then PathingAPI, `Frontend/Controllers` and the duplicated map assets are deleted.
3. **Bot Manager and resident agent.**
   - The manager gets its registry, `AgentHub`, commands and telemetry fan-out.
   - `HeadlessServer` becomes resident: `AgentLink`, `BotRuntimeHost`, a `TelemetryPublisher`
     (a `BotSnapshot` once a second through read-only taps), the data sync, a logon task and a
     restart loop.
   - Forever's run loop moves out of `ForeverControllerTrial/Program.cs` into a reusable
     `ForeverRun` class.
   - The Control Center gets a fleet page, a per-bot console and assignments.
4. **Retire and simplify.**
   - Rebuild `LocalPathingApi` on `RoutePlanner`.
   - Remove `PPatherService`'s two-call search, `PatherGate`/`RateLimit`, the RemoteV1 and V3
     clients, the AnTCP server, the legacy `api/PPather/*` endpoints, and SpotAStar if unused.
   - Optional remote planning: `ServicePathingApi` behind a `FailoverPathingApi` decorator.
     The request carries the bot's last floor, as Classic's `WorldPosZ` does, so the service
     stays stateless. A per-client cache keyed by client id comes only if repeat requests
     turn out too slow ([known issues](known-issues.md), "Design notes").
   - BlazorServer becomes the PC's local setup UI (addon and frame config, key bindings),
     and route generation moves server-side.

## Phase 1 as built

Run it with `PathingService/run.sh` (`--exp=` picks the client); Swagger UI is at
`http://localhost:5010/swagger`. Notes for the next phases:

- **Verification.**
  - `ServiceTests` has 52 tests, all passing on Linux. The tile tests skip on machines
    without the precata navmesh.
  - The parity gate covers 14 known-trouble routes: the planner returns exactly the bot's
    points for each.
  - A one-off before/after harness confirmed the `NavmeshPathfinder` refactor changes nothing
    for the bot. On the commit before Phase 1 and after it, 15 routes were byte-identical,
    both with a fresh service per route and with 30 consecutive queries through one service.
  - 16 parallel route requests over two continents answered 200 with identical points in
    0.13 s.
- **`ServiceTests` is plain `net10.0`**, not the repository's Windows TFM. Nothing in it needs
  `Core`; a later test that does can live in a second project.
- **`global.json` opts `dotnet test` into Microsoft.Testing.Platform.** xUnit v3 4.x requires
  it on the .NET 10 SDK. The CI's `dotnet test --no-build` works unchanged.
- **The route contract has no `startIndoors` yet.** It is added together with the Phase 4 fix
  ([known issues](known-issues.md), item 1), so the field never exists without an effect.
- **The client never throws.** It converts transport failures and refusals into
  `RouteStatus.Unavailable`/`BadRequest` or a failed `ServiceResult`, and retries twice under
  the standard resilience handler.
- **Bake, world data, the watch hub and Forever routes** are still PathingAPI's. They move in
  Phase 2 with the pages that use them.

## Phase 2 starting notes

What the Control Center session needs to know before it plans. Verify against the code
first; these were true on 2026-10-05.

- **What moves from PathingAPI:**
  - Pages: `Index` (3D live view and test routes), `Leaflet` (map), `Search`, `Route`
    (recorded paths), `CloseNPC` (NPC routes), `SwaggerFrame`.
  - Shared components: `Viewport3D`, `SearchPanel`, `StatusBar`, `Panel`, `BusyButton`.
  - Scripts and styles: `wwwroot/js/pathing-scene.js` (Babylon, fetched on demand),
    `pathing-ui.js`, `css/site.css`.
  - The rest: `Catalog/` (locations from `teleport_locations.txt`, recorded paths, test
    routes), `Status/ServerStatus`, and the watch stream (`Watch/WatchBroadcaster` with
    `WatchSettings`, `NavmeshTileMesh`, `PathStyle`).
  - The hub is `Hubs/WatchHub` at `/watchHub`, MessagePack. Its messages are `drawPath`,
    `drawLine`, `drawSphere`, `removeMesh`, `clear` and `log`.
- **The map** is Frontend's script pieces: `leaflet-watch.js`, then map, layers, coords,
  spawns, sidebar, paths and signalr, plus navmesh, bake and zones. It also needs Leaflet
  1.7.1 from unpkg and Frontend's static libraries.
  - It fetches data from `/tiles`, `/dbc`, `/area`, `/npcspawnlocations`,
    `/mailboxlocations` and `/path` (`Frontend.UseCustomStaticFiles`).
  - It authors through `api/Road`, `api/DangerZone` and `api/Path`.
  - In the Control Center, proxy the data URLs and the hub to the Pathing Service with YARP
    (`Yarp.ReverseProxy`, 2.3.0 is current), and move the authoring calls to the `/api/v1`
    routes.
- **What the Pathing Service must gain for those pages:**
  - World data as static files.
  - Bake and admin: `PPatherService`'s `StartBake`, `CancelBake`, `BakeStatus`,
    `ClearNavmeshCache`, `BuildAreaGrid` and `GetNavmeshTiles`. Host one `PPatherService` for
    admin only, never for queries.
  - A watch stream. `RoutePlanner` raises no events yet; add an observer for routes found
    and for tiles each lane loads.
  - The location catalog and the test routes.
  - A Forever route library. First move `Core/Forever/Tasks/ForeverRouteFile` and what it
    builds (`ForeverRoute`, `ForeverMapPoint`, `ForeverArea`, `ForeverUntil`, the task types)
    out of `Core`. Check that closure before promising it; it can slip to a later phase.
- **Traps:**
  - A lane caches missing tiles as empty for its life, so reopen it after a bake
    ([known issues](known-issues.md), item 12).
  - On-demand Forever bakes use `Utilities/ForeverCasc`, which PathingAPI references and the
    solution does not contain.
  - In PathingAPI, opening a 3D page resets the engine; in the service a reset must never
    disturb the lanes other callers use.
  - The map's JS mirrors `DataConfig`'s era logic, and `CoreTests` reads `Configs` from
    `leaflet-watch.js`.
  - In the Control Center the era comes from the service's capabilities, not from the URL.
- **Decide with the user first:**
  - **BlazorServer's own map:** it authors roads and danger zones through
    `Frontend/Controllers`. Deleting those means pointing it at the Pathing Service or
    dropping authoring from the bot's dashboard.
  - **The 3D live view:** keep it, or keep only the 2D map.
  - **The Control Center's port** (5000, 5001 and 5010 are taken).
  - **Scope:** whether bake admin and the Forever route library are in this phase.
- **Not in Phase 2:**
  - The route generator page: it needs `Core`'s `RouteGenerator` (Phase 4).
  - Recording paths, which needs the game. `Frontend/Helper/PathEditor` can still serve the
    route editor.
  - The bot pages.

## Findings while designing

Everything spotted during this work is tracked, with where it lives and when it goes away,
in [known issues](known-issues.md). The ones that shaped this design:

- **`NavmeshPathfinder` remembers its caller** (floor, corridor band, stall memo). That is right
  for one walker per process and wrong for a shared service. The planner gives each request
  fresh memory. A remote bot will send its last floor with each request, keeping the service
  stateless. Why the memory is worth keeping is in known issues, "Design notes".
- **`startIndoors` never reaches the navmesh**, from the bot or from PathingAPI's `WorldRoute2`.
  The planner mirrors that for parity; both sides get fixed together in Phase 4.
- **The old authoring controllers trust names and skip validation.** `PathingService` checks
  path names and continents, and validates cost zones on save.
- **A map route can throw while converting back to map coordinates** (overlapping zones).
  The planner keeps such a point in the request's zone.

## Open questions

- **One service, several clients.** `ContinentDB` is static and the first client loaded wins.
  v1 runs one Pathing Service per client (`--exp`). Serving Classic and Forever from one
  process needs that state to belong to an instance first.
- **Tile distribution to agents.** The options are the CDN download script (today), a pull from
  the Pathing Service with a manifest, or both.
- **Launching the WoW client.** For now the agent waits for a client someone started. Whether
  it should start and log in a client itself is open.
- **Several clients on one Classic PC.** The code supports it through WGC and PostMessage, but
  the decision is one bot per PC. The contracts identify bots by agent, so this could be
  revisited without changing them.
