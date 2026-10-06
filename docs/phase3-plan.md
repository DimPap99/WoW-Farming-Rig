# Phase 3 - plan, progress and what is left

**Status (2026-10-06):** all six steps and the finishing docs are built and pushed to `main`. What
is left needs Windows, and most of it a game: the Windows checklist below (item 1 passed), then decision 7, deleting BlazorServer's
Forever runs page. This file holds the whole plan as agreed, what each step delivered and how it
was checked, and the list to test on Windows. The design itself is
[service-architecture.md](service-architecture.md); what was found along the way is in
[known-issues.md](known-issues.md).

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

### Step 5 - the Control Center's fleet, console and assignments - DONE

The user's "console per account": one place to see every bot, give it a route or a profile, start
and stop it, and read its log. Built and checked on the Windows PC with simulated agents; no game.
`ControlCenter/CLAUDE.md` has the layout and what bites.

- `Hosting/ServiceStatus`: the polling loop both pollers now share (poll under a semaphore,
  `Online`, `Error`, `Changed`, `Reconnected`, one log line when the state flips); a subscriber
  that throws is logged and stops neither the others nor the loop. `PathingStatus` keeps its
  capabilities and health on it, and `Bots/BotManagerStatus` adds the `ManagerHealth`.
  `ServiceDescription` names a service, its address and how to start it; `OfflineNotice` takes
  it, for both services.
- `Bots/FleetFeed`: the one hosted client of `/fleetHub` (MessagePack, `X-Api-Key`), its
  connection from an injected `IFleetConnectionFactory`. It reconnects forever through its own
  loop, as `AgentLink` does, rather than SignalR's retry policy, which only covers a connection
  that was once up. After every connect it reads the bots over REST and raises `Resynced`; what
  the hub streams during that read is held and applied after the list. It keeps each `BotInfo`
  (a snapshot updates `Latest`, `Run` and `Game`) and raises `Changed`, `Log` and `RunEnded`.
- **Fleet** (`/fleet`): a card per bot with its state, client, vitals, activity, agenda, place
  (the zone's name from `WorldCache`), progress and assignment, Start and Stop, and the way to its
  console. **Bot console** (`/fleet/{agentId}`): the same live panel, Start and Stop, the
  assignment form, the run history, Forget for an offline bot, and the log terminal.
  `WaitingForClick` is said in plain words on both. A "Bots" section in the rail; a manager chip
  in the status bar (bots online of all, a link to the fleet).
- The log: `Components/Shared/Terminal` with BlazorServer's terminal look under `pa-` names, and
  `BotLogBuffer`, which joins the manager's backlog to the stream (lines held while the backlog
  is read; a line known by its time and text; a backlog read again after a reconnect fills in
  what was missed, in time order).
- The form's fields are `AssignmentDraft`: a choice not made yet, or `AssignmentRules`' reason as
  you type, keeps Save off, and a save checks again. A route a run would refuse is listed but
  cannot be chosen. Commands are sent once; 409 and 504 show the manager's reason.
- `Utilities/SimAgent` (not in the .sln): `AddBotAgent` with a made-up runtime (a wait for the
  click, then a walk in Durotar with vitals, activity, agenda, progress and a log line a second),
  client probe, profiles and process. `--Sim:Client=som` is a Classic PC that refuses Forever runs.
- Decided along the way:
  - While the manager is down, a page keeps the last fleet it knew, dimmed, under the offline
    notice, as the 3D view does, instead of replacing it: the first browser run showed a console
    coming back with only the new manager's few log lines.
  - The console keys its root by the bot, not each part: two sibling parts keyed alike threw on
    the first re-render and ended the circuit.
  - The agent logged a stop as "The manager asks: Stop null"; it now logs "Stop".
  - Known issue 26 (ARIA states written as empty values, so the 3D view's toggles never looked
    pressed) was found by these checks and fixed.
