# BlazorServer — the bot, with a web UI

The main entry point. Composes `Core` + `Game` + `Frontend` into a running bot and
serves the browser UI on **:5000**. Almost no logic of its own — if you are changing
behaviour, the change usually belongs in `Core`, and if you are changing a page, in
`Frontend`.

```
Program.cs                  host + DI composition
DependencyInjection.cs      wires Core/Game/Frontend services
MdnsAdvertisingService.cs   advertises http://wowbot.local over mDNS
app.manifest                per-monitor DPI awareness (required for correct capture)
addon_config.json           addon layout, written by the in-app configurator
frame_config.json           screen frame rectangles, written by the configurator
data_config.json            DataConfig Root for this host
appsettings.json            Pathing / Navmesh / SplineFollower options, PathingService:Url
run.bat / build.bat         opens the browser, then `dotnet run -c Release --no-build`
```

## Running

```powershell
.\build.bat
.\run.bat [-- args]
```

`run.bat` launches `http://localhost:5000` first, then starts the host with
`--no-build`, so **build first** or you run a stale binary.

## Things that will bite you

**The `*.json` config files here are live state, not just settings.**
`addon_config.json` and `frame_config.json` are rewritten by the in-app configurator
against the user's actual screen. Do not hand-edit them expecting them to survive, and
do not treat a diff in them as a code change.

**`data_config.json` decides where all data comes from**, and it is read from the
**process working directory** — so this file, not the repo root's, is what a running
BlazorServer uses. See `DataConfig/CLAUDE.md`.

**DPI awareness is load-bearing.** `app.manifest` declares `PerMonitor` /
`dpiAware=true`. Without it the captured screen rectangle does not match what the addon
draws, and every pixel read is wrong. Do not drop the manifest to "simplify" packaging.

**`out*.log` files litter this directory.** They are gitignored (`*.log`) and are
Serilog output from previous runs — not fixtures. Ignore them when scanning the folder.

**mDNS is best-effort.** `MdnsAdvertisingService` learns the real Kestrel port from the
`ApplicationStarted` event rather than assuming 5000, and hostname comes from
`MDNS_HOSTNAME`. A failure here must never stop the bot starting.

**Stopping must release input before tearing down components.** See `Game/CLAUDE.md` —
ordering at shutdown is how held movement keys get stranded.

**The map authors through the Pathing Service.** `/api/v1/*` is forwarded (YARP) to
`PathingService:Url` (default `http://localhost:5010`): recorded paths, cost zones, navmesh
coverage and bakes on the map are the service's. With no service running the forwarder answers
502 and the map says "offline" on its notice line until the service answers; the map's tiles and
data are served from this host's own `Json` folder and keep working. A malformed
`PathingService:Url` is logged and answered 502 with why; it never stops the bot. Run the service
against the same `Json` the bot reads (same PC, or changes committed) until the agent's data sync
exists (Phase 3). There are no controllers here any more.

**The forward is a door to the Pathing Service.** This host listens on `*:5000` and adds the
service's key, while the service serves this machine and its key whoever stands behind it. So the
forward applies the rules to its own caller first (`ServiceClients/Security/ForwardPolicy`, known
issue 47): this PC gets anything; another machine on the LAN reads and authors what the map authors
(paths, roads, danger zones: `ForwardPolicy.LeafletMapAuthors`), but deletes no tiles and starts or
cancels no bake or area grid without the key (403, which the bake panel shows); no page on another
site writes at all; a request this host forwarded itself is refused (508). Bake and clear from the
bot PC itself, or from the Control Center.
