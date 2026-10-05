# World view - moving the Control Center off Leaflet and Babylon

**Status (2026-10-05):** agreed with the user and under way; steps 1 to 6 are done, the look (step 7) is next. The user found the Leaflet map and the
Babylon 3D view buggy and hard to use, and asked to move everything to the new map, keep the
drawing tools (polygons and the rest), then improve the look. Phase 3 step 6 and the Windows
checklist are parked for another session ([phase3-plan.md](phase3-plan.md)).

## Why deck.gl

One library for a flat map and its 3D tilt, with the same data: paths, points, polygons, meshes,
images, picking. The C# page owns the state and sends it whole (`WorldScene`); the script only
draws and reports what the pointer did. That removes the inherited map's class of bugs (stale
layers, global state, two views of one scene). The ground is the navmesh itself (the continent's
relief from the tile graph, each tile's polygons up close), so no minimap art is needed, which
this PC does not have and Forever never had.

## Inventory: where each feature goes

| Today | Feature | Moves to |
|---|---|---|
| Live view `/` (Babylon) | every route the service plans (watch hub), test routes with run and reverse | Live view on the world view; a server-side watch feed like `FleetFeed` |
| Route search `/search` (Babylon) | route between named places, jitter, seed | the route designer (`/routes`) |
| NPC routes (Babylon) | zone, NPC type and NPC, random start, route there or back | NPC routes on the world view |
| Path library `/paths` (Babylon) | list, draw a path, search one leg, delete, screenshot | the route designer's library |
| Path editor `/paths/edit/...` (SVG) | select, move, insert, remove points; halve, double; save, save as, revert | the route designer (handles on the map; `PathEditor` for halve and double) |
| Map `/Leaflet` | draw tools: polyline, polygon, circle, rectangle; edit and delete | the world view's tools |
| Map | cost zones: roads (polyline, width), danger circles and rectangles (avoid, block) | a cost-zones page on the world view |
| Map | spawns: NPCs by type, elites, herbs and mining nodes, skinnables, mailboxes, teleports; zone and faction filters; NPC search | a map page with layers and a search panel |
| Map | custom points of interest (right-click), coordinates, URL sync | the map page |
| Map | navmesh coverage (baked, resident), bake an ADT by clicking, bake status; ADT grid | the map page (admin panel) |
| Forever routes (text) | edit, check, save route files | stays a text editor, with the route drawn on the world view and coordinates picked from it |

BlazorServer keeps its own Leaflet map (MapAssets) until Phase 4 makes it the PC's setup UI.

## The drawing and editing tools

Shapes are part of the scene: a line, a polygon, points, a circle or a rectangle, each with a kind
(how it looks) and whether it can be edited. A tool on the scene says what a click draws:
- line, polygon, points: a click adds a vertex; double-click or Enter finishes, Escape cancels,
  Backspace takes the last vertex back;
- rectangle: two corners; circle: the centre, then a point on the rim.

A selected editable shape shows handles: drag a vertex to move it, drag a midpoint to insert one,
right-click a vertex to remove it (circles: centre and rim; rectangles: corners). The script edits
locally while dragging and reports the finished geometry; the page keeps the result. A drag that
starts on a handle is stopped before the map's controller sees it, so it moves the handle, not the
map.

Zones are named from the zone maps alone (the Control Center has no area table): a point is in the
zone whose map it lies furthest inside. Zone maps overlap a lot, so this is right in the middle of
a zone and approximate at a border; a Forever route only needs each point inside its step's map,
which it always is.

## Order of work

1. **Done.** The tools in `WorldView` (shapes, drawing, handles, selection): `world-edit.js`, the
   `WorldShape`/`WorldTool` types and the `OnDrawn`/`OnEdited`/`OnSelected` events. How they work
   and what bit is in `ControlCenter/CLAUDE.md`.
2. **Done.** The route designer on `/routes`: waypoints clicked, dragged, inserted and removed on the
   map, or added from named places; each leg planned once (`RouteLegs`) and coloured by its own
   status, band or continent search; the library (open, save under a name, delete, screenshot),
   reverse, halve (the end kept) and double; areas drawn as polygons or rectangles; saved as a
   Forever route (`ForeverExport`: a travel step per zone, each area roamed after the waypoint
   nearest it). Checked in Chrome: placing, dragging (the map stays), inserting, removing, an area,
   the Forever file loading as a run loads it, a recorded path opened on its zone.
3. **Done.** Live view and NPC routes on the world view; Route search, Path library and the path
   editor went (the designer does all three). The Live view reads a new route-only subscription of
   the watch hub (`WatchRoutes`/`routePlanned`, with each route's continent), so watching costs the
   service no tile meshes; NPCs are markers to click. **Open:** the 3D view's Terrain toggle (the
   game geometry under tiles, needing game files) has no new home; deck.gl could draw it as a mesh
   if it is still wanted. Its page is gone, its code goes in step 6.
4. The map page: cost zones (roads, danger zones), spawns and search, custom points, navmesh
   coverage and bakes, the ADT grid. In parts:
   - **Done.** Cost zones (`/cost-zones`): roads drawn as lines, danger zones as circles and
     rectangles, avoid or block, each kept in the file of the zone it was drawn in (the area grid's
     zone); changes collect until Save, which the service checks and applies to the next route.
     Names show close in or on the selected shape, so a continent of roads stays readable.
   - **Done.** The map (`/map`): a zone's layers - NPCs by type, elites, herbs, veins, skinnable
     creatures (every spawn), mailboxes, places - from new typed service endpoints
     (`world/spawns`, `world/npcs/search`) instead of the browser reading the data files;
     creatures found by name on the continent; points marked by clicking, with their zone map
     coordinates. The Leaflet page stays as "Minimap (old)" for what is left.
   - **Done.** Navmesh (`/navmesh`): the ADT grid in how much of each ADT is baked, the tiles the
     planner holds, an ADT chosen on the map to bake or the whole continent, progress as it goes,
     the area grid rebuilt, the cache cleared (after a confirm). Bakes say why they cannot start
     where the service has no game files. Coordinates show under the pointer on every page.
   - Not moved, on purpose: the old minimap's art (the world view draws the navmesh instead, for
     every client), and BlazorServer's own path recording (it stays in BlazorServer).
