# Frontend — the bot's Blazor UI

A razor class library hosted by `BlazorServer`: the bot's dashboard, its configuration pages
and its map page. The central UI is the Control Center, which does not reference this project;
the map's scripts live in `MapAssets` (see its CLAUDE.md), which both hosts serve.

## Layout

```
DependencyInjection.cs   static file routes for the map's data (WorldData: /path, /tiles, /dbc,
                         /area, /locations, ...), from the bot's own data folder
Pages/                   Leaflet, RecordPath, ClassConfig, ... (routable)
Shared/MainLayout.razor  nav menu, incl. which items a client version gets
Shared/SubscribingComponentBase.cs   event subscriptions + thread-safe redraw
Shared/BagGrid.razor     the bag table (Swag, Mail), slot content templatable
Shared/WowheadLink.razor an item, spell or NPC linked to the client's Wowhead page
Shared/PropertyRows.razor  settings rows by reflection (Class Config, Addon Config)
Shared/Terminal.razor    the dark log window (Log, Chat, Forever runs); its rig-*
                         classes live in site.css
Helper/                  Currency, ItemQualityCss, JsonFolderWatcher, JS teardown
KeyBindings/             the Key Bindings page's checks: KeyboardKeys,
                         KeyBindingCheck (needed vs bound), ActionBarSlotMap
Forever*.cs, Pages/Forever*.razor   WoW: Forever only (see docs/forever-port.md)
wwwroot/script/          the dashboard's own scripts (route.js, gather.js, ...);
                         the map's are in MapAssets
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
`/tiles/<continent>` for shared-art continents (`DataConfig.SharedTileFolders`) *before* the
general `/tiles`. Reversing that serves the wrong era's art. The Pathing Service registers the
same routes from its own folder (`PathingService/World/WorldDataFiles.cs`).

**`PhysicalFileProvider` throws on a missing directory** — it does not create one. Use
the `UseDataFolder` helper, which `CreateDirectory`s first. A missing folder here does
not break one route, it kills host startup.

**Never gate UI on a hardcoded client-version list.** Use `DataConfig.HasLeaflet(...)`
(era-derived). The map and its nav entry were stuck on `SoM or TBC` long after wrath,
cata and mop worked, which is exactly how that goes stale.

**The map authors through the Pathing Service.** Recorded paths, cost zones, navmesh coverage
and bakes are `/api/v1` calls, which BlazorServer forwards to `PathingService:Url`; the
controllers that used to answer them here are gone. With the service down, those layers say
"offline" and the rest of the map works. `RecordPath` saves through the bot's own files and
keeps `PathEditor`, now in `ServiceClients`.

**PathingAPI is gone.** Nothing here has to tolerate a second host any more; the Control
Center has its own pages.

## Editing JS

`wwwroot/script/*.js` (and MapAssets' scripts) are served straight from disk — no bundler,
no build step. A browser reload is enough; only C# changes need a rebuild.
