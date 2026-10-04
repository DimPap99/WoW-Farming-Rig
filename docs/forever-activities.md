# Forever activities and the selector

How the WoW: Forever controller decides what to do on each frame. It borrows
upstream's goal picker (the cheapest goal that can run wins, re-checked every
frame) and keeps an explicit state machine inside each part. Code lives in
`Core/Forever/` (`ForeverFacts`, `ForeverSelector`, `Activities/`) and
`Core/Goals/ForeverAutonomousGoal.cs`. The tasks the run works through are in
[forever-tasks.md](forever-tasks.md).

## The loop

`ForeverAutonomousGoal` is the single goal upstream's `GoapAgent` runs on
Forever. Its own state is `Start`, `Running`, `Complete` or `Failed`:

- **Start** waits for the read-only preflight (`TryReadOnlyPreflight`): a
  verified class on a verified map, out of combat, enough health and mana,
  the add-on's telemetry, the client settings looting needs, the profile's
  actions on their slots, the navigator's checks, and the agenda's start-up
  check. It fails after 5 seconds of refusals.
- **Running**, on every fresh frame, in this order:
  1. The frame must be a complete one from a verified setup; 5 seconds
     without one fails the run. A dead player fails the run.
  2. Where every unit in view stands is noted (`ForeverCorpses`), and the
     health bar is read for damage (`ForeverDanger`).
  3. A kill is verified before anything else acts (`TryVerifyKill`).
  4. The profile's Adhoc heals and buffs run, except while looting out of
     combat or with a corpse selected out of combat.
  5. The facts are built, the selector picks the activity, the navigator is
     told what the activity's current step allows, and the activity runs.
- **Complete** once the agenda is done; **Failed** with a reason otherwise.

## Facts

`ForeverFacts` is built once per frame; every activity reads the same facts.

| Fact | Meaning |
|---|---|
| `Now` | the frame's time, a `Stopwatch` timestamp |
| `InCombat` | the player is in combat |
| `InDanger` | in combat and losing health or with an attacker in view (`ForeverDanger`) |
| `TakingDamage` | health fell within the last 3 seconds |
| `Engaged` | a unit is chosen to click or locked to fight |
| `BodiesPending` | kills not yet looted or given up |
| `CanOpenFight` | enough to open a fight out of combat: 70% health and, for mana users, 60% mana |
| `LastKillAt` | when the last kill was verified, or 0 |

`Describe()` gives the short line the selector logs.

## The selector

`ForeverSelector(logger, params IForeverActivity[])` sorts the activities by
`Cost`. `Pick(facts)` returns the cheapest one whose `Wants(facts)` is true and
changes nothing; `Switch(bars, facts)` switches to it, calling the old one's
`Exit` first (its state hooks release held keys and the mouse) and the new
one's `Enter`, and logs:

```
Forever activity: Task -> Engage (engaged)
```

## The activities

| Activity | Cost | Wants to run when | Steps and their bounds |
|---|---|---|---|
| `ForeverEngageActivity` | 4 | a unit is chosen to click or locked to fight | `Select` 8 s, `Approach` 20 s (snag hop), `Fight` 75 s |
| `ForeverLootActivity` | 4.5 | `Loot` is on, nothing fights the player, and bodies are pending or a round is under way | `Round` 120 s (snag hop) |
| `ForeverThreatActivity` | 4.6 | in combat | `Watch` 45 s |
| `ForeverRestActivity` | 5 | out of combat, and a rest is due or the vitals are below what a pull needs | `Rest` (30 s for a dead target to clear, 45 s to recover) |
| `ForeverTaskActivity` | 20 | always | the agenda's current task (see [forever-tasks.md](forever-tasks.md)) |

The costs keep the precedence explicit:

- A unit engaged comes before everything.
- In danger the loot round does not want to run, so Threat goes first and the
  bodies wait.
- While combat only lingers, the bodies are looted.
- An attack during a rest is combat, where Threat takes over.
- The task runs only when nothing else wants to.

### Engage

The fight with one unit, from the click on its plate to its death.

- `Offer(plate)`: a nameplate to click; the click begins at once and the
  fight follows in `Select`. Used by Threat and by tasks that pull.
- `Adopt(bars, identity)`: the selected unit already fights the player and is
  in reach; the fight starts in `Fight` without a click.
- `TryVerifyKill(bars)`: the host's kill check. A verified kill raises
  `Killed(bars)`, sets `LastKillAt` and ends the engagement.
- `Exclusions`: units set aside after a setback (1 or 2 minutes), which
  Threat and the tasks pass over.

A setback with the unit (it could not be clicked, its marker stayed out of
sight, the way was blocked, the character got stuck, it stayed out of range)
sets the unit aside and ends the engagement; the selector then moves on. In
combat only a failed click may be recovered, and more than 5 setbacks without
a kill stop the run (`ForeverRecovery`).

### Threat

In combat with nothing engaged. It adopts the selected unit if it fights in
reach, else offers an attacking plate. While combat only lingers it stands
still: 3 seconds after a kill, and for as long as nothing hits the player. It
turns round to look for an attacker out of view only while taking damage.

### Loot

A round goes from body to body (`IForeverLootRoutine`, `ForeverLoot`) with the
client's Interact Key and Auto Loot. `Collected` counts the bodies looted;
`RoundEnded` fires when a round finishes or runs out of time, and makes a rest
due.

### Rest

`Pending` is set by a kill out of combat and by the end of a loot round. While
pending, the dead target is cleared and health and mana come back to 90% and
60% before the next pull. Without a rest due, it only waits until the vitals
are enough to pull.

### Task

Runs the agenda's current task: the `ForeverTasks` API, route files and the
bounded trials all end up here. See [forever-tasks.md](forever-tasks.md).

## What a step allows

Each state declares what it allows besides its work (`ForeverAllowed`), and
shared routines such as walking do only what the current step allows:

| Allowance | Declared on |
|---|---|
| `SnagHop` (hop when held movement stalls) | Engage's `Approach`, Loot's `Round`, walking in tasks |
| `IdleHops` (now and then one to three hops) | walking in tasks: hunting here, travelling, roaming |

Everything else allows nothing.

## Adding an activity

1. Write a class implementing `IForeverActivity` with its own
   `ForeverStateMachine` (states, exits, time bounds, release hooks and
   allowances declared once in the constructor).
2. Choose its cost against the table above and write `Wants` from the facts.
   If it needs a fact that is not there, add it to `ForeverFacts.From`.
3. `Exit` must release whatever the activity holds, and be safe to call at
   any time: the selector may switch away on any frame.
4. Register it in `ForeverGoalFactory` and add it to the selector in
   `ForeverAutonomousGoal`'s constructor.
5. Add logic checks: `Utilities/ForeverVisualReader/LogicTest.Phases.cs` has
   the selector's precedence checks to extend.

## Status and logs

While running, `Status` is the current activity and step, such as
`Engage/Fight`, `Threat`, `Loot`, `Rest` or `Task/Roam the area around ...`.
Each activity's state machine names itself in its log lines ("Forever engage
stopped in Approach: ..."). The trial runner prints every status change.

Offline checks: `dotnet run --project Utilities/ForeverVisualReader -- --logic-test`.
