# Navigation

## Pathfinders

Pathfinding allows the bot to navigate the game world - walking around obstacles, across terrain, and to your defined routes.

### Setting up navigation — the short version

**For every supported client there is one answer: the built-in navmesh engine, with
tiles downloaded from the CDN.** It is already the default, so setup is a single
command:

```powershell
.\scripts\download-navmesh.ps1 -Era precata     # use your client's era from the table
```

**You do not need the WoW client, `Json\MPQ`, or any external service to navigate.**
The engine answers from the downloaded tiles alone.

| Your client | Era | What to run | Engine |
|---|---|---|---|
| Vanilla / SoM 1.13–1.15 | `precata` | `download-navmesh.ps1 -Era precata` | Navmesh (default) |
| TBC 2.5.x | `precata` | `download-navmesh.ps1 -Era precata` | Navmesh (default) |
| Wrath 3.3.5 | `precata` | `download-navmesh.ps1 -Era precata` | Navigation data exists; bot unsupported |
| Cataclysm **4.3.4** | `cata` | `download-navmesh.ps1 -Era cata` | Navmesh (default) |
| Cataclysm **Classic 4.4.x** | `cata` | `download-navmesh.ps1 -Era cata` | Navmesh (default) |
| Mists **5.4.8** | `mop` | `download-navmesh.ps1 -Era mop` | Navmesh (default) |
| MoP **Classic 5.5.x** | `mop` | `download-navmesh.ps1 -Era mop` | Navmesh (default) |

> **The download scripts are scoped to WoW Classic and WoW: Forever for now.** They fetch
> `precata` Azeroth and Kalimdor, plus the `forever` era once the CDN publishes it, and
> refuse anything else. To download Outland, Northrend, `cata` or `mop` again, add it to
> `$DownloadScope` in `scripts/download-common.ps1` - nothing else names an era.

**Every client uses the same navmesh engine.** An original client and its modern
re-release are the same world, so they share one era and one set of tiles: `cata` covers
4.3.4 and Cata Classic 4.4.x, `mop` covers 5.4.8 and MoP Classic 5.5.x. Verified with a
`cata` install holding zero game files: Elwynn→Stormwind 410 points, Ratchet→Crossroads
569, the log reporting `disk-only, no game files`.

Each era bundle is **self-contained**: `-Era cata` includes all four continents,
`-Era mop` all five. Unlike the minimap art, navmesh tiles are **never** shared
between eras — a stale mesh would route the bot through walls that exist in your
world, so you download exactly one era and that is all you need.

You only need the game archives in `Json\MPQ` if you want to **bake tiles yourself**
(a custom agent config, or an era with no published bundle) or run the legacy
`Pathing:Engine=SpotAStar`. Details below.

### Running multiple bots

**Give every instance its own in-process navmesh engine** - the default. Point each
BlazorServer / HeadlessServer at the same downloaded tiles (a shared `data_config.json`
`Root` is fine, the tile cache is read-only) and run them independently. No pathing
server, no MPQ, nothing shared at runtime.

The **Pathing Service** (`PathingService/`) answers concurrent queries - one lane per
continent, fresh pathfinder memory per request, no 429 - but it serves the authoring UI, the
[Control Center](pathing-api.md); bots keep planning in process (see
[service architecture](service-architecture.md)). The old one-at-a-time remote server,
PathingAPI, is gone, so **one navmesh per bot is the supported setup.**

Cost of running them separately is small: with disk-only pathing there is **no ADT
geometry in memory at all**, just the navmesh tile cache - tiles average ~16 KB and only
the tens-to-hundreds around the active route stay resident.

### Navmesh engine (default, recommended)