- **Verified on Windows, 2026-10-05:**
  - Tests: 19 in `ServiceTests/Fleet` - the feed against the in-memory manager and fake agents
    (an agent appears; snapshots update it; log batches and ended runs are raised; a restarted
    manager is read again; what streams during a read is applied after the list), both pollers,
    the log join, the draft, and the pages rendered with the manager down, with a bot, and with a
    bot known before the manager went down. `ControlCenterTests` moved onto the shared
    `ControlCenterFactory`. `dotnet test -c Release`: 220 passed, 1 skipped (the minimap tiles).
  - Browser: Playwright .NET with the installed Chrome (headless) against the Pathing Service,
    the Bot Manager and the Control Center on side ports, with three simulated agents (two
    Forever, one Classic); all 12 checks passed. The fleet lists them, with the live chip and the
    status bar's count. A console shows the agent's backlog. Minutes 0 are refused with the
    rules' reason, and a route saves. Start asks for the click in plain words, then the bot runs,
    its zone shows by name and the log streams with no line twice. Start is off while a run is
    under way, and Stop ends it into the history. The manager stopped under an open console
    shows the notice over the dimmed console and comes back by itself, its run history kept and
    every log line once. A Classic bot refuses a bad path, saves a profile, starts and stops. A
    Forever route on the Classic bot is refused, and the history says why. An agent stopped and
    started under the open fleet goes offline and comes back. Forget takes an offline bot off the
    fleet, and it returns when its agent dials in. Start and Stop work from a fleet card.
- Not in this step: a real run driven from the console (Windows checklist items 5 to 7, with the
  user at the PC), and each bot's position as a marker in the 3D live view (the watch hub can
  draw it; "The bot's live view" in the design doc's Phase 3 starting notes).

### Step 6 - data sync, logon task, restart loop - DONE (`c3544d5d`)

- **The sync endpoints** (`PathingService/Sync`). `GET /api/v1/sync/{set}` answers a `SyncManifest`:
  each file's name, size and `SyncHash` (SHA-256 with CRLF read as LF).
  `GET /api/v1/sync/{set}/files/{**name}` answers the file's bytes as stored. The sets
  (`Contracts.Pathing.SyncSets`) are `paths` (`Json/path` without `_forever`), `forever-routes`, and
  `costzones`, which needs `?era=`, one of `DataConfig.Eras`, whatever `--exp` the service runs. Names
  pass each folder's own rules (`PathName`, `ForeverRouteLibrary.TryResolve`, relative names for
  zones). The `paths` manifest (554 files, 1.7 MB) answers in about 15 ms, so nothing is cached.
- **`BotAgent/Sync/DataSync`**, a run's preparation (state `Syncing`).
  - What it pulls:
    - a Forever run: its route;
    - a Classic run: the override, and every path the profile names;
    - both: the cost zones of the running client's era (`AgentGame.Era`). With no client running
      there is no era to sync, and the runtime refuses the run next anyway.
  - It fetches only what differs. A local copy that differs from what `Json/.sync/state.json` says
    was last synced is kept in `Json/.sync/backup/<time>/` first. It writes atomically and never
    deletes.
  - With the service down, or none set (`PathingService:Url`), the run uses this PC's copies and the
    line says so. A route or an override this PC lacks refuses the run, with why.
  - A stop while syncing ends the run "stopped before it started": `BotRuntimeHost` catches the
    cancellation.
