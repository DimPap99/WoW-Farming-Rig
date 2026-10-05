# ControlCenter - the central web UI

The Control Center from `docs/service-architecture.md`: the place to watch routes, author paths
and cost zones, administer bakes, and run the fleet - a console per bot - for every bot. It holds
no data and plans nothing. It is a client of the Pathing Service and the Bot Manager, and it never
references `PPather`, `Core`, `Game`, `WinAPI` or `Frontend`: only `Contracts`, `ServiceClients`
and `MapAssets` (static files, no code).

## Run

```bash
PathingService/run.sh      # first: the Control Center is its client
BotManager/run.sh          # for the fleet pages
ControlCenter/run.sh       # http://localhost:5020
ControlCenter/run.sh --PathingService:Url=http://other-host:5010 --BotManager:Url=http://other-host:5030
```

The port is `Contracts.ControlCenter.ControlCenterDefaults.DefaultPort` (5020); `--urls`
overrides it, `--LogFile=` (empty) turns the log file off. On Windows, `dotnet run -c Release`
from this folder does the same as `run.sh`. Bots for the fleet pages without a game:
`Utilities/SimAgent` (a simulated agent; run two or three, each with its own `--Agent:Name` and
`--Agent:IdentityFile`). Never `HeadlessServer --agent` on a game PC for that: it sees the WoW
client and would start a real run.

## Layout

```
Program.cs                         host (a class, so tests can host it beside the service)
Hosting/ControlCenterRegistration  AddControlCenter / UseControlCenter - the composition root
Hosting/ServiceProxy.cs            YARP: /api/v1, /watchHub, /swagger and WorldData to the service
Hosting/ServiceStatus.cs           the base of both pollers (2 s): Online, Error, Changed,
                                   Reconnected; ServiceDescription names a service for the pages
Hosting/ControlCenterLogging.cs    Serilog to console + daily file
Pathing/PathingStatus.cs           the Pathing Service's poller: + capabilities, health
Pathing/WorldCache.cs              zones, continents, places, test routes; reloads on reconnect
Pathing/WatchFeed.cs               the routes the service plans, as a route watcher of /watchHub,
                                   connected while a page watches; WatchConnectionFactory
Pathing/ZoneLookup.cs              a point's zone from the service's area grid, kept per cell
Bots/BotManagerStatus.cs           the Bot Manager's poller: + ManagerHealth (bots, online)
Bots/FleetFeed.cs                  the one client of the manager's /fleetHub: every BotInfo, kept
                                   current; Changed, Resynced, Log, RunEnded for the pages
Bots/FleetConnectionFactory.cs     the feed's hub connection (MessagePack, the manager's key)
Bots/BotLogBuffer.cs               a console's log: the backlog joined to the stream, each line once
Bots/AssignmentDraft.cs            the assignment form's fields, Missing and Refusal (AssignmentRules)
World/WorldScene.cs                what a page shows on the world view: routes, markers, labels, shapes;
                                   tools, the drawing events, WorldShapes and WorldKinds
World/RouteDrawing.cs              a route's lines by how it ended (found, partial + gap, none, planning)
World/ZoneLabels.cs                zone names on a continent; a point as a player reads it
Routes/RouteLegs.cs                the route designer's legs: one request per pair of waypoints, kept
                                   by their ends, four at a time
Routes/ForeverExport.cs            a designed route as a Forever route file (travel per zone, roams)
Routes/RecordedPaths.cs            library files as waypoints and back (one zone's map coordinates)
Components/App.razor, Routes.razor the document; InteractiveServer without prerendering
Components/Layout/                 MainLayout (rail), NavMenu, StatusBar
Components/Shared/                 ControlCenterComponent (base), WorldView, Viewport3D, SearchPanel,
                                   Panel, BusyButton, ResultLine, OfflineNotice, Terminal, Aria,
                                   Words (counts and yards), Dialogs (confirm), ...
Components/Bots/                   BotCard, BotState, BotLive, BotCommands, AssignmentForm,
                                   RunHistory, BotLog, FeedChip; BotFormat (words and styles)
Components/Designer/               the route designer's sections: RouteLibrary (open, save, delete
                                   recorded paths), ForeverSave (the route as a Forever route file);
                                   not Components/Routes, which would clash with the router
Components/Pages/                  LiveView (/), RouteMap (/routes, the route designer), NpcRoutes,
                                   Map (/Leaflet/...), ForeverRoutes (/forever-routes), Fleet
                                   (/fleet), BotConsole (/fleet/{agentId}), ApiReference
wwwroot/css/site.css               the whole theme; pa- prefixed so the map keeps Bootstrap
wwwroot/js/world-view.js           the world view on deck.gl (fetched on demand): flat map, tilts into 3D
wwwroot/js/world-edit.js           its drawing tools: shapes, drafts, handles, the pointer and keys
wwwroot/js/pathing-scene.js        the Babylon 3D view (Viewport3D), no longer on any page; goes in
                                   step 6 of docs/world-view-plan.md
wwwroot/js/pathing-ui.js           script loader, toast, remembered rail state
```

