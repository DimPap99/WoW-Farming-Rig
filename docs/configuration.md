# Configuration

## BlazorServer Configuration Process

The app reads the game state using small blocks of color shown at the top of the screen by an Addon. This needs to be configured.

1. Start the **WoW client** and log in with your character.

2. Run `C:\WoW-Farming-Rig\BlazorServer\run.bat` to start the app and open the browser dashboard.

1. If you get `"Unable to find the Wow process is it running ?"` in the console window then it can't find game executable.

1. When running the BlazorServer for the first time you will have to follow a setup process:
    * Start the game and login with a character
    * Navigate to `2. Addon Configuration`
    * Fill the `Author` input form
    * Fill the `Title` input form
    * Then press `Save` button -> Log should see `AddonConfigurator.Install successful`
    * Should see a loading screen
    * At the top left corner of the game window should see flashing pixels / cells
    * Navigate to `5. Frame Configuration`.
    * Click on `Auto` -> `Start` to validate the frame configuration.

1. Addon Control panel `Status` is `Update Available`
    * Press `Save` button
    * Should see a loading screen
    * Restart the BlazorServer
    * Complete `5. Frame Configuration` steps again
    * Click on `Auto` -> `Start` to validate the frame configuration.

### Command-line Configuration Overrides

`run.bat` forwards all arguments to `dotnet run`, so any setting from `BlazorServer/appsettings.json` can be overridden using `--Section:Property=Value` syntax.

**Example:**

```
run.bat --Reader:Type=WGC --Pathing:Mode=Local --Reader:UseGpu=true
```

| Section | Property | Type | Default | Valid Values | Description |
|---|---|---|---|---|---|
| `Process` | `Id` | int | `-1` | Any process ID | WoW process ID. `-1` for auto-detect |
| `Reader` | `Type` | string | `DXGI` | `DXGI`, `WGC` | Screen reader type. `WGC` = Windows Graphics Capture (supports background window) |
| `Reader` | `UseGpu` | bool | `false` | `true`, `false` | Use GPU acceleration for screen reading |
| `Pathing` | `Mode` | string | `Local` | `Local`, `RemoteV1`, `RemoteV3` | Pathfinding mode. `Local` uses the in-process navmesh — no external service, nothing to start. The remote modes are **probed, not required**: if `RemoteV3` gets no answer the bot falls back to `RemoteV1`, then to `Local`, so a misconfigured remote degrades rather than fails. The log line `Using Local(LocalPathingApi) engine Navmesh` confirms what was picked |
| `Pathing` | `Engine` | string | `Navmesh` | `Navmesh`, `SpotAStar` | In-process engine for `Local` mode (and PathingAPI). `Navmesh` (default) answers queries from pre-baked DotRecast tiles under `Json/PathInfo/navmesh/<era>/`. With `Json\MPQ` present it also bakes missing tiles on demand; without it the engine runs **disk-only** and answers from the downloaded tiles alone. `SpotAStar` is the legacy spot-grid A* and always needs the archives |
| `Pathing` | `hostv1` | string | `localhost` | hostname/IP | RemoteV1 pathing server host |
| `Pathing` | `portv1` | int | `5001` | port number | RemoteV1 pathing server port |
| `Pathing` | `hostv3` | string | `127.0.0.1` | hostname/IP | RemoteV3 pathing server host |
| `Pathing` | `portv3` | int | `47111` | port number | RemoteV3 pathing server port |
| `Diagnostics` | `Enabled` | bool | `false` | `true`, `false` | Enable diagnostic mode |
| `Overlay` | `Enabled` | bool | `false` | `true`, `false` | Enable NPC overlay |
| `Overlay` | `ShowTargeting` | bool | `false` | `true`, `false` | Show targeting overlay |
| `Overlay` | `ShowSkinning` | bool | `false` | `true`, `false` | Show skinning overlay |
| `Overlay` | `ShowTargetVsAdd` | bool | `false` | `true`, `false` | Show target vs add overlay |

These properties can also be edited directly in `BlazorServer/appsettings.json`.

## BlazorServer Dashboard

## Optional - Running HeadlessServer

Similar to BlazorServer project, except without Frontend. Should consume less system resources in general.

While the bot is running there's no way to adjust / tweak the values. In order to take effect, have to restart.

Firstly be sure to compile the project `HeadlessServer\build.bat`

