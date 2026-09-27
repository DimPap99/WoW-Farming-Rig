# Goal Groups

The bot uses a Goal-based AI system (GOAP - Goal Oriented Action Planning). Each goal handles a specific situation. Understanding when each goal activates helps you configure your class profile correctly.

| Goal | When It Activates | What It Does |
| --- | --- | --- |
| **Pull** | Target acquired, not in combat | Initiates combat with ranged attacks or by running to target |
| **Combat** | In combat with a target | Main damage rotation, uses abilities based on requirements |
| **Flee** | Health too low, outnumbered | Escape combat, run away, heal up |
| **Adhoc** | Various triggers (buffs, health, etc.) | Background tasks like buffing, eating, drinking |
| **Parallel** | Runs alongside other goals | Continuous checks like health monitoring |
| **Wait** | Eating/drinking | Waits for food/drink buffs to complete |
| **NPC** | Bags full, gear broken, etc. | Travels to vendor/repair NPCs |
| **AssistFocus** | Focus target set, assist mode | Follows and assists another player |

### Pull Goal

**Why Pull Goal?** Initiating combat properly matters. Hunters want to open with an arrow, Mages with a frostbolt, Warriors with a charge. Pull Goal defines the opening move(s) used to start each fight - what you cast when you have a target but haven't started fighting yet.

