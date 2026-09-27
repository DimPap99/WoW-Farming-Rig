# Modes

The mode controls how the bot behaves - whether it navigates autonomously, requires your input, or follows another player. Choose based on your playstyle and what you want to automate.

The mode is set in the root of the class file.

e.g. Rogue.json
```json
{
    "PathFilename": "Herb_EPL.json",
    "Mode": "AttendedGather",
    "GatherFindKeys":  ["1","2"]
}
```

## Understanding Modes

| Mode | Best For | You Control | Bot Controls |
| --- | --- | --- | --- |
| `"Grind"` | AFK farming | Nothing | Everything (movement, targeting, combat, looting) |
| `"AttendedGrind"` | Semi-AFK with oversight | Movement & targeting | Combat rotation |
| `"CorpseRun"` | Instance farming recovery | Nothing | Corpse navigation only |
| `"AttendedGather"` | Herb/Mining routes | Node interaction | Route following, alerting |
| `"AssistFocus"` | Dungeon runs, multiboxing | Focus target selection | Following, combat |

## Mode Details

| Mode | Description |
| --- | --- |
| `"Grind"` | **Fully autonomous farming.** Default mode.<br>[Follows the Route](goals.md#follow-route-goal). Attempts to find Non-Blacklist targets to kill.<br>(*if enabled*) Attempts to `"Loot"` and GatherCorpse nearby corpses.<br>(*if exists*) Executes `"Adhoc"`, `"NPC"`, `"Parallel"`, `"Pull"`, `"Combat"`, `"Wait"` Sequences |
| `"AttendedGrind"` | **You steer, bot fights.** Similar to `"Grind"` but navigation ([Follow Route Goal](goals.md#follow-route-goal)) disabled.<br>Use this when you want to manually choose where to go and what to fight, but let the bot handle combat. Good for testing combat rotations or farming specific mobs. |
| `"CorpseRun"` | **Automatic corpse recovery.** Relies on navigation. [Follows the Route](goals.md#follow-route-goal)<br>Runs back to the corpse after dying.<br>Useful if you are farming an instance and die - the bot will run you back some or all of the way to the instance entrance. |
| `"AttendedGather"` | **Semi-automated gathering.** Must `Start Bot` under `Gather` tab and stay at `Gather` tab.<br>[Follows the Route](goals.md#follow-route-goal) and scans the minimap for yellow nodes indicating herb or mining nodes.<br>Once nodes are detected, navigation stops and alerts you with beeping sound!<br>**You** click the herb/mine. The `LogComponent` shows prompts to proceed.<br>**Important**: `Falling` and `Jumping` are treated the same - if you lose ground contact, the bot takes over control. Be patient.<br>(*if exists*) Executes `"Adhoc"`, `"NPC"`, `"Parallel"`, `"Pull"`, `"Combat"`, `"Wait"` Sequences |
| `"AssistFocus"` | **Follow and assist another player.** Navigation ([Follow Route Goal](goals.md#follow-route-goal)) disabled.<br>Requires a friendly `focus` target. Follows the focus target around.<br>When friendly `focustarget` within 11 yards, attempts to Interact with it.<br>When hostile `focustarget` in combat exists, assists by attacking it.<br>After combat, (*if enabled*) attempts to Loot and GatherCorpse nearby corpses.<br>Works inside instances - great for dungeon runs or multiboxing.<br>(*if exists*) Executes `"Adhoc"`, `"Parallel"`, `"Pull"`, `"AssistFocus"`, `"Combat"`, `"Wait"` Sequences.<br>**Note**: Vanilla doesn't have `focus`, so `party1` is used instead. Party is **required**. |