Everything has to be setup inside the [Class Configuration](class-profiles.md#class-configuration) file, in prior.

A successful [Configuration process](#blazorserver-configuration-process) has a result of a following configuration files
* `data_config.json`
* `addon_config.json`
* `frame_config.json`

To see how to first time run the `HeadlessServer` please look at `HeadlessServer\install.bat`.

A few use case when you need to run `install.bat`
* After the first project download
* After git project clone
* After downloading a new version of the project
* After made a change in the source code which result a new Addon Version
* After switched from **FullScreen** to **Windowed mode** thus a `frame_config.json` needed to be recreated

For normal quick startup of `HeadlessServer` please look at the `HeadlessServer\run.bat` or `HeadlessServer\rundev.bat`.

**Required** cli parameter: relative [Class Configuration](class-profiles.md#class-configuration) file name under the [/Json/class/](../Json/class) folder.

**Optional** cli parameters:

Long forms take **two** dashes (`--mode`), short forms one (`-m`).

| cli | Description | Default Value | Possible Values |
| ---- | ---- | ---- | ---- |
| `-m`<br>`--mode` | Pathfinder type | `Local` | `Local` or `RemoteV1` or `RemoteV3` |
| `-p`<br>`--pid` | World of Warcraft process id | `-1` | open up task manager to find PID |
| `-r`<br>`--reader` | Addon data screen reader backend | `DXGI` | `DXGI` or `WGC`. See [Screen Capture Methods](installation.md#screen-capture-methods) |
| `--hostv1` | Navigation Remote V1 host | `localhost` | - |
| `--portv1` | Navigation Remote V1 port | `5001` | - |
| `--hostv3` | Navigation Remote V3 host | `127.0.0.1` | - |
| `--portv3` | Navigation Remote V3 port | `47111` | - |
| `-n`<br>`--viz` | While Remote V1 is available, show Path Visualization<br>Can display Remote V3 Paths as well. | `false` | - |
| `-d`<br>`--diag` | Diagnostics, when set, takes screen captures under `Json\cap\*.jpg` | `false` | - |
| `-o`<br>`--overlay` | Show NpcNameFinder Overlay | `false` | - |
| `-t`<br>`--otargeting` | While overlay enabled, show Targeting points | `false` | - |
| `-s`<br>`--oskinning` | While overlay enabled, show Skinning points | `false` | - |
| `-v`<br>`--otargetvsadd` | While overlay enabled, show Target vs Add points | `false` | - |
| `-g`<br>`--gpu` | Use GPU compute shader for NPC name finding | `false` | `true` or `false` |
| `--loadonly` | Loads the given class profile then exits | `false` | - |

> Both hosts default to `Local` — the in-process navmesh, nothing external to start.
> Pass `--mode RemoteV1` / `--mode RemoteV3` only if you actually run one of those
> services.

e.g. run from Powershell without any optional parameter
```ps
cd C:\WoW-Farming-Rig\HeadlessServer
.\run.bat Hunter_1.json
```

e.g. run from Powershell optional parameters, using `DXGI` reader and forced `Local` pathfinder.
```ps
cd C:\WoW-Farming-Rig\HeadlessServer
.\run.bat Hunter_1.json -m Local
```

e.g. run from Powershell optional parameters, only loads the profile then exits a good indicator that your profile can be loaded
```ps
cd C:\WoW-Farming-Rig\HeadlessServer
.\run.bat Hunter_1.json -m Local --loadonly
```

## WoW Client - Interface Options

Need to make sure that certain interface options are set.

The most important are `Click-to-Move` and `Do Not Flash Screen at Low Health`.

### Auto-Configured Settings

The addon automatically configures the following CVars when it loads. You do not need to set these manually:

| Category | Setting | Value | Purpose |
| ---- | ---- | ---- | ---- |
| Gameplay | Auto Interact | On | Automatic interaction |
| Gameplay | Auto Loot | On | Automatic looting |
| Gameplay | NPC Names | On | Required for NPC name detection |
| Camera | Camera Following Style | Always | Smooth camera tracking |
| Camera | Click-to-Move Camera Style | Always | Consistent camera behavior |
| Camera | Auto-Follow Speed | Max | Fast camera response |
| Graphics | Anti-Aliasing | None | Required for pixel reading |
| Graphics | Vertical Sync | Off | Reduces input lag |
| Graphics | Render Scale | 100% | Accurate pixel colors |
| Graphics | Glow Effect | Off | Clean pixel reading |
| Display | Contrast | 50 | Calibrated for pixel reading |
| Display | Brightness | 50 | Calibrated for pixel reading |
| Display | Gamma | 1.0 | Calibrated for pixel reading |

### Manual Settings

The following settings must still be configured manually from the main menu (ESC) -> Interface Options:

| Interface Option | Value | Why |
| ---- | ---- | ---- |
| Controls - Enable Interact Key | **true** | Required for INTERACTTARGET binding to work |
| Controls - Interact on Left click | &#9744; | Prevents accidental interactions while navigating |
| Combat - Do Not Flash Screen at Low Health | &#9745; | Screen flashing corrupts pixel color reading |
| Combat - Auto Self Cast | &#9745; | Buffs/heals automatically target self when no target |
| Names - Enemy Units (V) | &#9744; | Health bars can obscure NPC names, interfering with detection |
| Accessibility - Cursor Size | **32x32** | Larger cursor is easier for the bot to detect and classify |
| Accessibility - Minimum Character Name Size | **6** | Ensures NPC names are large enough to be detected reliably |

## WoW Client - Key Bindings

**Important Change**: The addon now automatically reads your in-game keybindings. You no longer need to manually configure most keybindings in the class profile - the addon will detect and use whatever keys you have bound in WoW.

### Automatic Keybinding Detection

When the addon loads, it reads your current keybindings from the game and sends them to the application. This means:

- You can keep using your existing keybinding setup
- Modifier keys (Shift, Ctrl, Alt) are fully supported
- The application will use whatever keys you have bound for each action

### Auto-Setup of Essential Bindings

On first run (or if essential bindings are missing), the addon will automatically set up:

**Targeting and Interaction:**
| Binding | Default Key | Why Needed |
| ---- | ---- | ---- |
| Target Nearest Enemy | `Tab` | Primary method for finding and engaging enemies |
| Target Last Target | `G` | Re-targets last killed mob for looting |
| Assist Target | `F` | Targets your target's target (useful for assist mode) |
| Target Pet | `NumpadMultiply` | Required for pet class healing/buffing pet |
| Pet Attack | `NumpadMinus` | Commands pet to attack current target |
| Interact With Target | `Alt-Home` | Opens loot windows, talks to NPCs, interacts with objects |
| Interact With Mouseover | `Alt-End` | Used by NPC name targeting for mouse-based interaction |
| Target Focus | `Alt-PageUp` | Used in Assist Focus mode to target the leader (TBC+) |
| Target Party Member 1 | `Alt-PageUp` | Vanilla alternative (no focus system) |
| Follow Target | `Alt-PageDown` | Used in Assist Focus mode to follow the leader |

**Custom Actions (Secure Buttons):**
| Binding | Default Key | Why Needed |
| ---- | ---- | ---- |
| Stop Attack | `Alt-Delete` | Stops combat when fleeing or to prevent accidental pulls |
| Clear Target | `Alt-Insert` | Clears target to allow fresh targeting |
| Config Toggle | `Shift-PageUp` | Toggles addon config mode for frame setup |
| Flush State | `Shift-PageDown` | Resets addon data queues when state gets out of sync |

**Note**: The command prefix (e.g., `dc`) is derived from your addon title configured during setup. If your addon is named "daq", commands would be `/daq`, `/daqflush`, etc.

### Movement Keys

Movement keys are still configured in the [Class Configuration](class-profiles.md#class-configuration) file since they may vary per profile:

| In-Game | ClassConfiguration Name | Default |
| ---- | ---- | ---- |
| Move Forward | ForwardKey | `UpArrow` |
| Move Backward | BackwardKey | `DownArrow` |
| Turn Left | TurnLeftKey | `LeftArrow` |
| Turn Right | TurnRightKey | `RightArrow` |
| Toggle Run/Walk | WalkKey | *unset* |

To use `WASD` movement, add to your class profile (or see `Json\class\Warrior_1_MovementKeys.json`):
```json
"ForwardKey": 87,   // W
"BackwardKey": 83,  // S
"TurnLeftKey": 65,  // A
"TurnRightKey": 68, // D
```

#### Toggle Run/Walk (`WalkKey`)

Optional, and **off by default**. When set, the WASD spline follower drops to walk speed
on the approach to a hairpin instead of arriving at full run, braking and pivoting —
which is what makes tight switchbacks (tower spirals, the Goldshire hairpin) look and
behave sanely.

Two things are required, and it silently does nothing if either is missing:

1. Bind the key **in game** under `Key Bindings → Movement → Toggle Run/Walk`.
2. Set the same key in your class profile:
   ```json
   "WalkKey": 145   // Scroll Lock, for example
   ```

Leaving it unset is supported — the follower just brakes and pivots instead. If you see
run-speed hairpin approaches, this is the setting that is missing; the log warns once
when the follower asks for walk speed and there is no key to honour it.

### Manual Binding Setup (Optional)

If you prefer to set up bindings manually or the auto-setup didn't work, you can use these slash commands:

| Command | Description |
| ---- | ---- |
| `/<prefix>bindings` | Sets up default action bar keybindings (F1-F12, Numpad) |
| `/<prefix>actions` | Creates and binds the custom secure action buttons |

**Note**: Replace `<prefix>` with your addon's command prefix (e.g., `/dcbindings` if your prefix is `dc`). The prefix is derived from the addon title you configured during setup.

These commands save bindings to your current binding set (account-wide or character-specific).

## Actionbar Key Bindings

The addon reads your actual action bar keybindings from the game. You can use any keys you prefer, including modifier combinations.

### Supported Action Bars

| Actionbar | Slot Range | Default Keys | WoW Binding Names |
| --- | --- | --- | --- |
| Main | 1-12 | `1,2,3..9,0,-,=` | ACTIONBUTTON1-12 |
| Bottom Right | 49-60 | `Numpad1-9, Numpad0` | MULTIACTIONBAR2BUTTON1-12 |
| Bottom Left | 61-72 | `F1-F12` | MULTIACTIONBAR1BUTTON1-12 |

### Using Your Own Keybindings

The application will automatically detect whatever keys you have bound to your action bars. For example:
- If you bound `Shift-1` to action slot 1, the app will press `Shift-1`
- If you bound `Q` to Bottom Left slot 61, the app will press `Q`

### Quick Setup with Default Bindings

If you want to use the recommended default bindings, run `/dcbindings` in-game. This will set up:
- Bottom Right bar (slots 49-60): `Numpad1` through `Numpad0`
- Bottom Left bar (slots 61-72): `F1` through `F12`

The Main action bar (slots 1-12) keybindings are left unchanged since most players already have these configured.

### Action Bar Validation

The frontend now includes an **Action Bar Validation** feature that checks if your spells are placed in the correct slots. If there are mismatches:
- A warning badge will appear showing the number of issues
- You can click to see which spells need to be moved
- Use the **Sync Actionbar** button to automatically place spells in the correct slots

### Known Limitation: Main Action Bar Slots 11 and 12

The Main Action Bar slots 11 and 12 use the `-` and `=` keys respectively. These keys are mapped to `OemMinus` and `OemPlus` ConsoleKey values, which are **keyboard layout dependent**.

**Currently, slots 11 and 12 only work correctly with US English keyboard layout.**

On non-US keyboard layouts, the physical key that produces `-` or `=` may have a different virtual key code. As a workaround:
- Avoid using slots 11 and 12 on the Main Action Bar
- Use the Bottom Right (Numpad) or Bottom Left (F1-F12) action bars instead, which work on all keyboard layouts

## Custom Actions (BindPad)

The BindPad addon is bundled in `Addons/BindPad/` and is required for TBC Classic 2.5.5+ compatibility. Blizzard patched `SecureActionButtonTemplate` macrotext in recent clients, breaking dynamically created secure buttons. BindPad's button works due to its initialization approach.

### How It Works

The addon uses BindPad's secure macro button (`BindPadMacro`) internally to execute custom actions like StopAttack and ClearTarget. These are bound to keys automatically on first run.

| Action | Default Key | What It Does |
| ---- | ---- | ---- |
| Stop Attack | `Alt-Delete` | Stops attacking and cancels casting |
| Clear Target | `Alt-Insert` | Clears current target |
| Config Toggle | `Shift-PageUp` | Toggles addon config mode (`/<prefix>`) |
| Flush State | `Shift-PageDown` | Refreshes addon state (`/<prefix>flush`) |

### Manual Setup

If the bindings were not set up automatically (e.g., you were in combat), run:
```
/<prefix>actions
```

Replace `<prefix>` with your addon's command prefix (e.g., `/dcactions` if your prefix is `dc`).

This creates the secure buttons and binds them to the default keys. The bindings are saved to your current binding set (account-wide or character-specific).

### BindPad Setup

BindPad is bundled in `Addons/BindPad/` and must be installed:
1. Copy the `BindPad` folder to your WoW `Interface/AddOns/` directory
2. Enable the BindPad addon in your addon list
3. The DataToColor addon will automatically use BindPad's secure button for custom actions

### Why Alt-Modified Keys?

The custom actions use Alt-modified keys (`Alt-Delete`, `Alt-Insert`, etc.) because:
- They work reliably with the PostMessage input method (background key sending)
- They are unlikely to conflict with your existing keybindings
- They avoid accidentally triggering actions when typing in chat
