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
Bots/BotManagerStatus.cs           the Bot Manager's poller: + ManagerHealth (bots, online)
Bots/FleetFeed.cs                  the one client of the manager's /fleetHub: every BotInfo, kept
                                   current; Changed, Resynced, Log, RunEnded for the pages
Bots/FleetConnectionFactory.cs     the feed's hub connection (MessagePack, the manager's key)
Bots/BotLogBuffer.cs               a console's log: the backlog joined to the stream, each line once
Bots/AssignmentDraft.cs            the assignment form's fields, Missing and Refusal (AssignmentRules)
Components/App.razor, Routes.razor the document; InteractiveServer without prerendering
Components/Layout/                 MainLayout (rail), NavMenu, StatusBar
Components/Shared/                 ControlCenterComponent (base), Viewport3D, SearchPanel,
                                   Panel, BusyButton, ResultLine, OfflineNotice, Terminal, ...
Components/Bots/                   BotCard, BotState, BotLive, BotCommands, AssignmentForm,
                                   RunHistory, BotLog, FeedChip; BotFormat (words and styles)
Components/Pages/                  LiveView (/), Map (/Leaflet/...), Search, PathLibrary (/paths),
                                   PathEditorPage (/paths/edit/...), NpcRoutes, ForeverRoutes
                                   (/forever-routes), Fleet (/fleet), BotConsole
                                   (/fleet/{agentId}), ApiReference
wwwroot/css/site.css               the whole theme; pa- prefixed so the map keeps Bootstrap
wwwroot/js/pathing-scene.js        the Babylon 3D view; Babylon itself is fetched on demand
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

**Tests host it in memory.** `ServiceTests/Api/ControlCenterFactory` points both services at
ports where nothing listens (`ClosedPort`) unless a test hands it a fake manager client, and
`RenderAsync` renders a page with `HtmlRenderer` (prerendering is off, so a plain GET returns only
the shell). `ControlCenterTests` checks the shell, the map assets and a 502 from the forwarding;
`ServiceTests/Fleet` the feed against the in-memory manager, the pollers, the log join, the
assignment draft and the fleet pages. A render there is a first render only: what goes wrong on
a re-render (a duplicate `@key`) shows in a browser - drive one with Playwright .NET and the
installed Chrome (`Channel = "chrome"`, headless), waiting on `location.pathname` after a
navigation.
