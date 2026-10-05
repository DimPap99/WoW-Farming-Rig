# ControlCenter - the central web UI

The Control Center from `docs/service-architecture.md`: the place to watch routes, author paths
and cost zones, and administer bakes, for every bot. It holds no data and plans nothing. It is a
client of the Pathing Service (and, from Phase 3, the Bot Manager), and it never references
`PPather`, `Core`, `Game`, `WinAPI` or `Frontend`: only `Contracts`, `ServiceClients` and
`MapAssets` (static files, no code).

## Run

```bash
PathingService/run.sh      # first: the Control Center is its client
ControlCenter/run.sh       # http://localhost:5020
ControlCenter/run.sh --PathingService:Url=http://other-host:5010
```

The port is `Contracts.ControlCenter.ControlCenterDefaults.DefaultPort` (5020); `--urls`
overrides it, `--LogFile=` (empty) turns the log file off.

## Layout

```
Program.cs                         host (a class, so tests can host it beside the service)
Hosting/ControlCenterRegistration  AddControlCenter / UseControlCenter - the composition root
Hosting/ServiceProxy.cs            YARP: /api/v1, /watchHub, /swagger and WorldData to the service
Hosting/ControlCenterLogging.cs    Serilog to console + daily file
Pathing/PathingStatus.cs           one poller (2 s): online, capabilities, health -> Changed
Pathing/WorldCache.cs              zones, continents, places, test routes; reloads on reconnect
Components/App.razor, Routes.razor the document; InteractiveServer without prerendering
Components/Layout/                 MainLayout (rail), NavMenu, StatusBar
Components/Shared/                 ControlCenterComponent (base), Viewport3D, SearchPanel,
                                   Panel, BusyButton, ResultLine, OfflineNotice, ...
Components/Pages/                  LiveView (/), Map (/Leaflet/...), Search, PathLibrary (/paths),
                                   PathEditorPage (/paths/edit/...), NpcRoutes, ForeverRoutes
                                   (/forever-routes), ApiReference
wwwroot/css/site.css               the whole theme; pa- prefixed so the map keeps Bootstrap
wwwroot/js/pathing-scene.js        the Babylon 3D view; Babylon itself is fetched on demand
wwwroot/js/pathing-ui.js           script loader, toast, remembered rail state
```

## Things that will bite you

**The browser talks only to the Control Center.** The map's script fetches `/tiles`, `/dbc`,
`/api/v1/...` and connects to `/watchHub` on its own origin; `ServiceProxy` forwards them to
the Pathing Service (WebSockets included). A prefix the map needs that is not in
`ServiceProxy.PathingPrefixes` answers 404 here, not at the service.

**A down service is a state, not an error.** Pages read `PathingStatus.Online` and show
`OfflineNotice` instead of calling; they come back by themselves when the poller sees the
service again (`Reconnected`). Calls go through `IPathingClient`, which never throws for a
service that is down - only for the page's own cancellation (`Token`), which handlers catch.

**An exception thrown into a render or a handler ends the circuit.** The page then stops
updating until reloaded. Catch `JSException` around script calls in `OnAfterRenderAsync`, as
`Map.razor` and `Viewport3D` do: the map's `init` once failed on missing data and took the
whole page down.

**Subscribe through `ControlCenterComponent`.** `PathingStatus` raises its events on the
poller's thread: pair each `+=` with its `-=` in `Subscribe`, and redraw with `Redraw()`. A
component that awaits JS teardown implements `IAsyncDisposable` and calls `Dispose()` itself.

**The era comes from the service, not the URL.** The map page passes the service's client to
the map script; the `/Leaflet/{client}` segment is advisory, as the map's own continent selector
writes it. Forever has no minimap art, so its map page says so and points to the 3D view.

**The 3D view replays, it never resets.** On connecting, `pathing-scene.js` calls the hub's
`Replay`; nothing on the server is reset for a new viewer. The hub connection is stopped when
the view is left, so the service streams nothing to a page that has no scene.

**Terrain needs the game files where the service runs.** The toggle is disabled with the reason
when `PathingCapabilities.GameFiles` is false. It asks for the 25 tiles nearest the camera; the
meshes come back to this viewer only, under the four terrain layers.

**Bind text a button depends on with `@bind:event="oninput"`.** A plain `@bind` updates on
`change`, which fires only when the box loses focus, and clicking a disabled button does not take
focus away: the Forever routes page's Save and New stayed disabled after typing until it did.

**Format numbers for CSS and SVG with the invariant culture.** A decimal comma makes
`width: 82,5%` or an SVG coordinate invalid, and it is silently ignored.

**Tests host it in memory.** `ServiceTests/Api/ControlCenterTests` points it at a port where
nothing listens and checks the shell, the map assets and a 502 from the forwarding.
