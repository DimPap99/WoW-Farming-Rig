# Features

These are capabilities of the supported Classic and Cataclysm clients. WoW: Forever currently has a read-only diagnostic dashboard; see [Forever port status](forever-port.md).

The current navigation system uses pre-baked, era-specific navmesh tiles and can run without local game archives. Class profiles can use generated routes with a repeatable seed, and the dashboard can inspect detected keybindings. See [navigation](navigation.md), [generated routes](class-profiles.md#generated-routes), and the [user interface](user-interface.md) for setup and limits.

## General Features

- Game fullscreen or windowed mode
- Addon supports all available client languages
- Class profiles for supported clients; examples are under [/Json/class/](../Json/class)

## Combat and Actionbar Features

- Highly configurable combat rotation described in [Class Configuration](class-profiles.md#class-configuration)
- Utilizing the Actionbar related APIs to retrieve ActionbarSlot (usable, cost)
- **Automatic Keybinding Detection**: The addon reads your actual in-game keybindings, so you can use your own key setup
- **Modifier Key Support**: Support for Shift, Ctrl, and Alt modifiers (e.g., `Shift-1`, `Alt-F1`). Note: Only single modifiers are supported, not combinations like `Shift-Alt-1`.
- **Action Bar Validation**: Verifies that spells are placed in the correct action bar slots and warns about mismatches
- **Auto-Setup**: Essential keybindings and custom actions are automatically configured on first run
- Support for up to `34` action bar slots across Main, Bottom Right, and Bottom Left action bars

## Navigation and Grind Features

- Pathfinder in the current zone to the grind location
- Grind mobs in the described `PathFilename`
- Blacklist certain NPCs

## Resource Management

- Loot and `GatherCorpse` which includes (Skin, Herb, Mine, Salvage)
- Vendor goods
- Repair equipment
- Improved Loot Goal
- Added Skinning Goal -> `GatherCorpse` (Skin, Herb, Mine, Salvage)
- Introduced a concept of `Produce`/`Consume` corpses. Killing multiple enemies in a single combat can consume them all.

## Additional Features

- Corpse run
- Semi-automated gathering [mode](modes.md#modes)
- Frontend Dark mode / Light mode
- Frontend Runtime Class Profile picker
- Frontend Runtime Path Profile autocomplete search
- Frontend Edit the loaded profile
- Frontend `ActionbarPopulator` One click to populate Actionbar based on [Class Configuration](class-profiles.md#class-configuration). Uses naming convention: lowercase = macro, capitalized = spell.
- Frontend `Key Bindings` page to view detected in-game keybindings and test key presses
- Frontend `Spell Book` page to view known spells and their spell IDs
- Frontend `Mail` page to configure mail recipient, exclusions, and view mailable inventory
- `DataConfig`: change where the external data(DBC, MPQ, profiles) can be found
- `NPCNameFinder`: extended to friendly/neutral units
- Support more resolutions
- Addon is rewritten/reorganized with performance in mind(caching and reduce cell paint) to achieve game refresh rate speed
