# The Pathing Service and the Control Center

Pathing is built into the bot, so you don't need to do anything special except provide
navmesh tiles - `download-navmesh.ps1`, no MPQ files required (see
[Setting up navigation](navigation.md#setting-up-navigation--the-short-version)). Each bot plans
and steers in its own process.

Two central services run beside the bots, on any OS (see
[service architecture](service-architecture.md)):

- **Pathing Service** (`PathingService/`, port 5010): plans routes for many callers at once,
  holds the recorded paths and cost zones, bakes navmesh tiles, serves the map's data and
  streams every route it plans. Its API has Swagger at `/swagger`.
- **Control Center** (`ControlCenter/`, port 5020): the web UI to watch routes, author paths and
  cost zones and run bakes. It is a client of the Pathing Service and needs no game client.

## Running them

With the .NET 10 SDK, on Linux, macOS or Windows:

```bash
pwsh scripts/download-navmesh.ps1     # pwsh: dotnet tool install --global PowerShell
pwsh scripts/download-leaflet.ps1
PathingService/run.sh                 # add e.g. --exp=forever
ControlCenter/run.sh
```

Then open http://localhost:5020. On Windows without bash, run each from its own folder, because
the Pathing Service reads `data_config.json` from its working directory:

```ps
cd C:\WoW-Farming-Rig\PathingService
dotnet run -c Release
```

```ps
cd C:\WoW-Farming-Rig\ControlCenter
dotnet run -c Release
```

Run the Pathing Service against the same `Json` folder the bot reads: on the bot's PC, or with
the authored files committed. Recorded paths and cost zones are files the bot reads, and a
running bot picks up a cost-zone save without a restart.

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

**Control Center settings**: `PathingService:Url` (default `http://localhost:5010`), `--urls`
(default `http://localhost:5020`) and `--LogFile=`.

## The pages

The status bar across the top shows the client data and era, how many navmesh tiles are on disk
and for which continents, whether the game files are on the service's machine ("tiles only"
when not), a running bake's progress, the planner's continents with their route counts and
last times, and whether the Pathing Service answers. A page whose service is down says so, and
comes back by itself.

| page | what it is for |
|---|---|
| **Live view** (`/`) | 3D view of every route the Pathing Service plans, from any caller, plus a filterable list of known-trouble **test routes**. Each runs as a real route request and shows its result; routes on continents with no tiles are struck through. |
| **Map** (`/Leaflet`) | The minimap with spawns, zones, recorded paths, roads and danger zones (draw, edit, save), navmesh coverage and bake controls, and a route search on the map's continent. |
| **Route search** | Plan between named places: curated spots plus every `.tele` location, filtered as you type. Options: jitter and seed. |
| **Path library** | Pick a recorded path (its zone is guessed from the name, its screenshot shown), draw it, step through its legs planning each, open it in the editor or delete it. |
| **Path editor** | The points of one recorded path: select, move, insert, remove, halve or double them; save, or save as a new path. |
| **NPC routes** | Pick a zone and an NPC type, roll a random start and route to the NPC. |
| **API** | The Pathing Service's Swagger: every endpoint with examples. |

In the 3D views, **Show** toggles what this view draws: the navmesh, the wireframe, and
**Terrain**, the game geometry under the tiles nearest the camera, by kind (ground, liquid,
objects, models). Terrain is extracted on demand and needs the game files where the Pathing
Service runs. **Replay** asks the service again for the tiles it holds and the last routes;
**Clear** empties this view only. Opening a view never resets anything on the server.
Babylon.js is only fetched when a 3D page first opens.

## Baking

Baking needs the game files where the Pathing Service runs: the archives in `Json\MPQ` for the
Classic clients, `ForeverCasc:Install` for WoW: Forever. Without them the bake controls say so.
Start one from the map's sidebar (bake the ADT under a click, the continent, cancel, clear a
continent's tiles), with `--bake=`, or through `POST /api/v1/bake`. When a bake ends, the
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

`Found`, `NoPath` and `NotBaked` are answers (200); only a bad request (400) and a continent
busy past its wait (503) are errors. The route shows in every open live view.

## The bot's own map

BlazorServer's map is the same map. It serves its tiles and data from the bot's `Json` folder
and forwards `/api/v1` to `PathingService:Url` (`BlazorServer/appsettings.json`), so recorded
paths, cost zones, navmesh coverage and bakes there are the Pathing Service's, and say
"offline" while it does not run.

## The legacy remote modes

`Pathing:Mode=RemoteV1` and `RemoteV3`, and `Pathing:PathVisualizer`, talked to PathingAPI on
port 5001 and to its AnTCP listener on 47111. PathingAPI is gone (Phase 2). A bot set to one of
them finds nothing at startup and plans locally instead, with one log line. The clients are
removed in Phase 4; until then keep the bot on `Local`.
