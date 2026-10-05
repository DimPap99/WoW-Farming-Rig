# PathingAPI — standalone pathing server

Hosts `PPatherService` over HTTP (+ Swagger, + a Blazor route visualiser) without a game
client attached. Used as **V1 Remote** by the bot, as the bake/authoring host, and as the
map you open at `/Leaflet`.

## Run

```powershell
dotnet run --project PathingAPI -c Release -- --exp=cata
```

On Linux/macOS, `PathingAPI/run.sh --exp=som` (it `cd`s into the project first, because
`data_config.json` and `wwwroot` resolve from the working directory). The project is
`net10.0-windows` like the rest of the solution; `Directory.Packages.props` turns on
`EnableWindowsTargeting` off Windows so it compiles, and nothing it runs touches Windows APIs.

| Argument | Meaning |
|---|---|
| `--exp=<client>` | `som`/`tbc`/`wrath`/`cata`/`mop`/`legacy_*`. Defaults to `som`. Decides `/dbc`, `/tiles` and the navmesh era. |
| `--Pathing:Engine=` | `Navmesh` (default) or `SpotAStar` |
| `--bake=<continent>` | background navmesh bake at startup; `all` for every continent |
| `--bake-area=<continent>` | area-id grid + subzone bounds extraction |
| `--urls http://…` | override the port (default 5001) |

## Layout

```
Controllers/PPatherController.cs  the whole HTTP surface (routes, bake, stats, viz)
AnTcp/AnTcpPathServer.cs          AmeisenNavigation binary protocol, opt-in
Pathing/PatherGate.cs             the one lock every caller takes (see below)
Pathing/RouteSearch.cs            the only code that runs the two-call search
RateLimit/                        [RateLimit] = hold the gate or answer 429
Watch/WatchBroadcaster.cs         engine events -> watch hub, once for the server
Watch/WatchSettings.cs            what is streamed (navmesh, geometry, test points)
Hubs/WatchHub.cs                  the hub; counts viewers so an idle server streams nothing
Status/ServerStatus.cs            the status bar's snapshot (tile count cached 15 s)
Catalog/                          search-panel places, test routes, recorded path files
Hosting/                          CLI arguments, DI registration, JSON converters
Startup.cs                        composition: logging, MVC, SignalR, Swagger
Shared/                           shell (MainLayout, NavMenu, StatusBar) and the tools:
                                  Viewport3D, Panel, SearchPanel, BusyButton, ...
Pages/                            Index (live view + test routes), Leaflet (map), Search,
                                  Route (recorded paths), CloseNPC (NPC routes), Swagger
wwwroot/css/site.css              the whole theme; pa- prefixed so the map keeps Bootstrap
wwwroot/js/pathing-scene.js       the Babylon 3D view; Babylon itself is fetched on demand
wwwroot/js/pathing-ui.js          script loader, test-route fetch, toast
```

## Things that will bite you

**One client at a time.** `[RateLimit]` holds the `PatherGate` for the whole action and
answers a concurrent request with **429**, and the V1 client turns that into an empty
route — indistinguishable from "no path exists". It dates from the SpotAStar era when a
search cost seconds. Do not point several bots at one instance; see the README's
"Running multiple bots".

**The service is a singleton with a two-call search.** `SetLocations` then `DoSearch`
over shared state, plus `PathJitterYards` / `PathEdgeMarginYards` / `lastStartIndoors`.
Search only through `RouteSearch`, under the `PatherGate`: HTTP takes it via
`[RateLimit]` (429 when held), AnTCP with `Enter()` (waits), the web UI with
`FindAsync`/`RunAsync` (waits, off the circuit thread). Before the gate, the three each
guarded the search their own way - a racy static bool, a private semaphore, nothing -
so they could still interleave with each other. `RouteSearch.Find` sets jitter and edge
margin on **every** call; endpoints that did not used to inherit the previous caller's.

**The watch stream is one broadcaster, not one per page.** `WatchBroadcaster` subscribes
to the engine once and sends to every hub client; engine events only queue work, so the
search thread never waits on a socket. Each open page used to subscribe and broadcast
itself, so N tabs sent everything N times to N clients. With no hub connection it queues
nothing. The 3D toolbar's "Stream" group edits `WatchSettings`, which apply to all
viewers - that is what the wire always did.

