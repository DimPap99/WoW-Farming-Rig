# Phase 3 - plan, progress and what is left

**Status (2026-10-05):** steps 1 to 4 of 6 are on `main` and pushed. Steps 5 and 6, the finishing
docs, and every check that needs Windows and a game are left. This file holds the whole plan as
agreed, what each step delivered and how it was checked, what remains, and the list to test on
Windows. The design itself is [service-architecture.md](service-architecture.md); what was found
along the way is in [known-issues.md](known-issues.md).

## The goal

Phase 3 of the services split (see "Phases" in the design doc): the **Bot Manager** and the
**resident bot agent**.

- A Forever route library in the Pathing Service that lists, checks and serves the route files in
  `Json/path/_forever`. First the route file model moves out of Core, with no behaviour change.
- `ForeverRun`: Forever's run loop moves out of `Utilities/ForeverControllerTrial/Program.cs` into
  a reusable class, with no behaviour change. The trial runner becomes a thin command line over it.
- A `BotManager` service (net10.0; Contracts and ServiceClients only): a registry of bots (a JSON
  file store behind `IBotRegistry`), an `AgentHub` agents dial out to (SignalR, MessagePack),
  assignments, commands (start, stop) and telemetry fanned out to the Control Center.
- HeadlessServer becomes the resident bot agent: `AgentLink` (dials out, reconnects with backoff,
  registers again), `BotRuntimeHost` with `IBotRuntimeFactory` (one run at a time, Classic or
  Forever), a `TelemetryPublisher` (a `BotSnapshot` once a second through read-only taps), the data
  sync an assignment needs, a logon task and a restart loop. With no manager configured it works on
  its own.
- The Control Center gets a fleet page, a per-bot console and assignments.

### Already decided before planning

- How the bot plays does not change (goals, GOAP, readers, input, navigation, Forever activities);
  code that moves keeps its behaviour.
- One bot per PC. The agent starts at user logon from a Task Scheduler task (it must run in the
  interactive session; a Windows Service cannot). It keeps playing while the manager is down, and
  it plans locally.
- Agents dial out; nothing calls an agent except over the connection it opened. The Pathing
  Service never calls bots: the manager assigns, and the agent fetches what it needs.
- Stop is a command: release input first, then tear down after a grace period (`StopGraceMs`).
- Every DTO, route and hub method is a type or a `public const` in Contracts. REST under `/api/v1`;
  streams over SignalR with MessagePack; agents send a protocol version when they register.
- Services never reference Core, Game, WinAPI or Frontend; the Control Center never references
  PPather or Core.
- Out of scope: remote planning, `LocalPathingApi` on `RoutePlanner`, removing RemoteV1/V3
  (Phase 4); known issues 15 to 17; anything but WoW Classic and WoW: Forever.

### Decisions taken when planning (the questions asked)

1. **Bot = the PC's agent.** Its id is a GUID created on first start and kept in
   `HeadlessServer/agent_identity.json` (gitignored); its name is `Agent:Name`, else the machine
   name. Account, character and client are facts the agent reports and the registry keeps as last
   seen. This id is the "client id" for Phase 4's per-client cache.
2. **Forever runs in process through `ForeverRun`.** The agent lifts the diagnostic-only guard for
   agent runs only and keeps the gates: verified profile, preflight, click into the game, time bound.
