# V1 Remote Pathing - PathingAPI

Pathing is built into the bot, so you don't need to do anything special except provide
navmesh tiles - `download-navmesh.ps1`, no MPQ files required (see
[Setting up navigation](navigation.md#setting-up-navigation--the-short-version)). You can though run
it on its own server to visualise routes as they are created by the bot, or to play with
route finding.

> **One client at a time.** The `RateLimit` filter answers a concurrent request with
> HTTP 429, so this is not a shared service for several bots - see
> [Running multiple bots](navigation.md#running-multiple-bots).

### Serving V3 clients from PathingAPI (AnTCP)

PathingAPI can also speak **AmeisenNavigation's binary AnTCP protocol**, so a bot
configured for `RemoteV3` talks to this server and gets routes from this project's
era-aware navmesh - no AmeisenNavigation binary, no MMAP files, no second process.

Off by default (it opens a socket); enable in `PathingAPI\appsettings.json`:

```json
"Pathing": { "AnTcp": { "Enabled": true, "Ip": "127.0.0.1", "Port": 47111 } }
```

Then set `Pathing:Mode=RemoteV3` on the bot. Port **47111** is the shipped `portv3`, so
`hostv3`/`portv3` need no change — enabling the listener is the only step.

> **Replacing an existing AmeisenNavigation install?** Its own default is **47110**, so
> either set `"Port": 47110` here or leave the bot's `portv3` pointing at whatever your
> old setup used. A port mismatch fails quietly: the ping fails and the bot falls back
> to `RemoteV1`/`Local`, so it still walks and nothing says the remote was ignored.

Only the **PATH** message is implemented, because it is the only one the bot ever
sends (`RemotePathingAPIV3` hardcodes it; its other traffic is the TCP connect used as
a ping). Other message types get an explicit empty reply rather than hanging. Unlike
the HTTP API, concurrent clients **queue** rather than receive 429 - a warm query is
single-digit milliseconds, so several bots share it comfortably even though the search
still serializes.

> One deliberate difference from AmeisenNavigation: when the client has no z it sends
> `area.LocTop / 2`, and `LocTop` is a world **Y** bound rather than a height - for
> Elwynn that is -3969 against terrain at 82. AmeisenNavigation absorbs it with a very
> tall poly-search extent; this engine uses tight vertical extents so multi-floor
> buildings resolve correctly, so the server instead snaps any z further than 128 yd
> from the walkable surface onto it. A bot that genuinely knows its height keeps it.

The bot will try to calculate a path in the following situations:

* Travelling to a vendor or repair.
* Doing a corpse run.
* Resuming the grind path at startup, after killing a mob, or if the distance to the next stop in the path is not a short distance.

## Running on its own server.

In visual studio just set PathingAPI as the startup project or from the command line:

```ps
cd C:\WoW-Farming-Rig\PathingAPI
dotnet run --configuration Release
```

Then in a browser go to http://localhost:5001

**Startup arguments**

| argument | Description | Default |
| ---- | ---- | ---- |
| `--exp=<client>` | Which client's data to serve. Decides `/dbc`, `/tiles` and the navmesh era | `som` |
| `--Pathing:Engine=` | `Navmesh` or `SpotAStar` | `Navmesh` |
| `--bake=<continent>` | Kick a background navmesh bake as the server starts; `all` for every continent. The API serves immediately — progress is on `GET api/PPather/Bake/Status` | off |
| `--bake-area=<continent>` | One-time area-id grid extraction, so area lookups answer without game files; `all` for every continent | off |
| `--urls http://…` | Override the listen address | `http://localhost:5001` |

```ps
dotnet run -c Release -- --exp=cata
dotnet run -c Release -- --exp=legacy_mop --bake=all
```

Baking needs the game archives in `Json\MPQ`; everything else does not. **`Json\MPQ` is
not era-aware** — it is one directory for whichever client you point it at, so running
`--exp=cata` with Wrath archives in there loads Wrath geometry silently and only fails
on a continent Wrath does not have.

`GET api/PPather/SelfTest` reports what the **active engine** actually needs: for
`Navmesh` it counts baked tiles per continent for the current era; for `SpotAStar` it
falls back to checking the archives. It used to check the archives unconditionally and
so reported failure on every tiles-only install even though pathing worked.

There are 3 pages:

* Watch API Calls
* Search
* Route
* Swagger

Requests to the API can be done in a new browser tab like this or via the Swagger tab. You can then view the result in the Watch API calls viewer.

```
http://localhost:5001/api/PPather/MapRoute?map1=1446&x1=51.0&y1=29.3&map2=1446&x2=38.7&y2=20.1
```

Search gives some predefined locations to search from and to.

## Running it along side the bot

In visual studio right click the solution and set Multiple startup projects to BlazorServer and PathingApi and run.

Or from 2 command lines `dotnet run` each.

```ps
cd C:\WoW-Farming-Rig\PathingAPI
dotnet run --configuration Release
```

```ps
cd C:\WoW-Farming-Rig\BlazorServer
dotnet run --configuration Release
```

## As a library used within the bot

The bot will use the PathingAPI to work out routes, these are shown on the route map as green points.
