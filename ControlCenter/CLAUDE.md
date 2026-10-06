# ControlCenter - the central web UI

The Control Center from `docs/service-architecture.md`: the place to watch routes, design routes
and cost zones, administer bakes, and run the fleet - a console per bot - for every bot. It holds
no data and plans nothing. It is a client of the Pathing Service and the Bot Manager, and it never
references `PPather`, `Core`, `Game`, `WinAPI` or `Frontend`: only `Contracts` and
`ServiceClients`. Every page that shows the world is on one map, the world view (deck.gl); the
Leaflet map is BlazorServer's alone now (`MapAssets`), and the Babylon 3D view is gone.

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
World/AdtGrid.cs                   the bake API's ADT grid (x down from the world's edge along x)
Routes/RouteLegs.cs                the route designer's legs: one request per pair of waypoints, kept
                                   by their ends, four at a time
Routes/ForeverExport.cs            a designed route as a Forever route file (travel per zone, roams)
Routes/RecordedPaths.cs            library files as waypoints and back (one zone's map coordinates)
Routes/ForeverRouteSketch.cs       a Forever route's text read leniently for drawing as it is written
Components/App.razor, Routes.razor the document; InteractiveServer without prerendering
Components/Layout/                 MainLayout (rail), NavMenu, StatusBar
Components/Shared/                 ControlCenterComponent (base), WorldView, Panel, BusyButton,
                                   ResultLine, OfflineNotice, Terminal, Aria, Words (counts and
                                   yards), Dialogs (confirm), JsTeardown, ...
Components/Bots/                   BotCard, BotState, BotLive, BotCommands, AssignmentForm,
                                   RunHistory, BotLog, FeedChip; BotFormat (words and styles)
Components/Designer/               the route designer's sections: RouteLibrary (open, save, delete
                                   recorded paths), ForeverSave (the route as a Forever route file);
                                   not Components/Routes, which would clash with the router
Components/Pages/                  LiveView (/), WorldMap (/map), RouteMap (/routes, the route
                                   designer), NpcRoutes, CostZones (/cost-zones), Navmesh (/navmesh),
                                   ForeverRoutes (/forever-routes), Fleet (/fleet), BotConsole
                                   (/fleet/{agentId}), ApiReference
