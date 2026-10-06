# Service architecture - design

**Status:** agreed 2026-10-05. Phases 0 to 3 are built: the Pathing Service, the Control Center,
the Bot Manager and the resident agent; PathingAPI is gone. See "Phase 1 as built" to "Phase 3 as
built". Phase 3's checks with Windows and a game are left
([phase3-plan.md](phase3-plan.md), "Windows checklist"); Phase 4 comes after.
**Scope:** how the parts of the rig talk to each other, what each one serves, and where things
are viewed. The bot's play logic (goals, GOAP, readers, input, navigation, the Forever
activities) is deliberately out of scope and does not change.

## Why

Before the split, each WoW client got one Windows process that was both bot and UI
(`BlazorServer` or `HeadlessServer`). Forever ran through `Utilities/ForeverControllerTrial`,
which `Frontend/ForeverRunHost` started as a stdio child. `PathingAPI` was a second web app with
its own copy of the map pages and the authoring endpoints. That had costs:

- **The UI depended on the game.** `AddWoWProcess` throws when no client is running, so the
  dashboard could not even start without one.
- **PathingAPI served one caller at a time.** It answered HTTP 429 when busy, and switching
  continent disposed the loaded engine.
- **Everything existed twice.** Two hosts, two layouts, two map pages, two copies of
  `teleport_locations.txt`, and the road, danger-zone and path controllers mounted in both.
- **A run's lifetime was tied to its host.** `ForeverRunHost --listen` stops the run when the
  host's pipe closes.

Phases 1 and 2 removed the first three: the Control Center needs no game client, the Pathing
Service plans for many callers at once, and everything lives once. The last is Phase 3's.

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
| Control Center | nothing; it is a client of the other two | central, any OS | `ControlCenter` (Phase 2, port 5020) |

### Rules

1. **Agents are resident and outlive the manager.**
   - An agent starts at user logon from a Task Scheduler task. It has to run in the interactive
     session to capture the screen and send input, which a Windows Service in session 0 cannot
     do.
   - It dials out to the manager over SignalR, reconnects with backoff, and re-registers. A run
     keeps going while the manager is down.
   - A crash restarts it. A failed host is built again in the process (`ResidentHost`, as
     `BlazorServer/Program.Main` does). A process that ends, such as one whose stuck run asked
     for a new process (exit code 75), is started again by `run-agent.ps1`. The logon task runs
     that script and restarts it if it fails. With no manager configured the agent works
     standalone, as the CLI always did.
2. **Stop is a command, not a kill.** The agent's `BotRuntimeHost` stops a run by releasing
   input first, as `Game/CLAUDE.md` requires, and tears it down after a grace period (the
   `StopGraceMs` logic from `ForeverRunHost`).
3. **Navigation is local; data is central.** Before each run the agent pulls what the run needs
   into its local data folder: the assigned route or the profile's paths, and the era's cost
   zones. Navmesh tiles still come from the CDN script. A sync that rewrites cost-zone files takes
   effect without a restart, because `PPatherService` already watches them.
4. **One planner implementation.** `PPather/Planning/RoutePlanner` serves the Pathing Service
   now and the agent's local mode later. A path previewed in the Control Center is the path
   the bot will compute, and a parity test enforces that.
5. **Failures are typed.** A route request returns `Found`, `Partial`, `NoPath`, `NotBaked`, `Unavailable`
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
- **Security.** Everything binds to localhost by default. A request from this machine is always
  served; one from another machine needs the shared key (`ApiKey`) in the `X-Api-Key` header, on
  every manager and pathing endpoint and hub, and without a configured key it is refused. A service
  bound beyond localhost without a key stops right after it starts. The rules live once, in
  `ServiceClients/Security/ServiceKeyPolicy`; clients send the key from `<Service>:ApiKey`, else
  `ApiKey`, and the Control Center and BlazorServer add it when they forward the browser's calls.
- **Versioning.** Routes carry `/api/v1`, and agents send a protocol version when they
  register.

## Data ownership

