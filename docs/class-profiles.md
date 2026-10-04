# Class Configuration

If one of the Properties is not explicitly mentioned during the configuration or in the examples, you can assume it uses the default value!

Each class has a configuration file in [/Json/class/](../Json/class) e.g. the config for a `Warrior` it is in file [Warrior_1.json](../Json/class/Warrior_1.json).

The configuration file determines what spells the character casts, when pulling and in combat, where to vendor and repair and what buffs to consider.

Take a look at the class files in [/Json/class/](../Json/class) for examples of what you can do.

Your class file probably exists and just needs to be edited to set the pathing file name.

### Understanding Class Configuration

The class configuration controls all aspects of bot behavior. Here's why each section matters:

| Section | Purpose |
| --- | --- |
| **Loot/Skin/Herb/Mine** | Controls what the bot does after killing mobs. Enable based on your character's professions. |
| **UseMount** | Speeds up travel between grind spots. Disable if you don't have a mount or in areas where mounting is problematic. |
| **PathFilename** | Defines where the bot walks. Without a path, the bot won't know where to go. |
| **NPCMaxLevels_Above/Below** | Prevents attacking mobs that are too high (dangerous) or too low (no XP). |
| **Blacklist** | Avoids specific mobs that cause problems (e.g., quest mobs, mobs with annoying abilities). |
| **Pull/Combat/Flee** | The core combat rotation. Pull defines how to start fights, Combat is the main rotation, Flee is emergency escape. |
| **NPC** | Defines vendor/repair routes. Essential for extended grinding sessions. |
| **Mail/MailConfig** | Automatically sends excess items and gold to another character. Useful for keeping inventory clear during long sessions. |

