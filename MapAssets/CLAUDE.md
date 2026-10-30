# MapAssets - the Leaflet map, once

The map's scripts, libraries, styles and icon atlas, as static files only: no code and no
project references. BlazorServer (through Frontend) references it and serves the files at
`_content/MapAssets/`; its page `LeafletComponent` calls the script's `init`. The Control Center
had a copy of the page until its world view replaced it (64aa9c22), and the scripts no longer
assume a second host.

## Layout

```
wwwroot/script/leaflet-watch.js     the map, part 1: eras, Configs, tile bases, shared state,
                                    and the Pathing Service helpers (pathingFetch, mapNotice)
wwwroot/script/leaflet-map.js ...   the map's other parts, in load order: map (data, init),
  ... leaflet-paths.js              layers, coords, spawns, sidebar, paths
wwwroot/script/leaflet-zones.js     cost-zone authoring (roads, danger zones)
wwwroot/script/leaflet-poi.js       custom points, mirrored into the host page's search panel
wwwroot/script/leaflet-navmesh.js   navmesh tile overlay
wwwroot/script/leaflet-bake.js      bake controls
wwwroot/script/icon_atlas.js        sprite positions in img/atlas.png
wwwroot/leaflet, leaflet-pixi, leaflet-groupedlayercontrol   the libraries
wwwroot/css/leaflet-map.css, leaflet-sidebar.css             the map's own rules
```

## Things that will bite you

**Everything the map reads comes from its host's origin.** World data from `/tiles`, `/dbc`,
`/area`, `/npcspawnlocations`, `/mailboxlocations`, `/path` and `/locations`
(`Contracts.Pathing.WorldData`); authoring and coverage from `/api/v1/...`
(`Contracts.Pathing.PathingApi`). BlazorServer serves the data itself and forwards `/api/v1` to
the Pathing Service. Change a route here, change the contract and the host.

**The Pathing Service may be down.** Call it through `pathingFetch` (in `leaflet-watch.js`),
which never throws and reports "offline" once, until the service answers again; tiles, spawns
and the player keep working. A refusal comes back as problem details, and `error` holds the
sentence for a person. A write is not sent to a service that serves another era's data
(`pathingOtherEra`): a Classic map would save into Forever's folders.

**A person must see what went wrong.** `mapNotice` puts a message on the map's own notice line
(bottom left), kept until closed, and in the console: refusals, failed saves, "saved, not in use
yet", offline. The console alone is what the map had until the Phase 2 review (known issue 49).

**A zone map is saved whole.** `costZoneSave` sends a zone map's whole road and danger lists, which
replace the files, and an empty road list deletes the road file. So a page that could not read the
zones never saves: it reads them again first, and refuses with a notice when that fails (known
issue 48). Two pages editing the same zone map still overwrite each other: the last save wins.

**Library names, not URLs.** A recorded path is named relative to the path folder
(`16_Loch Modan.json`, `_herb/x.json`); read and save it at `/api/v1/paths/<name>`, each
segment escaped (`pathNameUrl`). The old controller listed `/path/<name>` and saved it under
`Json/path/path/`.

**Danger zone modes are names on the wire.** The service sends `Avoid`/`Block`; the popup
works with 0/1 (`costZoneModeFromWire`, `costZoneModeToWire`).

**The map script is in pieces.** `leaflet-watch.js` and
`leaflet-map/layers/coords/spawns/sidebar/paths.js` are contiguous runs of one script,
listed in that order in `BlazorServer/App.razor`.
They share one global scope, so a function may call into any piece, but a load-time statement
(`const x = new L.…`) sees only earlier pieces. After moving code across a boundary, update the
table in `scripts/leaflet-pieces.mjs` and run `node scripts/leaflet-pieces.mjs check`; `join`
prints the whole script. `Configs` stays in `leaflet-watch.js`: `CoreTests` reads it from there.

**The JS mirrors `DataConfig` and must be kept in step.** `clientEra()`, `tileEra()`,
`TILE_SHARED_CONTINENTS` and the `Configs` blocks in `leaflet-watch.js` duplicate
`ClientEra` / `TileEra` / `TileSharedContinents` / `LeafletEras`. Change one, change both.

**Tiles fall back to the CDN automatically.** `resolveTileBase()` HEAD-probes a few
low-zoom local tiles per continent and uses `https://bot.tortoiseclothing.org/<era>`
when they are absent. The candidate list has five entries because offset-cropped
continents have no `z2x0y0`. The choice is made once at map load -
`localStorage.leafletTilesLocal = '0'` forces the CDN.

**Area names come from `WorldMapArea.json`, not `AreaTable.json`.** The worldmaparea
carries subzone rows (name + `ParentAreaId` + bounds) when `Json/subzones/<exp>` existed
at generation time. `AreaTable` is only a fallback for an era whose file predates that. Without
`WorldMapArea.json` the map stops after the tiles and says so.

**The host decides the era, not the URL.** The `/Leaflet/{expansion}` segment is advisory:
BlazorServer passes `PlayerReader.Version`.

**Asset paths are `_content/MapAssets/...`.** The atlas is referenced from the scripts and
the CSS (`../img/atlas.png`); a host links the CSS and scripts with `@Assets[...]`.

## Editing JS

`wwwroot/` is served straight from the source folder in development (static web assets): no
bundler, no build step. A browser reload is enough; only C# changes need a rebuild.