This is the `Sequence` of [KeyAction(s)](class-profiles.md#keyaction) that are used when pulling a mob.

e.g.
```json
"Pull": {
    "Sequence": [
        {
            "Name": "Concussive Shot",
            "Key": "9",
            "BeforeCastStop": true,
            "Requirements": [
                "HasRangedWeapon",
                "!InMeleeRange",
                "HasAmmo"
            ]
        }
    ]
}
```

### Assist Focus Goal

**Why Assist Focus Goal?** For multiboxing or dungeon runs with a friend. The bot follows whoever you set as focus and helps them fight. Useful for running a healer that follows your main, or a DPS that assists your tank.

The `Sequence` of [KeyAction(s)](class-profiles.md#keyaction) that are used while the `AssistFocus` Mode is active.

Upon any [keyAction's](class-profiles.md#keyaction) [requirement](requirements.md#requirement) are met, the **Assist Focus Goal** is become active.

Upon entering **Assist Focus Goal**, the player attempts to target the `focus`/`party1`.

Then as defined, in-order, executes those [KeyAction(s)](class-profiles.md#keyaction) which fulfills its given [requirement(s)](requirements.md#requirement).

Upon exiting **Assist Focus Goal**, the current target will be deselected / cleared.

**Note**: You can use every [Buff](requirements.md#buff--debuff--general-boolean-condition-requirements) and [Debuff](requirements.md#buff--debuff--general-boolean-condition-requirements) names on the `focus`/`party1` without being targeted. Just have to prefix it with `F_` example: `F_Mark of the Wild` which means `focus`/`party1` has `Mark of the Wild` buff active.

e.g. of a Balance Druid
```json
"AssistFocus": {
    "Sequence": [
        {
            "Name": "Mark of the Wild",
            "Key": "4",
            "Form": "None",
            "Requirements": [
                "!Mounted",
                "!F_Mark of the Wild",
                "SpellInRange:5"
            ]
        },
        {
            "Name": "Thorns",
            "Key": "7",
            "Form": "None",
            "Requirements": [
                "!Mounted",
                "!F_Thorns",
                "SpellInRange:8"
            ]
        },
        {
            "Name": "Regrowth",
            "Key": "0",
            "HasCastBar": true,
            "WhenUsable": true,
            "AfterCastAuraExpected": true,
            "Requirements": [
                "!Mounted",
                "!F_Regrowth",
                "FocusHealth% < 65",
                "SpellInRange:6"
            ],
            "Form": "None"
        },
        {
            "Name": "Rejuvenation",
            "Key": "6",
            "BeforeCastStop": true,
            "AfterCastWaitBuff": true,
            "Requirements": [
                "!Mounted",
                "!F_Rejuvenation",
                "FocusHealth% < 75",
                "SpellInRange:7"
            ],
            "Form": "None"
        }
    ]
},
```

### Flee Goal

**Why Flee Goal?** Sometimes fights go wrong - you pull an elite, get adds, or just can't win. Flee Goal lets you define "oh no" situations and survival abilities to use while running away. The bot will retrace its steps to safety.

It's an opt-in goal.

Define custom rules for when the character should run away from an encounter that seems impossible to survive.

The goal will be executed while the player is in combat and the **first** [KeyAction](class-profiles.md#keyaction) custom [Requirement(s)](requirements.md#requirement) are met.

While the goal is active
* the player going to retrace the past locations which were deemed to be safe.

When the goal exits
* Clears the current target.

The path will be simplified to ensure straight line of movement.

To opt-in the goal execution you have to define the following the [Class Configuration](class-profiles.md#class-configuration)

```json
"Flee": {
    "Sequence": [
        {
            "Name": "Flee",
            "Requirement": "MobCount > 1 && Health% < 50"
        }
    ]
},
```

Example for a mage
```json
"Flee": {
  "Sequence": [
    {
      "Name": "Flee",
      "Requirement": "MobCount > 1" //&& Health% < 50
    },
    {
      "Name": "Frost Nova",
      "Key": 6,
      "Requirement": "InMeleeRange"
    },
    {
      "Name": "HP Potion",
      "Key": 7,
      "Requirement": "Health% < 50"
    },
    {
      "name": "Blink",
      "Key": "F3",
      "Requirement": "!InMeleeRange"
    }
  ]
},
```

Example for accidentally pulling an elite mob
```json
"Flee": {
  "Sequence": [
    {
      "Name": "Flee",
      "Requirement": "MobCount > 1 || TargetElite" //&& Health% < 50
    },
  ]
},
```

### Combat Goal

**Why Combat Goal?** This is the core of your character's fighting ability - the rotation. Combat Goal defines what abilities to use in what order during a fight. The bot continuously checks the list and uses the first ability whose requirements are met.

The `Sequence` of [KeyAction(s)](class-profiles.md#keyaction) that are used when in combat and trying to kill a mob.

The combat goal does the first available command on the list.

The goal then runs again re-evaluating the list before choosing the first available command again, and so on until the mob is dead.

e.g.
```json
"Combat": {
    "Sequence": [
        {
            "Name": "Fireball",
            "Key": "2",
            "HasCastBar": true,
            "Requirement": "TargetHealth% > 20"
        },
        {
            "Name": "AutoAttack",
            "Requirement": "!AutoAttacking"
        },
        {
            "Name": "Approach",
            "Log": false
        }
    ]
}
```

#### Backing out of melee

No dedicated action is needed for this - a plain `KeyAction` bound to the backpedal key does it. `PressDuration` holds the key, `Interrupt` is a *keep-going* condition rather than a stop one, so the press is cut short as soon as it stops holding. `BaseAction` keeps it out of the spell/cast-time machinery, and `BeforeCastFaceTarget` aims first, since backpedalling is relative to facing.

```json
{
    "Name": "Stepback",
    "Key": "S",
    "PressDuration": 3000,
    "BaseAction": true,
    "BeforeCastFaceTarget": true,
    "Requirements": [
        "Has Pet",
        "TargetsPet",
        "InMeleeRange"
    ],
    "Interrupt": "InMeleeRange && TargetAlive"
}
```

That one only fires while the pet holds the mob - backing away from something chasing you just drags it along. A mage kiting off a root uses the same shape with `"Frostbite"` and `"TargetsMe"` instead.

### Adhoc Goals

**Why Adhoc Goals?** These handle "between fight" activities - things you do when you're not actively killing something. Buffs that expired, health/mana that needs restoring, pet summoning, etc. The bot checks these when out of combat and performs them if conditions are met.

**When to use Adhoc vs Parallel:**
- **Adhoc**: For actions that need to happen sequentially (eating, buffing, etc.)
- **Parallel**: For actions that can happen simultaneously (eating AND drinking at the same time)

These `Sequence` of [KeyAction(s)](class-profiles.md#keyaction) are done when not in combat and are not on cooldown. Suitable for personal buffs.

e.g.
```json
"Adhoc": {
    "Sequence": [
        {
            "Name": "Frost Armor",
            "Key": "3",
            "Requirement": "!Frost Armor"
        },
        {
            "Name": "Food",
            "Key": "=",
            "Requirement": "Health% < 30"
        },
        {
            "Name": "Drink",
            "Key": "-",
            "Requirement": "Mana% < 30"
        }
    ]
}
```

**Note**: that the default combat requirement can be overridden.

e.g. high level [Death Knight](../Json/class/DeathKnight_70_Unholy.json)
```json
{
    "Cost": 3.1,
    "Name": "Horn of Winter",
    "Key": "F4",
    "InCombat": "i dont care",
    "WhenUsable": true,
    "Requirements": [
        "!Horn of Winter || (CD_Horn of Winter <= GCD && TargetAlive && TargetHostile)",
        "!Mounted"
    ]
},
```

e.g. high level [Warlock](../Json/class/Warlock_66_Demo_pet_pull.json)
```json
{
    "Name": "Life Tap",
    "Cost": 3,
    "Key": "N9",
    "InCombat": "i dont care",
    "Charge": 2,
    "Requirements": [
        "!Casting",
        "Health% > TAP_MIN_MANA%",
        "Mana% < TAP_MIN_MANA%"
    ]
},
```


### Parallel Goals

**Why Parallel Goals?** Some actions can happen at the same time - for example, eating food AND drinking water simultaneously. Parallel Goals press all matching keys together, saving time between fights.

These `Sequence` of [KeyAction(s)](class-profiles.md#keyaction) are done when not in combat and are not on cooldown.

The key presses happen simultaneously on all [KeyAction(s)](class-profiles.md#keyaction) which meet the [Requirement(s)](requirements.md#requirement).

Suitable for `Food` and `Drink`.

e.g.
```json
"Parallel": {
    "Sequence": [
        {
            "Name": "Food",
            "Key": "=",
            "Requirement": "Health% < 50"
        },
        {
            "Name": "Drink",
            "Key": "-",
            "Requirement": "Mana% < 50"
        }
    ]
}
```

### Wait Goals

**Why Wait Goals?** Sometimes you need to pause and wait - for health/mana to regenerate after eating, for a buff to complete its channel, or for a pet to arrive. Wait Goals keep the bot idle until conditions are met.

These actions cause the bot to wait while the [Requirement(s)](requirements.md#requirement) are met. During this time the player will be idle until a lower cost action can be executed.

e.g.
```json
"Wait": {
    "Sequence": [
    {
        "Cost": 19,
        "Name": "HP regen",
        "Requirements": [
            "FoodCount == 0 || !Usable:Food",
            "Health% < 90"
        ]
    }
    ]
},
```
---

### NPC Goals

**Why NPC Goals?** When grinding for extended periods, your bags fill up and equipment breaks. NPC Goals automate trips to vendors and repairers so you don't have to babysit the bot. When bags are full or items are broken, the bot will automatically navigate to the NPC, interact, and return to grinding.

**Choosing between modes:**
- **Manual Route**: More reliable. You record a path to the NPC. Use this for NPCs in tricky locations (inside buildings, behind obstacles).
- **Auto Route**: Easier setup but less reliable. Bot finds its own path. Only works well for NPCs in open areas without obstacles.

It has two modes:
* [Manual NPC Route](#manual-npc-route): have to specify a `"PathFilename"`
* [Auto NPC Route](#auto-npc-route): based on the `"KeyAction.Name"`, can detect the strategy. **(EXPERIMENTAL)**

#### Manual NPC Route

e.g. using a prerecoded path to follow
```json
"NPC": {
    "Sequence": [
    {
        "Name": "Repair",
        "Key": "C",
        "Requirement": "Items Broken",
        "UseMount": true, // When ClassConfig.UseMount is disabled, still allows to use mounts
        "PathFilename": "Tanaris_GadgetzanKrinkleGoodsteel.json",
        "Cost": 6
    },
    {
        "Name": "Sell",
        "Key": "C",
        "Requirement": "BagFull",
        "UseMount": true, // When ClassConfig.UseMount is disabled, still allows to use mounts
        "PathFilename": "Tanaris_GadgetzanKrinkleGoodsteel.json",
        "Cost": 6
    }
    ]
}
```

The "Key" is a key that is bound to a macro. The macro needs to target the NPC, and if necessary open up the repair or vendor page. The bot will click the key and the npc will be targetted. Then it will click the interact button which will cause the bot to move to the NPC and open the NPC options, this may be enough to get the auto repair and auto sell greys to happen. But the bot will click the button again in case there are further steps (e.g. SelectGossipOption), or you have many greys or items to sell.

e.g. Sell macro - bound to the `"C"` key using BindPad or Key bindings
```lua
/tar Jannos Ironwill
/run DataToColor:sell({"Light Leather","Cheese","Light Feather"});
```

e.g. Repair macro
```lua
/tar Vargus
/script SelectGossipOption(1)
```

e.g. Delete various items
```lua
/run c=C_Container for b=0,4 do for s=1,c.GetContainerNumSlots(b) do local n=c.GetContainerItemLink(b,s) if n and (strfind(n,"Slimy") or strfind(n,"Pelt") or strfind(n,"Mystery")) then c.PickupContainerItem(b,s) DeleteCursorItem() end end end
```

Because some NPCs are hard to reach, there is the option to add a short path to them e.g. `"Tanaris_GadgetzanKrinkleGoodsteel.json"`. The idea is that the start of the path is easy to get to and is a short distance from the NPC, you record a path from the easy to reach spot to the NPC with a distance between spots of 1. When the bot needs to vend or repair it will path to the first spot in the list, then walk closely through the rest of the spots, once they are walked it will press the defined Key, then walk back through the path.

e.g. `Tanaris_GadgetzanKrinkleGoodsteel.json` in the `Json/path` folder looks like this:
```json
[{"X":51.477,"Y":29.347},{"X":51.486,"Y":29.308},{"X":51.495,"Y":29.266},{"X":51.503,"Y":29.23},{"X":51.513,"Y":29.186},{"X":51.522,"Y":29.147},{"X":51.531,"Y":29.104},{"X":51.54,"Y":29.063},{"X":51.551,"Y":29.017},{"X":51.559,"Y":28.974},{"X":51.568,"Y":28.93},{"X":51.578,"Y":28.889},{"X":51.587,"Y":28.853},{"X":51.597,"Y":28.808}]
```
If you have an NPC that is easy to get to such as the repair NPC in Arathi Highlands then the path only needs to have one spot in it. e.g.
```json
[{"X":45.8,"Y":46.6}]
```

---

#### Auto NPC Route

This is rather an **experimental** feature, and it is known to be unstable but it provides an easy way to add npc interaction in the **current zone**.

With `CrossZoneSearch` enabled, the **current zone** restriction can be bypassed.

The key limitation is the navigation, it is known to get stuck with [Indoors](https://wowwiki-archive.fandom.com/wiki/API_IsIndoors) NPCs — beware of that!

The `"KeyAction.Name"` has a special formula which can be followed to have different behaviour!

* Formula: `[TYPE] {[npc1 | npc2 | npc3 | npcN]}`

The `[TYPE]` can be one of the following
* `Gossip`
* `QuestGiver`
* `Trainer`
* `ClassTrainer`
* `ProfessionTrainer`
* `Vendor` / `Sell`
* `VendorAmmo`
* `VendorFood`
* `VendorPoison`
* `VendorReagent`
* `Repair`
* `FlightMaster`
* `Innkeeper`
* `Banker`
* `StableMaster`

When either zero or list of npc names with `|` separated characters one of the following scenario going to happen:
* When **no** npc name is specified, the **closest** **[TYPE]** of that NPC is considered.
* When **one** npc name is specific, only that npc going to be considered.
* Finally when **one or more** npc name is specified, the **closest** will be picked!

examples of full automatic npc detection or multiple whitelisted names:
```json
"NPC": {
    "Sequence": [
        {
            "Cost": 6,
            "Name": "Repair", // the closest NPC of the Repair(type) is used, highly experimental can lead unexpected behaviours
            "Key": "C",
            "Requirement": "Durability% < 35"
        },
        {
            "Cost": 6,
            "Name": "Sell Adlin Pridedrift | Rybrad Coldbank", // only two npc are consdered of type Vendor
            "Key": "C",
            "Requirements": [
                "BagFull",
                "BagGreyItem"
            ]
        }
    ]
}
```

**Note:** `Durability%` is the average across all 18 equipped slots, so it is dominated by
armour and lags well behind a weapon that is about to break. Use
[`WeaponDurability%`](requirements.md#value-base-requirements) - the lowest of main hand, off hand and
ranged - when the repair trip is really about the weapon:
`"Requirement": "Durability% < 35 || WeaponDurability% < 30"`.

#### Class Trainer

A `ClassTrainer` entry walks to the trainer and **learns spells**, instead of opening a
merchant window.

There is no new syntax for saying which spells to learn. Every `IntVariables` key whose
name starts with `SPELL_` is the whitelist, and its array is that spell's **ranks in
ascending order** - the same arrays a profile already writes for `Spell:` requirements:

```json
"IntVariables": {
    "SPELL_CHARGE": [100, 6178, 11578],       // Charge rank 1, 2, 3
    "SPELL_REND":   [772, 6546, 6547],        // Rend rank 1, 2, 3
    "SPELL_BLOODRAGE": 2687,                  // a single rank is fine too
    "WAIT_CHARGE_MS": 4000                    // ignored, no SPELL_ prefix
},
"NPC": {
    "Sequence": [
        {
            "Cost": 5,
            "Name": "ClassTrainer",
            "Requirements": [
                "HasTrainableSpell",
                "Money > 100"
            ]
        }
    ]
}
```

##### Two modes: whitelist, or `TrainAll`

The whitelist above is the default. `"TrainAll": true` on the entry instead buys
**everything the trainer offers that you can afford**, ignoring `SPELL_` entirely:

```json
{ "Cost": 5, "Name": "ClassTrainer", "TrainAll": true, "Requirements": ["Money > 5000"] }
```

|  | whitelist *(default)* | `"TrainAll": true` |
| --- | --- | --- |
| What it buys | only ids listed in `SPELL_*` `IntVariables` | everything offered and affordable |
| Who decides | the bot, before setting off | the trainer's own list, on arrival |
| Gate to use | `HasTrainableSpell` | `Money` and/or `SecondsSinceTrained` |
| Goes quiet when | nothing whitelisted is learnable | a visit buys nothing, until you next level |
| Best for | a curated levelling profile | "just teach me everything" |

> **Do not combine `TrainAll` with `HasTrainableSpell`.** That requirement only reads the
> `SPELL_` whitelist, so both gates have to pass and the trip stays locked to whitelist
> eligibility. Once those spells are all known it turns false permanently and the trainer
> is never visited again - even though `TrainAll` would happily have bought everything
> else. Gate `TrainAll` on `Money` and a cooldown instead.

`TrainAll` cannot use `HasTrainableSpell` because what a trainer sells is only knowable
once its window is open - there is nothing to check beforehand. That is also why it needs
its own stopping rule: after a visit that buys nothing it goes quiet until your next
level-up, since levelling is the only thing that changes what is on offer.

In the log a `TrainAll` visit reads `offering 0 spell(s) to learn - everything offered`.
The zero is expected: no id list is sent, the addon does the choosing. It reports back
what it actually bought, so `Trained N spell(s)` and the action bar placement still name
real spells.

One limitation: to report a purchase the addon has to resolve the service to a spell id,
which it does through the tooltip. A service whose id will not resolve is **skipped
rather than bought**, so the bot never claims a purchase it cannot account for. If a
trainer visibly has spells but nothing is bought, that is the case to suspect.

How it decides *(whitelist mode)*:
* Only **your own class's** trainer is considered. `ClassTrainer` is a single NPC flag
  shared by every class, so the search additionally requires the NPC's subtitle to name
  your class - a Warrior goes to a "Warrior Trainer" and never to the Mage one standing
  next to it. *(Death Knight and Demon Hunter are not wired up for this yet.)*
* A spell is **trainable** when the player does not know that exact rank and is at or
  above the level the spell requires. Both come from data the bot already has - the
  in-game spellbook and `Json/dbc/<client>/spells.json`.
* At the trainer, the addon matches the whitelist against what that trainer actually
  offers, checks each price against your purse, and buys what it can afford - cheapest
  rank first, since lower ranks are prerequisites for higher ones.
* `HasTrainableSpell` turns false once there is nothing left to learn, which is what
  stops the bot walking back. **Include it in the requirements** or the entry will keep
  firing.

**Gating it further.** `HasTrainableSpell` on its own is usually enough - it is true
exactly while something is learnable, goes false once it is learnt, and becomes true
again on the level-up that unlocks the next rank. The bot also stops asking by itself
when a trainer quotes a price it cannot pay, and when no trainer of your class is
reachable in the zone.

If you want more control, prefer facts that **do not decay while the bot walks**:

```json
// only bother when there is plausibly enough coin
{ "Name": "ClassTrainer", "Requirements": ["HasTrainableSpell", "Money > 1000"] }

// at most one trainer trip every ten minutes
{ "Name": "ClassTrainer", "Requirements": ["HasTrainableSpell", "SecondsSinceTrained > 600"] }
```

> **A requirement is re-checked while the goal runs, not just when it starts.** A
> narrowing window such as `SecondsSinceVendored < 120` therefore expires *during* the
> walk to the trainer: the goal is dropped part-way and, since that number only grows,
> it never re-opens until the next vendor trip. Use a widening test
> (`SecondsSinceTrained > 600`) or a state one (`Money > 1000`) for anything the bot has
> to travel for. `SecondsSinceVendored` is fine for a check made on the spot.

Likewise `SpellsTrained == 0` means "once per bot run", which is not what a levelling
profile wants - a 1-10 profile trains repeatedly as it levels. It is there for
reporting, not for gating.

`VendoredOrRepairedRecently` is a different thing again: a flag latched by a vendor
visit and cleared only by a successful **mail** run, so without mail enabled it stays
true forever after the first sale.

Notes:
* Ids the running client does not have are skipped with a warning, so a profile shared
  between expansions does not break - e.g. `Victory Rush` does not exist in Classic Era.
* If the trainer teaches nothing on the whitelist (wrong class, or a profile listing
  spells it does not sell) that NPC is skipped from then on and the search moves to the
  next one.
* If a wanted spell is offered but unaffordable, the entry goes quiet until the wallet
  grows, rather than walking back and forth.
* The NPC search obeys [`CrossZoneSearch`](class-profiles.md#class-configuration) like every other NPC
  entry, so by default only trainers in the **current zone** are considered. If none is
  found there, the entry goes quiet until you move to another zone - otherwise it would
  keep being chosen and keep finding nothing, since `HasTrainableSpell` can stay true
  for a whole zone's worth of levels.
* **Mists of Pandaria (5.4.8) grants class spells on level up** and its trainers do not
  sell them, so a `ClassTrainer` entry has nothing to do there. It reports "teaches
  nothing" and stops rather than failing.

---

#### Safe Path Transitions

**Problem:** When bags are full or repairs are needed, the bot immediately abandons the grind path and uses pathfinding to navigate directly to the vendor path start. This direct route may pass through dangerous mob-dense areas, which is especially risky for Hardcore players.

**Solution:** Use the `PathEnd_0` requirement to ensure vendor visits only trigger when you've completed your grind path loop. This way, the bot follows your safe, pre-recorded grind path to its end before transitioning to the vendor path.

**How it works:**
1. Design your grind path so it **ends** near a safe location (close to your vendor path start)
2. Design your vendor path to **start** from that same safe location
3. Add `PathEnd_0` to your vendor requirements
4. The bot will complete the full grind loop before vendoring, avoiding dangerous shortcuts

**Example configuration:**
```json
"NPC": {
    "Sequence": [
        {
            "Name": "Sell Adlin Pridedrift | Rybrad Coldbank",
            "Key": "C",
            "PathFilename": "1_Gnome_Vendor.json",
            "Requirements": [
                "BagFull",
                "BagGreyItem",
                "PathEnd_0"
            ],
            "Cost": 6
        },
        {
            "Name": "Repair",
            "Key": "C",
            "PathFilename": "1_Gnome_Vendor.json",
            "Requirements": [
                "Items Broken",
                "PathEnd_0"
            ],
            "Cost": 6
        }
    ]
}
```

**Notes:**
- `PathEnd_0` refers to the first path (index 0) in your `PathFilename` configuration
- If you have [Multiple Paths with Requirements](class-profiles.md#multiple-paths-with-requirements), use `PathEnd_1`, `PathEnd_2`, etc. for other paths
- Use `PathEnd_Any` if you want to trigger when any of your paths reaches its end
- See [PathEnd requirement](requirements.md#requirement) for more details

---

#### NPC KeyAction.MacroText

When visiting an NPC, it is possible to specify a templated macro text, where the template variables show up as `$` prefixed variables.

If you want to use the template variable you need to first specify it [StringVariables](class-profiles.md#stringvariables).

Roughly speaking the following block does the following, if the player has less then 200

```json
"NPC": {
    "Sequence": [
        {
        "Cost": 6,
        "Name": "VendorAmmo",
        "Requirements": [
            "!BagItem:ITEM_ARROW:MIN_COUNT_ARROW"
        ],
        "MacroText": "/run local a={'$ITEM_NAME_ARROW',$AMMO_SLOT} for i=1,GetMerchantNumItems() do if GetMerchantItemInfo(i)==a[1] then for j=1,a[2] do BuyMerchantItem(i,200) end end end"
        }
    ]
}
```

the final result will be the following

```lua
/run local a={'Rough Arrow',5} for i=1,GetMerchantNumItems() do if GetMerchantItemInfo(i)==a[1] then for j=1,a[2] do BuyMerchantItem(i,200) end end end
```


### Repeatable Quests Handin

In theory if there is a repeatable quest to collect items, you could set up a NPC task as follows. See 'Bag requirements' for [Requirement(s)](requirements.md#requirement) format.
```json
{
    "Name": "Handin",
    "Key": "K",
    "Requirements": ["BagItem:12622:5","BagItem:12623:5"],
    "PathFilename": "Path_to_NPC.json",
    "Cost": 6
}
```

### Follow Route Goal

Uses the [Path](class-profiles.md#path) settings, follows the given route, uses pathfinding depending on the loaded [Class Configuration](class-profiles.md#class-configuration)s [Mode](modes.md#modes).

Basic informations
* Base cost 20.
* It can be added multiple times via the [Class Configuration.Paths](class-profiles.md#class-configuration) property array.

Meanwhile attempts to
* find a new possible non blacklisted target
* find a possible gathering node
