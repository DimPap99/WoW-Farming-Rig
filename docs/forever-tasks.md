# Forever agenda and tasks

The API for telling the WoW: Forever bot what to do: travel somewhere, roam
an area until enough is killed, hunt where it stands. Tasks are data; code
and [route files](forever-routes.md) build the same tasks. Code lives in
`Core/Forever/Tasks/` and `Core/Forever/Activities/ForeverTaskActivity.cs`;
how the run interleaves tasks with fighting, looting and resting is in
[forever-activities.md](forever-activities.md), and how it walks in
[forever-movement.md](forever-movement.md).

## The model

- **A task is a record**: what to do and when it is done
  (`ForeverTask(Name, Until)`). It holds no state and presses nothing.
- **A runner carries a task out** over many frames (`IForeverTaskRunner`).
- **The agenda** (`ForeverAgenda`) holds the tasks in order. Its current
  task runs whenever no other activity wants to run.
- **A task never blocks.** On any frame a fight, a loot round or a rest can
  take over (the task's runner is exited and releases what it holds), and the
  task resumes afterwards from where the character is.
- **Kills count toward the task that was current** when they were verified,
  including kills in fights that took over from it.
- A task ends when its `Until` holds or its runner reports it done; the next
  task then starts with fresh progress. The run completes when the agenda is
  done, unless the agenda repeats.

## Building tasks: `ForeverTasks`

```csharp
using Core.Forever;

ForeverTask there = ForeverTasks.TravelTo(new ForeverMapPoint(1411, 52.5, 44.5));

ForeverTask hunt = ForeverTasks.RoamArea(
    ForeverArea.Rectangle(1411, (51.5, 44.0), (53.5, 46.0)),
    new ForeverUntil(Kills: 3, Minutes: 10));

ForeverTask back = ForeverTasks.TravelTo(
    new ForeverMapPoint(1411, 52.5, 43.0),
    via: [new ForeverMapPoint(1411, 53.0, 44.0)],
    pulls: ForeverPulls.Any);
```

| Method | Does | Done when |
|---|---|---|
| `TravelTo(to, via = null, pulls = None)` | walks along the baked mesh through `via` in order to `to` | it arrives (or its `Until`, normally never) |
| `RoamArea(area, until, pulls = Any, seed = null)` | walks into the area, then from spot to spot inside, looking round at each | `until` holds |
| `HuntHere(until)` | looks round where it stands, takes what it sees, wanders within 25 yards, looks again | `until` holds, or (with `NothingInView`) a full look-around finds nothing |

`HuntHere` is what the bounded trials run (`--auto-run`, `--sweep`). `TravelTo`
and `RoamArea` are described in detail in [forever-movement.md](forever-movement.md).

## Places: `ForeverMapPoint` and `ForeverArea`

Coordinates are a zone map's, 0 to 100, as the game shows them and as
upstream's recorded routes store them. `UIMapId` names the map (1411 is
Durotar, 1420 Tirisfal Glades).

- `ForeverMapPoint(UIMapId, X, Y)`. `TryToWorld(maps, out world, out
  instanceMapId, out reason)` converts it to world yards through the same
  conversion the live preflight checks against the client's own position. A
  point off the map or on a map not verified is refused with why.
- `ForeverArea(UIMapId, Corners)`: a polygon of three or more corners in
  order. `ForeverArea.Rectangle(uiMapId, a, b)` builds one from two opposite
  corners; `ForeverArea.FromCorners` takes two corners as a rectangle and
  three or more as a polygon, as route files give them. `TryToWorld` gives a
  `ForeverWorldArea` (`Contains`, `RandomPoint`, `Centre`, `Width`, `Height`).

Only verified maps can be used (`ForeverVerifiedSetup`), and a task's points
must be on the player's continent and on the baked mesh.

## When a task is done: `ForeverUntil`

Any condition that is set ends the task once it holds; with none set the task
does not end by itself.

| Condition | Holds when |
|---|---|
| `Kills` | this many kills were verified while the task was current |
| `Minutes` | this many minutes passed since the task began, fights and rests included |
| `Level` | the player has reached this level |
| `NothingInView` | a full look-around found nothing to take (hunting here only) |

`ForeverUntil.AfterKills(n)`, `AfterMinutes(m)`, `AtLevel(l)`,
`WhenNothingInView` and `Never` build the common ones;
`new ForeverUntil(Kills: 3, Minutes: 10)` combines them, whichever comes first.

## Pulling: `ForeverPulls`

`Any` takes eligible units in view as the task goes; `None` fights only what
attacks. Units that attack are always fought, whatever the task says (that is
the Threat activity). Before every pull, an attack the client reports unusable
for a reason other than mana stops the run with why (`ForeverPulling`).
Defaults: travelling `None`, roaming `Any`.

## The agenda

`ForeverAgenda(tasks, repeat = false)`:

| Member | Meaning |
|---|---|
| `Current`, `Tasks` | the task now, and all of them |
| `Progress` | the current task's kills and start time (`ForeverTaskProgress`) |
| `Add(task)` | appends a task after those planned |
| `Advance()` | ends the current task; a repeating agenda starts over after the last |
| `Finished` | no task left (never for a repeating agenda) |
| `Laps` | times a repeating agenda started over |
| `Describe()` | "task 2 of 3, lap 2: Roam the area around 52.5, 45 on map 1411 (1 kills in 2.3 minutes)" |

A session gets its agenda from `ForeverRunOptions`: `Tasks` and `RepeatTasks`
when set; otherwise one `HuntHere` task built from `MaxCompletedCycles` (until
that many kills) and `StopWhenNoTargets` (until nothing is in view).

```csharp
services.AddSingleton(new ForeverRunOptions
{
    Tasks = [there, hunt, back],
    RepeatTasks = true,
});
```

Before anything moves, the controller's preflight asks the agenda's start-up
check (`ForeverTaskActivity.TryPreflight`): every travel point on a verified
map of the player's continent and on the baked mesh, every roam area with
walkable ground inside. It is asked until it passes once.

## Writing a new kind of task

1. A record deriving from `ForeverTask`, with a factory method in
   `ForeverTasks`.
2. A runner implementing `IForeverTaskRunner`:
   - `Enter(bars, facts)`: first start, and every resume after a fight or a
     rest; resume from where the character is.
   - `Update(bars, facts, progress)`: one frame's work; `Done` when the
     runner's own part is over (the agenda checks `Until` itself), `Failed`
     with a reason to stop the run.
   - `Exit()`: release whatever is held; it is called whenever something
     more urgent takes over.
   - `Allowed` and `Status`: from the runner's own state machine.
3. A case in `ForeverTaskActivity.StartCurrent` that builds the runner, and
   one in `TryPreflight` if the task can be checked before the run moves.
4. For route files, a step kind in `ForeverRouteFile` (see
   [forever-routes.md](forever-routes.md)).
5. Logic checks beside `LogicTest.Travel.cs` and `LogicTest.Roam.cs`.

A runner should move the character only through `ForeverNavigator` and
`ForeverWalker`, so held keys, mouse steering and the per-state allowances
stay the same everywhere (see [forever-movement.md](forever-movement.md)).

Quests are expected to arrive this way: new step kinds (talk to an NPC at
given coordinates, accept, hand in) and new `Until` conditions (a quest's
objectives done); see "Quests (later)" in [forever-port.md](forever-port.md).
