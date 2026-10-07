# World view - moving the Control Center off Leaflet and Babylon

**Status (2026-10-06):** done: steps 1 to 7, the look included (see the last section). The user found the Leaflet map and the
Babylon 3D view buggy and hard to use, and asked to move everything to the new map, keep the
drawing tools (polygons and the rest), then improve the look. Phase 3 step 6 and the Windows
checklist are parked for another session ([phase3-plan.md](phase3-plan.md)). What comes next - the
pages as modes of one World workspace, route design with node kinds, cost models - is in
[world-workspace-plan.md](world-workspace-plan.md).

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
   Bootstrap's reset stayed under the theme until step 7.
7. **Done.** The look: a shaded relief, routes and roads over a casing, outlined zone names thinned
   where they would collide, lists that use a tall panel's height, the pointer's zone and map
   coordinates, and Bootstrap gone. What and why below.

Each step is checked in Chrome with Playwright (screenshots, interactions) before the next.

## Step 7: the look (2026-10-06)

The user's words: once everything works, "see how we can improve it aesthetically". Every page
was photographed before and after; what is here was clearly better, what was not is said too.

1. **A shaded relief, flat.** Zoomed out, the ground was one flat colour per 133-yard tile: a block
   map. It is now one picture (`reliefImage` in `world-view.js`) drawn as a `BitmapLayer` over the
   relief cells, which stay under it for the heights a hover or a click picks (`pickable: '3d'`);
   tilted, the cells show as before, as steps, now lit by their slope too.
   - Four pixels to a tile. A pixel per tile, as first planned, blurred into soft staircases at a
     zone's zoom, so the heights are smoothed between tile middles in the script.
   - The smoothing is a Mitchell cubic: a B-spline made Alterac's hills blobs, Catmull-Rom brought
     out tile-to-tile noise in Winterspring's snow.
   - Each pixel is lit from the north-west, slopes three times as steep as they are; the light
     stays within 0.6 to 1.3, and lit snow under white (232), so white markers show on it.
   - Where nothing is baked, the picture darkens, then clears, along a smoothed mask: inner holes
     (a tile with nothing walkable) are soft hollows, not black squares.
   - Where a tile shows its polygons, a dark square (`relief-holes`) under them keeps what is not
     walkable dark, as without the picture. Only while tiles show: an emptied `SolidPolygonLayer`
     kept drawing its squares (in `ControlCenter/CLAUDE.md`).
   - Kalimdor's picture (800 x 880) takes about 60 ms when the continent loads.
2. **Casings.** Every route, dashed ones too (a dash's casing a dash as long), and every drawn line
   (roads) over a dark path 3 px wider; all casings under all routes. A green route on green
   ground now reads.
3. **Zone names** in a distance-field font with a dark outline, light text. Where two would
   overlap, the larger zone's shows (`WorldLabel.Size`, the side of its map's square) and the other
   waits for a closer look; the script measures each name in the labels' font (`apart`). deck.gl's
   `CollisionFilterExtension` was tried first and did not work on this view. Waypoint numbers are
   outlined too, beside their dot instead of boxed over it: the boxes hid dots and legs.
4. **Panels.** A tall panel's last list fills the panel and scrolls in it (`pa-fill`: Cost zones,
   so the selected zone and Save stay in view); a list in the middle of one grows with the window
   (`max(240px, 36vh)`: the designer's waypoints, the Live view's routes). Sticky list headings are
   solid (rows showed through), scrollbars thin, and the Navmesh page's "no game files" is a notice,
   not console text.
5. **The pointer readout** names the zone and its map coordinates: "Durotar 46.0, 41.7", brighter,
   then world yards and the height (water too, on a tile's polygons). The browser reads the zones
   from `/api/v1/world` and the zone at a point from the area grid (`/api/v1/area`) once the
   pointer rests, kept per 16-yard cell. The ground lost its tooltip, which followed the pointer
   everywhere: the readout says it.
6. **Bootstrap is gone.** Only its 4.3.1 reboot was used; the theme carries a small reset in its
   place (top of `site.css`). Fleet, Forever routes, the error pages and the API page came out
   pixel-identical. Two things differ on purpose: a button clicked with the mouse shows no focus
   ring (the reboot drew one; keyboard focus still shows the theme's), and a label no longer keeps
   an 8 px margin a span label did not have, so every field spaces its label by its 6 px gap.

Not changed: marker and shape names keep their boxes (they read well); the relief's colours by
height are as they were, which paints low land as sea (known issue 29).

Then, at the user's asking (2026-10-06):
7. **The game's map art**, as the Leaflet map showed it, under the routes where the service has it:
   the minimap's tile pyramid (`Json/leaflet/<era>/<continent>`), placed by `MapArt` (from
   `PathingService/World/MapArtTable`, kept in step with `leaflet-watch.js` by a test). The
   **Map** button turns it off; tilted, the relief shows instead. Zoomed in, the walkable polygons
   lie see-through over it. Forever has no art, so its views keep the relief.
8. **Tools you can see.** The tool buttons were bare icons in a column under 3D/Fit and went unseen;
   each now has its name under a "Draw" heading. The Map page drew points only; it now draws lines,
   polygons, rectangles and circles too, as the Leaflet map did, each listed with what it measures
   (length, area, size, radius) and copied as zone map coordinates.

How to check:
- `dotnet test`: 278 tests (none skipped where the minimap art is downloaded).
  `ServiceTests/Api/ControlCenterTests` renders the world view pages offline.
- In a browser, on Linux: `local/browser-checks/` (not in the repository) holds `shots.mjs`, a
  Node script on `playwright-core` driving the installed Chrome headless (WebGL through
  SwiftShader), with `restart-cc.sh` (rebuild the Control Center and serve it on 5120, against the
  Pathing Service on 5110) and `fleet-up.sh` (the Bot Manager on 5130 and two simulated agents).
  `node shots.mjs shots/<name> [scenario ...]` puts every page in a fixed state - a test route
  run, Durotar's layers on, a recorded path opened, a failing route asked for - and photographs
  it; `shots/before` holds the pictures from before step 7. The Windows PC's Playwright .NET
  checks are in its own `local/browser-checks/`.

What bites:
- `wwwroot` scripts and styles are fingerprinted and compressed at build time: build the Control
  Center again after changing them, or the browser gets the old ones.
- The cost zone check (Windows) saves into `Json/road`. It deletes its zone again and the files
  come back byte-identical, but look at `git status` after it.
- Never press "Clear the cache" on the Navmesh page in a check: it deletes the baked navmesh.
- deck.gl 9.4 (luma.gl 9) names the GPU parameters `depthCompare` and `depthWriteEnabled`.
- Headless Chrome hides scrollbars unless told not to (`ignoreDefaultArgs: ['--hide-scrollbars']`).

Open for the user:
- The Babylon view's terrain toggle (the game geometry under tiles, needing game files where the
  service runs) has no home in the Control Center now. deck.gl could draw it as a mesh, if wanted.
- Swimming costs 1.3 times walking (known issue 28): it is why a continent route from Durotar to
  Tanaris runs on the sea floor. Changing it changes how the bot walks.
- Low land drawn as sea (known issue 29) needs a water share per tile in the overview. With the
  map art on, it shows only where the art has no tile, or tilted.
