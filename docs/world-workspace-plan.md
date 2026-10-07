# World workspace - one screen for the map, routes, costs and the navmesh

**Status (2026-10-07):** a handoff; nothing here is built yet. Written at the end of the Forever
world-data work (branch `feature/forever-world-data`: QuestieDB's data, the quest API, the layers
panel, the Forever `Visit` and `Quest` steps). Read [world-view-plan.md](world-view-plan.md) first -
how the pages moved onto the deck.gl world view - and the gotchas in `ControlCenter/CLAUDE.md`.

## What the user asked for

Condensed from their words:

1. "A lot of tools show the same thing for the map": Live view, Map, Routes, NPC routes, Cost zones,
   Navmesh and Forever routes are seven screens over one map. Gather everything about the map and
   routes into one screen and switch between modes.
2. **First priority:** design a route themselves - draw a path, or throw down nodes that join into one.
3. Node types, in the UI and later in the Pathing Service: some nodes mandatory, so that whatever
   cost model is chosen every path passes them and adapts around them ("plan a route that must pass
   here"). Not to build in the service now - design for it.
4. Later: a route that bends to take gathering (skinning, herbs, mining) or quests into account.
5. See the navmesh's nodes, as a mode.
6. Edit the path costs (what a gathering node costs, and so on) and see which routes come out; build
   cost models from what is on the map, Horde and Alliance towns included.
7. Better functionality and looks overall.

## Where things stand

### Seven screens over one map

| Rail item | Page | What it does | Its own pickers and tools |
|---|---|---|---|
| Live view | `/`, `/live` (`LiveView`) | every route the service plans (`WatchFeed`), the test routes | none |
| Map | `/map` (`WorldMap`) | `WorldLayers` (NPCs by type, nodes, elites, places, quests), creature search, marks that measure | zone (in the layers); point, line, polygon, rectangle, circle |
| Routes | `/routes` (`RouteMap`) | the designer: waypoints and their planned legs, areas to roam, the path library, the Forever export; `WorldLayers` bottom right | continent; Waypoints, Area, Box area |
| NPC routes | `/npc-routes` | a route to an NPC of a type, from a random or picked start | zone, type, NPC |
| Cost zones | `/cost-zones` | roads and danger circles and boxes per zone file; a save applies at once | continent; Road, Danger circle, Danger box |
| Navmesh | `/navmesh` | coverage per ADT, resident tiles, bake an ADT or a continent, area grids | continent; Choose ADT |
| Forever routes | `/forever-routes` | route files as text, checked as a run loads them, drawn beside the text; `WorldLayers` over its map | pick a point |

Each page builds its own `WorldView` and keeps its own continent and zone. Switching tabs drops the
camera, the zone, the layers and anything unsaved: a route being designed is lost without a prompt
(no page guards navigation). The rail is `Components/Layout/NavMenu.razor`.

### Building blocks that stay

- `WorldView` with `wwwroot/js/world-view.js` and `world-edit.js`: the map, the scene (`WorldScene`),
  the tools (point, line, polygon, points, rectangle, circle) and the handles (drag, insert, remove).
- `WorldLayers` (`Components/Shared`): the layers, a zone's quests and one quest's places, as
  `Markers`; `OnChanged`, `OnFocus`, `OnWaypoint` and `QuestActions` let a page use them.
- `RouteLegs` (one route request per leg, kept by its ends), `ForeverExport`, `RecordedPaths`,
  `RouteLibrary`, `ForeverSave`, `ZoneLookup`, `WorldCache`, `PathingStatus`.

### How costs work today - read before designing cost models

- `PPather/Navmesh/WowQueryFilter`: a step costs its length × the polygon's area cost (ground 1.0,
  water 1.3, magma 4.0) × the cost-zone factor at the polygon's centre; a blocked polygon fails
  `PassFilter`.