5. **Done.** Forever routes: the route drawn beside its text as it is written (read leniently by
   `ForeverRouteSketch`: comments, trailing commas), each travel leg planned as the bot would,
   each roam's area outlined; a point picked on the map gives its zone map coordinates, inserted
   at the text's cursor or copied. The service's check stays the word on whether a run loads it.
6. **Done.** Babylon (`pathing-scene.js`, `Viewport3D`) and Leaflet are out of the Control Center:
   the Map page, the MapAssets scripts and its project reference, `SearchPanel`, and the browser's
   SignalR client (hubs are followed from the server). MapAssets stays, BlazorServer's own map.
   Bootstrap's reset stays under the theme for now.
7. The look: an aesthetics pass over the world view and the pages on it. Not started; see the
   handoff below.

Each step is checked in Chrome with Playwright (screenshots, interactions) before the next.

## Handoff: step 7, the look (2026-10-06)

Steps 1 to 6 are on `origin/main` (4e9ebc8f to 64aa9c22). Nothing of step 7 is written yet. The
user's words for it: once everything works, "see how we can improve it aesthetically".

What I would do, most visible first:

1. **A shaded relief for the flat view.** Zoomed out, the ground is one flat colour per 133-yard
   tile, so a continent reads as a block map. Draw it instead as one image: a canvas with one pixel
   per tile, its colour the height ramp (`heightColor`) times a hillshade (light from the
   north-west; slope from the four neighbours, about three times exaggerated; a missing neighbour
   counts as level), as a `deck.BitmapLayer` with linear filtering. Orientation, from
   `overviewOf`: view X = -world y and tile x runs along world y, so column = maxTx - tx; view Y =
   world x and tile z runs along it, so row = maxTz - tz (north up). Bounds `[left, bottom, right,
   top]` = `[-(origin + (maxTx + 1) * size), origin + minTz * size, -(origin + minTx * size),
   origin + (maxTz + 1) * size]`. Draw it after the relief cells with `parameters: {depthCompare:
   'always', depthWriteEnabled: false}`: the cells stay underneath, pickable for the readout's and
   the tools' heights (`pickable: '3d'`), and the detail polygons still draw over it (the cells
   under shown detail tiles are already left out). Tilted keeps the stepped cells: a flat image
   would lose the heights.
2. **Routes with a casing**: a wider dark path (about 3 px more) under each solid route, so a
   route reads over any ground.
3. **Outlined names**: zone labels in an SDF font with an outline (`fontSettings: {sdf: true}`,
   `outlineWidth`, `outlineColor`) instead of bare light text.
4. **Panels**: `.pa-waypoints` stops at 240 px even in a tall panel whose body already scrolls;
   let lists grow there. Check spacing and section headings across Routes, Map, Cost zones,
   Navmesh and Forever routes.
5. **The pointer readout** could add the zone and its map coordinates (the zones are known only
   server-side today: `ZoneLookup`).
6. **Bootstrap** is loaded only for its reset now; try without it and compare every page.

Screenshot every page before and after; keep what is clearly better.

How to check:
- `dotnet test`: 278 tests, one skipped (minimap art). `ServiceTests/Api/ControlCenterTests`
  renders the world view pages offline.
- In a browser: a Playwright .NET harness drove `designer`, `live`, `npc`, `map`, `zones`,
  `navmesh` and `forever` checks against the Pathing Service and Control Center on side ports
  (5110, 5120). It is not in the repository (Windows PC: `local/browser-checks/`). Each check
  waits on `worldView.describe()` (continent, layers, routes, markers with screen positions,
  the tools' state and handles), clicks and drags on the canvas, and takes screenshots.

What bites:
- `wwwroot` scripts and styles are fingerprinted and compressed at build time: build the Control
  Center again after changing them, or the browser gets the old ones.
- The cost zone check saves into `Json/road`. It deletes its zone again and the files come back
  byte-identical, but look at `git status` after it.
- Never press "Clear the cache" on the Navmesh page in a check: it deletes the baked navmesh.
- deck.gl 9.4 (luma.gl 9) names the GPU parameters `depthCompare` and `depthWriteEnabled`.

Open for the user, not for step 7:
- The Babylon view's terrain toggle (the game geometry under tiles, needing game files where the
  service runs) has no home in the Control Center now. deck.gl could draw it as a mesh, if wanted.
- Swimming costs 1.3 times walking (known issue 28): it is why a continent route from Durotar to
  Tanaris runs on the sea floor. Changing it changes how the bot walks.
