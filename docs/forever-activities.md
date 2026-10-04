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
  verified class, out of combat, enough health and mana, the add-on's
  telemetry, the client settings looting needs, the profile's actions on
  their slots (each takes the rank its slot holds, see below), the
  navigator's checks (any zone the map data has on a baked continent, with
  the client's map and world positions agreeing), and the agenda's start-up
  check. It fails after 5 seconds of refusals.
- **Running**, on every fresh frame, in this order:
  1. The frame must be a complete one from a verified setup. Without one
     nothing is pressed: after 5 seconds the run logs that it paused and why
     (the add-on's bars not found at the top left, found but unreadable, no
     new screen capture, or which input did not read; the trial runner also
     saves that stretch's first unreadable capture in `local/logs`), resumes
     when a frame reads, and stops after 60 seconds. A dead player fails the run.
  2. Where every unit in view stands is noted (`ForeverCorpses`), and the
     health bar is read for damage (`ForeverDanger`).
  3. A kill is verified before anything else acts (`TryVerifyKill`).
  4. The profile's Adhoc heals and buffs run, except while looting out of
     combat or with a corpse selected out of combat.
  5. The facts are built, the selector picks the activity, the navigator is
     told what the activity's current step allows, and the activity runs.
- **Complete** once the agenda is done; **Failed** only for what no fallback
  can make good (below).

## When something fails: falling back

A failure is handled by the goal above the one that failed, as a person who
cannot loot a body leaves it, cannot reach a mob picks another, and cannot
get to one place goes on to the next. The run is never left with nothing to
do. The ladder, from the smallest goal up:

| What failed | Falls back to |
|---|---|
| a press on a body | another press a step closer, 3 presses in all (Loot, below) |
| a body | the next body; full bags leave them all |
| a unit (a click, the way to it, its reach) | the unit set aside for a while, the next pull (Engage) |
| an activity (`ForeverActivityResult.Failed`) | its own `FallBack`, then it starts afresh and the selector picks again |
| a place a task walks to | another place: a point on the way is skipped, a destination walked again, a roam spot given up |
| a task | the agenda's next task (`ForeverAgenda.GiveUp`, see [forever-tasks.md](forever-tasks.md)) |
| the last task of a route that does not repeat | hunting where the run stands for 3 minutes, then that task again |

Each activity's `FallBack(reason)` says what falling back means for it:
Engage sets its unit aside for a minute out of combat (in combat it stays a
threat, and Threat takes it again); Threat watches afresh; Loot leaves the
round's bodies; Rest is over (the selector rests again while the vitals stay
low); Task gives the task up. The controller logs each one:

```
Forever Task failed: walking to ... made no progress for 20 seconds; falling back: Travel to ... is given up; next: Hunt here (1 in a row without progress)
```

