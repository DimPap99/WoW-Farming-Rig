# Installation

This section guides you through the complete setup process. Here's an overview of what you'll do:

| Step | What You'll Do | Why It's Needed |
| --- | --- | --- |
| 1 | Download repository | Get the application source code |
| 2 | Download navmesh tiles | Provides world geometry for pathfinding |
| 3 | Configure system requirements | Ensure WoW displays correctly for pixel reading |
| 4 | Build the application | Compile the source code into runnable programs |
| 5 | Configure the addon | Set up communication between WoW and the application |
| 6 | Start the dashboard | Launch the web interface for control and monitoring |
| 7 | (Optional) HeadlessServer | Run without UI for lower resource usage, or as this PC's bot agent |
| 8-11 | Configure WoW client | Set up keybindings and interface options |
| 12 | Create class profile | Define your combat rotation and behavior |

**Tip**: Steps 1-6 are one-time setup. After initial setup, you'll only need to start WoW, run the application, and load a profile.

## Download this Repository

Clone the repository into a folder such as `C:\WoW-Farming-Rig`. Substitute your own folder path in the commands below.

> **Clone with submodules.** The pathfinder depends on the [DotRecast](https://github.com/DimPap99/DotRecast) git submodule, so a plain `git clone` leaves `external/DotRecast` empty and the build will fail. Clone it in one step:
> ```
> git clone --recurse-submodules https://github.com/DimPap99/WoW-Farming-Rig.git
> ```
> If you already cloned without submodules (or you pull an update that changes them), run:
> ```
> git submodule update --init --recursive
> ```
> Downloading the repo as a ZIP does **not** include submodule contents — use `git clone --recurse-submodules`.

## Pathing setup

The in-process navmesh engine is the default, and its tiles come from
the CDN. If you have not already:

```powershell
.\scripts\download-navmesh.ps1 -Era precata     # Classic; scope: docs/navigation.md
```

The older backends (V1 Local/Remote with MPQ archives, and the external V3
AmeisenNavigation server) are **deprecated** and no longer needed by anything. Their
setup instructions, and where to get the game archives if you want to *bake* your own
tiles, live in
[`docs/legacy-pathing-setup.md`](legacy-pathing-setup.md).

## System / Video Requirements

Tested resolutions with either full screen or windowed:
* 1024 x 768
* 1920 x 1080
* 3440 x 1440
* 3840 x 2160

For Nvidia users, under Nvidia Control panel settings
* Make sure the `Image Sharpening` under the `Manage 3D Settings`-> Global settings or Program Settings(for WoW) is set to `Sharpening Off, Scaling disabled`!

Known issues with other applications:
* `f.lux` can affect final image color on the screen thus prevents NpcNameFinder to work properly.

## Screen Capture Methods

The bot supports two screen capture backends:

| Feature | DXGI | WGC |
|---------|------|-----|
| **Minimum Windows** | Windows 8+ | Windows 10 2004 (build 19041+) |
| **Background capture** | No — game window must be uncovered | Yes — works behind other windows |
| **Borderless capture** | N/A | Yes (build 20348+, no yellow border) |
| **Default** | Yes | No (opt-in via `--reader WGC`) |
| **GPU compute support** | Yes | Yes |

**DXGI** is the default and most compatible.

Use **WGC** if you want to capture the game window while it's behind other windows (requires Win10 2004+).

If **WGC** is requested but unsupported, the application automatically falls back to **DXGI**.

## In-game Requirements

Required game client settings. Press `ESC` -> `System`
  * System > Graphics > Anti-Aliasing: `None`
  * System > Graphics > Vertical Sync: `Disabled`
  * System > Advanced > Contrast: `50`
  * System > Advanced > Brightness: `50`
  * System > Advanced > Gamma from: `1.0`
  * System > Render Scale: `100%`
  * Disable Glow effect - type in the chat `/console ffxGlow 0`
  * To keep/save this settings make sure to properly shutdown the game.

## Optional - Replace Default Game Font

Highly recommended to replace the default in-game font with a much **Bolder** one with [this guide](https://classic.wowhead.com/guides/changing-wow-text-font)

Should be only concerned about `Friz Quadrata: the "Everything Else" Font` which is the `FRIZQT__.ttf` named file.

Example - [Robot-Medium](https://fonts.google.com/specimen/Roboto?thickness=5) - Shows big improvement to the `NpcNameFinder` component which is responsible to find - friendly, enemy, corpse - names above NPCs head.

## Optional - Enable Minimum Character Name Size

In the modern client under ESC > Options > Accessibility > General > **Minimum Character Name Size = 6**

This feature sets a minimum size of the character/npc names above their head.

However it has one downside, there's distance based alpha blending. It could cause issues with Corpse names.

See [troubleshooting](troubleshooting.md) if name detection is unreliable.

## Build Requirements

* Windows 10 and above
* [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
* `AnyCPU`, `x86` and `x64` build supported.
* Windows on ARM64 (e.g. a Win11 ARM64 VM on Apple Silicon) is supported natively — see [Windows on ARM64](#windows-on-arm64-apple-silicon).

## Build the Solution

One of the following IDE or command line
* [Visual Studio 2026](https://visualstudio.microsoft.com/downloads/)
* [Visual Studio Code](https://code.visualstudio.com/)
* [Powershell](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell-on-windows?view=powershell-7.5)

e.g. Build from Powershell
```ps
cd C:\WoW-Farming-Rig
dotnet build -c Release
```

or look at the `BlazorServer\build.bat`, or look at the `HeadlessServer\build.bat` files.

## Windows on ARM64 (Apple Silicon)

The bot runs on Apple silicon through a Windows 11 ARM64 VM — the ARM64 StormLib build
ships with the repo and is selected automatically. Setup steps are in
[`docs/windows-arm64.md`](windows-arm64.md).
