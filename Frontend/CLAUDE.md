# Frontend — shared Blazor UI

A razor class library shared by `BlazorServer` (the bot) and `PathingAPI` (the pathing
server). Both host the same pages, so **anything added here must tolerate the other
host**: PathingAPI has no `PlayerReader` and no game client, BlazorServer has no bake
API.

## Layout

```
DependencyInjection.cs   static file routes (/path, /tiles, /dbc, /area, ...)
Pages/                   Leaflet, RecordPath, ClassConfig, ... (routable)
Shared/MainLayout.razor  nav menu, incl. which items a client version gets
Shared/SubscribingComponentBase.cs   event subscriptions + thread-safe redraw
Shared/BagGrid.razor     the bag table (Swag, Mail), slot content templatable
Shared/WowheadLink.razor an item, spell or NPC linked to the client's Wowhead page
Shared/PropertyRows.razor  settings rows by reflection (Class Config, Addon Config)
Shared/Terminal.razor    the dark log window (Log, Chat, Forever runs); its rig-*
                         classes live in site.css
Helper/                  Currency, ItemQualityCss, JsonFolderWatcher, JS teardown,
                         PathEditor (the Path Editor's points and selection)
KeyBindings/             the Key Bindings page's checks: KeyboardKeys,
                         KeyBindingCheck (needed vs bound), ActionBarSlotMap
Controllers/             Road / DangerZone / Path authoring endpoints
Forever*.cs, Pages/Forever*.razor   WoW: Forever only (see docs/forever-port.md)
wwwroot/script/
  leaflet-watch.js       the map, part 1: eras, Configs, tile bases, shared state
  leaflet-map.js ...     the map's other parts, in load order: map (data, init),
  ... leaflet-signalr.js layers, coords, spawns, sidebar, paths, signalr
  leaflet-navmesh.js     navmesh tile overlay
  leaflet-bake.js        bake controls
  leaflet-zones.js       zone polygons
wwwroot/css/leaflet-map.css  the map's own rules, linked by both hosts
```

## Things that will bite you

**Subscribe through `SubscribingComponentBase`.** Reader and service events fire on
their own threads. Inherit it, pair each `+=` with its `-=` in `Subscribe(add, remove)`,
hand timers and watchers to `OnDispose`, and redraw with `Redraw()`. A hand-written
`Dispose` without `@implements IDisposable` is never called; `LogComponent`,
`ScreenshotComponent` and `RecordPath` each leaked a handler per page visit that way.
The exception is a component that awaits JS teardown (`LeafletComponent`, `RecordPath`):
it implements `IAsyncDisposable` itself, because Blazor calls only `DisposeAsync` on a
component that has both. Its JS cleanup goes through `InvokeTeardownAsync`.

**`Shared/` components do not see `Pages/` ones.** Pages live in `Frontend.Pages`,
which `_Imports.razor` does not import. Without `@using Frontend.Pages`, a tag like
`<ItemComponent>` renders as an unknown HTML element, and the build only warns (RZ10012).

**Razor keeps the line break between two sibling elements.** Only whitespace next to
a code block (`@if`, `@foreach`) is dropped. A `rig-line` in a `Terminal` is a flex row
for that reason: flex ignores whitespace between items, and only `rig-msg` keeps
`pre-wrap`. Put `pre-wrap` on a whole line and every indented span shows its newline.

**Format numbers for CSS with the invariant culture.** The server may run in a culture
with a decimal comma, and `width: 82,5%` or `rotate(-2,41rad)` is silently ignored.
`ForeverStatus` shows how.

**Static file routes are order-sensitive.** `DependencyInjection` registers
`/tiles/<continent>` for shared-art continents *before* the general `/tiles`. Reversing
that serves the wrong era's art.

**`PhysicalFileProvider` throws on a missing directory** — it does not create one. Use
the `UseDataFolder` helper, which `CreateDirectory`s first. A missing folder here does
not break one route, it kills host startup.

**Never gate UI on a hardcoded client-version list.** Use `DataConfig.HasLeaflet(...)`
(era-derived). The map and its nav entry were stuck on `SoM or TBC` long after wrath,
cata and mop worked, which is exactly how that goes stale.

**The map script is in pieces.** `leaflet-watch.js` and
`leaflet-map/layers/coords/spawns/sidebar/paths/signalr.js` are contiguous runs of one
script, listed in that order in `BlazorServer/App.razor` and
`PathingAPI/Pages/_Host.cshtml`. They share one global scope, so a function may call
into any piece, but a load-time statement (`const x = new L.…`) sees only earlier
pieces. After moving code across a boundary, update the table in
`scripts/leaflet-pieces.mjs` and run `node scripts/leaflet-pieces.mjs check`; `join`
prints the whole script. `Configs` stays in `leaflet-watch.js`: `CoreTests` reads it
from there.

**The JS mirrors `DataConfig` and must be kept in step.** `clientEra()`, `tileEra()`,
`TILE_SHARED_CONTINENTS` and the `Configs` blocks in `leaflet-watch.js` duplicate
`ClientEra` / `TileEra` / `TileSharedContinents` / `LeafletEras`. Change one, change both.

**Tiles fall back to the CDN automatically.** `resolveTileBase()` HEAD-probes a few
low-zoom local tiles per continent and uses `https://bot.tortoiseclothing.org/<era>`
when they are absent. The candidate list has five entries because offset-cropped
continents have no `z2x0y0`. The choice is made once at map load —
`localStorage.leafletTilesLocal = '0'` forces the CDN.

**Area names come from `WorldMapArea.json`, not `AreaTable.json`.** The worldmaparea
carries subzone rows (name + `ParentAreaId` + bounds) when `Json/subzones/<exp>` existed
at generation time. `AreaTable` is only a fallback for an era whose file predates that.

**PathingAPI decides the era, not the URL.** `Pages/Leaflet.razor` in this project reads
`PlayerReader.Version`; PathingAPI's copy reads `DataConfig.Exp`. Neither should trust
the `{expansion}` route segment.

## Editing JS

`wwwroot/script/*.js` is served straight from disk — no bundler, no build step. A
browser reload is enough; only C# changes need a rebuild. Cache-bust query strings
(`?v=…`) are appended by the host.