- `PPather/Navmesh/CostZones`: roads and danger zones are rasterized onto the MCNK chunk grid
  (33.33 yd), one factor per chunk, so a polygon is priced with one hash lookup. Roads attract by
  making everything else dearer: once a continent has any zone, an unauthored chunk costs
  1/0.6 ≈ 1.67, a road's core 1.0, ramping between them (smoothstep). It is done that way round
  because Detour's heuristic assumes nothing costs less than 1 a yard; costs below 1 cut long routes
  short (see the comment on `CostZones.OffRoadFactor`: Moonbrook to Goldshire stopped at 628 of
  2,417 yd). A danger zone multiplies by its penalty (at most `MaxPenaltyFactor`, 25) or blocks.
- Files: `Json/road/<era>/<continent>/<uiMap>.json` (roads) and `dangerzone/<uiMap>.json`.
  `CostZoneStore` writes them in the bot's format, checks them with the loader's rules, and reloads
  the continent's planner lane at once; the agent's data sync mirrors the era's folder.
- Zones belong to a planner lane (`lane.Pathfinder.Zones`): a request cannot bring its own.
- **Forever runs load no cost zones** (`Core/Forever/ForeverNavmesh.OpenDiskOnly`; known issue 40):
  what the Control Center shows for Forever bends its preview, never the bot.

### The navmesh as the map draws it