- **`ProfilePaths`** reads a profile's JSON (comments and trailing commas allowed). It takes every
  `PathFilename`, `OverridePathFilename` and `PathsFilenames`, at any depth and in any letter case,
  and reads `\` as `/`. The 101 repository profiles name 66 paths, all in the library.
- **Shared pieces.**
  - ServiceClients: `SyncHash`; `AtomicFile`, moved from the Pathing Service, with `WriteAllBytes`;
    `IPathingClient.GetSyncManifestAsync`/`GetSyncFileAsync`, `ServiceHttp.GetBytesAsync`,
    `PathingServiceOptions.FromIfConfigured`.
  - DataConfig: `ForeverRoutes` (`ForeverRouteFile.Folder` is defined from `ForeverRouteFolder`),
    `RoadOf(era)`, `Eras` and `Sync`. BotAgent references DataConfig now.
- **HeadlessServer.**
  - `--pathing-service`, and the sync in the host, so a profile run on the command line syncs too
    when a service is set.
  - `ResidentHost`, in agent mode: a host that fails is built again after 3 s. A run still under
    way, or a restart asked for, ends the process with 75 instead.
  - Found while planning: on .NET 10 a faulted `BackgroundService` stops the host and `Run()`
    returns with exit code 0 (Microsoft's breaking-change note moves that to .NET 11), so faults are
    read from the services' `ExecuteTask`.
  - `run-agent.ps1` starts the agent again unless it exits 0. It waits 5 s, doubling to 5 min while
    the agent keeps failing within a minute, and logs to `run-agent.log`.
  - `install-agent-task.ps1` registers the logon task: current user, interactive, limited,
    minimized, restart on failure, no time limit, one instance. `uninstall-agent-task.ps1` removes
    it and leaves a running agent alone.
- **Logging.** `headless_appsettings.json` sets `System.Net.Http.HttpClient: Warning` and
  `Polly: Fatal`. Polly logs its last failed attempt at Error, with the stack trace, and that
  reached the Control Center's console: a simulated agent showed it in the browser check.
- **Verified on Linux, 2026-10-06.**
  - Tests: 339 in all (`ServiceTests/Api/SyncApiTests`, `ServiceTests/Agent/DataSyncTests` and
    `ProfilePathsTests`, `AgentTests/Agent/ResidentHostTests` and `DataSyncCompositionTests`).
  - The real HeadlessServer agent (no game) ran against a scratch Pathing Service and Bot Manager on
    side ports, each with a data root of its own:
    - A Forever assignment pulled `crossroads_trip.json` byte for byte and recorded it; then the run
      was refused with "no WoW client".
    - A local edit made while the file also changed centrally: the edit went to
      `.sync/backup/<time>/forever-routes/` and the central version replaced it. The next start
      said "Up to date".
    - `Druid_10_bear.json` with a path override pulled 3 files: the override,
      `_pack\1-20\Night elf\09-11_Teldrassil_Near Lake Al'Ameth.json` and its vendor path.
      `Druid_1-10.json` pulled its 8 path groups.
    - With the service stopped, a start said it uses this PC's copies, after about 4 s of retries.
      A route the PC lacked was refused with that reason. Once the service was back, the next run
      pulled straight away.
    - No HTTP or Polly line reached the log.
  - `run-agent.ps1` under pwsh 7, against a Release build:
    - after `kill -9` it logged "ended with 137 (it failed)" and started the agent again 5 s later;
    - after a SIGTERM the agent exited 0 and the loop ended;
    - `--` reaches dotnet: the arguments are one splatted array.
  - In Chrome, a simulated agent with `--Sim:Data`: the console showed "Syncing data" while the sync
    waited on a stopped service, then the sync's line, then "Waiting for a click".
  - The install and uninstall scripts parse. `ScheduledTasks` exists only on Windows, so registering
    the task is Windows checklist item 11.

### Finishing - DONE

- `docs/service-architecture.md`: the status line, "Phase 3 as built" in place of the starting
  notes, the sync endpoints.
- `docs/known-issues.md`: item 31 (a file deleted centrally stays on the bot PCs), and the agent's
  log levels under item 30. Item 24 waits for the runs page to go.
- CLAUDE.md files: the root, BotAgent, HeadlessServer, PathingService, DataConfig, Utilities.
- `docs/forever-routes.md`: running a route from the Control Center through an agent.
- `docs/pathing-api.md`: the Control Center's pages as they are now (the world view, the fleet),
  the Bot Manager and the agents; `docs/configuration.md`: HeadlessServer's agent options; the
  README's feature summary.
- Merged fast-forward to `main` and pushed.

## Windows checklist

