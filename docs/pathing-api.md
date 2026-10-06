# The central services: Pathing Service, Bot Manager, Control Center

Pathing is built into the bot, so you don't need to do anything special except provide
navmesh tiles - `download-navmesh.ps1`, no MPQ files required (see
[Setting up navigation](navigation.md#setting-up-navigation--the-short-version)). Each bot plans
and steers in its own process.

Three central services run beside the bots, on any OS (see
[service architecture](service-architecture.md)):

- **Pathing Service** (`PathingService/`, port 5010): plans routes for many callers at once,
  holds the recorded paths, Forever routes and cost zones, bakes navmesh tiles, serves the map's
  data and the files agents sync, and streams every route it plans. Its API has Swagger at
  `/swagger`.
- **Bot Manager** (`BotManager/`, port 5030): the bots, their assignments, start and stop, and
  each bot's run history. Every game PC's bot agent dials out to it.
- **Control Center** (`ControlCenter/`, port 5020): the web UI to watch routes, author paths,
  routes and cost zones, run bakes, and run the bots. It is a client of the other two and needs
  no game client.

## Running them

With the .NET 10 SDK, on Linux, macOS or Windows:

```bash
pwsh scripts/download-navmesh.ps1     # pwsh: dotnet tool install --global PowerShell
pwsh scripts/download-leaflet.ps1
PathingService/run.sh                 # add e.g. --exp=forever
BotManager/run.sh
ControlCenter/run.sh
```

Then open http://localhost:5020. On Windows without bash, run each from its own folder, because
the Pathing Service reads `data_config.json` from its working directory:

```ps
cd C:\WoW-Farming-Rig\PathingService
dotnet run -c Release
```

```ps
cd C:\WoW-Farming-Rig\BotManager
dotnet run -c Release
```

```ps
cd C:\WoW-Farming-Rig\ControlCenter
dotnet run -c Release
```

Recorded paths, Forever routes and cost zones are files the bot reads from its own `Json`. A bot
agent pulls the ones a run needs from the Pathing Service before the run starts (see
[The Bot Manager and the agents](#the-bot-manager-and-the-agents)). BlazorServer, and an agent with
no Pathing Service set, read their `Json` as it is: for them, run the Pathing Service on that PC,
or commit the authored files. A running bot picks up a cost-zone change without a restart.

**Pathing Service arguments**

| argument | Description | Default |
| ---- | ---- | ---- |
| `--exp=<client>` | Which client's data to serve. Decides `/dbc`, `/tiles`, the navmesh era and the cost zones | `som` |
| `--bake=<continent>` | Start a background navmesh bake as the service comes up; `all` for every continent. Progress is on `GET /api/v1/bake` | off |
| `--bake-area=<continent>` | Extract the area-id grid, so area lookups answer without game files; `all` for every continent | off |
| `--urls http://…` | Listen elsewhere | `http://localhost:5010` |
| `--LogFile=` | The rolling log beside the service; empty turns it off | `out.log` |

For WoW: Forever, bakes and terrain read the installed client through CASC: set
`ForeverCasc:Install` and `ForeverCasc:Listfile` in `PathingService/appsettings.json`.

**Bot Manager settings**: `--urls` (default `http://localhost:5030`), `--Data=` (where the bots,
assignments and run history are kept; default `./data`) and `--LogFile=`.

**Control Center settings**: `PathingService:Url` (default `http://localhost:5010`),
`BotManager:Url` (default `http://localhost:5030`), `--urls` (default `http://localhost:5020`) and
`--LogFile=`.

## The pages

Every page with a map shares one map, the world view, flat or tilted in 3D. It draws the game's
map art where the service has it, and the navmesh's shaded relief everywhere; zoomed in, each
tile's walkable polygons. The zone, map coordinates and height under the pointer show at the
bottom.

The status bar across the top shows:
- the client data and era, and the baked tiles;
- whether the game files are on the service's machine ("tiles only" when not);
- a running bake;
- the bots online;
- whether each service answers.

A page whose service is down says so, and comes back by itself.

| page | what it is for |
|---|---|
| **Live view** (`/`) | Every route the Pathing Service plans, from any caller, as it plans it; and the known-trouble **test routes**, each run as a real route request. |
| **Map** (`/map`) | What is where in a zone: NPCs by type, elites, herbs, veins, skinnable creatures, mailboxes and places; creatures found by name. Points, lines and shapes can be drawn and measured, with their zone map coordinates. |
| **Fleet** (`/fleet`) | Every bot the Bot Manager knows: its state, client, vitals, activity, place and assignment, with Start and Stop. A bot's **console** adds the assignment form, the run history and the bot's log. |
| **Routes** (`/routes`) | The route designer: waypoints clicked on the map or taken from named places, each leg planned. Recorded paths open, change and save here, and a route saves as a Forever route. |
| **NPC routes** | Route to an NPC of a type in a zone, from a random start. |
| **Cost zones** | Roads routes are drawn towards, and danger zones they avoid or never cross: draw, edit, save. |
| **Navmesh** | Where the navmesh is baked and which tiles the planner holds. Bake an ADT or a continent, or rebuild the area grid. |
| **Forever routes** | WoW: Forever route files as text, checked as a run loads them and drawn beside the text. |
| **API** | The Pathing Service's Swagger: every endpoint with examples. |

## Baking

Baking needs the game files where the Pathing Service runs: the archives in `Json\MPQ` for the
Classic clients, `ForeverCasc:Install` for WoW: Forever. Without them the bake controls say so.
Start a bake from the **Navmesh** page (the ADT chosen on the map, or the whole continent; cancel;
clear a continent's tiles), with `--bake=`, or through `POST /api/v1/bake`. When a bake ends, the
planner looks at the disk again, so the new tiles are used by the next route.

**`Json\MPQ` is not era-aware**: it is one directory for whichever client you point it at, so
running `--exp=cata` with Wrath archives in there loads Wrath geometry silently and only fails
on a continent Wrath does not have.

## The API

Everything is under `/api/v1` (`Contracts/Pathing/PathingApi.cs`); Swagger lists it with
examples. A route:

```bash
curl -s localhost:5010/api/v1/routes/find -H 'content-type: application/json' -d '{
  "uiMapId": 1446, "space": "Map",
  "from": { "x": 51.0, "y": 29.3, "z": 0 }, "to": { "x": 38.7, "y": 20.1, "z": 0 } }'
```

`Found`, `Partial`, `NoPath` and `NotBaked` are answers (200). Only a bad request (400) and a
continent busy past its wait (503) are errors. The route shows in every open live view. The sync
endpoints (`/api/v1/sync`) serve the files agents mirror; the Bot Manager's API is at
`http://localhost:5030/swagger`.

## The Bot Manager and the agents

Each game PC runs one bot agent: HeadlessServer with `--agent`. It dials out to the Bot Manager,
so it needs no inbound port. It runs what the manager assigns, one run at a time, and keeps
playing while the manager is down.

```ps
cd C:\WoW-Farming-Rig\HeadlessServer
.\build.bat
.\run.bat --agent --manager http://<central-pc>:5030 --pathing-service http://<central-pc>:5010 --name "PC 1"
```

To start it at every logon instead, install its task once (no administrator rights needed):

```ps
powershell -ExecutionPolicy Bypass -File .\install-agent-task.ps1 -Manager http://<central-pc>:5030 -PathingService http://<central-pc>:5010
```

The task runs `run-agent.ps1` in a minimized window. It starts the agent again whenever the agent
ends with anything but 0: a crash, or a run that could not be torn down. Ctrl+C in that window
stops the agent, releasing the game's keys first, until the next logon. `uninstall-agent-task.ps1`
removes the task. Given no URLs, both take them from `headless_appsettings.json`.

The bot then shows on the **Fleet** page with its client. Its console takes an assignment, starts
and stops it, and shows the bot's log and runs. An assignment is either:
- a Forever route and its minutes, or
- a Classic profile from that PC's `Json\class`, with an optional path in place of its first.

**What a run syncs.** Before a run starts, the agent pulls what it needs from the Pathing Service
into its own `Json`:
- a Forever run: its route;
- a Classic run: the path override, and every path its profile names, path groups included;
- both: the cost zones of the client's era.

Only files that differ are fetched; line endings do not count. A local file changed since the
agent last synced it is kept in `Json\.sync\backup\<time>\` before it is replaced, and the console
says so. Nothing is ever deleted. With the Pathing Service down, or none set, the run uses the PC's
own copies; only a route or path override the PC lacks refuses it. On a PC that is a git checkout,
synced files show as local changes.

**Across machines.** The services listen on localhost only. To reach them from a game PC, bind
them beyond it (`--urls=http://0.0.0.0:5030`) and set the same `ApiKey` on every service and agent,
in their settings or the environment. A service bound beyond localhost without a key refuses to
start. The Control Center adds the key when it forwards the browser's calls, so a browser never
holds it.

## The bot's own map

BlazorServer keeps its own Leaflet map (`MapAssets`). It serves its tiles and data from the bot's
`Json` folder and forwards `/api/v1` to `PathingService:Url` (`BlazorServer/appsettings.json`), so
recorded paths, cost zones, navmesh coverage and bakes there are the Pathing Service's. Those
parts say "offline" while the service does not run.

## The legacy remote modes

`Pathing:Mode=RemoteV1` and `RemoteV3`, and `Pathing:PathVisualizer`, talked to PathingAPI on
port 5001 and to its AnTCP listener on 47111. PathingAPI is gone (Phase 2). A bot set to one of
them finds nothing at startup and plans locally instead, with one log line. The clients are
removed in Phase 4; until then keep the bot on `Local`.