wwwroot/css/site.css               the whole theme and its own small reset (no Bootstrap); pa- prefixed
wwwroot/js/world-view.js           the world view on deck.gl (fetched on demand): flat map, tilts into 3D
wwwroot/js/world-edit.js           its drawing tools: shapes, drafts, handles, the pointer and keys
wwwroot/js/pathing-ui.js           script loader, toast, remembered rail state, insert at the cursor
```

## Things that will bite you

**The browser talks only to the Control Center.** The world view's script fetches
`/api/v1/navmesh/...` and a path's screenshot comes from the world data (`/path/...`) on its own
origin; `ServiceProxy` forwards them to the Pathing Service (WebSockets included, for `/watchHub`),
adding the service's key (`PathingService:ApiKey`, else `ApiKey`) so the browser never holds it.
Hubs the pages follow (`WatchFeed`, `FleetFeed`) are followed from the server. Whoever reaches the
Control Center reaches the services: keep it on localhost, or behind your own access control. A
prefix the browser needs that is not in `ServiceProxy.PathingPrefixes` answers 404 here, not at
the service.

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
`WorldView` does: the old map's `init` once failed on missing data and took the whole page down.

**Subscribe through `ControlCenterComponent`.** The pollers raise their events on their own
thread and the feed on SignalR's: pair each `+=` with its `-=` in `Subscribe`, redraw with
`Redraw()`, and change a component's state inside `InvokeAsync`. Both raise through `Subscribers`,
so a subscriber that throws is logged and stops neither the others nor the poller. A component
that awaits JS teardown implements `IAsyncDisposable` and calls `Dispose()` itself.

**Every page that shows the world is on the world view.** `WorldView` (deck.gl,
`wwwroot/js/world-view.js`) is a flat, north-up map that tilts into 3D: drag pans, the wheel zooms,
right-drag turns and tilts. The page sends a `WorldScene` (routes, markers, labels) whole when it
changes, and hears clicks while `Picking`; the ground the view fetches itself from the service's
`navmesh/{continent}/overview` (one height per baked tile) and, zoomed in to under 1,600 yards
across, `navmesh/{continent}/tiles/{x}/{z}` (the walkable polygons). So it needs no minimap art and
works for Forever too; where the service has the art (`ContinentInfo.Art`, a `MapArt` from
`PathingService/World/MapArtTable`), it is laid over the relief while flat, the game's minimap as
BlazorServer's Leaflet map draws it: `/tiles/{continent}/z{z}x{x}y{y}.webp`, at the zoom where a
tile's pixel is about a screen pixel, with coarser tiles under the ones still loading, at most 320
kept. Over it a tile's polygons are see-through and the dark under them is left out. The **Map**
button turns it off (remembered per browser, `cc.world.art`) and shows only where there is art
(the script sets `data-art` on the view). Flat, the overview is drawn as one shaded picture (`reliefImage`: four
pixels a tile, the heights smoothed between tile middles, each pixel lit from the north-west, where
nothing is baked darkened and cleared) over the tiles' cells, which stay under it for the heights a
hover or a click picks; tilted, the cells themselves show, as steps. Routes and lines are drawn
over a dark casing, zone names outlined in a distance-field font and thinned where they would
overlap (`apart`, the larger zone's name first: `WorldLabel.Size`). The readout at the foot names
the pointer's zone and map coordinates: the zones from `/api/v1/world`, the zone at a point from
the area grid (`/api/v1/area`, asked once the pointer rests, kept per 16-yard cell as `ZoneLookup`
keeps it), both called by the browser through `ServiceProxy`. The ground has no tooltip: the
readout says where and how high. The page draws WoW's x north and y west as screen up and
left (`X = -y, Y = x`). Flat is orthographic, so ground at different heights lines up; tilted is in
perspective. The ground is drawn unlit (`material: false`): deck.gl's default lighting turned it
black once zoomed in. `worldView.describe()` reports each view's continent, ground and layers, for a
browser check to wait on (with each marker's place on the screen). A click on a marker comes back
as `OnMarker` (not while `Picking`). How the pages moved here: `docs/world-view-plan.md`.

**The Live view watches routes, not a scene.** `WatchFeed` joins the service's watch hub as a route
watcher (`WatchHubMethods.WatchRoutes`): it gets each route the planner answers as a `WatchRoute`,
with its continent, and none of the 3D scene - no tiles, no drawings - so the service builds no
meshes for it. It is connected only while a page holds a lease (`Watch()`); the
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
A click goes to the tool in use if there is one, else to picking (`Picking`) whatever lies under
it, else it selects a shape or a marker. Each tool's button carries its name (`WorldTool.Label`)
under a "Draw" heading: icons alone went unseen. The Map page's tools draw marks - a point, a
line, a polygon, a rectangle, a circle - listed with what they measure and copied as zone map
coordinates; nothing saves them. A scene's `Frame` and `FrameOn` are kept until a scene is
actually sent: a page whose answers come quickly replaces a scene before the view has even been
made (deck.gl loads on first use), and the framing asked for must not be lost with it.

**deck.gl keeps drawing a `SolidPolygonLayer` whose data was emptied.** deck.gl 9.4 drew the last
polygons of such a layer on: the relief's dark under shown tiles stayed as a black square after
zooming out. Leave a solid polygon layer out of the list when it has nothing to draw; `PathLayer`,
`ScatterplotLayer`, `TextLayer` and `PolygonLayer` (which drops its fill when empty) clear as they
should. deck.gl's `CollisionFilterExtension` did not thin overlapping zone names on this view
(nothing hidden flat, a few names half faded tilted), so the script thins them itself.

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

**The Babylon 3D view is gone from here.** Its Terrain toggle (the game geometry under 25 tiles,
needing game files where the service runs) has no new home: whether the world view should draw it
is open (`docs/world-view-plan.md`); the watch hub still answers `Terrain`.

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
