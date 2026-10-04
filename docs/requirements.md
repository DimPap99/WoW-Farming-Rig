# Requirement

Requirements are the brain of your combat rotation. They define **when** each ability should be used.

**Why are Requirements important?**
- Without requirements, the bot would spam abilities randomly
- Requirements let you create intelligent rotations that react to combat situations
- You can prioritize abilities based on health, mana, buffs, debuffs, and more

A requirement is something that must be evaluated to be `true` for the [KeyAction](class-profiles.md#keyaction) to run.

Not all [KeyAction](class-profiles.md#keyaction) requires requirement(s), some rely on
* `Cooldown` - populated manually (you set how often to use)
* `ActionBarCooldownReader` - populated automatically (reads actual game cooldown)
* `ActionBarCostReader` - populated automatically (checks if you have enough mana/rage/energy)

**Common Requirement Patterns:**

| Pattern | Example | Use Case |
| --- | --- | --- |
| Use on cooldown | No requirement, rely on Cooldown | Main damage abilities |
| Use when buff missing | `"!Battle Shout"` | Self-buffs |
| Use at low health | `"Health% < 30"` | Emergency heals, healthstones |
| Use at high resource | `"Rage > 50"` | Resource dump abilities |
| Use when target debuff missing | `"!Sunder Armor"` | Maintain debuffs on target |
| Use to finish target | `"TargetHealth% < 20"` | Execute-style abilities |

Can specify `Requirements` for complex condition.

e.g.
```json
{
    "Name": "Execute",                                            //<--- Has no Requirement
    "Key": "7",
    "WhenUsable": true
},
{
    "Name": "Soul Shard",
    "Key": "9",
    "HasCastBar": true,
    "Requirements": ["TargetHealth% < 36", "!BagItem:6265:3"]     //<--- Requirement List
},
{
    "Name": "Curse of Weakness",
    "Key": "6",
    "WhenUsable": true,
    "Requirement": "!Curse of Weakness"                           //<--- Single Requirement
}
```

### **Negate a requirement**

Every requirement can be negated by adding one of the `Negate keyword` in front of the requirement.

Formula: `[Negate keyword][requirement]`

| Negate keyword |
| --- |
| `"!"` |

e.g.
```json
"Requirement": "!BagItem:Item_Soul_Shard:3"
```
---

### **And / Or multiple Requirements**

Two or more Requirement can be merged into a single Requirement object.

By default every Requirement is concatenated with `[and]` operator which means in order to execute the [KeyAction](class-profiles.md#keyaction), every member in the `RequirementsObject` must be evaluated to `true`. However this construct allows concatenation with `[or]`. Nesting parenthesis are also supported.

Formula: `[Requirement1] [Operator] [RequirementN]`

| Operator | Description |
| --- | --- |
| "&&" | And |
| "\|\|" | Or |

e.g.
```json
"Requirements": ["Has Pet", "TargetHealth% < 70 || TargetCastingSpell"]
"Requirement": "!Form:Druid_Bear && Health% < 50 || MobCount > 2",
"Requirement": "(Judgement of the Crusader && CD_Judgement <= GCD && TargetHealth% > 20) || TargetCastingSpell"
```
---
### **Value base requirements**

Value base requirement is the most basic way to create a condition.

Formula: `[Keyword] [Operator] [Numeric integer value]`

**Note:** `[Numeric integer value]` always on the _right-hand_ side expression value

| Operator | Description |
| --- | --- |
| `==` | Equals |
| `!=` | Not Equals |
| `<=` | Less then or Equals |
| `>=` | Greater then or Equals |
| `<` | Less then |
| `>` | Greater then |

Arithmetic operators can be used to build complex expressions:

| Operator | Description |
| --- | --- |
| `+` | Addition |
| `-` | Subtraction |
| `*` | Multiplication |
| `/` | Division |
| `%` | Modulo (returns remainder). For divisibility check use `Deaths % 2 == 0` |

| Keyword | Description |
| --- | --- |
| `Health%` | Player health in percentage |
| `TargetHealth%` | Target health in percentage |
| `FocusHealth%` | Focus health in percentage |
| `PetHealth%` | Pet health in percentage |
| `Mana%` | Player mana in percentage |
| `Mana` | Player current mana |
| `Rage` | Player current rage |
| `Energy` | Player current energy |
| `Focus` | Player current focus (Hunter from Mists of Pandaria on) |
| `RunicPower` | Player current runic power |
| `BloodRune` | Player current blood runes |
| `FrostRune` | Player current frost runes |
| `UnholyRune` | Player current unholy runes |
| `TotalRune` | Player current runes (blood+frost+unholy+death) |
| `Combo Point` | Player current combo points on the target |
| `Holy Power` | Player current Holy Power points on the target |
| `Durability%` | Player worn equipment average durability across **all 18** slots. Dominated by armour, since 15 of the 18 slots are armour. **0-99** value range. |
| `MainHandDurability%` | Main hand weapon durability. **0-99** value range. |
| `OffHandDurability%` | Off hand weapon or shield durability. **0-99** value range. |
| `RangedDurability%` | Ranged weapon (bow / gun / crossbow / wand) durability. **0-99** value range. |
| `WeaponDurability%` | Lowest of the three weapon slots above - the "is any weapon about to break" check. **0-99** value range. |
| `BagCount` | How many items in the player inventory |
| `FoodCount` | Returns the highest amount of food type, item count |
| `DrinkCount` | Returns the highest amount of drink type, item count |
| `MobCount` | How many detected, alive, and currently fighting mob around the player |
| `MinRange` | Minimum distance(yard) between the player and the target  |
| `MaxRange` | Maximum distance(yard) between the player and the target |
| `LastAutoShotMs` | Time since last detected AutoShot happened in milliseconds |
| `LastMainHandMs` | Time since last detected Main Hand Melee swing happened in milliseconds |
| `SinceDamageTakenMs` | Time in milliseconds since player health last decreased (damage taken) |
| `MainHandSpeed` | Returns the player Main hand attack speed in milliseconds |
| `MainHandSwing` | Returns the player predicted next main hand swing time |
| `RangedSpeed` | Returns the player ranged weapon attack speed in milliseconds |
| `RangedSwing` | Returns the player predicted next ranged weapon swing time |
| `SpellQueueWindow` | Returns SpellQueueWindow C_Var |
| `-SpellQueueWindow` | Returns SpellQueueWindow C_Var negative value. |
| `BowReload` | Returns the default 500ms time plus the player latency.  |
| `CD` | Returns the context [KeyAction](class-profiles.md#keyaction) **in-game** cooldown in milliseconds |
| `CD_{KeyAction.Name}` | Returns the given `{KeyAction.Name}` **in-game** cooldown in milliseconds |
| `Cost_{KeyAction.Name}` | Returns the given `{KeyAction.Name}` cost value |
| --- | --- |
| `Buff_{IntVariable_Name}` | Returns the given `{IntVariable_Name}` remaining **player buff** up time in milliseconds |
| `Debuff_{IntVariable_Name}` | Returns the given `{IntVariable_Name}` remaining **player debuff** up time in milliseconds |
| --- | --- |
| `TBuff_{IntVariable_Name}` | Returns the given `{IntVariable_Name}` remaining **target debuff** up time in milliseconds |
| `TDebuff_{IntVariable_Name}` | Returns the given `{IntVariable_Name}` remaining **target debuff** up time in milliseconds |
| --- | --- |
| `FBuff_{IntVariable_Name}` | Returns the given `{IntVariable_Name}` remaining **focus buff** up time in milliseconds |
| --- | --- |
| `CurGCD` | Returns the player current remaining GCD time |
| `GCD` | Alias for `1500` value |
| `Kills` | In the current session how many mobs have been killed by the player. |
| `Deaths` | In the current session how many times the player have died. |
| `Level` | Returns with the player current level. |
| `SessionSeconds` | Returns with the elapsed time in Seconds since the Session started.<br>The Session starts when the `Start Bot` button is pressed! |
| `SessionMinutes` | Returns with the elapsed time in Minutes since the Session started.<br>The Session starts when the `Start Bot` button is pressed! |
| `SessionHours` | Returns with the elapsed time in Hours since the Session started.<br>The Session starts when the `Start Bot` button is pressed! |
| `ExpPerc` | Returns with the player experience as percentage to hit next level. |
| `SecondsSinceVendored` | Seconds since the last successful vendor/repair visit. A very large number until the first one, so "it has been a while" reads true from the start. **Only grows** - see the warning under [Class Trainer](goals.md#class-trainer) before using it as an upper bound on a goal that has to travel. |
| `SecondsSinceTrained` | Seconds since the last successful [class trainer](goals.md#class-trainer) visit, same convention. Safe as a cooldown (`> N`). |
| `SpellsTrained` | How many spells have been learnt from a trainer this session. Intended for reporting - as a gate it only expresses "once per bot run". |
| `Money` | Returns the player's purse in **copper** - the unit every price in the game is quoted in. e.g. `Money > 5000` is "more than 50 silver". |
| `Gold` | Returns the player's purse in whole **gold**, the copper and silver remainder dropped. e.g. `Gold >= 10`. |
| `UIMapId` | Returns the player's current [UIMapId](../Json/dbc/som/WorldMapArea.json) |
| `PathDist` | Returns the context [PathSettings](class-profiles.md#pathsettings) of closest distance (in yards) from the player location to the Path. |
| `PathDist_{PathSettings.Id}` | Returns the closest distance (in yards) from the player location to the Path. |

The durability keywords all report **0-99**, where **0** means broken and **99** means fully repaired.

**99 is also what an empty slot reports**, along with an equipped item that has no durability at all - a thrown weapon, an off hand tome, a Wrath relic. That is deliberate: it keeps `WeaponDurability%` (the lowest of the three weapon slots) meaningful for any spec. A caster with an empty off hand and no ranged weapon gets `WeaponDurability% == MainHandDurability%`, a dual wielder gets the worse of the two, a hunter gets the worst of all three - and a slot that cannot break never drags the value down.

Weapons wear out several times faster than armour, so the all-slot `Durability%` average is late to notice a weapon about to break. Prefer `WeaponDurability%` for a repair gate, on its own or next to `Durability%`:

```json
"Requirement": "Durability% < 35 || WeaponDurability% < 30"
```

For the `MinRange` and `MaxRange` gives an approximation range distance between the player and target.

**Note:** _Every class has its own unique way to find these values by using different in game items/spells/interact._

| MinRange | MaxRange | alias Description |
| --- | --- | --- |
| 0 | 2 | "InCloseMeleeRange" |
| 0 | 5 | "InMeleeRange" |
| 5 | 15 | "IsInDeadZoneRange" |

Its worth mention that `CD_{KeyAction.Name}` is a dynamic value.<br>Each [KeyAction](class-profiles.md#keyaction) has its own `in-game Cooldown` which is not the same as `KeyAction.Cooldown`!

e.g. Single Requirement
```json
"Requirement": "Health%>70"
"Requirement": "TargetHealth%<=10"
"Requirement": "PetHealth% < 10"
"Requirement": "Mana% <= 40"
"Requirement": "Mana < 420"
"Requirement": "Energy >= 40"
"Requirement": "Rage > 90"
"Requirement": "BagCount > 80"
"Requirement": "MobCount > 1"
"Requirement": "WeaponDurability% < 30"      // The most worn of main hand / off hand / ranged is below 30%
"Requirement": "MainHandDurability% < 25"    // Only the main hand weapon
"Requirement": "MinRange < 5"
"Requirement": "MinRange > 15"
"Requirement": "MaxRange > 20"
"Requirement": "MaxRange > 35"
"Requirement": "LastAutoShotMs <= 500"
"Requirement": "LastMainHandMs <= 500"
"Requirement": "SinceDamageTakenMs < 5000"
"Requirement": "CD_Judgement < GCD"                 // The remaining cooldown on Judgement is less then GCD(1500)
"Requirement": "CD_Hammer of Justice > CD_Judgement" // The remaining cooldown on Hammer of Justice is greater then 8 seconds
"Requirement": "Rage >= Cost_Heroic Strike"          // Create a condition like if player current rage is greater then or equal the cost of Heroic Strike
"Requirement": "MainHandSpeed > 3500"   // Main hand attack speed is greater then 3.5 seconds
"Requirement": "MainHandSwing > -400"   // 400 milliseconds before next predicted main swing happen
"Requirement": "MainHandSwing > -400"   // 400 milliseconds before next predicted main swing happen
"Requirement": "Dead && Deaths % 2"           // Player is currently dead and died for the second time in the current session
"Requirement": "Deaths % 2 == 1"              // Player died odd number of times (explicit comparison)
"Requirement": "Energy - Cost_Sinister_Strike >= 0"  // Enough energy remaining after cast
```

e.g. List of Requirements
```json
"Requirements": [
    "TargetHealth% > DOT_MIN_HEALTH%",  // where DOT_MIN_HEALTH% is a user defined variable
    "!Immolate"
],
```

#### Complex Expressions

Both sides of a comparison can be full arithmetic expressions mixing variables, constants, and operators. Parentheses control evaluation order, and standard math precedence applies (`*`/`/`/`%` bind tighter than `+`/`-`).

e.g.
```json
"Requirement": "(Health% + Mana%) / 2 > 50"             // Average of health and mana above 50%
"Requirement": "Energy - Cost_Sinister_Strike >= 0"      // Enough energy after spell cost
"Requirement": "MainHandSwing > -SpellQueueWindow"       // Swing timer vs queue window
"Requirement": "SessionMinutes % 40 < 20"                // First 20 min of every 40 min cycle
"Requirement": "Kills % 5 == 0 && SessionMinutes > 10"   // Every 5th kill after 10 minutes
```

e.g. for `CD`: It's a good idea to put `CD` in healing spells to take consideration of the spell interruption.
```json
{
    "Name": "Flash of Light",
    "Key": "6",
    "HasCastBar": true,
    "WhenUsable": true,
    "Requirements": ["Health% < 60", "TargetHealth% > 20", "CD == 0", "MobCount < 2", "LastMainHandMs <= 1000"],
    "Cooldown": 6000
},
```

e.g. for `CD_{KeyAction.Name}`: Where `Hammer of Justice` referencing the `Judgement` **in-game** Cooldown to do an awesome combo!
```json
{
    "Name": "Judgement",
    "Key": "1",
    "WhenUsable": true,
    "Requirements": ["Seal of the Crusader", "!Judgement of the Crusader"]
},
{
    "Name": "Hammer of Justice",
    "Key": "7",
    "WhenUsable": true,
    "Requirements": ["Judgement of the Crusader && CD_Judgement <= GCD && TargetHealth% > 20 || TargetCastingSpell"]
}
```
---
### **npcID requirements**

If a particular npc is required then this requirement can be used.

Formula: `npcID:[intVariableKey/Numeric integer value]`

When the variable is an array (e.g. `[6195, 6196, 6197]`), returns true if the target matches ANY NPC ID in the array.

e.g.

* `"Requirement": "!npcID:6195"` - target is not [6195](https://tbc.wowhead.com/npc=6195)
* `"Requirement": "npcID:6195"` - target is [6195](https://tbc.wowhead.com/npc=6195)
* `"Requirement": "npcID:MyAwesomeIntVariable"` - single or array IntVariable

---
### **Bag requirements**

If an `itemid` must be in your bag with given `count` quantity then can use this requirement.

Useful to determine when to create warlock Healthstone or soul shards.

Formula: `BagItem:[intVariableKey/itemid]:[count/IntVariablesKey]`

When the variable is an array (e.g. `[5512, 5511, 5509]`), the item counts across all IDs are summed and compared to the required count. This is useful for items with multiple ranks like Healthstones.

e.g.

* `"Requirement": "BagItem:5175"` - Must have a [Earth Totem](https://tbc.wowhead.com/item=5175) in bag
* `"Requirement": "BagItem:Item_Soul_Shard:3"` - Must have at least [3x Soulshard](https://tbc.wowhead.com/item=6265) in bag
* `"Requirement": "!BagItem:19007:1"` - Must not have a [Lesser Healthstone](https://tbc.wowhead.com/item=19007) in bag
* `"Requirement": "!BagItem:6265:3"` - Must not have [3x Soulshard](https://tbc.wowhead.com/item=6265) in bag
* `"Requirement": "!BagItem:MyAwesomeIntVariable:69"`
* `"Requirement": "BagItem:ITEM_HEALTHSTONE_ALL:1"` - Using array IntVariable, true if ANY item in the array is in bag

---
### **Form requirements**

If the player must be in the specified `form` use this requirement.

Useful to determine when to switch Form for the given situation.

Formula: `Form:[form]`

| form |
| --- |
| None
| Druid_Bear |
| Druid_Aquatic |
| Druid_Cat |
| Druid_Travel |
| Druid_Moonkin |
| Druid_Flight |
| Druid_Cat_Prowl |
| Priest_Shadowform |
| Rogue_Stealth |
| Rogue_Vanish |
| Shaman_GhostWolf |
| Warrior_BattleStance |
| Warrior_DefensiveStance |
| Warrior_BerserkerStance |
| Paladin_Devotion_Aura |
| Paladin_Retribution_Aura |
| Paladin_Concentration_Aura |
| Paladin_Shadow_Resistance_Aura |
| Paladin_Frost_Resistance_Aura |
| Paladin_Fire_Resistance_Aura |
| Paladin_Sanctity_Aura |
| Paladin_Crusader_Aura |
| DeathKnight_Blood_Presence |
| DeathKnight_Frost_Presence |
| DeathKnight_Unholy_Presence |

e.g.
```json
"Requirement": "Form:Druid_Bear"    // Must be in `Druid_Bear` form
"Requirement": "!Form:Druid_Cat"    // Shouldn't be in `Druid_Cat` form
```

---
### **Race requirements**

If the character must be the specified `race` use this requirement.

Useful to determine Racial abilities.

Formula: `Race:[race]`

| race |
| --- |
| None |
| Human |
| Orc |
| Dwarf |
| NightElf |
| Undead |
| Tauren |
| Gnome |
| Troll |
| Goblin |
| BloodElf |
| Draenei |

e.g.
```json
"Requirement": "Race:Orc"          // Must be `Orc` race
"Requirement": "!Race:Human"    // Shouldn't be `Human` race
```

---
### **Target requirements**

If the current target must be a specific creature type use this requirement. Uses the DBC creature database to look up the target's type by NPC ID.

Useful for abilities that only work on certain creature types (e.g. Paladin's Exorcism on Undead/Demon).

Formula: `Target:[type]`

| type |
| --- |
| Beast |
| Dragonkin |
| Demon |
| Elemental |
| Giant |
| Undead |
| Humanoid |
| Critter |
| Mechanical |
| NotSpecified |
| Totem |
| NonCombatPet |
| GasCloud |

e.g.
```json
"Requirement": "Target:Undead"                          // Target must be Undead
"Requirement": "Target:Demon"                             // Target must be Demon
"Requirement": "!Target:Humanoid"                         // Target must not be Humanoid
"Requirement": "Target:Undead || Target:Demon"            // Target is Undead or Demon
```

---
### **MouseOver requirements**

If the current mouseover must be a specific creature type use this requirement. Uses the DBC creature database to look up the mouseover's type by NPC ID.

Formula: `MouseOver:[type]`

| type |
| --- |
| Beast |
| Dragonkin |
| Demon |
| Elemental |
| Giant |
| Undead |
| Humanoid |
| Critter |
| Mechanical |
| NotSpecified |
| Totem |
| NonCombatPet |
| GasCloud |

e.g.
```json
"Requirement": "MouseOver:Undead"                          // MouseOver must be Undead
"Requirement": "MouseOver:Demon"                           // MouseOver must be Demon
"Requirement": "!MouseOver:Humanoid"                       // MouseOver must not be Humanoid
"Requirement": "MouseOver:Undead || MouseOver:Demon"       // MouseOver is Undead or Demon
```

---
### **Equipment requirements**

Check if the player has an item equipped in the specified `slot`. Optionally check for a specific `itemId`.

Useful to determine weapon availability for combat rotations (e.g., dual-wield classes, ranged pulls).

Formula: `Equipment:[slot]` or `Equipment:[slot]:[itemId]`

| slot |
| --- |
| Ammo |
| Head |
| Neck |
| Shoulder |
| Shirt |
| Chest |
| Waist |
| Legs |
| Feet |
| Wrists |
| Hands |
| Finger_1 |
| Finger_2 |
| Trinket_1 |
| Trinket_2 |
| Back |
| Mainhand |
| Offhand |
| Ranged |
| Tabard |

e.g.
```json
"Requirement": "Equipment:Mainhand"                        // Must have main hand weapon equipped
"Requirement": "!Equipment:Offhand"                        // Should not have off hand equipped
"Requirement": "Equipment:Mainhand && Equipment:Offhand"   // Must be dual-wielding
"Requirement": "Equipment:Mainhand:4513"                   // Must have item 4513 equipped in main hand
```

---
### **RangedWeapon requirements**

Check what **kind** of weapon occupies the ranged slot. `Equipment:Ranged` only says
something is there; this says whether it is a bow, a gun, a crossbow, a wand or a thrown
weapon.

The motivating case is ammunition: a bow and a crossbow consume arrows, a gun consumes
bullets, and a wand consumes neither. Without this a restocking entry has to hardcode one
ammo item and is wrong for the other half of the weapons.

Formula: `RangedWeapon:[subclass]`

| subclass |
| --- |
| Bow |
| Gun |
| Crossbow |
| Thrown |
| Wand |

Any other [`ItemWeaponSubclass`](../SharedLib/Data/ItemWeaponSubclass.cs) name is accepted
too. An unknown name throws at profile load rather than silently never matching.

e.g.
```json
"Requirement": "RangedWeapon:Gun"                          // Gun equipped - needs bullets
"Requirement": "RangedWeapon:Bow || RangedWeapon:Crossbow" // Either - needs arrows
"Requirement": "!RangedWeapon:Wand"                        // Anything but a wand
```

---
### **Spell requirements**

If a given Spell `name` or `id` must be known by the player then you can use this requirement.

Useful to determine when the given `Spell` is exists in the spellbook.

It has the following formulas:

* `Spell:[name]`. The `name` only works with the English client name.
* `Spell:[id]`
* `Spell:[IntVariablesKey]` - Resolves to a single spell ID or an array of IDs. When the variable is an array (e.g. `[100, 6178, 11578]`), returns true if ANY spell in the array is known.

e.g.

* `"Requirement": "Spell:687"` - Must have know [`id=687`](https://tbc.wowhead.com/item=687)
* `"Requirement": "Spell:Demon Skin"` - Must have known the given `name`
* `"Requirement": "!Spell:702"` - Must not have known the given [`id=702`](https://tbc.wowhead.com/item=702)
* `"Requirement": "!Spell:Curse of Weakness"` - Must not have known the given `name`
* `"Requirement": "Spell:SPELL_CHARGE"` - Using IntVariable (single or array)

---
### **Talent requirements**

If a given Talent `name` must be known by the player then you can use this requirement.

Useful to determine when the given Talent is learned. Also can specify how many points have to be spent minimum with `rank` which can be constant or a variable in [`IntVariables`](class-profiles.md#intvariables)

Formula: `Talent:[name]:[rank/IntVariablesKey]`. The `name` only works with the English client name.

e.g.

```json
"Requirement": "Talent:Improved Corruption"    // Must known the given `name`
"Requirement": "Talent:Improved Corruption:5"  // Must know the given `name` and at least with `rank`
"Requirement": "!Talent:Suppression"        // Must have not know the given `name`
```
---
### **Player Buff remaining time requirements**

First in the [`IntVariables`](class-profiles.md#intvariables) have to mention the buff icon id such as `Buff_{your fancy name}: {icon_id}`.

It is important, the addon keeps track of the **icon_id**! Not **spell_id**

e.g.
```json
"IntVariables": {
    "Buff_Horn of Winter": 134228,
    "Item_Healthstone_All": [19005, 19004, 5512, 19006, 19007, 5511, 19009, 5509, 19008, 5510, 19011, 19010, 19012, 9421, 19013],
},
```

Then in [KeyAction](class-profiles.md#keyaction) you can use the following requirement:

e.g.
```json
{
    "Cost": 3.1,
    "Name": "Horn of Winter",
    "Key": "F4",
    "InCombat": "i dont care",
    "WhenUsable": true,
    "Requirements": [
        "Buff_Horn of Winter < 5000", // The remaining time is less then 5 seconds
        "!Mounted"
    ],
    "AfterCastWaitBuff": true
}
```
---
### **Player Debuff remaining time requirements**

First in the [`IntVariables`](class-profiles.md#intvariables) have to mention the debuff icon id such as `Debuff_{your fancy name}: {icon_id}`.

It is important, the addon keeps track of the **icon_id**! Not **spell_id**

A single icon id:
```json
"IntVariables": {
    "Debuff_Poison": 135368
},
```

An **array of icon ids** — the variable returns the max remaining time across all listed icons, so the requirement is true when **any** of them is active:
```json
"IntVariables": {
    "Debuff_POISON": [136006, 136007, 136016, 136064, 136067, 136077, 136093, 134437, 132273, 132274, 132105],
    "Debuff_DISEASE": [136127, 136134, 134324, 135914]
},
```

Then in [KeyAction](class-profiles.md#keyaction) you can use the following requirement:

e.g.
```json
{
    "Name": "Stoneform",
    "Key": "F11",
    "Requirements": [
        "Debuff_POISON > 1 || Debuff_DISEASE > 1"
    ]
}
```
---
### **Target Debuff remaining time requirements**

First in the [`IntVariables`](class-profiles.md#intvariables) have to mention the debuff icon id such as `TDebuff_{your fancy name}: {icon_id}`

It is important, the addon keeps track of the **icon_id**! Not **spell_id**

e.g.
```json
"IntVariables": {
    "TDebuff_Blood Plague": 237514,
    "TDebuff_Frost Fever": 237522
},
```

Then in [KeyAction](class-profiles.md#keyaction) you can use the following requirement:

e.g.
```json
{
    "Name": "Pestilenc",
    "Key": "F6",
    "WhenUsable": true,
    "Requirements": [
        "Frost Fever && Blood Plague && (TDebuff_Frost Fever < 2000 || TDebuff_Blood Plague < 2000)",   // Frost Fever and Blood Plague is up
        "InMeleeRange"                                                                                // and their duration less then 2 seconds
    ]
}
```
---
### **Target Buff remaining time requirements**

First in the [IntVariables](class-profiles.md#intvariables) have to mention the buff icon id such as `TBuff_{your fancy name}: {icon_id}`

It is important, the addon keeps track of the **icon_id**! Not **spell_id**

e.g.
```json
"IntVariables": {
    "TBuff_Battle Shout": 132333,
},
```
---
### **Focus Buff remaining time requirements**

First in the [IntVariables](class-profiles.md#intvariables) have to mention the buff icon id such as `FBuff_{your fancy name}: {icon_id}`

It is important, the addon keeps track of the **icon_id**! Not **spell_id**

e.g.
```json
"IntVariables": {
    "FBuff_Battle Shout": 132333,
    "FBuff_Mount": 132239
},
```
---
### **CanRun requirements**

Formula: `CanRun:_KeyAction_Name_`

Where the `_KeyAction_Name_` is one of the [KeyAction.Name](class-profiles.md#keyaction).

It is suitable for [Interrupt Requirement](interrupt-requirements.md#interrupt-requirement), for such scenario:
* The player already casting a `Castbar` based spell which has a long cast time like 2-3 seconds
* However meanwhile a higher priority action can be used, which would be beneficial.
* Like: A mob is in low hp, so an instant cast spell such as `Earth Shock`, `Fireblast` would execute the enemy.

e.g.
```json
"Requirement": "CanRun:Frost Shock"
"Requirement": "CanRun:Fire Blast"
"Requirement": "!CanRun:Heartstone"
```

---
### **Trigger requirements**

If you feel the current Requirement toolset is not enough for you, you can use other addons such as **WeakAura's** Trigger to control a given bit.

1. **The regarding in-game Addon API is looks like this**

    ```lua
    YOUR_CHOOSEN_ADDON_NAME_FROM_ADDON_CONFIG:Set(bit, value)
    -- bit: 0-23
    -- value: 0 or 1 or true or false

    --e.g. by using default addon name
    DataToColor:Set(0,1)
    DataToColor:Set(0,0)
    DataToColor:Set(23,true)
    DataToColor:Set(23,false)
    ```

    e.g. you can make your awesome WeakAura. Then you can put the following Addon API in the

    WeakAura -> Actions -> On Show -> Custom
    ```lua
    DataToColor:Set(0,1) -- enable Trigger 0
    ```

    WeakAura -> Actions -> On Hide -> Custom
    ```lua
    DataToColor:Set(0,0)  -- disable Trigger 0
    ```

    Example Weakaura for hunter pet in 10y range for feed pet
    ```lua
    !WA:2!1rvWUTTrq0O6dPGOiXXT1afTaLWT1ibTiW2fnfOO9GOmvScKTuiPJtApqUK7qXnMC3f7Uu2khf6HCwFc6CpXpH8fqi0Va)j4VGUlPQHBrIoioZUZS78EZ82o93Qyl8w43(DcwPkNqbXOtdgo4exXLJstLGQtfMi55OzbWLQpALmdHzx8Q29(Q7uHOjzmXygHQI75EsGRh3(wjeMYefivipurkG1ED4BMukvScteNYXif4btbQ6kuPEvKIKCgbRYC6QDfOefHrLpXtQqc1UlWSW2SJIB)Y)SdrkuaRhlNj(fFq9W9(H9FKdHsuBxF)S4uTLmB367hvV57l29E0CLGmzciK3BxXAZ3(1ZjkO4eub05kzzCt9nwaPKl98h7oC41LsiSaKs0eiyghIENzH)nNi(dmUtanHss8ZyxmIgT6)4ELS5tpglxZO05M4l11CKJ5)d4GYtGOtGD2FVQMJMovxHqwtGzikoAHKd55nLOJsxc12x1KriJdcKM625x)TLyiUmn1uHIJChoU)Pdx0Gftc8pF8WUVY1l0Z9PUNeE4a)UodDpCL5gsB59bhgDd891he5YQWED9dc9d66f0e5nhxuScLRTTCm9zRARkpt5y3ldsoVHEIHm0uctKTsaOC)Bk)MZ5g0enVXCawATWSrdOIQUfHN5r1XTEBLEe58OQB1l4B27OUbHh7)0WZoAGUD5TOKUUXAXFGbztHGw)Jzy4VUd)lFVdTTgEMzx816rCqqr5Vq3g0mZFSqc5PTt(oJccgDSg2ufFZ(cYBSFEjcR7bi7GGLA(ZdMygITCYzi8lAk7KCKugvV32EfL5kILJg0jBxpWYRzNDJLe6KCi(OtnQk96osYBataZn3JV25lwlhFzRCCJLIMRXqboknqwMWOysJ8XI)nFyzjxajedM2yLwbQ1ZJ4TjjMT(raXR1sns6m94r)GLkwY0ws4JhV9oem)BZknKJTEO1MqT3FVz2nnnB99yNca2SZbLeCL354H)0lF4Zrf)EvQq3e9vgAAJRBFiPVzjt9h73ZZ19eVeJq9zBO)fRbtkzI18lyc8zceF(zRn4F)hgA4z6jfssOktaAbxoEwvlN18cWZ60PZgl1d1aU5fN)8tQi02dqdoRfikP18j13RF9UougfEhGKMQgOtuz3DfUu0erOrbO1Ngkxo3eJbg1eNceHQZTMu)67wFEDEDH28t))RSL07hF8p)4d2A6F)Y)5
    ```

1. **Then outside of the game, you can specify the following in the [Class Configuration](class-profiles.md#class-configuration) file ex. `class/Hunter_62.json`**

    Formula: `Trigger:[bit]:[name]`

    Where the `bit` is 0-23 and the `name` is a free text which shows up in the frontend.

    e.g.
    ```json
    "Requirement": "Trigger:0:Pet in range"           // Trigger 0 must be true
    "Requirement": "Trigger:23:My Awesome trigger"    // Trigger 23 must be true
    "Requirement": "!Trigger:6:Suppression"        // Trigger 6 must be false
    ```
---
### **Buff / Debuff / General boolean condition requirements**

Allow requirements about what buffs/debuffs you have or the target has or in general some boolean based requirements.

| General boolean condition | Description |
| --- | --- |
| `"TargetYieldXP"` | The target yields experience upon death. (Grey Level) |
| `"TargetsMe"` | The target currently targets the player |
| `"TargetsPet"` | The target currently targets the player's pet |
| `"TargetsNone"` | The target currently has not target |
| `"TargetElite"` | The target is Elite [unit classification](https://wowpedia.fandom.com/wiki/API_UnitClassification) |
| `"SoftTarget"` | The player has an available soft target |
| `"SoftTargetDead"` | The player has an available soft target which is dead |
| `"AddVisible"` | Around the target there are possible additional NPCs |
| `"InCombat"` | Player in combat. |
| `"TargetCastingSpell"` | Target casts any spell |
| `"Swimming"` | The player is currently swimming. |
| `"Falling"` | The player is currently falling down, not touching the ground. |
| `"Flying"` | The player is currently flying, not touching the ground. |
| `"MenuOpen"` | Returns true if the Game Menu window is open (ESC) |
| `"ChatInputVisible"` | Returns true if the Chat inputbox is open (ENTER) |
| `"AnyBagOpen"` | Returns true if any bag frame is visible on screen |
| `"CharacterFrameOpen"` | Returns true if the Character Info window is open (C) |
| `"SpellBookFrameOpen"` | Returns true if the Spellbook window is open (P) |
| `"FriendsFrameOpen"` | Returns true if the Social/Friends window is open (O) |
| `"Dead"` | The player is currently dead. |
| `"CannibalizeCorpse"` | A Humanoid or Undead corpse is within 5 yards of the player. |
| `"DamageTakenFromTotem"` | The player has taken damage from a Totem creature type. Useful to detect nearby totems. |
| `"Has Pet"` | The player's pet is alive |
| `"Pet HasTarget"` | Players pet has target |
| `"Pet Happy"` | Pet happiness is green |
| `"PetCanPull"` | The pet is alive and at least one of its spells is castable right now. False once the pet runs out of power - an Imp with no mana for Firebolt stands still instead of closing to melee, so a pull handed to the pet never happens. |
| `"Mounted"` | Player riding on a mount (druid form excluded) |
| `"BagFull"` | Inventory is full |
| `"BagGreyItem"` | Indicates that there are at least one Grey Quality level item. |
| `"Items Broken"` | Has any broken(red) worn item |
| `"HasRangedWeapon"` | Has equipped ranged weapon (wand/crossbow/bow/gun). To tell *which*, see [RangedWeapon requirements](#rangedweapon-requirements). |
| `"HasAmmo"` | AmmoSlot has equipped ammo and count is greater than zero |
| `"HasTrainableSpell"` | There is at least one spell on the [class trainer whitelist](goals.md#class-trainer) the player does not know yet and is high enough level to learn. Also false while the spellbook is still loading. Note the bot applies two further checks of its own that this variable does not expose - see the [Class Trainer](goals.md#class-trainer) notes. |
| `"Casting"` | The player is currently casting any spell. |
| `"HasTarget"` | The player currently has a target. |
| `"TargetAlive"` | The player currently has an alive target. |
| `"TargetHostile"` | The player currently target is hostile. |
| `"InMeleeRange"` | Target is approximately 0-5 yard range |
| `"InCloseMeleeRange"` | Target is approximately 0-2 yard range |
| `"InDeadZoneRange"` | Target is approximately 5-11 yard range |
| `"InCombatRange"` | Class based - Have any ability which allows you to attack target from current place |
| `"OutOfCombatRange"` | Negated value of "InCombatRange" |
| `"AutoAttacking"` | Auto spell `Auto Attack` is active |
| `"Shooting"` | (wand) Auto spell `Shoot` is active |
| `"AutoShot"` | (hunter) Auto spell `Auto Shot` is active |
| `"HasMainHandEnchant"` | Indicates that main hand weapon has active poison/sharpening stone/shaman buff effect |
| `"HasOffHandEnchant"` | Indicates that off hand weapon has active poison/sharpening stone/shaman buff effect |
| `"PathEnd_{PathSettings.Id}"` | Returns true when:<br/>* Player has not yet started walking along the path.<br/>* or reached the destination of the path.<br/>**Note**: path can be reversed based on the [PathSettings.PathThereAndBack](class-profiles.md#pathsettings)! |
| `"PathEnd_Any"` | Same as the above, However returns true when any of is true. |

<table>
<tr><th>Buffs</th><th>Debuffs</th></tr>
<tr valign="top"><td>

| Class | Buff Condition |
| --- | --- |
| All | `"Well Fed"` |
| All | `"Eating"` |
| All | `"Drinking"` |
| All | `"Mana Regeneration"` |
| All | `"Clearcasting"` |
| Druid | `"Mark of the Wild"` |
| Druid | `"Thorns"` |
| Druid | `"Tiger's Fury"` |
| Druid | `"Prowl"` |
| Druid | `"Rejuvenation"` |
| Druid | `"Regrowth"` |
| Druid | `"Omen of Clarity"` |
| Mage | `"Frost Armor"` |
| Mage | `"Ice Armor"` |
| Mage | `"Molten Armor"` |
| Mage | `"Mage Armor"` |
| Mage | `"Arcane Intellect"` |
| Mage | `"Ice Barrier"` |
| Mage | `"Ward"` |
| Mage | `"Fire Power"` |
| Mage | `"Mana Shield"` |
| Mage | `"Presence of Mind"` |
| Mage | `"Arcane Power"` |
| Paladin | `"Seal of Righteousness"` |
| Paladin | `"Seal of the Crusader"` |
| Paladin | `"Seal of Command"` |
| Paladin | `"Seal of Wisdom"` |
| Paladin | `"Seal of Light"` |
| Paladin | `"Seal of Blood"` |
| Paladin | `"Seal of Vengeance"` |
| Paladin | `"Blessing of Might"` |
| Paladin | `"Blessing of Protection"` |
| Paladin | `"Blessing of Wisdom"` |
| Paladin | `"Blessing of Kings"` |
| Paladin | `"Blessing of Salvation"` |
| Paladin | `"Blessing of Sanctuary"` |
| Paladin | `"Blessing of Light"` |
| Paladin | `"Righteous Fury"` |
| Paladin | `"Divine Protection"` |
| Paladin | `"Avenging Wrath"` |
| Paladin | `"Holy Shield"` |
| Paladin | `"Divine Shield"` |
| Priest | `"Fortitude"` |
| Priest | `"Inner Fire"` |
| Priest | `"Divine Spirit"` |
| Priest | `"Renew"` |
| Priest | `"Shield"` |
| Priest | `"Spirit"` |
| Priest | `"Inner Focus"` |
| Priest | `"Abolish Disease"` |
| Priest | `"Power Infusion"` |
| Priest | `"Prayer of Shadow Protection"` |
| Priest | `"Shadow Protection"` |
| Rogue | `"Slice And Dice"` |
| Rogue | `"Stealth"` |
| Warlock | `"Demon Armor"` |
| Warlock | `"Demon Skin"` |
| Warlock | `"Shadow Trance"` |
| Warlock | `"Soulstone Resurrection"` |
| Warlock | `"Soul Link"` |
| Warlock | `"Fel Armor"` |
| Warlock | `"Fel Domination"` |
| Warlock | `"Demonic Sacrifice"` |
| Warlock | `"Sacrifice"` |
| Warrior | `"Battle Shout"` |
| Warrior | `"Bloodrage"` |
| Shaman | `"Lightning Shield"` |
| Shaman | `"Water Shield"` |
| Shaman | `"Shamanistic Focus"` |
| Shaman | `"Focused"` |
| Shaman | `"Stoneskin"` |
| Shaman | `"Elemental Mastery"` |
| Shaman | `"Stormstrike"` |
| Shaman | `"Nature's Swiftness"` |
| Hunter | `"Aspect of the Cheetah"` |
| Hunter | `"Aspect of the Pack"` |
| Hunter | `"Aspect of the Hawk"` |
| Hunter | `"Aspect of the Monkey"` |
| Hunter | `"Aspect of the Viper"` |
| Hunter | `"Aspect of the Dragonhawk"` |
| Hunter | `"Lock and Load"` |
| Hunter | `"Rapid Fire"` |
| Hunter | `"Quick Shots"` |
| Hunter | `"Trueshot Aura"` |
| Death Knight | `"Blood Tap"` |
| Death Knight | `"Horn of Winter"` |
| Death Knight | `"Icebound Fortitude"` |
| Death Knight | `"Path of Frost"` |
| Death Knight | `"Anti-Magic Shell"` |
| Death Knight | `"Army of the Dead"` |
| Death Knight | `"Vampiric Blood"` |
| Death Knight | `"Dancing Rune Weapon"` |
| Death Knight | `"Unbreakable Armor"` |
| Death Knight | `"Bone Shield"` |
| Death Knight | `"Summon Gargoyle"` |
| Death Knight | `"Freezing Fog"` |

</td>
<td valign="top">

| Class | Debuff Condition |
| --- | --- |
| Druid | `"Demoralizing Roar"` |
| Druid | `"Faerie Fire"` |
| Druid | `"Rip"` |
| Druid | `"Moonfire"` |
| Druid | `"Entangling Roots"` |
| Druid | `"Rake"` |
| Paladin | `"Judgement of the Crusader"` |
| Paladin | `"Hammer of Justice"` |
| Paladin | `"Judgement of Wisdom"` |
| Paladin | `"Judgement of Light"` |
| Paladin | `"Judgement of Justice"` |
| Paladin | `"Judgement of Any"` |
| Mage | `"Frostbite"` |
| Mage | `"Slow"` |
| Priest | `"Shadow Word: Pain"` |
| Priest | `"Holy Fire"` |
| Priest | `"Vampiric Embrace"` |
| Priest | `"Silence"` |
| Priest | `"Shackle Undead"` |
| Warlock | `"Curse of"` |
| Warlock | `"Curse of Weakness"` |
| Warlock | `"Curse of Elements"` |
| Warlock | `"Curse of Recklessness"` |
| Warlock | `"Curse of Shadow"` |
| Warlock | `"Curse of Agony"` |
| Warlock | `"Siphon Life"` |
| Warlock | `"Corruption"` |
| Warlock | `"Immolate"` |
| Warrior | `"Rend"` |
| Warrior | `"Thunder Clap"` |
| Warrior | `"Hamstring"` |
| Warrior | `"Charge Stun"` |
| Hunter | `"Serpent Sting"` |
| Hunter | `"Hunter's Mark"` |
| Hunter | `"Viper Sting"` |
| Hunter | `"Explosive Shot"` |
| Hunter | `"Black Arrow"` |
| Death Knight | `"Blood Plague"` |
| Death Knight | `"Frost Fever"` |
| Death Knight | `"Strangulate"` |
| Death Knight | `"Chains of Ice"` |

</td></tr> </table>

e.g.
```json
"Requirement": "!Well Fed"         // I am not well fed.
"Requirement": "!Thorns"           // I don't have the thorns buff.
"Requirement": "AutoAttacking"     // "Auto Attack" spell is active.
"Requirement": "Shooting"          // "Shoot" spell is active.
"Requirement": "Items Broken"      // Worn armor is broken (red).
"Requirement": "BagFull"           // Inventory is full.
"Requirement": "HasRangedWeapon"   // Has an item equipped at the ranged slot.
"Requirement": "InMeleeRange"      // Determines if the target is in melee range (0-5 yard)
"Requirement": "!F_Thorns"         // Focus don't have the thorns buff.
"Requirement": "F_Rejuvenation"    // Focus Has Rejuvenation buff active.
```

---
### **SpellInRange requirements**

Allow requirements about spell range to be used, the spell in question depends upon the class being played.

`"SpellInRange:0"` or `"!SpellInRange:0"` for a Warrior is Charge and for a Mage is Fireball.

This might be useful if you were close enough for a Fireball, but not for a Frostbolt.

Formula: `SpellInRange:[Numeric integer value/IntVariablesKey]`

| Class | Spell | id |
| --- | --- | --- |
| Rogue | Sinister Strike | 0 |
| Rogue | Throw | 1 |
| Rogue | Shoot Gun | 2 |
| Druid | Wrath | 0 |
| Druid | Bash | 1 |
| Druid | Rip | 2 |
| Druid | Maul | 3 |
| Druid | Healing Touch | 4 |
| Druid | Mark of the Wild | 5 |
| Druid | Regrowth | 6 |
| Druid | Rejuvenation | 7 |
| Druid | Thorns | 8 |
| Warrior | Charge | 0 |
| Warrior | Rend | 1 |
| Warrior | Shoot Gun | 2 |
| Warrior | Throw | 3 |
| Priest | Shadow Word: Pain | 0 |
| Priest | Shoot | 1 |
| Priest | Mind Flay | 2 |
| Priest | Mind Blast | 3 |
| Priest | Smite | 4 |
| Priest | Divine Spirit | 5 |
| Priest | Power Word: Fortitude | 6 |
| Priest | Power Word: Shield | 7 |
| Priest | Lesser Heal | 8 |
| Priest | Prayer of Mending | 9 |
| Priest | Renew | 10 |
| Priest | Shadow Protection | 11 |
| Paladin | Judgement | 0 |
| Paladin | Exorcism | 1 |
| Paladin | Flash Heal | 2 |
| Paladin | Holy Light | 3 |
| Paladin | Blessing of * | 4 |
| Paladin | Greater Blessing of * | 5 |
| Mage | Fireball | 0 |
| Mage | Shoot| 1 |
| Mage | Pyroblast | 2 |
| Mage | Frostbolt | 3 |
| Mage | Fire Blast | 4 |
| Hunter | Raptor Strike | 0 |
| Hunter | Auto Shot | 1 |
| Hunter | Serpent Sting | 2 |
| Hunter | Feed Pet | 3 |
| Warlock | Shadow Bolt | 0 |
| Warlock | Shoot | 1 |
| Warlock | Health Funnel | 2 |
| Shaman | Lightning Bolt | 0 |
| Shaman | Earth Shock | 1 |
| Shaman | Healing Wave | 2 |
| Shaman | Lesser Healing Wave | 3 |
| Shaman | Water Breathing | 4 |
| Shaman | Chain Heal | 5 |
| Shaman | Earth Shield | 6 |
| Death Knight | Icy Touch | 0 |
| Death Knight | Death Coil | 1 |
| Death Knight | Death Grip | 2 |
| Death Knight | Dark Command | 3 |
| Death Knight | Raise Dead | 4 |

Shared [CheckInteractDistance](https://wowwiki-archive.fandom.com/wiki/API_CheckInteractDistance) API

**Note:** It only works outside of combat since [16th Nov 2023](https://twitter.com/WeakAuras/status/1725238451421782093)

| Unit | id |
| --- | --- |
| focustarget Inspect | 12 |
| focustarget Trade | 13 |
| focustarget Duel | 14 |
| focus Inspect | 15 |
| focus Trade | 16 |
| focus Duel | 17 |
| pet Inspect | 18 |
| pet Trade | 19 |
| pet Duel | 20 |
| target Inspect | 21 |
| target Trade | 22 |
| target Duel | 23 |

e.g.
```json
"Requirement": "SpellInRange:4"
"Requirements": ["Health% < 80", "SpellInRange:2"]
```

---
### **Target Casting Spell requirement**

Combined with the `KeyAction.UseWhenTargetIsCasting` property, this requirement can limit on which enemy target spell your character will react or ignore.

Firstly `"TargetCastingSpell"` as it is without mentioning any spellID. Simply tells if the target is doing any cast at all.

Secondly can specify the following Format `"TargetCastingSpell:spellID1,spellID2,..."` which translates to "if Target is casting spellID1 OR spellID2 OR ...".

e.g. Rogue_20.json
```json
{
    "Name": "Kick",
    "UseWhenTargetIsCasting": true
},
{
    "Name": "Kick",
    "Requirement": "!TargetCastingSpell"
},
{
    "Name": "Kick",
    "UseWhenTargetIsCasting": true,
    "Requirement": "TargetCastingSpell:9053,11443"
},
{
    "Name": "Kick",
    "Requirement": "!TargetCastingSpell:11443"
}
```

### **WoW: Forever requirements**

The Forever client exposes much less than the Classic pixel strip, so a Forever
profile may use only requirements with a checked visual source. The profile is
refused at load, naming the requirement, if it uses anything else.

| Requirement | Meaning on Forever |
| --- | --- |
| `"Health%"`, `"Mana%"`, `"TargetHealth%"` | Percentages read from the native status bars. Compare them with numbers, e.g. `"Health% < 70"`. `Mana%` is only readable on a mana user. |
| `"Spell:<id>"` | The spell is in the spellbook. |
| `&&`, `\|\|`, `!`, parentheses | As above. |

Every action is also checked automatically for being on its main-bar slot (1 to 12),
bound to its key, usable with enough mana, and off cooldown. Forever runs only the
`Pull`, `Combat` and `Adhoc` lists:

* `Adhoc` holds self heals and buffs. `"InCombat"` decides when each runs, as above. An
  action with `"AfterCastAuraExpected": true` is a buff: it is cast only while no player
  aura shows its slot's icon.
* `Pull` holds openers. Each runs once per target, and only while every buff is up.
* `Combat` is the priority list. `"AutoAttack"` presses Start Attack once per target.

Every action is its key pressed: a profile with `"MacroText"` is refused, since a macro
would be typed into chat. A self heal such as Holy Light is its key and lands on the
player through the client's Auto Self Cast. `"Cooldown"` (milliseconds) limits how often
an action is pressed, e.g. a 15-second sting. An action with `"AfterCastWaitSwing": true`
resolves on the next melee swing, like Raptor Strike, and gets a longer wait for its
success event.

See `Json/class/Paladin_Forever_Auto.json`, `Json/class/Hunter_Forever_Auto.json` and the
[Forever port status](forever-port.md).