3. **Classic assignments, minimal:** a class profile and an optional path override (path index 0,
   as the dashboard's path picker does). The agent reports its local class profiles.
4. **The Control Center saves route files as text,** checked by the run's loader first, so `//`
   comments survive.
5. **Data sync = what an assignment needs, as a file mirror:** a manifest (name, size, SHA-256 of
   the text with CRLF read as LF) and raw bytes; never deletes; a local file that differs from what
   the agent last synced is backed up before it is replaced, and reported. Navmesh tiles stay with
   the CDN script.
6. **Bot Manager on port 5030; the API key comes now.** Header `X-Api-Key`; loopback trusted; a
   service bound beyond localhost refuses to start without a key.
7. **BlazorServer's "Forever runs" page stays** until the agent passes the Windows checks, then it
   is deleted (known issue 24).
8. **Six steps,** each on its own branch, verified and merged fast-forward to `main`; one push at
   the end. (Steps 1-4 were pushed together at this handoff, on request.)

### The starting notes, corrected against the code

- The route model's namespace was `Core.Forever` (not `.Tasks`). `DataConfig` is its own project.
- `ForeverUntil.IsMet` had callers outside the .sln (`Utilities/ForeverVisualReader`).
- Core cannot be composed without a running game (`WowProcess` throws; `BotController`'s
  constructor waits for the player's class), so the agent composes Core **per run**.
- `AddWoWProcess` left a foreground polling thread running (fixed: known issue 21).
- The trial runner has eight modes with their own time bounds (route trial 195 s fixed, auto-run
  170, travel 300, sweep 600, roam 900, route 1200; `--minutes` overrides the autonomous ones).
- Releasing input = turning the bot off (`ToggleBotStatus`): `AbortEvent`, `StopMoving`,
  `input.Reset()`. Only one thread may toggle.
- `LoggerSink` lives in Frontend, so the agent has its own sink (`AgentLogSink`).
- CommandLineParser refused `--Section:Key` arguments, which HeadlessServer's notes said worked
  (fixed: known issue 22).

## Layout as built

| Project | TFM | References | Role |
|---|---|---|---|
| `BotManager` | net10.0 web | Contracts, ServiceClients | registry, agent hub, fleet hub, assignments, commands, run history (port 5030) |
| `BotAgent` | net10.0 | Contracts, ServiceClients | the agent's identity, link, run host, telemetry, log stream; no Core |
| `HeadlessServer` | Windows TFM | Core, BotAgent | the bot; with `--agent` the resident agent (Classic and Forever runtimes) |
| `AgentTests` | Windows TFM | Core, HeadlessServer | tests that need Core; runs on Linux too |
| `SharedLib/Forever` | net10.0 | - | the Forever route model (moved out of Core) |
| `ServiceClients/Security` | net10.0 | - | the shared key rules (`ServiceKeyPolicy`), `ApiKeyHandler` |

## Steps

### Step 1 - the route model out of Core, the Forever route library - DONE (`aab1b53a`)

- `ForeverRouteFile`, the task records, `ForeverUntil`, `ForeverMapPoint`, `ForeverArea` moved to
  `SharedLib/Forever`; `IsMet` stays in Core as a C# 14 extension (`ForeverUntilChecks`).
- `ForeverRouteFile.TryParse(text, ...)`; `TryLoad` reads the file and calls it, same messages.
- Pathing Service: `GET/PUT/DELETE /api/v1/forever/routes[/{name}]`, `POST .../check`; text saved as
  given (comments kept), refused routes never written, checked against `dbc/forever` whatever
  `--exp`.
- Control Center: the **Forever routes** page (list, check, edit text, save, new, delete).
- Verified on Linux: tests; the page in headless Chrome (save keeps a comment byte for byte, a
  refused save names the step, the service stopped and restarted under the page). Found and fixed:
  inputs bound on `change` kept Save/New disabled until focus left (now `oninput`).

### Step 2 - `ForeverRun` - DONE (`2a5f1fd0`)

- `Core/Forever/ForeverRun.cs` (+ `ForeverRunRequest`): the loop, composition, gates, lines and
  exit codes, unchanged; output through `IForeverRunOutput`; typed stage and readable goal/tracker
  for the agent; `ReleaseInput` for a host whose run will not end.
- `ForeverAutonomousGoal.RunState` and `ForeverTrialGoal.Phase` are public; the run compares types.
- The trial runner is a command line over it.
- Verified on Linux: for 33 command lines (usage errors, refused/accepted routes, no client) the old
  and new runner print identical stdout/stderr and exit codes; the moved loop was diffed line by
  line against the original. **A live run is on the Windows list.**

### Step 3 - contracts, the API key, the Bot Manager - DONE (`2740d769`)

- `Contracts/Bots` (routes, hubs, records), `Contracts/Common/ServiceKey`.
- The key: `ServiceKeyPolicy` in ServiceClients; the Pathing Service and the manager apply it; the
  clients send it; the Control Center and BlazorServer add it when forwarding the browser's calls.
- `BotManager`: JSON registry, `BotFleet` (online agents, snapshots, last 500 log lines, commands
  as client results with a timeout), REST endpoints, both hubs, Swagger, `run.sh`.
- `ServiceClients`: `IBotManagerClient` (queries retried, commands never), `AssignmentRules`,
  `ServiceHttp`.
- Verified on Linux: tests (fake agent over the test server); live: a raw SignalR agent registered,
  streamed, took an assignment, started, refused a second start, stopped; the manager refused to
  serve `0.0.0.0` without a key (exit 1); with a key the LAN address needs it and localhost does
  not; the Control Center reached a keyed Pathing Service over the LAN.

### Step 4 - the agent in HeadlessServer - DONE (`90f08b33`)

- `BotAgent`: `AgentIdentity`, `AgentOptions`, `AgentLink`, `BotRuntimeHost`, `TelemetryPublisher`,
  `AgentLogSink`, `AddBotAgent`.
- HeadlessServer on the Generic Host: `--agent`, `--manager`, `--name`; `ClassicRuntime`,
  `ForeverRuntime`, `WowGameProbe`, `ClassProfileFolder`, `StartFromCommandLine`, `AgentProcess`
  (exit 75). Frontend reference dropped.
- Verified on Linux: tests (the agent against the in-memory manager with fake runtimes: release
  before teardown, a stuck run forced then abandoned, the manager restarting, standalone); live:
  the real HeadlessServer as an agent registered with a live manager, a start came back Refused
  ("no WoW client is running"), it rejoined a restarted manager, and SIGTERM ended it cleanly.
  **Everything with a game is on the Windows list.**

### Step 5 - the Control Center's fleet, console and assignments - TO DO

- `ControlCenter/Bots/BotManagerStatus.cs`: a 2 s poller like `PathingStatus` (online, health).
- `ControlCenter/Bots/FleetFeed.cs`: a server-side SignalR client to `/fleetHub` (MessagePack, the
  key in its headers), reconnecting forever, raising events for components (BotChanged, BotRemoved,
  Snapshot, Log, RunEnded). The Control Center needs the `Microsoft.AspNetCore.SignalR.Client` and
  MessagePack protocol packages.
- Registration: `services.AddBotManagerClient()` (`BotManager:Url`, default `localhost:5030`; key
  from `BotManager:ApiKey` or `ApiKey`).
- Pages (on the `pa-workspace` styles step 1 added):
  - **Fleet** (`/fleet`): every bot - online or last seen, client and character, state and activity,
    vitals, position, kills, assignment, Start/Stop.
  - **Bot console** (`/fleet/{agentId}`): live panel (including "waiting for a click in the game"),
    the log terminal (backlog from `GET .../log`, then the stream), the assignment form (Forever: a
    route from the library + minutes; Classic: one of the agent's profiles + an optional path from
    the path library; validated with `AssignmentRules`), Start/Stop, run history.
  - A "Bots" nav section and a manager chip in the status bar; "offline" when the manager is down.
- Tests: `ControlCenterTests` renders the pages with the manager down.
- Verify on Linux: the three services from scratch folders (see "How to verify on Linux"), a fake
  agent (BotAgent's `AgentLink` + `BotRuntimeHost` with a simulated runtime, or HeadlessServer
  `--agent` itself, which runs on Linux and refuses runs), Playwright: the fleet shows it, the
  console streams, an assignment saves, start/stop work, the manager and agent restart under an
  open page.

### Step 6 - data sync, logon task, restart loop - TO DO

- Pathing Service: `GET /api/v1/sync/{set}?era=` (manifest: name, size, SHA-256 of the text with
  CRLF read as LF) and `GET /api/v1/sync/{set}/files/{**name}?era=` (raw bytes). Sets:
  `forever-routes`, `costzones` (`Json/road/<era>`, any known era), `paths` (`Json/path` minus
  `_forever`). Names checked (`PathName`, `ForeverRouteLibrary.TryResolve`). Contracts in
  `Contracts/Pathing`, client calls in `IPathingClient`.
- `BotAgent/Sync/DataSync : IRunPreparation` (the run host already calls preparations, state
  "Syncing"): for Forever, the route and the era's cost zones; for Classic, the profile's paths
  (every `PathFilename`, path group and the override, read from the profile JSON) and the cost
  zones. Compare hashes, download what differs, back up a local change before replacing it, write
  atomically, keep `.sync/state.json`, never delete. Service down or not configured
  (`PathingService:Url` unset): run on local copies and say so; a missing local file refuses.
- HeadlessServer: a restart loop around the host (as `BlazorServer/Program.Main`); the process
  exit code 75 means "start me again".
- `HeadlessServer/install-agent-task.ps1` / `uninstall-agent-task.ps1`: a Task Scheduler task at
  logon, current user, interactive, limited, restart on failure, running `run-agent.ps1`, which
  starts the agent again unless it exits 0. Parse-check with `pwsh` on Linux; really test on Windows.
- Tests: the sync endpoints (bytes identical, comments kept, names refused, era); `DataSync` against
  the in-memory Pathing Service (download, skip identical, CRLF counts as identical, back up a local
  edit, never delete, service offline).
- Note for the docs: on a bot PC that is a git checkout, synced files show as local changes.

### Finishing - TO DO

- `docs/service-architecture.md`: "Phase 3 as built" (replacing "Phase 3 starting notes"), the status
  line, the Windows checklist.
- `docs/known-issues.md`: anything new from steps 5-6; item 24 once the runs page is deleted.
- CLAUDE.md files: ControlCenter (fleet pages, manager client), PathingService (sync), root.
- `docs/forever-routes.md`: running a route from the Control Center through an agent.
- README's feature summary if it changes.
- Merge to `main` and push.

## Windows checklist

Compile-checked on Linux, **not verified**: each needs Windows and a game. A Claude session on the
Windows PC can run these; report what happens next to each.

**Before:** `git pull`; install the .NET 10 SDK; `git submodule update --init`; the addons as in
`docs/forever-port.md`; `HeadlessServer\install.bat` copies `data_config.json`, `addon_config.json`
and `frame_config.json` from BlazorServer.

1. **Build and tests.** `dotnet build WoWFarmingRig.sln`, `dotnet build Utilities/ForeverControllerTrial`,
   `dotnet build Utilities/ForeverVisualReader`, `dotnet test` (202 expected; tile tests skip without
   the precata tiles). `dotnet run --project Utilities/ForeverVisualReader -c Release -- --logic-test`
   runs the Forever logic checks, which now import `SharedLib.Forever`.
2. **ForeverRun, no behaviour change.** With WowB logged in:
   `dotnet run --project Utilities/ForeverControllerTrial -- --auto-check`, then
   `-- --route crossroads_trip.json --minutes 5`: the route listing, "Click into the game window",
   the run, phase lines and 2 s snapshots as before; Ctrl+C stops it and no key stays held.
   Compare with an older log in `local/logs`.
3. **BlazorServer's Forever runs page** still starts, watches and stops a route (it runs the
   rebuilt trial runner).
4. **HeadlessServer standalone, Classic.** `HeadlessServer\run.bat Warrior_1.json` with a Classic
   client: plays as before. Ctrl+C: "Application is shutting down", the character stops (keys
   released), the window's process exits (it used to hang). A Forever client: refused with "a
   Classic run needs a Classic client".
5. **The agent with a manager.** Start the manager (`dotnet run --project BotManager`, Swagger at
   `http://localhost:5030/swagger`), then `HeadlessServer\run.bat --agent --manager http://localhost:5030`.
   `GET /api/v1/bots` shows the bot with its client. Until step 5's pages exist, drive it from
   Swagger or PowerShell:
   ```powershell
   $id = (Invoke-RestMethod http://localhost:5030/api/v1/bots)[0].agentId
   Invoke-RestMethod -Method Put -ContentType application/json -Body '{"kind":"Forever","forever":{"route":"crossroads_trip.json","minutes":5}}' "http://localhost:5030/api/v1/bots/$id/assignment"
   Invoke-RestMethod -Method Post "http://localhost:5030/api/v1/bots/$id/start"
   Invoke-RestMethod "http://localhost:5030/api/v1/bots/$id" | ConvertTo-Json -Depth 5   # latest snapshot
   Invoke-RestMethod -Method Post "http://localhost:5030/api/v1/bots/$id/stop"
   Invoke-RestMethod "http://localhost:5030/api/v1/bots/$id/runs" | ConvertTo-Json -Depth 5
   ```
   Expect: the agent's console shows the route listing and asks for the click; the snapshot's state
   is WaitingForClick, then Running with vitals, position, activity and kills; stop releases the
   keys and the run is reported Stopped; a run log in `local/logs/agent-<route>-<time>.log`.
6. **A Classic assignment** the same way (`{"kind":"Classic","classic":{"classProfile":"Warrior_1.json","pathOverride":null}}`):
   starts, streams, stops with keys released.
7. **The manager down mid-run.** Stop the manager while a run moves: the bot plays on. Start it
   again: the agent registers again within seconds, the bot shows its run; when the run ends its
   report arrives.
8. **The key across machines.** Manager on another PC: `ApiKey=<secret>` and
   `--urls=http://0.0.0.0:5030` there; on the bot PC `$env:ApiKey="<secret>"` and
   `--manager http://<that-pc>:5030`. Without the key the agent is refused (401) and keeps trying.
9. **Stop grace.** If a run ever fails to end after a stop, after ~15 s the agent logs that it
   releases the keys, reports the run Abandoned and exits with code 75 (known issue 23 is one way:
   a Classic run started while the addon's data is not on screen).
10. **Logoff.** With a run going, sign out of Windows: the run stops and releases (SIGTERM path).
11. **After step 6:** the logon task (`install-agent-task.ps1`), a reboot, the agent starting at
    logon and rejoining; the restart loop; the sync into a git checkout.

When these pass, decision 7 applies: delete BlazorServer's Forever runs page and its host (known
issue 24).

## How to verify on Linux

The scratch setup used for steps 1-4 (it lives outside the repository; recreate it as needed):

- A scratch data root: a folder with symlinks to every `Json/*` entry except `path` and `road`,
  which are copied, so nothing tracked changes. The Pathing Service runs from a scratch folder with
  a `data_config.json` whose `Root` is that folder (`dotnet PathingService/bin/Debug/net10.0/PathingService.dll`).
- The Control Center run from outside its project folder needs `ASPNETCORE_ENVIRONMENT=Development`,
  or it serves no static assets (`blazor.web.js`) and the page stays blank.
- The Bot Manager: `dotnet BotManager/bin/Debug/net10.0/BotManager.dll --Data=<scratch>/data`.
- An agent on Linux: HeadlessServer itself (`dotnet HeadlessServer/bin/Debug/net10.0-windows10.0.19041.0/HeadlessServer.dll --agent --manager http://localhost:5030 --Agent:IdentityFile=<scratch>/identity.json`,
  from the HeadlessServer folder); it refuses every run but registers, streams and obeys.
- Browser checks: Playwright .NET (`Microsoft.Playwright` 1.58) with the system Chrome, headless,
  `Channel = "chrome"`, SwiftShader args for WebGL; wait on `location.pathname` for Blazor navigation.
- Background jobs ignore SIGINT in a non-interactive shell: stop an agent with SIGTERM.
- `WebApplicationFactory` builds its server lazily and not thread-safely: start it (`_ = factory.Server`)
  before another thread uses it (see `ServiceTests/Agent/AgentUnderTest.cs`, `ManagerSlot.Bring`).
