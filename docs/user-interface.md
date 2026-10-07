# User Interface

## Other devices

The user interface is shown in a browser on port **5000** [http://localhost:5000](http://localhost:5000).

If you would like to easily access the UI from elsewhere in your LAN. in powershell run:

```$env:USE_MDNS = 'true'```

once you run the batch file you will now be able to access the UI via [http://wowbot.local:5000](http://wowbot.local:5000).

If you are running multiple bots on the same network, you can use a custom name per bot, in powershell run:

```$env:MDNS_HOSTNAME = 'yourcustomname'```

Now the UI will be available at [http://yourcustomname.local:5000](http://yourcustomname.local:5000).

If you do not accept the "Allow" prompt when you first start the BlazorServer app you will need to open up port **5000** in your firewall.

Control Panel\System and Security\Windows Defender Firewall - Advanced Settings

* Inbound Rules
* New Rule
* Port
* TCP
* Specific local ports: **5000**
* Allow the Connection
* Private, Public, Domain (You may get away with just Private)
* Name "Wow bot"

From another device the map still shows everything and saves recorded paths, roads and danger
zones. Deleting baked tiles ("Clear cache") and starting or cancelling a bake work only on the bot's
own PC, or from the Control Center: they change what every bot uses, and the dashboard has no login.
The map says so when it refuses.

## Components

The web UI is the primary way to interact with the bot. It shows what the bot is doing, allows you to configure settings, and provides debugging tools. Each component serves a specific purpose:

| Component | Purpose | When to Use |
| --- | --- | --- |
| Screenshot | Visual feedback of what the bot "sees" | Debugging detection issues |
| Player Summary | Character stats and target info | Monitoring bot state |
| Route | Path visualization and navigation | Creating/editing grind routes |
| Goal | Shows current AI decision state | Understanding bot behavior |
| Key Bindings | View detected keybindings | Verifying bindings, testing keys |
| Spell Book | View known spells with IDs | Finding spell IDs for profiles |

The UI has the following components:

### Screenshot

**Note**: Due the high resource usage of the component, it does not updated all the time, only when a given **Core** component requires it.

### Player Summary

Show the player state. A hyper link to wowhead appears for the mob you are fighting so you can check out what it drops.

### Route

This component shows:

* The main path
* Your location
* The location of any deaths
* Pathed routes

Pathed routes are shown in Green.


### Leaflet

**Supported:** every era the bot supports — `precata` (`som` 1.13–1.15, `tbc`
2.5.x, `wrath` 3.3.5), `cata` (4.3.4) and `mop` (5.4.8). Clients inside one era
share a tile set, because the world art only changed between them.

**Nothing is required to see the map.** With no tiles on disk the page streams them
straight from the CDN: `resolveTileBase()` in `leaflet-watch.js` probes a few known
low-zoom tiles on the local `/tiles` route per continent, and falls back to
`https://bot.tortoiseclothing.org/<era>/<Continent>/...` when they are missing. So
downloading is an **optimization** - worth it for offline use or to avoid per-tile
latency - not a prerequisite.

* The fallback is decided **once per continent at map load**, so a CDN that is
  unreachable at that moment yields a blank map rather than a retry.
* `localStorage.leafletTilesLocal = '0'` forces the CDN even when local tiles exist
  (handy for checking what a fresh user sees).

**Download them for offline use:**
```powershell
.\scripts\download-leaflet.ps1                       # everything in scope
.\scripts\download-leaflet.ps1 -Continent Kalimdor
```
Writes `<Root>/leaflet/<era>/<Continent>` (destination honors your
`data_config.json` `Root`). `.bat\download-leaflet.bat` is a double-click
wrapper. Only the download scope is fetched (Classic's Azeroth and Kalimdor, and
Forever - see [navigation](navigation.md)): the CDN ships one `<era>-tiles.zip`
per era, and the script reads its directory and range-requests just the
continents in scope instead of the whole bundle.

| era | clients | tiles | bundle |
|---|---|---|---|
| `precata` | `som`, `tbc`, `wrath`, `legacy_*` | 19,562 | 77 MB |
| `cata` | `cata`, `legacy_cata` | 12,262 | 53 MB |
| `mop` | `mop`, `legacy_mop` | 26,972 | 101 MB |

> The `cata` bundle holds only Azeroth and Kalimdor. Northrend and Outland are
> served from `precata` (`DataConfig.TileEra`) because their minimap art is
> effectively unchanged, so pair `-Era cata` with `-Era precata` for full
> coverage. `mop` ships all five continents, Pandaria included.

**Or generate them from your own client:**
```
python scripts/extract-minimap.py
```

* Requires `pip install Pillow` (decodes BLP, writes webp).
* Reads the client archives from `Json\MPQ` by default (override with the
  `WOW_MPQ` env var); uses the bundled `PPather\MPQ\StormLib_x64.dll`.
* Writes `Json\leaflet\<era>\<Continent>\z{z}x{x}y{y}.webp`; pick the era with
  `LEAFLET_ERA` (default `precata`).
* Prints the `Configs` entry (resX/resY/offset) for each continent, matching
  `MapAssets\wwwroot\script\leaflet-watch.js`.

The `/tiles` route resolves to `Json\leaflet\<era>` (`DataConfig.Leaflet`), so
running the bot as som, tbc or wrath all serve the same `precata` tiles.

This component is meant to replace the Route later on, it has *'readonly'* mode when no authoring is enabled.

---

While in the sidebar the Leaflet option there's the authoring tool.

Which allows the draw shapes
* polyline
* rectangle
* circle with custom radius
* single point

#### Roads and danger zones — steering the navmesh

Drawn shapes are not just annotations: they **price the navmesh query**, so the routes
the bot actually walks change to match what you draw.

| You draw | Effect on routing |
| ---- | ---- |
| **Polyline** = a road (safe corridor) | Routes are pulled onto it. A road costs **0.6×** what the same distance costs off-road, so the pathfinder prefers the long way round on a road over a short scramble across open ground. |
| **Rectangle** = danger zone | The enclosed area is priced up (`Avoid`) or made impassable (`Block`). |
| **Circle** = danger zone | Same, as a centre plus radius — the natural shape for "stay this far away from that elite camp". |

Each danger zone carries a **`Penalty`** multiplier and a **`Mode`**:

* **`Avoid`** — multiplies the cost of crossing, capped at 25× so one zone cannot swamp
  the search. The bot crosses anyway if there is no reasonable alternative.
* **`Block`** — impassable. This can genuinely leave a destination unreachable, which is
  the point, but draw it carefully.

**Query-time, no rebake.** Zones are rasterized onto the 33.33 yd terrain-chunk grid and
consulted by the Detour query filter as a single lookup. Editing a road or a zone takes
effect on the **next path request** — the navmesh cache is untouched and nothing is
invalidated. The files are watched and hot-reloaded; a partial read (the watcher firing
mid-save) is discarded rather than applied, so a half-written file will not drop your
roads.

Saved per **era**, continent and UI map (`DataConfig.Road`, which is era-partitioned like
the navmesh and tiles — clients that share a world share their roads):

```
Json\road\<era>\<Continent>\<uiMapId>.json               roads
Json\road\<era>\<Continent>\dangerzone\<uiMapId>.json    danger zones
```

Both are plain JSON and hand-editable. Tuning knobs live under `Navmesh:Query` —
`RoadCore` (default `8`) is the half-width in yards that still routes at full road cost,
the carriageway itself; beyond it the cost ramps back up to open ground.

> A road is priced as the **1.0 baseline** and open ground made dearer, rather than
> pricing roads below 1.0. Detour's A* heuristic assumes no traversal is ever cheaper
> than ~1.0 per unit distance, so a sub-1.0 road made it overestimate the remaining cost
> and long routes came back truncated — Moonbrook to Goldshire reached 628 yd of 2417 yd.
> The relative preference is identical either way.

After clicking on any zone, that zone will be the currentArea.

When the current area is Active, at the left side context based filters can be enabled to show certain types of npcs(vendors, skinnable npcs, or nodes)

On the other hand, when pressing the *redcross* button it is possible to record the player movement as a path.

How to record and save a path ?
* Make sure to have a loaded class profile
* Navigate to Leaflet
* Go to the start location
* Press the *redcross* button to start the record
* When the desired route is reached, press the *redcross* button once again.
* You should see a yellow highlight of the path has been added then it will become red.
* **Right click** on the recorded path, to enable edit mode.
* Edit mode is active when you should be able to move the route node points.
* Mouse **middle click** or **scroll button click** on the Route to save it (should see a yellow highlight once again)
* The route will be saved as the `CurrentZoneName_YYYY_MM_DD_HH_MM_SS.json` under the `/json/path` folder. (example: `Durotar_2025_04_21_15_45_21.json`)
* The saved route can be loaded and edited the same way.

---

### Goal

This component contains a button to allow the bot to be enabled and disabled.

This displays the finite state machine. The state is goal which can run and has the highest priority. What determines if the goal can run are its pre-conditions such as having a target or being in combat. The executing goal is allowed to complete before the next goal is determined.

Some goals (combat,pull target) contain a list of spells which can be cast. The combat task evaluates its spells in order with the first available being cast. The goal then gives up control so that a higher priority task can take over (e.g. Healing potion).

The visualisation of the pre-conditions and spell [requirement(s)](requirements.md#requirement) makes it easier to understand what the bot is doing and determine if the class file needs to be tweaked.