Compile-checked on Linux, **not verified**: each needs Windows and a game. A Claude session on the
Windows PC can run these; report what happens next to each.

**Before:** `git pull`; install the .NET 10 SDK; `git submodule update --init`; the addons as in
`docs/forever-port.md`; `HeadlessServer\install.bat` copies `data_config.json`, `addon_config.json`
and `frame_config.json` from BlazorServer.

1. **Build and tests.** `dotnet build WoWFarmingRig.sln`, `dotnet build Utilities/ForeverControllerTrial`,
   `dotnet build Utilities/ForeverVisualReader`, `dotnet test` (202 expected then, 339 since step 6;
   tile tests skip without the precata tiles). `dotnet run --project Utilities/ForeverVisualReader -c Release -- --logic-test`
   runs the Forever logic checks, which now import `SharedLib.Forever`.
   **Passed on Windows, 2026-10-05.** All three build with no errors. `dotnet test`: 201 passed,
   1 skipped (the minimap tiles); the precata navmesh is on that PC, so the tile tests ran.
   `--logic-test`: 302 checks, all passed. One test fix on the way: two tests took port 9 (discard)
   as closed, but Windows' Simple TCP/IP Services listens there and never answers, so the Control
   Center test hung for 100 s and failed. Both now use a free port (`ServiceTests/Api/ClosedPort`).
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
   `http://localhost:5030/swagger`), the Pathing Service and the Control Center, then
   `HeadlessServer\run.bat --agent --manager http://localhost:5030`. The fleet page
   (`http://localhost:5020/fleet`) shows the bot with its client; its console assigns
   `crossroads_trip.json` for 5 minutes, starts and stops it, and shows the log and the run.
   Without the Control Center, Swagger or PowerShell do the same:
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
11. **The logon task, the restart loop and the sync.** Only the last point needs the game.
    - `HeadlessServer\build.bat`, then
      `powershell -ExecutionPolicy Bypass -File HeadlessServer\install-agent-task.ps1 -Manager http://localhost:5030 -PathingService http://localhost:5010`
      (or set both in `headless_appsettings.json` and pass neither). Task Scheduler lists
      "WoW Farming Rig agent": at logon of this user, only when logged on, without highest
      privileges.
    - Log off and on (or reboot): a minimized window runs the agent, the fleet page shows the bot
      online, and `HeadlessServer\run-agent.log` says "Starting the agent".
    - End `HeadlessServer.exe` in Task Manager: the window logs that it ended and starts it again in
      5 s, and the bot comes back online. Ctrl+C in the window: "Application is shutting down",
      "The agent was stopped (exit 0)", and it stays down until the next logon or
      `Start-ScheduledTask -TaskName "WoW Farming Rig agent"`.
    - The sync into the git checkout:
      - Save a new route in the Control Center's Forever routes and assign it. Start pulls it into
        `Json\path\_forever` ("Pulled 1 file ...") before the run asks for the click, or is refused
        with "no WoW client"; `git status` shows the file.
      - Edit it locally, change it in the Control Center and start again: the edit is in
        `Json\.sync\backup\<time>\`.
      - Stop the Pathing Service and start: the console says the run uses this PC's copies.
    - `uninstall-agent-task.ps1` removes the task; a running agent keeps running until Ctrl+C.
    - With the game: a Classic assignment whose profile's paths are only on the Pathing Service
      pulls them, and the bot walks them.

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
- Step 6's sync check needs two data roots: the service's (as above) and the agent's (only `class`
  linked). The agent's `HeadlessServer.dll` runs from a folder whose `data_config.json` points at its
  root, with a copy of `headless_appsettings.json`. Side ports 5210/5220/5230 keep clear of the
  user's services (5010-5030) and the browser checks (5110-5130).
- `cd dir && nohup cmd &` backgrounds the whole list: `$!` is the subshell, not dotnet. Find the
  process by its port (`ss -ltnp`), and remember that `pgrep -f` matches the shell running it too
  (check `/proc/<pid>/exe`).