Only what no fallback can make good stops the run (`ForeverAutonomousGoal.FallBack`):
the game lost focus (a click elsewhere is how the player takes over), the
client setup broke (the add-on's telemetry, the looting settings, a binding,
an action off its slot or unusable, such as a shot without ammo, a map with
no baked mesh: the same checks as the preflight, `TrySetup`; in a fight only
whether the attacks can be used, as the slots' auras do not read there), the player
died, the frame stayed unreadable for 60 seconds, or the code threw. And as a
guard, ten failures in a row with no kill, no body looted and no task done
between them (`ForeverSetbacks.MaxInARow`): whatever the run falls back to
keeps failing, and it stops with the first reason. A cast whose success could
not be proved still latches the caster closed (by design), so it now ends the
run through that guard.

**Spell ranks.** A profile names each spell by one rank's `SpellId`, and the
client puts every newly trained rank on the bar. The rotation's start-up check
(`ForeverRotation.TryPreflight`) gives an action the ID of the rank on its
slot whenever the spell data names the same spell there, and logs it once, so
the slot checks, the cast gate and the cast confirmation follow the bar after
training. Another spell on the slot is refused, naming the action.

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

The fight with one unit, from the click on its plate to its death: `Select`,
`Approach`, `Fight`, and `Sight` when the unit goes out of the player's line
of sight.

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
a kill fail the engagement (`ForeverRecovery`), which the run falls back from.

**The pet goes first.** With a living pet and `AutoPetAttack`, the client's
Pet Attack key sends it at the target, and on a pull the player's attacks
wait until the mob has turned on the pet (its plate's `TargetsPet`) and stayed
on it for `ForeverRotation.PetHoldsMobMs` (1.5 s), so the mob stays on the
pet and the player at range. Nothing waits once the mob or anything else goes
for the player or an auto attack already runs, nor past
`ForeverRotation.PetEngageMs` (8 s) for a pet that cannot draw the mob. The log
says `Forever pet holds the mob; the attacks begin`, or why the pull was not
held. A mob with another hostile unit standing near it
(`ForeverPullOptions.HostileNear`) is pulled on its own instead: the pet stays
back, the opener draws the mob to the player, and the pet goes in once the mob
fights, since the pet's run in would draw the other one too
(`Forever pulling the mob on its own: ...`).

**Ranged or melee: the stance.** `ForeverRotation.Stance` reads how the fight
stands to the target. Ranged while its own plate says it targets someone
other than the player (the pet, or no one yet) and a ranged attack can be
used: the player keeps the range, and when the mob comes too close for every
ranged attack (close, with each shot's slot out of range: inside the bow's
8-yard minimum) the fight steps back, facing it, with move-backward held
(`ForeverNavigator.StepBack`), until the shots reach again. Melee once the mob
targets the player, or with no ranged attack usable (no ammo): the shots go on
while it comes, and melee once it is there. Steps back that do not bring the
range back within 2.5 seconds (the mob follows, or the ground behind is not
walkable) give way to fighting where it stands for 4 seconds. A Paladin's
Judgement reaches a mob in melee, so a Paladin never steps back; and while the
roster cannot tell who the mob is on, the fight stays as it stands. The rule is
the user's: "when the aggro is not on u u should always prefer to stay ranged.
u go meele if u have no ammo or if the mob is on u. and if its not on u u try
to go ranged again". A target close and out of view, as one behind the
player, is turned to, never searched for on foot.

**Out of sight.** When the game refuses an attack with "out of line of
sight", or Auto Shot reports it, in combat, the fight goes to `Sight`: the
goal is to see the unit again. The game reports no position for the unit, so
`ForeverCorpses.TryLocate` places it, from the bearing of its nameplate and how
far it can be, or exactly where the minimap's tracking shows it. The player
walks along the mesh toward that, as close as the mesh comes (round or up to
whatever blocks the view), and steps aside where it comes no closer. The next
instant attack is pressed on the move, without stopping, once the player has
moved 3 yards since the last refusal or 4 seconds have passed; one that lands
proves the unit is in sight, and the fight goes on from there, standing. In
contact, at death or when the target changes, `Fight` takes over; after 20
seconds without sight it is a setback.

### Threat

In combat with nothing engaged. It adopts the selected unit if it fights in
reach, else offers an attacking plate (`ForeverTargetPolicy.TryChooseAttacker`).
An attacker is any unit that fights the player or the pet, and it is fought
whatever its level, kind or tap: the profile's level window and `TargetMask`
decide only what the run pulls, and the cast gate's level cap only what it
opens on. When the roster knows an attacker whose marker cannot be clicked
(none shows, as for one that went for the pet behind the player, or it lies
beside the clickable middle of the screen, `ForeverTargetPolicy.Clickable`),
it turns to it at once, toward where it was last seen, until its plate can
be clicked. While combat only
lingers it stands still: 3 seconds after a kill, and for as long as nothing
hits the player. It turns round to look for an attacker no plate shows only
while taking damage.

### Loot

A round goes from body to body (`IForeverLootRoutine`, `ForeverLoot`) with the
client's Interact Key and Auto Loot. `Collected` counts the bodies looted;
`RoundEnded` fires when a round finishes or runs out of time, and makes a rest
due. A body lies where its unit was last placed: along its nameplate's
bearing, as far as the plate allowed, or at one point when the minimap's
tracking showed it (the log counts those). The walk to it gets as close as the
mesh allows, within 5 yards, and the Interact Key's soft target finds the body
from there. The soft target takes only what lies in front, so at the far end
of the line, or as near it as the way goes, the round turns round once
(0.35 of full steering) looking for the body before it gives it up.

**Pressing.** A body the key finds that reads lootable (`CanLootUnit`) is in
reach with loot on it, and is pressed at once: every earlier live run looted
that way. A body of the fight that does not read lootable may be out of reach,
or empty and only to skin, which `CanLootUnit` cannot tell apart, so the key
is trusted from closer: the round steps toward it (it is in front) until it
reads lootable or for 0.6 seconds, then presses. Each press gets 3 seconds to
be answered, time enough for a Skinning cast:

- a window opens: the loot, unless a cast came between the press and it, when
  the press skinned a body with nothing to loot and the window held the hide;
- "too far away", or no answer at all: a step closer and another press, 3
  presses in all (`ForeverLoot.MaxAttempts`, logged `Forever loot: nothing
  answered the press; a step closer for press 2 of 3`), then the body is given up;
- "Inventory is full", or the bag grid shows no free slot in a bag that takes
  any item (`ForeverLoot.BagsFull`): every body is left, the one reason the
  round leaves bodies of the fight unlooted;
- not the player's to loot (another's kill, already looted, being looted), or
  "Requires Skinning ..." on a body with nothing on it: that body is left at once.

A window that closes with items left is logged and the round goes on; that
body cannot be skinned. One that Auto Loot leaves open for 5 seconds gives the
body up (it closes as the round walks away). Any other body the key finds is
taken while it shows loot. The messages come from ForeverProbe's error latch
(`VisualGameError.TooFarAway`, `InventoryFull`, `LootRefused`, `SkillTooLow`).

Live on 2026-10-04 the rule "nothing lootable means nothing to loot" left two
bodies with loot on them; the user's rule since: "If the interact key finds
them then trust the interact key. First loot, then skin." Pressing the moment
the key first found such a body, about 10 yards off, then opened nothing on
two bodies (`5-crossroads2`, `5-crossroads7`); the step closer before each
press, and the retries, are the user's: "we should have some retries e.g. 3.
where we go close and if we see the interact key we try to loot. (we should
only leave if we have no bag space)".

**Skinning.** With the profile's `"Skin": true` (upstream's flag), a body is
skinned as soon as its loot is taken: once the soft target settles on the
emptied body (within 1.5 s) the Interact Key is pressed again, the client
casts Skinning, and the window it opens is emptied by Auto Loot. The game
decides whether a body can be skinned and the skill suffices: a body the soft
target does not settle on has nothing to skin, and a press that opens no
window within 6 seconds leaves it (`Forever left a body unskinned: ...`); a
skinned one logs `Forever skinned a body`. Skinning never stops the run.

