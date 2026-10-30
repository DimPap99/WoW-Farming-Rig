# WoW Farming Rig

Adapted from [Xian55/WowClassicGrindBot](https://github.com/Xian55/WowClassicGrindBot).

WoW Farming Rig is a Windows based World of Warcraft automation project. A Lua add-on exposes game state through pixels; a .NET 10 application reads those pixels, chooses actions, and sends input to the game. The web dashboard, headless host, and pathfinding service are in this repository.

Current development includes a WoW: Forever port. See the status table below for which clients are playable and which features are still diagnostic.

## Project status

| Client | Status | Navigation tiles |
| --- | --- | --- |
| Classic Era, Season of Mastery, Season of Discovery (1.13–1.15) | Supported; Season of Discovery abilities and runes are incomplete | `precata` |
| Burning Crusade Classic (2.5.x), Wrath Classic (3.4.x) | Supported | `precata` |
| Cataclysm 4.3.4 and Cataclysm Classic 4.4.x | Supported | `cata` |
| Mists of Pandaria 5.4.8 and MoP Classic 5.5.x | Add-on work remains; automation is not playable | `mop` |
| WoW: Forever beta 1.60.1 | Read-only diagnostics; automation is not playable | Not available |

Wrath 3.3.5 is not planned because the bot requires *interact with target*, which that client lacks. Client details and limitations are in [navigation](docs/navigation.md#cataclysm-and-mists-of-pandaria-support) and the [Forever port status](docs/forever-port.md).

## What it does

- Reads game state through the `DataToColor` add-on and supports configurable combat, looting, gathering, vendors, mail, trainers, and class profiles on supported clients.
- Navigates with an in-process DotRecast navmesh. Pre-baked tiles can be downloaded by era, so normal navigation does not need local game archives or an external pathing server.
- Offers a Blazor dashboard for configuration and monitoring, a headless host, and central services that run on Linux and macOS as well as Windows. The Pathing Service plans routes and stores recorded paths, Forever routes and road and danger-zone costs. The Bot Manager keeps the bots and their assignments. The Control Center shows every bot with a console each, beside the World workspace: one map whose modes explore what is where (NPCs, gathering nodes and quests, with every place a quest sends a character to), design routes (nodes the route must pass or may bend through, stops and areas; legs walked exactly as drawn or replanned on the navmesh, with or without the cost zones), and edit cost zones and bakes. Each game PC runs one resident bot agent: it starts at logon, pulls a run's files from the Pathing Service first, and keeps playing while the services are down. See [service architecture](docs/service-architecture.md).
- Supports generated grind routes, keybinding detection, and DXGI or Windows Graphics Capture screen readers.
- Shows a read-only Forever diagnostic dashboard with visual health and power percentages, map position, target state, cooldown fill, and action-slot metadata. This port does not start the bot or send game input.

See the [feature reference](docs/features.md), [architecture](docs/architecture.md), and [Forever port status](docs/forever-port.md) for details.

## Quick start

For a **supported** client on Windows 10 or later:

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and clone with the DotRecast submodule:

   ```powershell
   git clone --recurse-submodules https://github.com/DimPap99/WoW-Farming-Rig.git
   cd WoW-Farming-Rig
   ```

2. Download navigation tiles. The download scripts currently fetch WoW Classic (`precata`
   Azeroth and Kalimdor) and WoW: Forever; see [navigation](docs/navigation.md) to widen that:

   ```powershell
   .\scripts\download-navmesh.ps1 -Era precata
   ```

3. Build and launch the dashboard with `BlazorServer\build.bat` and `BlazorServer\run.bat`. Start the WoW client, complete the add-on setup in the browser, load a class profile, and press **Start**.

The [installation guide](docs/installation.md) covers display requirements, add-on setup, capture backends, and headless operation. The [central services](docs/pathing-api.md) (the Pathing Service, the Bot Manager and the Control Center: route planning, the world map, route design, bakes and the fleet of bots) also run on Linux and macOS. Already cloned without submodules? Run `git submodule update --init --recursive` before building.

## Documentation

The [documentation index](docs/index.md) links the user guides and technical notes. Start with:

| Task | Guide |
| --- | --- |
| Install and configure | [Installation](docs/installation.md) · [Configuration](docs/configuration.md) |
| Create a profile | [Class profiles](docs/class-profiles.md) · [Goals](docs/goals.md) · [Requirements](docs/requirements.md) |
| Navigate and record routes | [Navigation](docs/navigation.md) · [Route recording](docs/route-recording.md) |
| Fix a problem or upgrade | [Troubleshooting](docs/troubleshooting.md) · [Keybinding migration](docs/keybinding-migration.md) |
| Follow the Forever port | [Forever port status](docs/forever-port.md) |

## Contributing

Bug reports, questions, feature requests, documentation updates, and pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting a change. Use this repository's [issue tracker](https://github.com/DimPap99/WoW-Farming-Rig/issues) for current work.

## Licensing

There is currently no repository-wide license file. Included third-party components retain their own notices and licenses in the source tree.