`navmesh/{continent}/overview` (one height per tile: the relief) and, under 1,600 yd across,
`navmesh/{continent}/tiles/{x}/{z}` (a tile's polygons, water flagged). On the server but never
drawn: the tile graph (`NavmeshTileGraph`: a node per connected region of a tile, joined to its
neighbours) and Detour's own polygon links, the nodes A* actually searches.

### This PC

No Forever tiles here (`Json/PathInfo/navmesh` holds precata only), so Forever legs answer
`NotBaked` and the designer draws its waypoints' legs as failed dashed lines. That is likely why
"no tool to throw down nodes" seemed missing: the Waypoints tool exists (top right, under Draw),
but its legs cannot be planned without tiles. A Classic service (`--exp=som`) plans them.

### What the bot follows

- Forever: route files (`Travel` with `To` and `Via`, `Roam`, `Visit`, `Quest`). A `To` is strict:
  reached within 3 yd, walked again up to three times. A `Via` is flowed through within 6 yd, and
  passed by when the walk cannot get past it. That is already a hard and a soft node.
- Classic: recorded paths (one zone's map coordinates, `Json/path`), followed point to point.

## The target

### One page, modes

`/world`, with `?mode=explore|route|costs|navmesh|live` and `zone`, `quest`, `route` beside it. The
old addresses redirect: `/map` to Explore, `/routes` to Route, `/cost-zones` to Costs, `/navmesh` to
Navmesh, `/live` to Live, `/npc-routes` to Explore ("route to this NPC").

```
+------+----------------------------------------------------------------------+
| rail |  Azeroth > Elwynn Forest           [ zones, NPCs, quests, places  ^K ] |
|      +----+----------------+------------------------------+----------------+
| World| Ex | MODE PANEL     |                              | INSPECTOR      |
| Fleet| Ro | the mode's     |          the map             | what is        |
| Forev| Co | controls       |      (one WorldView)         | selected: an   |
| API  | Na |                |                    [Layers]  | NPC, a quest,  |
|      | Li |                |                    [Draw]    | a node, a zone |
|      +----+----------------+------------------- readout --+----------------+
+------+----------------------------------------------------------------------+
  mode strip: Ex(plore)  Ro(ute)  Co(sts)  Na(vmesh)  Li(ve)
```

- One `WorldView` for the page, and a shared context every mode reads: continent, zone, camera,
  layers, selection (a cascading `WorldWorkspace` object).
- Each mode is a component that gives the page its panel, its tools, its part of the scene and its
  handlers for the view's events. Only the active mode's tools and handlers are live; the layers'
  markers stay on in every mode.
- Mode state lives in per-circuit session objects (`RouteDesign`, `CostDraft`, ...) registered
  scoped, so switching modes keeps the route being designed, and modes use each other's work: Costs
  previews the designer's route under a cost model.
- The rail becomes: World, Fleet, Forever routes (the text editor, for now), API.

| Mode | From | Adds |
|---|---|---|
| Explore | Map, NPC routes | the inspector for what is clicked; "route here" from an NPC or a marker opens Route with it as a node |
| Route | Routes, the drawing of Forever routes | node kinds, leg kinds, free drawing, stats, undo; opens and saves recorded paths and Forever routes |
| Costs | Cost zones | cost models, the heatmap, a route compared under two models |
| Navmesh | Navmesh | Detour's nodes and links, the tile graph, a route explained; bakes and area grids as now |
| Live | Live view | as now |

### Looks

- A mode strip with labels on hover; one left panel per mode (collapsible, resizable, remembered); an
  inspector on the right for the selection; the readout at the foot.
- One search over everything - zones, places, NPCs, quests, a coordinate such as
  `Elwynn 42.1, 65.8` - on Ctrl+K.
- Marker icons instead of dots (deck.gl's `IconLayer` with one atlas: quest `!` and `?`, herb, ore,
  skull for elites, bag for vendors, bed for innkeepers, wings for flight masters, anvil for repair),
  clustered when zoomed out; a legend under the Layers button.
- Undo and redo for routes and cost zones; shortcuts (1-5 for the modes, N node, D draw, Esc, Del,
  Ctrl+Z); empty and loading states that say what to do next.
- Remembered per browser: the last mode, zone, camera, layers and panel widths (`pathingUi`'s
  storage, which already survives a private window).

## Nodes and legs: the route model

Every waypoint is mandatory today: the designer plans leg by leg between consecutive waypoints.
Kinds matter once something may change the route - a cost model, a detour, a reorder. The user's
ask: some nodes are mandatory whatever the cost model, and the route adapts around them.

| Node kind | Meaning | What may change it | Forever export | Classic export |
|---|---|---|---|---|
| **Required** (a pin) | the route passes within its radius (3 yd by default), in this order | nothing | a `Travel`'s `To` (strict) | a path point |
| **Shape** | bends a leg the way it was drawn | a plan may drop it when the cost model routes better without it | a `Via` (passed by when stuck) | a path point |
| **Stop** | a Required node where the bot does something: an NPC by role or name, a quest's start or end, a mailbox | nothing; its target may resolve to a place (the nearest vendor) | `Visit` | a path point, and the class profile's NPC goal |
| **Optional** | a candidate: a herb, a vein, a quest's camp | taken only while the detour stays within a budget | a `Via`, when taken | a path point, when taken |
| **Area** | a place to roam until kills, minutes or a level | nothing | `Roam` (or `Quest` for a quest's camp) | none yet |
| **Avoid** | a circle this route keeps out of, or pays to cross | nothing; it is this route's own, not a cost zone | none yet (the route's own cost set, later) | none |

| Leg kind | Meaning |
|---|---|
| **Planned** | the navmesh's way under the route's cost model (today's legs) |
| **Straight** | the line as drawn; checked against the mesh where there are tiles (off-mesh stretches red); needs no tiles |

Free drawing: drag to sketch; the stroke is simplified (Douglas-Peucker, about 10 yd) into Shape
nodes joined by Straight legs.

### The Pathing Service, later: a plan request

Not to build now, but the shape to aim for, so the UI's model does not change when it lands:

```
POST /api/v1/routes/plan
RoutePlan(string? Client, string Continent, RouteNode[] Nodes, string? CostModel, CostModelDraft? Draft,
          bool Loop, RouteSearch Search)
RouteNode(Point3 At, NodeKind Kind, float Radius = 3, NodeTarget? Target = null, LegKind ToNext = LegKind.Planned)
RoutePlanAnswer(RouteStatus Status, PlannedLeg[] Legs, int[] OptionalTaken, NodeSkip[] Skipped, float Yards, float Cost)
```

- Required, Stop and Area nodes cut the plan into segments, planned in order; nothing moves them.
- Optional nodes are inserted into their segment by the cheapest extra cost first while it stays
  within its detour budget (an orienteering heuristic, fine for tens of candidates); the answer says
  which were taken and why the others were not.
- Shape nodes are kept unless the plan is asked to straighten (drop a shape node whose leg costs no
  more without it).
- Avoid nodes become danger circles in the request's own cost set (the per-request cost set of the
  cost models, below).
- The bot follows the same plan: a route file carries the node kinds (Forever: `To` = Required,
  `Via` = Shape, `Visit` = Stop), and the bot's planner - the shared `RoutePlanner` of the service
  architecture's Phase 4 - plans segments the same way.

## Cost models: edit the costs, see what comes out

A **cost model** is a named, saved set of rules the planner prices polygons by:

- the base area costs (ground, water, magma) and the road preference (today's 0.6);
- rules from what is on the map: from a layer - herbs, veins, skinnable beasts, elites, quest camps,
  mailboxes, Alliance, Horde and neutral towns - an effect (prefer, avoid with a penalty, block,
  ignore) with a strength and a radius;
- the authored zones (today's road and danger files), on or off.

Constraints:

- **Nothing cheaper than 1 a yard.** "Prefer" works as roads do: the preferred chunks at 1.0,
  everything else dearer. With several preferences, normalize so the cheapest chunk is 1.0.
- **One grid.** Every rule rasterizes onto the 33 yd chunk grid: a herb's 25 yd radius is one to four
  chunks. Fine for routing; draw the heatmap by chunk, too.
- **Required nodes win.** A model never moves a Required or Stop node; it prices the legs between
  them. A block can leave one unreachable: the answer names the leg, as `Partial` and `NoPath` do.

**Preview without saving.** A route request names a model or carries a draft; the planner builds its
`CostZones` (cached by the model's hash - rasterizing a continent's herbs takes milliseconds) and the
filter uses it for that query. That is safe, because a lane answers one query at a time and
`WowQueryFilter.BeginQuery` already snapshots the active set: give it an override. Then **Compare**:
the designer's nodes planned under models A and B, both drawn, with yards, estimated minutes (run
speed 7 yd/s), cost, nodes passed within 20 yd and hostile ground crossed.

**Towns by side.** Not in the data today. A first version derives them from Forever's NPCs: clusters
of NPCs that serve one side (guards, vendors, innkeepers; `Creature.FriendlyTo`, QuestieDB's) of
eight or more within about 60 yd, as a hull with a 30 yd margin, labelled by side (both sides:
neutral), served as a layer (`GET world/towns?continent`). Later: the baked area grid's sub-areas
with the area table's faction and town flags, which `Utilities/ReadDBC_CSV` would have to extract.

**Storage and the bot.** Models per era beside the cost zones, such as
`Json/road/<era>/models/<name>.json` (the loader reads only continent folders, but check
`ClientContinents` and the sync manifest's name rules), synced with the zones; a route names its
model (`"CostModel"` in a Forever route file, a setting for Classic). Forever runs must load zones
first (known issue 40).

## Gathering- and quest-aware routes

Three ways, cheapest first:

1. **Optional nodes** (above): Route mode offers the herbs, veins and skinnable beasts near the route
   as Optional nodes; the plan takes those within a detour budget ("+5 herbs for +180 yd").
   Explicit and explainable, and the bot follows what was taken. Recommended first.
2. **Prefer rules** in a cost model: routes drift through node-rich ground without stopping at each node.
3. **A loop generator**: an area and layers in; a loop through the densest nodes out (nearest
   neighbour, then 2-opt over planned leg costs), as Shape and Optional nodes - a farming loop.

Quests: "Add quest to route" in Route mode puts in its Stop (start), Area (camp) and Stop (end)
nodes, ordered by cheapest insertion; exported, they are the `Visit` and `Quest` steps the Forever
routes page's "Insert its steps" writes today.

## Navmesh mode

- As now: coverage, resident tiles, bakes, area grids.
- Close in: the polygons (drawn today) and Detour's graph - a dot per polygon's centre, a line per
  link - coloured by area (ground, water, magma) or by the cost model's factor, blocked ones crossed.
- Far out: the tile graph's regions and joins (`NavmeshTileGraph`, built per bake already).
- A route explained: the polygons it passed through (its corridor), its yards per area and per rule,
  its total cost - why it went this way.

New endpoints: `GET navmesh/{continent}/graph?box` (the tile graph's nodes and joins in a box), the
tile geometry with each polygon's area, flags and links, and `Explain` on a route request.

## Phases

Each phase ships on its own.

1. **The workspace shell.** `/world` with the mode strip and the shared context. Explore and Route
   move in first (WorldMap and RouteMap as modes, their state in scoped sessions), then Costs,
   Navmesh and Live; the redirects; the rail. No new features. Done when switching modes keeps the
   camera, the zone, the layers and an unsaved route; every old address lands on its mode; the
   render tests and a browser check pass.
2. **Design a route yourself** - the user's first priority. Node kinds Required, Shape, Stop and Area
   in the UI (picked on the node in the inspector, each with its own marker shape); leg kinds
   Planned and Straight; free drawing; insert by clicking a leg; reorder in the list; a loop; undo
   and redo; stats (yards, minutes, climb, herbs and veins within 20 yd, elites near); snap a node to
   the marker under the pointer (an NPC becomes a Stop for that NPC). Export to Forever (Required as
   `To`, Shape as `Via`, Stop as `Visit`, Area as `Roam`) and to a recorded path (dense points).
   Straight legs make the designer useful without tiles. Done when a route drawn by hand on Forever
   without tiles exports to a route file the service accepts, Classic mixes planned and straight
   legs, and undo walks back every edit.
3. **The navmesh's nodes.** The graph endpoints and drawing above; a route explained.
4. **Cost models.** The format, per-request cost sets, the heatmap, Compare, towns by side; models
   saved and synced; Forever runs load zones (issue 40).
5. **The plan request and Optional nodes** in the Pathing Service, then gathering and quests on top;
   the bot follows node kinds.
6. **Looks**, alongside the rest: icons, clustering, the legend, search, the inspector.

## Constraints from the codebase

- The Control Center references `Contracts` and `ServiceClients` only; the Pathing Service never
  references Core, Game, WinAPI or Frontend.
- Formats on disk stay as they are - cost zone files, recorded paths, Forever route files with their
  comments; `ServiceTests/Stores` pins them. New fields are additions a reader may ignore.
- A parent's re-render sets every parameter again: act on a parameter only when it changed (see "What
  is where is one component" in `ControlCenter/CLAUDE.md`). An exception in a render or a handler
  ends the circuit.
- deck.gl: leave a `SolidPolygonLayer` out when it is empty; thin labels yourself; a drag on a handle
  must stop before the controller sees it. Release builds fingerprint `wwwroot`: rebuild before a
  browser check. A circuit message is at most `MaxCircuitMessageBytes` (1 MB).
- Costs: never below 1 a yard; penalties at most 25; a cost-zone file's raster work at most
  `CostZoneLoader.MaxRasterCells`.
- Related known issues: 40 (Forever runs ignore cost zones, and the planner resolves Forever
  endpoints otherwise than the bot), 47 (no logins yet: cost models, bakes and clears are admin work
  later), 48 (a zone saved from the bot's own map can overwrite the Control Center's), 59 and 60
  (Forever's data gaps; a visit does not talk yet).

## Testing

- Render tests: `ServiceTests/Api/ControlCenterFactory.RenderAsync<T>(parameters, uri)`, against a
  Forever service in memory (`WorldLayersTests` shows how).
- Endpoints and the planner: `ServiceTests` (tests that need tiles skip without precata ones).
- In a browser: Playwright .NET with the installed Chrome (`Channel = "chrome"`, headless).
  `worldView.describe()` gives each marker's place on the screen, so a check can click one. The
  layers were checked from a scratch console project (`Microsoft.Playwright` 1.58.0, in the NuGet
  cache); a test project that starts both services would keep such checks.
- Run: `PathingService/run.sh --exp=forever` (or the default, `som`, for planned legs on this PC's
  precata tiles), then `ControlCenter/run.sh`.

## Open questions for the user

1. The landing page: the World workspace (in which mode?) or the Fleet?
2. Forever route files: keep the text page as the place to edit them, or edit them as nodes in Route
   mode, comments becoming notes on nodes?
3. A Straight leg in a Forever route: walked exactly (dense `Via` points), or planned on the mesh
   between its points?
4. Cost models: one set per era shared by every bot, or chosen per route?
5. Should Forever runs apply cost zones and models (known issue 40)?
6. Are towns from NPC clusters good enough for a first version?

## A first session

Phase 1 with Explore and Route as modes, then phase 2's node kinds, Straight legs and undo: the
user's "design a route myself".