**Clearing the target.** Looting and resting deselect a dead target with a
key, never with Escape, which opens the game menu whenever the target is
already gone or a window is open. The key is the profile's `ClearTarget`
macro key when the client binds it to a `/cleartarget` macro on the main bar,
else the clear-target binding: ForeverProbe makes its own `/cleartarget`
button and binds the first free key of Shift+Insert, Shift+F12, Shift+F11,
Shift+Home and Shift+End to it for the session (an override binding, so the
player's saved bindings do not change), and reports it in the binding grid.
The start-up check refuses a run without either.

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
4. `FallBack(reason)` lets go of what failed, the way this activity falls
   back (set something aside, leave it, give it up), resets its machine
   (`ForeverStateMachine.Reset`) and says what it did, for the log.
5. Register it in `ForeverGoalFactory` and add it to the selector in
   `ForeverAutonomousGoal`'s constructor.
6. Add logic checks: `Utilities/ForeverVisualReader/LogicTest.Phases.cs` has
   the selector's precedence checks to extend.

## Status and logs

While running, `Status` is the current activity and step, such as
`Engage/Fight`, `Threat`, `Loot`, `Rest` or `Task/Roam the area around ...`.
Each activity's state machine names itself in its log lines ("Forever engage
stopped in Approach: ..."). The trial runner prints every status change.

Offline checks: `dotnet run --project Utilities/ForeverVisualReader -- --logic-test`.