## Things that will bite you

**The browser talks only to the Control Center.** The map's script fetches `/tiles`, `/dbc`,
`/api/v1/...` and connects to `/watchHub` on its own origin; `ServiceProxy` forwards them to
the Pathing Service (WebSockets included), adding the service's key (`PathingService:ApiKey`, else
`ApiKey`) so the browser never holds it. That also means whoever reaches the Control Center reaches
the services: keep it on localhost, or behind your own access control. A prefix the map needs that is not in
`ServiceProxy.PathingPrefixes` answers 404 here, not at the service.

**The Bot Manager is called from the server, never forwarded.** `/api/v1` is the root of both
services, and here it belongs to the Pathing Service (`ServiceProxy`). Pages call the manager
through `IBotManagerClient` (`BotManager:Url`, its key from `BotManager:ApiKey`, else `ApiKey`),
so the browser never reaches it; don't add it to `ServiceProxy`.

**A down service is a state, not an error.** Pages read their service's status (`PathingStatus`,
`BotManagerStatus`, both a `ServiceStatus`) and show `OfflineNotice` with its `Service` and
`Error` instead of calling; they come back by themselves when the poller sees the service again
(`Reconnected`). Calls go through `IPathingClient` and `IBotManagerClient`, which never throw for
a service that is down - only for the page's own cancellation (`Token`), which handlers catch. The
clients retry queries, so a poll of a service that just went down takes a few seconds to say so.

**The fleet pages read the feed, nothing polls the bots.** `FleetFeed` is one hosted SignalR
client of `/fleetHub` that reconnects forever. The hub replays nothing, so after every connect it
reads the bots over REST and raises `Resynced`; what the hub streams while that read is under way
is held and applied after the list, so a bot that registered meanwhile is not overwritten by the
older list. Snapshots update a bot's `Latest`, `Run` and `Game`, as the manager's own `BotInfo`
does. The hub sends every bot's snapshots and logs to everyone: a console filters by agent id.