The bot ships an in-process **[DotRecast](https://github.com/DimPap99/DotRecast) navmesh** engine, now the default (`Pathing:Engine=Navmesh`). It runs from small **pre-baked navmesh tiles** and produces smooth, high-quality routes - the basis for the WASD spline follower. Versus the older backends it needs **no external service** and **no MPQ at query time**; the runtime query is pure managed, cross-platform code, and the tile data is tiny enough to distribute. Tiles are baked once from the game's MPQ files (or downloaded pre-baked).

Recommended for every playable client and available for Mists navigation work. Mists automation is not yet playable. It supersedes both the legacy in-process grid pathfinder and the external navigation service.

**Get the tiles — download pre-baked (no client/MPQ needed):**
```powershell
.\scripts\download-navmesh.ps1 -Force
```
Pulls the pre-baked tiles from the CDN into `Json\PathInfo\navmesh\<era>\` (destination honors your `data_config.json` `Root`). Options: `-Continent Kalimdor`, `-Era precata` / `-Era forever` (the scripts' download scope, see above). If you run a custom agent config (different settings hash), bake instead (see [navmesh bake skill](../.claude/skills/navmesh-bake-upload/SKILL.md)).

Downloaded tiles are enough on their own: with no `Json\MPQ\` at all, the navmesh
engine runs **disk-only** - it answers path and height queries from the baked tiles
and logs `disk-only, no game files` per continent. This matters most from Cataclysm
onward, where a client is tens of GB and there is no reason to keep one installed
just to walk over tiles that are already baked. Two things still need the archives,
and say so plainly rather than failing obscurely:

* **Baking** tiles (`POST api/PPather/Bake`, `BakeTile`, area-grid extraction) - it reads ADT geometry.
* **`Pathing:Engine=SpotAStar`** - it walks the triangle world directly and has no tile cache to fall back to.

> Bake settings feed the tile cache directory name, so a disk-only install must be
> configured the same as whatever baked the tiles or it computes a different hash and
> sees none of them. Outland is the live example: it only resolves with
> `Navmesh:Bake:MinWorldZ: { "Expansion01": -700 }`, which is already the shipped default.

Tiles are published per **geometry era**, each complete on its own:

| era | clients | continents | tiles |
|---|---|---|---|
| `precata` | Vanilla, TBC, Wrath (`som`/`tbc`/`wrath`, `legacy_*`) | 4 | 51,113 |
| `cata` | Cataclysm 4.3.4 **and Cata Classic 4.4.x** (`cata`, `legacy_cata`) | 8 | 58,537 |
| `mop` | Mists 5.4.8 **and MoP Classic 5.5.x** (`mop`, `legacy_mop`) | 10 | 70,363 |

> Cataclysm and Mists ship maps that are **not** zones of the four originals and have
> their own MapID, so they need their own bake: Deepholm (646), the Lost Isles + Kezan
> (648), Gilneas (654) and the Maelstrom (730), plus the Wandering Isle (860) on Mists.
> Vashj'ir, Uldum, Mount Hyjal and Twilight Highlands are *not* among them - those are
> zones on Azeroth/Kalimdor and come with the main continents.

> The split matters: Cataclysm's Shattering rebuilt Azeroth and Kalimdor, so the eras are
> genuinely different worlds and never share navmesh tiles. Mists is separate from Cataclysm
> for the same reason - their old worlds are close but not identical, and Mists adds Pandaria.

> **Cataclysm *Classic* (4.4.x) and MoP Classic (5.5.x) use the same bundles** as their
> original counterparts — `ClientEra` maps them to the same era and the world is the same.

**Which should you use?**
- **Everyone**: the default in-process **navmesh** engine - high quality, no external service. Download pre-baked tiles for your era.
- **Baking your own tiles**: needs the original game archives (Vanilla-Wrath, 4.3.4, 5.4.8). The modern re-releases cannot bake from their own install — download the published bundles, or bake from the matching original client.
- **Legacy fallbacks**: the grid engine (`Pathing:Engine=SpotAStar`) remains available. V1 Remote has no server since PathingAPI was removed, and V3 needs an external AmeisenNavigation (see below).

### Legacy / alternative backends

These predate the navmesh engine and are kept for compatibility. Outdoors the app can discover an available service in this order:

* **V3 Remote**: Out of process [AmeisenNavigation](https://github.com/Xian55/AmeisenNavigation/tree/feature/multi-version-guess-z-coord) - external service, superseded by the in-process navmesh engine on **every** client. Kept for compatibility until Phase 4 removes it.
* **V1 Remote**: talked to PathingAPI, which is gone; a bot set to it plans locally instead ([the legacy remote modes](pathing-api.md#the-legacy-remote-modes)). Removed in Phase 4.
* **V1 Local**: In process [PPather](../PPather/) - MPQ-based. Its legacy spot-grid A* engine (`Pathing:Engine=SpotAStar`) is superseded by the navmesh engine.
* World map - Indoors pathfinder only works properly if `PathFilename` exists.
* Dungeons / instances **not** supported!

## Cataclysm and Mists of Pandaria support

**Pathfinding is uniform.** Every client from Vanilla to Mists uses the same in-process
navmesh engine, running from pre-baked tiles. Nothing about Cataclysm or Mists needs a
different pathfinder, an external service, or a game client on disk.

| client | status | tiles |
|---|---|---|
| **4.3.4 Cataclysm (2011)** | supported | `-Era cata` |
| **Cataclysm Classic 4.4.x** | supported | `-Era cata` |
| **5.4.8 Mists (2013)** | not yet playable — addon work outstanding | `-Era mop` |
| **MoP Classic 5.5.x** | not yet playable — addon work outstanding | `-Era mop` |

### What "not yet playable" means for Mists

Everything below the addon is done — navmesh, Leaflet maps, DBC, creature, area, spawn
and teleport data are all generated and published for both Mists clients, and the addon
loads and runs without errors. What remains is Mists-specific addon work. Cataclysm, by contrast,
is complete end to end including the addon.

The game-data side was the harder half and is finished: Mists chunk formats match 4.3.4
(same split ADT, MCNK header, M2 v272, WMO v17), and the two client-level differences are
handled — it ships a 5.0.x `world.MPQ` plus 23 `wow-update-base-*.MPQ` **incremental patch
archives** carrying updated terrain for **37% of root ADTs**, which the archive layer
chains in build order, and its `holes_high_res` terrain holes are read. Mists is its own
geometry era (`mop`) and never shares tiles with Cataclysm.

Published: 26,972 Leaflet tiles across all ten continents and a 70,363-tile navmesh bake
(`download-leaflet.ps1 -Era mop`, `download-navmesh.ps1 -Era mop`).

### Baking your own tiles

Only relevant if you want a custom agent config — otherwise download the published
bundles and skip this.

Baking reads terrain out of the game's own archives, so it needs an original client:
Vanilla through Wrath, 4.3.4 Cataclysm, or 5.4.8 Mists. The modern re-releases
(4.4.x, 5.5.x) store their data in a format the reader does not open, so they cannot bake
from their own install — download the published tiles, or bake from the matching original
client. The result is identical either way, because the re-release and its original are
the same world.

Developers: the measured per-client format differences and the storage design notes are in
[the storage abstraction doc](mpq-casc-storage-abstraction.md).