| Data | Owner | Others |
|---|---|---|
| Recorded paths `Json/path`, Forever routes `Json/path/_forever`, cost zones `Json/road/<era>`, navmesh tiles, area grids | Pathing Service | Agents keep synced local copies: a run's files are pulled before it starts, never deleted (Phase 3). Formats on disk stay as they are. |
| Bots, assignments, run history | Bot Manager | JSON file store behind `IBotRegistry`; SQLite only if it becomes necessary |
| Live state: vitals, goal, position, kills, log | Agent | streamed to the manager, always also logged to file |
| Committed static data: `Json/dbc`, `Json/area`, spells, items, class profiles | the repository | read locally by whatever needs it |

An agent pulls a run's files before it starts, from the Pathing Service its `PathingService:Url`
names. BlazorServer and an agent with no service set read their own `Json`; for them, run the
Pathing Service on the same PC, or commit the authored files to the repository.

## Pathing contracts (v1)

`Contracts/Pathing`, served by `PathingService`:

| Endpoint | Body / query | Answer |
|---|---|---|
| `POST /api/v1/routes/find` | `RouteRequest(Client, UiMapId, Space: World\|Map, From, To, Tuning, Search: Band\|Continent)` | `RouteResponse(Status, Points, Smoothed, ElapsedMs, Detail)`; a `Partial` route holds the points as far as the search got; `BadRequest` (400) for tuning past 10 yards of jitter or edge margin |
| `GET /api/v1/capabilities` | | `PathingCapabilities(ApiVersion, Client, Era, PathsAreSmoothed, Continents)` |
| `GET /api/v1/health` | | baked tiles per continent and resident lanes |
| `GET /api/v1/paths?filter=` | | `RecordedPathInfo[]` |
| `GET/PUT/DELETE /api/v1/paths/{name}` | `Point3[]` on PUT | `RecordedPath` / 204 / 404 |
| `GET /api/v1/forever/routes` | | `ForeverRouteInfo[]`, each checked as a run loads it |
| `GET/PUT/DELETE /api/v1/forever/routes/{name}` | `ForeverRouteText` on PUT | `ForeverRouteDocument` (text and check) / 204, or 400 with the step / 404 |
| `POST /api/v1/forever/routes/check` | `ForeverRouteDraft` | `ForeverRouteInfo`, nothing saved |
| `GET /api/v1/area?mapId&x&y` | | `AreaAtPoint(AreaId, Z, UiMapId)`: the baked area, its zone (a sub-area's parent), the ground's height; 400 beyond the edge of the world (17,066.67 yards from 0) |
| `GET /api/v1/costzones/{continent}` | | `CostZoneSet` (roads and danger zones by uiMap) |
| `PUT /api/v1/costzones/{continent}/roads/{uiMapId}` | `RoadZones` | 204, or 400 with the reason |
| `PUT /api/v1/costzones/{continent}/danger/{uiMapId}` | `DangerZones` | 204, or 400 with the reason |
| `POST /api/v1/costzones/reload` | | 204 |
| `GET /api/v1/sync/{set}?era=` | | `SyncManifest(Set, Era, Files)`: each file's name, size and SHA-256 with CRLF read as LF; sets `paths`, `forever-routes`, `costzones` (by era, any era); 400 for an unknown set or era |
| `GET /api/v1/sync/{set}/files/{**name}?era=` | | the file's bytes as stored; 400 for a name outside the set, 404 |
| `GET /api/v1/world` | | `WorldInfo(Client, Era, HasMap, Continents, Zones, NpcFlags)` |
| `GET /api/v1/world/npcs?uiMapId&flag` | | `NpcInfo[]` of that type in the zone |
| `GET /api/v1/world/npcs/search?mapId&name` | | `NpcInfo[]` of creatures on the continent whose name holds the text (50 at most) |
| `GET /api/v1/world/spawns?uiMapId&layer` | | `SpawnPoint[]`: every elite, herb, vein, skinnable spawn or mailbox in the zone (`SpawnLayers`) |
| `GET /api/v1/catalog/locations` | | `NamedLocation[]` (curated spots and teleports) |
| `GET /api/v1/catalog/testroutes` | | `TestRoute[]` with ready `RouteRequest`s |
| `GET /api/v1/navmesh/{continent}/tiles` | | `NavmeshTile[]` (bounds, resident) |
| `GET /api/v1/navmesh/{continent}/tiles/{x}/{z}` | | `NavmeshTileGeometry` (the tile's walkable polygons, for drawing); 404 when not baked |
| `GET /api/v1/navmesh/{continent}/overview` | | `NavmeshOverview` (one walkable height per baked tile: a map's relief) |
| `DELETE /api/v1/navmesh/{continent}` | | `CacheCleared`; 409 while a job runs |
| `GET/POST /api/v1/bake` | `BakeRequest(Continent?, AdtX?, AdtY?)` on POST | `BakeStatus`; 202, 400, or 409 (busy, no game files) |
| `POST /api/v1/bake/cancel` | | `BakeStatus`; 409 when none runs |
| `POST /api/v1/areagrids?continent` | | 202; 409 without game files |
| `POST /api/v1/watch/paths`, `/watch/markers`, `/watch/clear` | `WatchPath`, `WatchMarker` | 202; 400 with the reason, such as a point beyond the edge of the world |
| `/watchHub` (SignalR, MessagePack) | `Replay()`, `Terrain(continent, tiles)`, `WatchRoutes()` (routes only, no scene) | `drawPath`, `drawLine`, `drawSphere`, `removeMesh`, `clear`, `log`, `addNavmeshTile`, `addTerrainTile`; to route watchers `routePlanned(WatchRoute)` and `clear` |
| `/tiles`, `/dbc`, `/area`, `/npcspawnlocations`, `/mailboxlocations`, `/path`, `/locations` | | the map's world data files (`WorldData`) |

## Bot Manager contracts (v1)

`Contracts/Bots`, served by `BotManager` on port 5030:

| Endpoint | Body | Answer |
|---|---|---|
| `GET /api/v1/health` | | `ManagerHealth(ApiVersion, AgentProtocol, Bots, Online)` |
| `GET /api/v1/bots` | | `BotInfo[]`: what each agent reported, its assignment, run and latest snapshot |
| `GET/DELETE /api/v1/bots/{agentId}` | | `BotInfo` / 204, or 409 while online / 404 |
| `PUT/DELETE /api/v1/bots/{agentId}/assignment` | `BotAssignment` on PUT | 204, or 400 with why / 404 |
| `POST /api/v1/bots/{agentId}/start`, `/stop` | | the agent's `CommandAck`; 409 offline, no assignment or refused; 504 no answer |
| `GET /api/v1/bots/{agentId}/log` | | the latest 500 `BotLogLine`s, oldest first |
| `GET /api/v1/bots/{agentId}/runs` | | `RunRecord[]`, newest first (200 kept) |
| `/agentHub` (SignalR, MessagePack) | from agents: `Register(AgentHello)` → `AgentWelcome`, `Snapshot`, `Log`, `RunEnded` | to agents: `Assign(BotAssignment?)`, `Command(BotCommand)` → `CommandAck` |
| `/fleetHub` (SignalR, MessagePack) | | to listeners: `BotChanged`, `BotRemoved`, `Snapshot`, `Log`, `RunEnded` |

An agent registers on every connection with `AgentProtocol` (1); the manager refuses another
protocol with why. A bot is the PC's agent: its id is a GUID the agent creates on its first start and
keeps on its PC, and the account, character and client are what it reports.

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
| `PathingService` | `net10.0`, web | Contracts, PPather, DataConfig, SharedLib, ForeverCasc | the Pathing Service |
| `ControlCenter` | `net10.0`, web | Contracts, ServiceClients | the central UI (Phase 2); every world page on the world view (deck.gl) |
| `MapAssets` | `net10.0`, Razor class library | nothing | the Leaflet map's scripts, libraries and styles, static files only; BlazorServer's |
| `ServiceTests` | `net10.0` | the above | xUnit, runs on Linux |
| `BotManager` | `net10.0`, web | Contracts, ServiceClients | the Bot Manager (Phase 3, port 5030) |
| `BotAgent` | `net10.0` | Contracts, ServiceClients, DataConfig | the agent's link, run host, data sync, telemetry and log stream, without Core |
| `HeadlessServer` | the Windows TFM | Core, BotAgent | the bot, and with `--agent` the resident agent (Classic and Forever runtimes) |
| `AgentTests` | the Windows TFM | Core, HeadlessServer | xUnit for what needs Core; runs on Linux too |

Dependency rules:
- Services never reference `Core`, `Game`, `WinAPI` or `Frontend`.
- The Control Center never references `PPather` or `Core`. It proxies the API, the world data and
  the watch hub to the Pathing Service with YARP, so the browser keeps same-origin URLs. It
  references Contracts and ServiceClients only (MapAssets until 2026-10-05, when its pages moved
  to the world view: `docs/world-view-plan.md`).

The heavy lifting lives in reusable classes:

| Class | Replaces / serves |
|---|---|
| `PPather/Planning/RoutePlanner` | the one route planner: Pathing Service now, agent local mode in Phase 4 |
| `PPather/Navmesh/NavmeshQueryMemory` | the per-walker state that `NavmeshPathfinder` kept in fields |
| `PPather/Navmesh/CostZoneWatcher`, `NavmeshSettings.CacheDirectory` | extracted from `PPatherService`; also used by the planner |
| `PathingService/Paths/RecordedPathStore`, `CostZones/CostZoneStore` | the formats the deleted `Frontend/Controllers` wrote, in one place |
| `PathingService/Navmesh/BakeAdmin` | PathingAPI's bake endpoints: one admin-only `PPatherService` for bakes, area grids and terrain, never for queries |
| `PathingService/Watch/WatchBroadcaster` | PathingAPI's broadcaster, fed by `RoutePlanner`'s events instead of one engine's |
| `ServiceClients/Pathing/PathEditor`, `ZoneMath` | the path editing model (recorder and editor) and map-world arithmetic for clients |
| agent `BotRuntimeHost` + `IBotRuntimeFactory` | `ForeverRunHost` / `ForeverRunOutput`; builds a Classic or Forever runtime per run (composition only) |
| `BotAgent/Sync/DataSync`, `PathingService/Sync/SyncLibrary` | the data sync: a run's files from the Pathing Service before it starts, compared by `ServiceClients/Sync/SyncHash` |
| `HeadlessServer/Hosting/ResidentHost`, `run-agent.ps1` | BlazorServer's restart loop, for the agent, and the loop around its process |

## Phases

0. **Own the repository** - done 2026-10-05. The repo is standalone rather than a GitHub fork,
   and the DotRecast submodule points at `DimPap99/DotRecast`.
1. **Pathing Service: planning and authoring API** - built 2026-10-05 (see below).
   - Delivers `Contracts`, the planner, `PathingService`, the pathing client and `ServiceTests`.
   - The bot is untouched.
2. **Control Center** - built 2026-10-05 (see below).
   - The map, route editor (on `PathEditor`, now in ServiceClients), path library, live route
     view, route search and NPC routes, all as clients of the Pathing Service.
   - The Pathing Service gained bake and admin, world data, the catalog and the watch hub.
   - PathingAPI, `Frontend/Controllers` and the duplicated map assets are deleted.
   - The Forever route library moved to Phase 3.
3. **Bot Manager and resident agent** - built 2026-10-06 (see below); its Windows checks are left.
   - A Forever route library in the Pathing Service, which agents fetch assigned routes from.
     Forever's route file model and loader move out of `Core/Forever` first, with no behaviour
     change.
   - The manager gets its registry, `AgentHub`, commands and telemetry fan-out.
   - `HeadlessServer` becomes resident: `AgentLink`, `BotRuntimeHost`, a `TelemetryPublisher`
     (a `BotSnapshot` once a second through read-only taps), the data sync, a logon task and a
     restart loop.
   - Forever's run loop moves out of `ForeverControllerTrial/Program.cs` into a reusable
     `ForeverRun` class.
   - The Control Center gets a fleet page, a per-bot console and assignments.
4. **Retire and simplify.**
   - Rebuild `LocalPathingApi` on `RoutePlanner`.
   - Remove `PPatherService`'s two-call search, the RemoteV1 and V3 clients and the bot's
     `PathVisualizer` (their server went with PathingAPI in Phase 2), and SpotAStar if unused.
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
- **Bake, world data and the watch hub** moved in Phase 2; the Forever route library moved to
  Phase 3.

## Phase 2 as built

Run both with `PathingService/run.sh` and `ControlCenter/run.sh`, then open
`http://localhost:5020`. The Control Center finds the service at `PathingService:Url`
(default `http://localhost:5010`) and says "offline" while it does not answer.

- **What the Control Center is.** (As built in Phase 2. Since 2026-10-05 its pages are on one
  deck.gl world view instead of Leaflet and Babylon, and it no longer references MapAssets:
  `docs/world-view-plan.md`.)
  - A Blazor Web App (InteractiveServer, no prerendering) referencing Contracts,
    ServiceClients and MapAssets only.
  - Pages: live view (3D, test routes), map, route search, path library, path editor, NPC
    routes and the API reference (the service's Swagger, forwarded).
  - YARP forwards `/api/v1`, `/watchHub` (WebSockets included), `/swagger` and the world data
    prefixes (`WorldData`) to the service, so the browser only ever talks to the Control
    Center.
  - One `PathingStatus` poller (every 2 s) feeds the status bar and the offline notices;
    `WorldCache` keeps zones, places and test routes and reloads them when the service comes
    back.
- **What the Pathing Service gained.**
  - World data as static files, the world info (zones with their bounds, NPC types) and NPCs
    by zone, the location catalog and the test routes.
  - Bake admin on one admin-only `PPatherService` (`BakeAdmin`): bake, cancel, status, clear
    a continent's tiles, area grids, startup `--bake=`/`--bake-area=`. Jobs run one at a time;
    without game files they are refused (409) up front.
  - The watch hub: `RoutePlanner` raises `RoutePlanned`, `TileLoaded` and `TileUnloaded`, and
    `WatchBroadcaster` streams them. A view opened late calls `Replay` instead of resetting
    anything.
  - Terrain on demand: the 3D view's Terrain toggle asks for the game geometry under the 25
    tiles nearest its camera. `BakeAdmin` extracts it and resets the engine afterwards, so the
    triangle cache does not stay resident. It needs the game files where the service runs.
- **The map is one copy.** `MapAssets` holds the Leaflet map's scripts, libraries, styles and
  icon atlas; BlazorServer (through Frontend) and the Control Center serve it at
  `_content/MapAssets/`. Its authoring calls go to `/api/v1`: the Control Center forwards them,
  and BlazorServer forwards `/api/v1` to its `PathingService:Url` (YARP).
- **Decisions taken with the user.** BlazorServer's map points at the Pathing Service; keep the
  3D view (navmesh and routes, with terrain on demand); port 5020; bake admin in Phase 2, the
  Forever route library in Phase 3; PathingAPI deleted with its legacy API (known issues,
  item 6).
- **Verification.**
  - `ServiceTests` has 86 tests, all passing on Linux, including the hub end to end over
    MessagePack, the bake admin on a root without game files, and known issue 12's regression.
  - Both services were run on Linux against a scratch copy of `Json` and every page was used in
    headless Chrome: test routes, search, NPC routes, path library and editor, the map with its
    cost zones and a saved danger zone, Swagger, and the service stopped and restarted under an
    open page.
  - Not verified here: terrain and bakes themselves (no game files on that machine), and
    BlazorServer, which needs a WoW client to start; it was compile-checked.
- **Notes for later.**
  - A point on the watch hub travels as `[x, y, z]`; MessagePack writes `Vector3` as a map,
    which neither view reads (known issues, item 18).
  - Forever has no minimap art, so its map page says so; the 3D view is its route view.

## Phase 3 as built

Built 2026-10-05 and 06 in six steps. What each delivered, how it was checked, and the Windows
checklist that is left are in [phase3-plan.md](phase3-plan.md). The central services start with
`PathingService/run.sh`, `BotManager/run.sh` and `ControlCenter/run.sh`. On a game PC the agent runs
with `HeadlessServer\run.bat --agent --manager <url> --pathing-service <url>`, or at every logon
from `HeadlessServer\install-agent-task.ps1`.

- **Forever's route model left Core.** `SharedLib/Forever` holds it. The Pathing Service lists,
  checks and serves the route files (`/api/v1/forever/routes`), and the Control Center edits them
  as text, comments kept.
- **`ForeverRun`** is Forever's run loop as a class in Core. The trial runner is a command line over
  it, and the agent runs Forever in process through it, keeping the trial runner's gates.
- **The Bot Manager** (`BotManager`, port 5030) keeps:
  - a JSON registry of bots, each the PC's agent with a GUID it keeps on its PC;
  - assignments, and start and stop answered as client results over the agent's own connection;
  - the run history, the agent hub and the fleet stream;
  - the shared API key.
- **The resident agent** is HeadlessServer `--agent`.
  - It dials out and registers on every connection, and runs one assignment at a time, Classic or
    Forever, with Core composed per run.
  - A stop releases input first. It streams a snapshot a second and its log, and keeps playing
    while the manager is down.
  - Before each run it pulls the run's files from the Pathing Service (`DataSync`, below).
  - A failed host is built again in the same process. A run that cannot be torn down ends the
    process with 75, and `run-agent.ps1`, which the logon task runs, starts it again.
- **The data sync** mirrors three sets: recorded paths, Forever routes and one era's cost zones
  (`/api/v1/sync`).
  - It compares SHA-256 hashes with CRLF read as LF, so a Windows checkout is not rewritten for its
    line endings, and fetches only what differs.
  - A local copy changed since it was last synced is kept in `Json/.sync/backup` first. Nothing is
    deleted.
  - With the service down or none set, a run uses this PC's copies. Only a route or path override
    the PC lacks refuses it.
  - On a bot PC that is a git checkout, synced files show as local changes.
- **The Control Center** has the fleet, a console per bot (its live state, Start and Stop, the
  assignment, the run history, the log) and a manager chip in the status bar.

Notes for Phase 4:
- The agent's id is the client id a per-client cache would key on, should remote planning need one.
- Navmesh tiles still come from the CDN script; the sync's manifest and file endpoints are the
  pattern for serving them too (see "Tile distribution" under open questions).
- A snapshot carries the bot's position (`BotSnapshot.Position`); nothing draws it on the world view
  yet. That is the bot's live view PathingAPI used to give through `RemoteV1`.
- BlazorServer's Forever runs page is deleted once the Windows checks pass (decision 7 of the plan,
  [known issue 24](known-issues.md)).
- A file deleted centrally stays on the bot PCs, because the sync never deletes
  ([known issue 31](known-issues.md)).

## Findings while designing

Everything spotted during this work is tracked, with where it lives and when it goes away,
in [known issues](known-issues.md). The ones that shaped this design:

- **`NavmeshPathfinder` remembers its caller** (floor, corridor band, stall memo). That is right
  for one walker per process and wrong for a shared service. The planner gives each request
  fresh memory. A remote bot will send its last floor with each request, keeping the service
  stateless. Why the memory is worth keeping is in known issues, "Design notes".
- **`startIndoors` never reaches the navmesh** from the bot, nor did it from PathingAPI's
  `WorldRoute2`. The planner mirrors that for parity; both sides get fixed together in Phase 4.
- **The old authoring controllers trust names and skip validation.** `PathingService` checks
  path names and continents, and validates cost zones on save.
- **A map route can throw while converting back to map coordinates** (overlapping zones).
  The planner keeps such a point in the request's zone.

## Open questions

- **One service, several clients.** `ContinentDB` is static and the first client loaded wins.
  v1 runs one Pathing Service per client (`--exp`). Serving Classic and Forever from one
  process needs that state to belong to an instance first.
- **Tile distribution to agents.** The options are the CDN download script (today), a pull from
  the Pathing Service with a manifest, or both. The data sync's manifest and file endpoints
  (`/api/v1/sync`) show the shape a tile pull would take.
- **Launching the WoW client.** For now the agent waits for a client someone started. Whether
  it should start and log in a client itself is open.
- **Several clients on one Classic PC.** The code supports it through WGC and PostMessage, but
  the decision is one bot per PC. The contracts identify bots by agent, so this could be
  revisited without changing them.