| Property Name | Description | Optional | Default value |
| --- | --- | --- | --- |
| `"Log"` | Should logging enabled for [`KeyAction(s)`](#keyaction). Requires restart. | true | `true` |
| `"LogBagChanges"` | Should bag changes logs enabled for. | true | `true` |
| `"Loot"` | Should loot the mob | true | `true` |
| `"Skin"` | Should skin the mob | true | `false` |
| `"Herb"` | Should herb the mob | true | `false` |
| `"Mine"` | Should mine the mob | true | `false` |
| `"Salvage"` | Should salvage the mob | true | `false` |
| `"UseMount"` | Should use mount when its possible | true | `false` |
| `"AllowPvP"` | Should engage combat with the opposite faction | true | `false` |
| `"TargetNeutral"` | Should detect neutral (yellow) nameplates in addition to hostile (red). Enable for starting zones (levels 1-5) where mobs are neutral. | true | `false` |
| `"AutoPetAttack"` | Should the pet start attacking as soon as possible | true | `true` |
| `"CrossZoneSearch"` | Allow NPC search across zone boundaries for cross-zone routes | true | `false` |
| `"KeyboardOnly"` | Use keyboard to interact only. See [KeyboardOnly](#keyboardonly) | false | `true` |
| --- | --- | --- | --- |
| `"PathFilename"` | [Path](#path) to use while alive | **false** or [Multiple Paths with Requirements](#multiple-paths-with-requirements) | `""` |
| `"PathThereAndBack"` | While using the path, [should go start to and reverse](route-recording.md#there-and-back) | true | `true` |
| `"PathReduceSteps"` | Reduce the number of path points | true | `false` |
| `"SideActivityRequirements"` | List of [Requirements](requirements.md#requirement) to limit when the player should search for target<br/>Great for enforcing how closely should follow the path. | true | `true` |
| --- | --- | --- | --- |
| `"Paths"` | Array of [PathSettings](#pathsettings).<br>Either define this array or use the above properties | true | `[]` |
| `"Seed"` | Pins the randomness of [generated routes](#generated-routes) so a session replays identically.<br>Unset draws a fresh seed per session and logs it. | true | `null` |
| `"Mode"` | What kind of [behaviour](modes.md#modes) should the bot operate | true | `Mode.Grind` |
| `"NPCMaxLevels_Above"` | Maximum allowed level above difference to the player | true | `1` |
| `"NPCMaxLevels_Below"` | Maximum allowed level below difference to the player | true | `7` |
| `"CheckTargetGivesExp"` | Only engage the target if it yields experience | true | `false` |
| `"Blacklist"` | List of names or sub names which must be avoid engaging | true | `[""]` |
| `"TargetMask"` | [`TargetMask`](#targetmask) — [UnitClassification](https://wowpedia.fandom.com/wiki/API_UnitClassification) types that allowed to engage with. | true | `"Normal, Trivial, Rare"` |
| `"NpcSchoolImmunity"` | List of NpcIDs which have one or more [SchoolMask](#npcschoolimmunity) immunities | true | `""` |
| `"IntVariables"` | List of user defined `integer` or `integer[]` variables. See [`IntVariables`](#intvariables) | true | `[]` |
| `"StringVariables"` | List of user defined `string` variables. See [`StringVariables`](#stringvariables) | true | `[]` |
| --- | --- | --- | --- |
| `"Pull"` | [KeyActions](#keyactions) to execute upon [Pull Goal](goals.md#pull-goal) | true | `{}` |
| `"Flee"` | [KeyActions](#keyactions) to execute upon [Flee Goal](goals.md#flee-goal). | true | `{}` |
| `"Combat"` | [KeyActions](#keyactions) to execute upon [Combat Goal](goals.md#combat-goal) | **false** | `{}` |
| `"AssistFocus"` | [KeyActions](#keyactions) to execute upon [Assist Focus Goal](goals.md#assist-focus-goal) | **false** | `{}` |
| `"Adhoc"` | [KeyActions](#keyactions) to execute upon [Adhoc Goals](goals.md#adhoc-goals) | true | `{}` |
| `"Parallel"` | [KeyActions](#keyactions) to execute upon [Parallel Goal](goals.md#parallel-goals) | true | `{}` |
| `"NPC"` | [KeyActions](#keyactions) to execute upon [NPC Goal](goals.md#npc-goals) | true | `{}` |
| `"Wait"` | [KeyActions](#keyactions) to execute upon [Wait Goal](goals.md#wait-goals) | true | `{}` |
| --- | --- | --- | --- |
| `"Mail"` | Enable mail functionality | true | `false` |
| `"MailFilename"` | External mail config file path (relative to `Json/mail/`) | true | `""` |
| `"MailConfig"` | Inline mail configuration object. See [Mail Goal](mail-goal.md#mail-goal) | true | `{}` |
| --- | --- | --- | --- |
| `"GatherFindKeys"` | List of strings for switching between gathering profiles | true | `string[]` |
| --- | --- | --- | --- |
| BaseActionKeys | Description | Optional | Default value |
| --- | --- | --- | --- |
| `"Jump.Key"` | Key to Jump | true | `"Spacebar"` |
| `"Interact.Key"` | Key to Interact with target. Supports modifiers. | true | `"Alt-Home"` |
| `"InteractMouseOver.Key"` | Key to Interact with mouseover. Supports modifiers. | true | `"Alt-End"` |
| `"TargetLastTarget.Key"` | Key to Target last target | true | `"G"` |
| `"ClearTarget.Key"` | Key to clear current target. Supports modifiers. | true | `"Alt-Insert"` |
| `"StopAttack.Key"` | Key to stop attack. Supports modifiers. | true | `"Alt-Delete"` |
| `"TargetNearestTarget.Key"` | Key to target nearest enemy | true | `"Tab"` |
| `"TargetTargetOfTarget.Key"` | Key to target - target of target | true | `"F"` |
| `"TargetPet.Key"` | Key to target pet | true | `"Multiply"` |
| `"PetAttack.Key"` | Key to send pet to attack | true | `"Subtract"` |
| `"TargetFocus.Key"` | Key to target focus (TBC+) or party member 1 (Vanilla). Supports modifiers. | true | `"Alt-PageUp"` |
| `"FollowTarget.Key"` | Key to follow target. Supports modifiers. | true | `"Alt-PageDown"` |
| `"Mount.Key"` | Key to use mount | true | `"O"` |
| `"StandUp.Key"` | Key to stand up / sit down | true | `"X"` |
| --- | --- | --- | --- |
| `"ForwardKey"` | [ConsoleKey](https://learn.microsoft.com/en-us/dotnet/api/system.consolekey) to be pressed to move forward | true | `"UpArrow"` |
| `"BackwardKey"` | [ConsoleKey](https://learn.microsoft.com/en-us/dotnet/api/system.consolekey) to be pressed to move backward | true | `"DownArrow"` |
| `"TurnLeftKey"` | [ConsoleKey](https://learn.microsoft.com/en-us/dotnet/api/system.consolekey) to be pressed to turn left | true | `"LeftArrow"` |
| `"TurnRightKey"` | [ConsoleKey](https://learn.microsoft.com/en-us/dotnet/api/system.consolekey) to be pressed to turn right | true | `"RightArrow"` |

The following [KeyActions](#keyactions) are `BaseActions`: `Jump`, `Interact`, `InteractMouseOver`, `TargetLastTarget`, `ClearTarget`, `StopAttack`, `TargetNearestTarget`, `TargetTargetOfTarget`, `TargetPet`, `PetAttack`, `TargetFocus`, `FollowTarget`, `Mount`, `StandUp`.

**Note on Modifier Keys**: BaseActions that support modifiers can be specified with `Shift-`, `Ctrl-`, or `Alt-` prefixes. For example:
```json
"Interact": {
    "Key": "Alt-Home"
},
"ClearTarget": {
    "Key": "Alt-Insert"
}
```

**WoW: Forever**: the guarded Forever runtime presses the key the client binds to each command (forward, strafe, `AutoAttack`, `Interact` and the main-bar buttons), so those need no entry here. It selects targets by clicking their nameplates rather than with `TargetNearestTarget`, so enemy nameplates must always be shown in the client. It pulls units within `NPCMaxLevels_Above` and `NPCMaxLevels_Below` of a kind `TargetMask` allows, choosing by cost, a unit on its own first (see [Forever tasks](forever-tasks.md)); a unit that attacks the player or the pet is fought whatever its level or kind. `ClearTarget.Key` names the key of a `/cleartarget` macro on the main bar, e.g. `"ClearTarget": { "Key": "7" }`; it is used only while the client binds that key to a macro slot, otherwise Escape, and only while a target is selected. With `"Loot": true` the bodies of a fight are looted once it is over: the runtime walks to where each fell and presses `Interact` when the client's Interact Key finds it, so the client needs Enable Interact Key and Auto Loot turned on. With `"Skin": true` each body is also skinned once its loot is taken, where the game allows it. With `AutoPetAttack` (the default) a living pet is sent at the target with the client's Pet Attack key before the opener, and again whenever it attacks something else, so Pet Attack must be bound in the client. A unit that attacks the pet counts as attacking the player. See [WoW: Forever port](forever-port.md).

Which are shared and unified among [Pull Goal](goals.md#pull-goal) and [Combat Goal](goals.md#combat-goal).

e.g override default [KeyActions](#keyactions) properties in the [Class Configuration](#class-configuration) file
```json
"Mount": {
    "Key": "N0"
},
"Jump": {
    "PressDuration": 200
}
```

### TargetMask

Based of [UnitClassification](https://wowpedia.fandom.com/wiki/API_UnitClassification) API.

| UnitClassification |
| --- |
| Normal |
| Trivial |
| Minus |
| Rare |
| Elite |
| RareElite |
| WorldBoss |

The mentioned values can be combined together like:

e.g.
```json
"TargetMask": "Normal, Trivial, Rare, Elite, RareElite",    // multiple combined
```

Where `Elite` and `RareElite` have been included compared to default.

### NpcSchoolImmunity

| SchoolMask |
| --- |
| None |
| Physical |
| Holy |
| Fire |
| Nature |
| Frost |
| Shadow |
| Arcane |

Defined as KeyValuePair where the key is the NPC Id and the value is the type of `SchoolMask` type is immune against.

The mentioned values can be combined together like:

e.g.
```json
"NpcSchoolImmunity": {
    "1043": "Shadow",                   // single
    "9026": "Fire",
    "1668": "Fire, Shadow, Arcane"      // multiple combined
}
```

### KeyboardOnly

By Default, the bot attempts to use the mouse for the following reasons:
* `Follow Route Goal` Targeting non blacklisted npcs
* `Loot Goal` and `Skinning Goal` while acquiring target

You can disable this behavior by setting [`KeyboardOnly`](#keyboardonly) to `true` in the [Class Configuration](#class-configuration). Which has the following effects:
* `Loot` limited, only capable of looting by selecting last target. So after each killed mob only the **last npc** can be looted.
* GatherCorpse(`Skin`, `Herb`, `Mine`, `Salvage`) unavailable.
* Target selection limited to only `TargetNearestTargetKey`, which significantly reduce how quickly can find the next target.

### IntVariables

Gives the ability to the user to define global integer variables along the whole [Class Configuration](#class-configuration) scope.

Each value can be either a single integer or an **array of integers**. Array values are useful for aura prefixes (`Buff_`, `Debuff_`, `TDebuff_`, `TBuff_`, `FBuff_`) where you want to check if **any** of several icon IDs is active. The variable evaluates to the **maximum remaining time** across all IDs in the array, so `Debuff_POISON > 1` is true when any of the listed poisons is active.

For example look at the Warlock profiles.
```json
"IntVariables": {
    "DOT_MIN_HEALTH%": 35,
    "TDebuff_Frost Fever": 237522,   // iconId https://www.wowhead.com/icons
    "TDebuff_Blood Plague": 237514,  // iconId https://www.wowhead.com/icons
    "FBuff_Rejuvenation": 12345,
    "Buff_Slice and Dice": 99999,
    "Debuff_Poison": 135368,
    "TBuff_Dispel on Target": 16846,
    "Item_Soul_Shard": 6265,
    // ...
    // ...
    "ITEM_ARROW": 2512,
    "MIN_COUNT_ARROW": 200,
    "AMMO_SLOT": 5
}
```

**Array syntax** — group multiple icon IDs under a single variable:
```json
"IntVariables": {
    "Debuff_POISON": [136006, 136007, 136016, 136064, 136067, 136077, 136093, 134437, 132273, 132274, 132105],
    "Debuff_DISEASE": [136127, 136134, 134324, 135914]
}
```
Then use a single requirement instead of chaining many `||` conditions:
```json
"Requirements": ["Debuff_POISON > 1 || Debuff_DISEASE > 1"]
```

### StringVariables

Similar as [IntVariables](#intvariables) just for string values

Note: if the `value` matches any of the [`IntVariables`](#intvariables) key, the `value` will be replaced with the IntVariable value.

When the variable name starts with `$ITEM_NAME` the value will be replaced with the Item English localized, in this example `Rough Arrow`.

When the variable name starts with `$NPC_NAME` the value will be replaced with the NPC English localized name.

```json
"StringVariables": {
    "$ITEM_NAME_ARROW": "ITEM_ARROW",
    "$AMMO_SLOT": "AMMO_SLOT"
}
```

### Path

The path that the player follows during [Follow Route Goal](goals.md#follow-route-goal), its a `json` file under [/Json/path/](../Json/path) which contains a list of `x`,`y`,`z` coordinates while looking for mobs.

### PathSettings

| Property Name | Description | Optional | Default value |
| --- | --- | --- | --- |
| `"PathFilename"` | [Path](#path) to use while alive | **false**, unless `"Generate"` is set | `""` |
| `"Generate"` | [Generate the route](#generated-routes) from spawn data instead of a file | true | `null` |
| `"Id"` | <b>Must be a Unique Integer value</b> to identify PathSettings. | true | `"Auto incremented from zero"` or `"Unless specified by user."` |
| `"PathThereAndBack"` | While using the path, [should go start to and reverse](route-recording.md#there-and-back) | true | `true` |
| `"PathReduceSteps"` | Reduce the number of path points | true | `false` |
| `"SideActivityRequirements"` | List of [Requirements](requirements.md#requirement) to limit when the player should search for target<br/>Great for enforcing how closely should follow the path. | true | `true` |

### Simple approach

When the below properties are defined in the [Class Configuration](#class-configuration), a new [PathSettings](#pathsettings) instance is created under in `Paths` array as the first element.

```json
"Id": 42,                                                               // Optional - Helps identify the path
"PathFilename": "_pack\\1-20\\Dwarf.Gnome\\1-4_Dun Morogh.json.json",   // the path to walk when alive
"PathThereAndBack": true,                                               // if true walks the path and the walks it backwards.
"PathReduceSteps": true,                                                // uses every other coordinate, halve the coordinate count
"SideActivityRequirements": [ "PathDist_0 < 10" ]                       // Limit when should search for target, note if multiple paths are used _0 has to be changed accordingly
```

I keep the previously mentioned properties for backward compatibility and also if you not interested in changing path during runtime.

Example can be found under [Warrior_1.json](../Json/class/Warrior_1.json).

### Multiple Paths with Requirements

With the latest update it is possible to change between multiple paths during runtime.

In that case properties what mentioned in [Simple approach](#simple-approach) are ignored.

Instead using another structure called [Class Configuration.Paths](#class-configuration) array, which is very similar, however there is an additional [Requirements](requirements.md#requirement) field. Backed by [PathSettings](#pathsettings) object.

Let's look at the following example
- It is really important to always have one `Path` which doesn't have any condition, serves as fallback.
- 3 paths defined here. 2 with conditions and 1 with fallback(no requirements which means it can always run)
- The definition order matters, the first element has the highest priority, while the last element in the array has the lowest.
- Each path is added as a new [Follow Route Goal](goals.md#follow-route-goal) with a custom cost. The base cost is 20, and its auto incremented by `0.1f`. So you can even add your own logic in between the goals.
- Each [Follow Route Goal](goals.md#follow-route-goal) component preserves it state from the last execution time.
- It can accept [Requirements](requirements.md#requirement) as condition.

```json
"Paths": [
{
    "PathFilename": "1-5_Gnome.json",                                 // Only runs when the player is below level 4
    "PathThereAndBack": false,
    "PathReduceSteps": false,
    "Requirements": [
        "Level < 4"
    ]
},
{
    "PathFilename": "_pack\\1-20\\Dwarf.Gnome\\1-4_Dun Morogh.json",  // Only runs when the player is at least level 4 but below level 5
    "Requirements": [
        "Level < 5"
    ]
},
{
    "PathFilename": "_pack\\1-20\\Dwarf.Gnome\\4-6_Dun Morogh.json",  // Runs when the player is at least level 5
    "PathThereAndBack": false,
    "PathReduceSteps": false
}
],
```

The previously mentioned example can be found under [Hunter_1.json](../Json/class/Hunter_1.json).

### Generated Routes

Instead of pointing at a recorded file, a path can describe **what to grind** and let the bot
build the route from the running client's own NPC spawn data and the baked navmesh.

This exists because a recorded route is tied to one race, one level band and one client - and
the Cataclysm/Mists zone revamps moved the world, so routes recorded on Classic/TBC/Wrath do
not survive there. The spawn data is already partitioned per client, so the same profile entry
produces a valid route on every one.

```json
"Paths": [
{
    "Generate": {
        "Subzone": "Northshire Valley",
        "Mobs": "Name:Wolf || Name:Kobold",
        "Mode": "Wander"
    },
    "Requirements": [ "Level < 5", "Race:Human" ]
},
{
    "Generate": {
        "Zone": "Elwynn Forest",
        "Mobs": "Type:Humanoid && Level <= PlayerLevel + 2 && !Elite"
    },
    "Requirements": [ "Level < 10" ]
}
],
```

| Property Name | Description | Optional | Default value |
| --- | --- | --- | --- |
| `"Zone"` | Zone name, e.g. `"Elwynn Forest"` | true | player's current zone |
| `"Subzone"` | Subzone name, e.g. `"Northshire Valley"`. Narrower than `Zone`; wins when both are set | true | `null` |
| `"UIMapId"` | Explicit map id; wins over both names | true | `0` |
| `"Name"` | Label shown as the goal name and in logs | true | derived from `Subzone`/`Zone` |
| `"Mobs"` | Which mobs shape the route - see [Mob filter](#mob-filter-creature-scope) | true | mobs within `PlayerLevel - 3 .. + 2`, non-elite |
| `"Mode"` | `"Wander"` regenerates a fresh set of spots every lap. `"Loop"` builds one fixed closed tour, ordered shortest-first | true | `"Wander"` |
| `"Focus"` | `"Density"` sticks to where mobs pack together and drops isolated spawns. `"Balanced"` drops only the sparsest. `"Coverage"` takes in everything that matched | true | `"Density"` |
| `"MinClusterShare"` | Override: a spawn cluster is kept only if it holds this share of what the biggest cluster holds | true | from `Focus` |
| `"DensityBias"` | Override: how hard **Wander** spots are drawn to dense areas. `0` uniform, `1` proportional, `2` packs dominate. Not used by `Loop` | true | from `Focus` |
| `"MinCellShare"` | Override: share of the busiest spot's mob count a place must hold to become a **Loop** stop. This is what `Focus` uses to skip thin outlying spots | true | from `Focus` |
| `"WaypointSpacingYards"` | Spacing of the emitted waypoints. Recorded routes sit near 17; much denser makes the follower brake constantly | true | `15` |
| `"Stops"` | How many hunting spots to pick. The route is then filled in by pathing between them, so the waypoint count is much higher than this | true | `8` |
| `"PaddingYards"` | How far the roaming area is grown beyond the spawn footprint | true | `25` |
| `"MaxVerticalDelta"` | Rejects a spot this far above the mob that anchored it - keeps routes off rooftops and ledges | true | `5` |
| `"MinSpacingYards"` | Minimum gap between two spots | true | `30` |

The generated route is **dense**: after picking the spots, each consecutive pair is pathed
through the navmesh and those points become the waypoints. Target acquisition happens while
walking, so a long straight hop between two spots would skip every mob beside it.

#### Wander vs Loop

`"Wander"` picks its spots at random - weighted toward dense areas - and picks a **fresh set
every lap**, so the bot never retraces the same line. Good for staying unpredictable.

`"Loop"` takes the densest spots in the area, measures the real walking distance between every
pair, and orders them into the shortest closed tour. The result is the same route every
session for a given zone and mob filter, and a lap covers the area once instead of
criss-crossing it. It is the drop-in replacement for a recorded route.

Set `"PathThereAndBack": false` with `"Loop"` - the tour already returns to its start, so
walking it backwards afterwards just repeats it in reverse.
| `"Seed"` | Pins this path's randomness. Overrides the profile-level `"Seed"`. `Loop` ignores it - that mode is deterministic already | true | `null` |

`"Seed"` at the [Class Configuration](#class-configuration) level pins every generated route in
the session. Left unset, a fresh seed is drawn per session **and written to the log**, so a run
can still be reproduced afterwards.

Generation needs a **baked navmesh** for the continent. Without one it fails, logs the reason,
and that path simply drops out of planning so the next one in the profile takes over - so keep
a fallback path with no requirements, exactly as with recorded routes.

Preview and tune these in the browser under **Route Gen**, which draws the result on the
Leaflet map and can save it to `Json/path` as an ordinary recorded route.

#### Mob filter (creature scope)

`"Mobs"` uses the same expression language as [Requirement](requirements.md#requirement) - `&&`, `||`, `!`,
parentheses, arithmetic and comparisons all work.

> **The subject is a creature, not the player.** `Level` here is the *mob's* level; the
> player's is `PlayerLevel`. So `"Level <= PlayerLevel + 2"` reads exactly as you'd say it.
> Player-only names such as `Health%` or `Race:` are not available here and will fail to
> parse, which is intentional - a filter must not silently test the wrong thing.

| Name | Meaning |
| --- | --- |
| `Level` | Average of the mob's min/max level |
| `MinLevel` / `MaxLevel` | The mob's level range |
| `Rank` | 0 normal, higher for elite/rare |
| `NpcId` | Creature entry id |
| `SpawnCount` | How many of this mob spawn inside the target zone |
| `PlayerLevel` | The player's level |
| `Elite` | `Rank > 0` |
| `Skinnable` | The mob can be skinned |
| `Hostile` | Attackable by the player's faction |
| `Type:[type]` | Creature type - same values as [Target requirements](requirements.md#target-requirements), e.g. `Type:Humanoid` |
| `Name:[text]` | Mob name contains `text`, case-insensitive. `Name:Wolf` covers Young Wolf, Rabid Wolf, ... |
| `Faction:[id]` | Faction template id |
| `Family:[id]` | Creature family id |

e.g.
```json
"Mobs": "Type:Humanoid && !Elite"                              // any non-elite humanoid
"Mobs": "(Name:Wolf || Name:Kobold) && Level >= PlayerLevel - 3"
"Mobs": "Faction:7 && !Name:Defias"                            // a faction, minus one family of mobs
"Mobs": "NpcId == 299 || NpcId == 6"                           // exactly these two
"Mobs": "SpawnCount > 8 && Skinnable"                          // dense skinning spots only
```

Regardless of the expression, service NPCs, critters, totems, pets and anything not hostile to
your faction are never picked.

#### Time-based Path Cycling

You can cycle between grinding spots on a timer using `SessionMinutes % N` in path requirements. `SessionMinutes` tracks how long the current session has been running, and the modulo `%` operator divides time into repeating intervals.

**Example: Alternate between two paths every 20 minutes (40-minute cycle)**

```json
"PathFilename": [
{
    "PathFilename": "spot_A.json",
    "Requirements": [
        "SessionMinutes % 40 < 20"
    ]
},
{
    "PathFilename": "spot_B.json",
    "Requirements": [
        "SessionMinutes % 40 >= 20"
    ]
}
],
```

For 3+ paths, divide the cycle into ranges. For example, a 60-minute cycle with three 20-minute segments:

- Path A: `"SessionMinutes % 60 < 20"`
- Path B: `"SessionMinutes % 60 >= 20 && SessionMinutes % 60 < 40"`
- Path C: `"SessionMinutes % 60 >= 40"`

### KeyActions

It's a container type for `Sequence` of [KeyAction](#keyaction).

### KeyAction

Each `KeyAction` has its own properties to describe what the action is all about.

Can specify conditions with [Requirement(s)](requirements.md#requirement) in order to create a matching action for the situation.

| Property Name | Description | Default value |
| --- | --- | --- |
| `"Name"` | Name of the KeyAction. **Naming convention**: Lowercase names indicate macros (e.g., `"stopattack"`, `"petattack"`), while capitalized names indicate spells (e.g., `"Frostbolt"`, `"Healing Wave"`). This affects how `ActionBarPopulator` and action bar validation handle the entry. | `""` |
| `"Key"` | [ConsoleKey](https://learn.microsoft.com/en-us/dotnet/api/system.consolekey) to press. Supports modifier prefixes: `Shift-`, `Ctrl-`, `Alt-` (e.g., `"Shift-1"`, `"Alt-F1"`) | `""` |
| `"Modifier"` | Modifier key to hold while pressing the key. Values: `"None"`, `"Shift"`, `"Ctrl"`, `"Alt"`. Alternative to using prefix in `Key`. **Note**: Combined modifiers (e.g., `Shift-Alt`) are not supported - only single modifiers. | `"None"` |
| `"Cost"` | [Adhoc Goals](goals.md#adhoc-goals) or [NPC Goal](goals.md#npc-goals) only, priority | `18` |
| `"PathFilename"` | [NPC Goal](goals.md#npc-goals) only, this is a short path to get close to the NPC to avoid walls etc. | `""` |
| `"HasCastBar"` | **Auto-detected** from the spell's effective cast time (`GetSpellInfo`), so it reflects talents like 5/5 Improved Corruption that flip a cast to instant. JSON setting is accepted but ignored. | _derived_ |
| `"InCombat"` | Should combat matter when attempt to cast?<br>Accepted values:<br>* `"any value for doesn't matter"`<br>* `"true"`<br>* `"false"` | `false` |
| `"Item"` | Like on use Trinket, `Food`, `Drink`.<br>The following spells counts as Item, `Throw`, `Auto Shot`, `Shoot` | `false` |
| `"PressDuration"` | How many minimum milliseconds to hold the key press down | `50` |
| `"Form"` | Shapeshift/Stance form to be in to cast this spell<br>If set, affects `WhenUsable` | `Form.None` |
| `"Cooldown"` | **Note this is not the in-game cooldown!**<br>The time in milliseconds before KeyAction can be used again.<br>This property will be updated when the backend registers the `Key` press. It has no feedback from the game. | `400` |
| `"Charge"` | How many consequent key press should happen before setting Cooldown | `1` |
| `"School"` | Indicate what type of [SchoolMask](#npcschoolimmunity) element the spell will do.  | `None` |
| `"MacroText"` | You can specify a macro text or macro template which can hold variables. make sure the MacroText is no longer then 255 characters. | `""` |
| `"BaseAction"` | Bypasses CastingHandler guard rails (GCD waiting, spell queue checks, cast verification). Use for actions that execute instantly without cast bars or cooldowns. See the `BaseActionKeys` table under [Understanding Class Configuration](#understanding-class-configuration). | `false` |
| --- | --- | --- |
| `"WhenUsable"` | Mapped to [IsUsableAction](https://wowwiki-archive.fandom.com/wiki/API_IsUsableAction) | `false` |
| `"UseWhenTargetIsCasting"` | Checks for the target casting/channeling.<br>Accepted values:<br>* `null` -> ignore<br>* `false` -> when enemy not casting<br>* `true` -> when enemy casting | `null` |
| `"Requirement"` | Single [Requirement](requirements.md#requirement) | `false` |
| `"Requirements"` | List of [Requirement](requirements.md#requirement) | `false` |
| `"Interrupt"` | Single [Requirement](requirements.md#requirement) | `false` |
| `"Interrupts"` | List of [Requirement](requirements.md#requirement) | `false` |
| `"CancelOnInterrupt"` | If the [Interrupt](interrupt-requirements.md#interrupt-requirement) [Requirement](requirements.md#requirement) has met, shall **Cancel** current castbar spellcast (sending ESC) | `false` |
| `"ResetOnNewTarget"` | Reset the Cooldown if the target changes | `false` |
| `"Log"` | Related events should appear in the logs | `true` |
| `"UseMount"` | Should use mount ? <br/>Limited to [AdhocNpcGoals](goals.md#npc-goals) such as `Repair`, `Sell`, `Vendor` routes. | `false` |
| `"TrainAll"` | Buy every spell the trainer offers and can be afforded, instead of only the ones whitelisted via `SPELL_` variables. <br/>Limited to [`ClassTrainer`](goals.md#class-trainer) NPC entries. **Do not pair with the `HasTrainableSpell` requirement** - see [Class Trainer](goals.md#class-trainer). | `false` |
| --- | Before keypress cast, ... | --- |
| `"BeforeCastFaceTarget"` | Attempt to look directly at target.<br>**Note**: it may not work for every scenario. | `false` |
| `"BeforeCastDelay"` | Delay in milliseconds. | `0` |
| `"BeforeCastMaxDelay"` | Max delay in milliseconds.<br>If set then using random delay between [`BeforeCastDelay`..`BeforeCastMaxDelay`] | `0` |
| `"BeforeCastStop"` | Stop moving. | `false` |
| `"BeforeCastDismount"` | Should dismount. [Adhoc Goals](goals.md#adhoc-goals) only. | `true` |
| --- | After Successful cast, ... | --- |
| `"AfterCastWaitSwing"` | Wait for next melee swing to land.<br>Blocks **CastingHandler**. | `false` |
| `"AfterCastWaitCastbar"` | Wait for the castbar to finish, `SpellQueueTimeMs` excluded.<br>Blocks **CastingHandler**. | `false` |
| `"AfterCastWaitBuff"` | Wait for Aura=__(player-target debuff/buff)__ count changes.<br>Only works properly, when the Aura **count** changes.<br>Not suitable for refreshing already existing Aura<br>Blocks **CastingHandler**. | `false` |
| `"AfterCastAuraExpected"` | Refreshing Aura=__(player-target debuff/buff)__<br>Just adds an extra(`SpellQueueTimeMs`) Cooldown to the action, so it wont repeat itself.<br>Not blocking  **CastingHandler**. | `false` |
| `"AfterCastWaitBag"` | Wait for any inventory, bag change.<br>Blocks **CastingHandler**. | `false` |
| `"AfterCastWaitCombat"` | Wait for player entering combat.<br>Blocks **CastingHandler**. | `false` |
| `"AfterCastWaitMeleeRange"` | Wait for interrupted either:<br>* target enters melee range<br>* target starts casting<br>* player receives damage<br>Blocks **CastingHandler**. | `false` |
| `"AfterCastStepBack"` | Start backpedaling for milliseconds.<br>If value set to `-1` attempts to use the whole remaining GCD duration.<br>Blocks **CastingHandler**. | `0` |
| `"AfterCastWaitGCD"` | the Global cooldown fully expire.<br>Blocks **CastingHandler**. | `false` |
| `"AfterCastDelay"` | delay in milliseconds.<br>Blocks **CastingHandler**. | `0` |
| `"AfterCastMaxDelay"` | delay in milliseconds.<br>If set then using random delay between [`AfterCastDelay`..`AfterCastMaxDelay`]<br>Blocks **CastingHandler**. | `0` |
| --- | --- | --- |

Some of these properties are optional and not required to be specified.

However you can create complex conditions and branches to suit the situation.

Important, the `AfterCast` prefixed conditions are ordered as shown in the table above.

#### Understanding KeyAction Properties

**Why use these properties?**

| Property Group | Purpose |
| --- | --- |
| `HasCastBar` | _Auto-detected_ from the spell's effective cast time via `GetSpellInfo` — the bot waits for the cast bar without you needing to declare it in JSON. Talent-aware: a spell that becomes instant via talents (e.g., 5/5 Improved Corruption) is automatically routed through the instant path. The JSON field is accepted for back-compat but ignored. |
| `WhenUsable` | Only attempts to cast when the game reports the ability is usable (enough mana/rage/energy, not on cooldown). Prevents wasting key presses. |
| `Cooldown` | Prevents the bot from spamming the same ability. This is the bot's internal cooldown, not the game's. Set to match GCD (~400ms) for most abilities. |
| `Form` | For Druids/Warriors - ensures you're in the correct shapeshift/stance before casting. Prevents "Can only use in Cat Form" errors. |
| `School` | Used with [`NpcSchoolImmunity`](#npcschoolimmunity) to skip spells against immune targets (e.g., don't cast Fire spells on fire-immune mobs). |
| `BaseAction` | Marks the action as a basic system action (jump, interact, target, move) that should execute immediately. Bypasses GCD waiting, spell queue checks, and cast verification — the bot won't wait to confirm success. Use for non-spell key presses that are expected to work 99.99% of the time. |
| `BeforeCastStop` | Stops movement before casting. Essential for spells with cast bars that can't be cast while moving. |
| `BeforeCastFaceTarget` | Turns to face the target. Useful for casters who might be kiting and need to turn around to cast. |
| `AfterCastWaitSwing` | For abilities that reset swing timer (e.g., Heroic Strike). Waits for the swing to land before continuing. |
| `AfterCastWaitBuff` | Waits until a buff/debuff appears. Useful for DoTs - ensures the DoT landed before moving to next ability. |
| `AfterCastStepBack` | Creates distance after casting. Useful for kiting - cast, step back, repeat. |
| [`Requirement(s)`](requirements.md#requirement) | Conditions that must be true to use this ability. Core of the combat rotation logic. |
| [`Interrupt(s)`](interrupt-requirements.md#interrupt-requirement) | Conditions that will interrupt a channeled spell or cancel casting. Useful for react to incoming damage. |

e.g. - bare minimum for a spell which has castbar. `HasCastBar` is auto-detected from the spell's cast time, so you no longer need to set it explicitly. The JSON field is accepted for back-compat:
```json
{
    "Name": "Frostbolt",
    "Key": "1",
}
```

e.g. - bare minimum for a spell which is instant (no castbar)
```json
{
    "Name": "Earth Shock",
    "Key": "6"
}
```

e.g. - using modifier keys (two equivalent ways)
```json
// Option 1: Modifier prefix in Key
{
    "Name": "Corruption",
    "Key": "Shift-1"
}

// Option 2: Explicit Modifier property
{
    "Name": "Corruption",
    "Key": "D1",
    "Modifier": "Shift"
}
```

e.g. for Rogue ability
```json
{
    "Name": "Slice and Dice",
    "Key": "3",
    "Requirements": [
        "!Slice and Dice",
        "Combo Point > 1"
    ]
}
```

There are a few specially named [KeyAction](#keyaction) such as `Food` and `Drink` which is reserved for eating and drinking.

They already have some pre baked [Requirement(s)](requirements.md#requirement) conditions in order to avoid mistype the definition.

The bare minimum for `Food` and `Drink` is looks something like this.
```json
{
    "Name": "Food",
    "Key": "-",
    "Requirement": "Health% < 50"
},
{
    "Name": "Drink",
    "Key": "=",
    "Requirement": "Mana% < 50"
}
```

When any of these [KeyAction(s)](#keyaction) are detected, by default, it going to be awaited with a predefined [Wait Goal](goals.md#wait-goals) logic.

Should see something like this, you can override any of the following values.

```json
"Wait": {
    "AutoGenerateWaitForFoodAndDrink": true,    // should generate 'Eating' and 'Drinking' KeyActions
    "FoodDrinkCost": 5,                         // can override the Cost of awaiting Eating and Drinking
    "Sequence": [
    {
        "Name": "Eating",
        "Cost": 5,                              // FoodDrinkCost
        "Requirement": "Food && Health% < 99"
    },
    {
        "Name": "Drinking",
        "Cost": 5,                              // FoodDrinkCost
        "Requirement": "Drink && Mana% < 99"
    },
    ]
},
```
---

### Casting Handler

**CastingHandler** is a component responsible for handling player spell casting,
let it be using an item from the inventory, casting an instant spell or casting a spell which has castbar.

From Addon version **1.6.0** it has been significantly changed to the point where it no longer blocks the execution until the castbar fully finishes, but rather gives back the control to the parent Goal such as [Adhoc Goals](goals.md#adhoc-goals) or [Pull Goal](goals.md#pull-goal) or [Combat Goal](goals.md#combat-goal) to give more time to find the best suitable action for the given moment.

As a result in order to execute the [Pull Goal](goals.md#pull-goal) sequence in respect, have to combine its [KeyAction(s)](#keyaction) with `AfterCast` prefixed conditions.

---
