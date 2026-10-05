# World view - moving the Control Center off Leaflet and Babylon

**Status (2026-10-05):** agreed with the user and under way; steps 1 to 5 are done. The user found the Leaflet map and the
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
6. Remove Babylon (`pathing-scene.js`, `Viewport3D`) and Leaflet from the Control Center (the Map
   page, the MapAssets scripts in `App.razor`, `SearchPanel`).
7. The look: an aesthetics pass over the world view and the pages on it.

Each step is checked in Chrome with Playwright (screenshots, interactions) before the next.