**Opening a 3D page resets the engine** (`Viewport3D ResetOnOpen`), so its chunks and
tiles stream to the new view; the live view (`/`) and the map do not, so watching never
disturbs a bot. A reset drops resident tiles and the next query reloads them from disk.

**"Clear spot cache" keeps the navmesh.** `DataConfig.DeletePPatherCache` deleted every
folder under `PathInfo`, `navmesh` included - one click away in the old toolbar, it wiped
the downloaded tiles. It now skips `PathInfo/navmesh`; removing tiles is
`DELETE Bake/Cache`.

**Switching continent disposes the engine.** `Initialise(mapId)` calls `Reset()` on a
different map, dropping resident tiles. Two callers on different continents thrash.

**The era comes from `DataConfig.Exp`, never from the URL.** `Pages/Leaflet.razor` used
to trust the `{expansion}` route segment (defaulting to `"som"`), which rendered one
era's art against another's data. It now overrides from the server and logs the
mismatch. The continent does come from the URL - the map's own selector reloads the page
at `/Leaflet/<exp>/<continent>` - so the map's search panel is locked to it.

**Only the pages that draw in 3D load Babylon.js** (~8.6 MB, `pathing-scene.js`
fetches it on the first `create`). Each `Viewport3D` creates one engine and disposes it
when the page goes; the old script built a new engine, render loop and set of document
listeners per visit and kept them all. It also defined globals (`removeMesh`, `clear`)
that overwrote the map's; everything now hangs off `window.pathingScene`.

**`Json/MPQ` is not era-aware** — it is one directory for whatever client you point it
at. Running `--exp=cata` with wrath archives in there silently loads wrath geometry and
only fails on a continent wrath lacks. For a different client, build a mirror root of
junctions whose `MPQ` points at that client's `Data`.

**A running instance locks the build output.** Solution builds fail with MSB3027 naming
the pid. Compile-check with `-c Debug` instead of killing the user's server.

**`GET SelfTest` reports what the *active engine* needs, not MPQ.** For `Navmesh` it
counts baked `.dnm` per continent for the active era and returns
`{ok, engine, exp, era, detail, mpqPresent, totalTiles, continents[]}`; for `SpotAStar`
it falls back to the archive check, because that engine has no tile cache. It used to be
`MPQSelfTest()` alone, which reported failure on every tiles-only or CASC install even
though pathing worked perfectly.

## AnTCP (binary, opt-in)

Speaks AmeisenNavigation's protocol so a `RemoteV3`-configured bot can use this server
instead of the external binary. Enable in `appsettings.json`:

```json
"Pathing": { "AnTcp": { "Enabled": true, "Ip": "127.0.0.1", "Port": 47111 } }
```

**Port 47111 is deliberate — it matches the bot's shipped `portv3`** so enabling the
listener is the only step. It used to default to 47110 (AmeisenNavigation's default),
which meant the two shipped configs never connected out of the box. Changed 2026-07-27;
if you are pointing at a real AmeisenNavigation install instead, that one still uses
47110.

**A port mismatch fails silently.** `PingServer` fails, `GetPather` falls through to
`RemoteV1`/`Local`, and the bot walks perfectly well — so "the remote server is being
ignored" is the symptom, not an error. Check the `Using {Type}` log line.

Wire format, little-endian:

```
request   int32 size(payload+1) | byte type | int32 mapId | int32 flags | float start[3] | float end[3]
response  int32 size(payload+1) | byte type | Vector3[n]        (single all-zero Vector3 = no path)
```

Only `PATH` (type 0) is implemented — it is the only message `RemotePathingAPIV3` ever
sends. Concurrent clients queue rather than 429.

**The z trap:** when the client has no height it sends `area.LocTop / 2`, and `LocTop`
is a world **Y** bound, not a height — for Elwynn that is -3969 against terrain at 82.
AmeisenNavigation absorbs it with a very tall poly-search extent; this engine keeps tight
vertical extents so multi-floor buildings resolve, so `SnapImplausibleZ` puts any z more
than 128 yd off the surface back on it. Do not "fix" this by widening the resolver.