**A console's log must not lose or repeat a line where the backlog meets the stream.** `BotLog`
starts holding streamed lines (`BotLogBuffer.BeginBacklog`) before it subscribes and reads the
backlog (the manager's last 500), then joins them. A `BotLogLine` has no number, so a line already
shown is known by its time and text (record equality), and a backlog read again after a reconnect
fills in what was missed, in time order.

**Commands are sent once.** Start and Stop are never queued or retried (the client does not
retry POSTs): 409 (offline, no assignment, refused) and 504 (no answer in 10 s) are shown with the
manager's reason. Stop stays enabled while the bot is online: it releases input. A run waiting
for `WaitingForClick` needs a person at the bot's PC, and both pages say so in plain words.

**Siblings may not share a `@key`.** The console's per-bot parts are recreated when the URL
names another bot by keying the console's root with the agent id, not each part: two sibling
components keyed alike threw on the first re-render and ended the circuit.

**An exception thrown into a render or a handler ends the circuit.** The page then stops
updating until reloaded. Catch `JSException` around script calls in `OnAfterRenderAsync`, as
`Map.razor` and `Viewport3D` do: the map's `init` once failed on missing data and took the
whole page down.

**Subscribe through `ControlCenterComponent`.** The pollers raise their events on their own
thread and the feed on SignalR's: pair each `+=` with its `-=` in `Subscribe`, redraw with
`Redraw()`, and change a component's state inside `InvokeAsync`. Both raise through `Subscribers`,
so a subscriber that throws is logged and stops neither the others nor the poller. A component
that awaits JS teardown implements `IAsyncDisposable` and calls `Dispose()` itself.

**The world view replaces the map and the 3D view, one page at a time.** `WorldView` (deck.gl,
`wwwroot/js/world-view.js`) is a flat, north-up map that tilts into 3D: drag pans, the wheel zooms,
right-drag turns and tilts. The page sends a `WorldScene` (routes, markers, labels) whole when it
changes, and hears clicks while `Picking`; the ground the view fetches itself from the service's
`navmesh/{continent}/overview` (one height per baked tile, drawn as a relief) and, zoomed in to
under 1,600 yards across, `navmesh/{continent}/tiles/{x}/{z}` (the walkable polygons). So it needs
no minimap art and works for Forever too. The page draws WoW's x north and y west as screen up and
left (`X = -y, Y = x`). Flat is orthographic, so ground at different heights lines up; tilted is in
perspective. The ground is drawn unlit (`material: false`): deck.gl's default lighting turned it
black once zoomed in. `worldView.describe()` reports each view's continent, ground and layers, for a
browser check to wait on (with each marker's place on the screen). The Live view, the route designer
(`RouteMap`, `/routes`) and NPC routes are on it; the Map page still uses Leaflet until it moves
(`docs/world-view-plan.md`). A click on a marker comes back as `OnMarker` (not while `Picking`).

**The Live view watches routes, not a scene.** `WatchFeed` joins the service's watch hub as a route
watcher (`WatchHubMethods.WatchRoutes`): it gets each route the planner answers as a `WatchRoute`,
with its continent, and none of the 3D scene - no tiles, no drawings - so the service builds no
meshes for it. It is connected only while a page holds a lease (`Watch()`), as the 3D view was; the
hub replays its last five routes on every join, and the feed keeps each once (by its time and ends),
the newest 30 across connections.

**The world view's tools edit locally and report the result; the page keeps it.** A page offers
`Tools` (a `WorldTool`: the shape a click draws and its title) and binds `Tool`; shapes are part of
the `WorldScene` (`WorldShape`, editable or not, and `Selected`). What a tool finishes comes back as
`OnDrawn`, what the pointer changes on the selected editable shape as `OnEdited` (its whole
geometry), clicks on shapes as `OnSelected` (null when a click lands beside every shape); the page
puts the result in its next scene. A drag on a handle must not pan the map: deck.gl hands a drag to
`Deck.onDragStart` before the map's controller, so `stopImmediatePropagation()` there keeps the map
still - toggling the controller's `dragPan` does not work, as deck.gl applies a new controller only
when the view state changes. A tool's button switches the tool in the script at once (a click on
the map right after it is the new tool's), and the same tool sent again by the page is a no-op, so
it cannot drop a corner drawn meanwhile. Handles hide while a shape is drawn, or the closing click
on a corner over a waypoint is taken by the waypoint. The ground picks in 3D (`pickable: '3d'`), so
a click knows its height even tilted; a point placed on the relief (one height per tile) comes
back with height 0, which the service reads as "find the ground here". Keys reach the tools while
the map's own element (`.pa-world-canvas`, made focusable by deck.gl) has the focus. Framing and
centring use the width a tall panel at the side leaves uncovered. A shape edited whole can be a few
thousand points, so the circuit takes messages up to `MaxCircuitMessageBytes` (1 MB), not 32 KB.

**Static assets are fingerprinted and compressed at build time.** In Release (`MapStaticAssets`),
a changed `wwwroot` script is served as it was built until the Control Center is built again:
rebuild before a browser check.

**The designer plans each leg once.** `RouteLegs` keys a leg by its ends to a tenth of a yard
(the precision the map sends) and the search, so the whole list of waypoints coming back from an
edit replans only the legs whose ends moved. A waypoint with height 0 takes the ground's from the
first leg found through it. Library files are one zone's map coordinates with no height (as the bot
records them; a file with heights is world yards); the zone is guessed from the file name.

**A point's zone comes from the service's area grid.** Zone maps overlap a lot - Durotar's covers
the east of the Barrens, the Barrens' the south of Durotar - so no rule on the map rectangles
names a point's zone right at a border. `ZoneLookup` asks `/api/v1/area` (the baked grid's area,
a sub-area such as a town resolved to its zone: `AreaAtPoint.UiMapId`) and keeps the answer per
16-yard cell; a page shows what is `Known` at once and draws again when `LearnAsync` learned
something. Where the grid is not baked, `ZoneMath.ZoneAt` guesses from the maps: the one the point
lies furthest inside, continent maps last. The Forever export takes the looked-up zone when its map
holds the point (a step's points must be 0 to 100 on its map), else the guess.

**The era comes from the service, not the URL.** The map page passes the service's client to
the map script; the `/Leaflet/{client}` segment is advisory, as the map's own continent selector
writes it. Forever has no minimap art, so its map page says so and points to the 3D view.

**The 3D view (Babylon, `Viewport3D`) is on no page any more.** It replayed rather than reset, and
its Terrain toggle (the game geometry under 25 tiles, needing game files where the service runs)
has no new home yet: whether the world view should draw it is open (`docs/world-view-plan.md`).

**Bind text a button depends on with `@bind:event="oninput"`.** A plain `@bind` updates on
`change`, which fires only when the box loses focus, and clicking a disabled button does not take
focus away: the Forever routes page's Save and New stayed disabled after typing until it did.

**Format numbers for CSS and SVG with the invariant culture.** A decimal comma makes
`width: 82,5%` or an SVG coordinate invalid, and it is silently ignored.

**Tests host it in memory.** `ServiceTests/Api/ControlCenterFactory` points both services at
ports where nothing listens (`ClosedPort`) unless a test hands it a fake manager client, and
`RenderAsync` renders a page with `HtmlRenderer` (prerendering is off, so a plain GET returns only
the shell). `ControlCenterTests` checks the shell, the map assets and a 502 from the forwarding;
`ServiceTests/Fleet` the feed against the in-memory manager, the pollers, the log join, the
assignment draft and the fleet pages. A render there is a first render only: what goes wrong on
a re-render (a duplicate `@key`) shows in a browser - drive one with Playwright .NET and the
installed Chrome (`Channel = "chrome"`, headless), waiting on `location.pathname` after a
navigation.
