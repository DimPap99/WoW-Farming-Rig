# Forever agenda and tasks

The API for telling the WoW: Forever bot what to do: travel somewhere, roam
an area until enough is killed, hunt where it stands. Tasks are data; code
and [route files](forever-routes.md) build the same tasks. The task records,
`ForeverUntil` and the map points and areas are data in `SharedLib/Forever/`,
which the services read too; the runners, the agenda and whether an `Until`
holds (`ForeverUntilChecks`) live in `Core/Forever/Tasks/` and
`Core/Forever/Activities/ForeverTaskActivity.cs`;
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
- **A task that fails is given up**, and the agenda goes on with the next
  (below); the run never stops for it.

## When a task fails

The agenda is the route's plan from the top: each task is a bigger goal than
the places and fights within it, and the next task is the next bigger goal.
So a failed task falls back to the next one, as the user put it: "if
something completely breaks at the first place then maybe the bigger goal in
that place is roam at the areas in that place we havent gone. then next
bigger goal is to go to the next area. and the one after that and to cross
roads."

1. **Inside a task** the runner falls back first: a roam spot it cannot get
   to is given up for the part of the area seen least, a point on the way of
   a travel is skipped, and a travel's destination is walked again from where
   the walk stalled, 3 walks in all ([forever-movement.md](forever-movement.md)).
2. **The task.** When the runner fails all the same (no spot of the area
   reachable, the destination not reached after 3 walks, 45 seconds of hunting
   here with nothing in view), `ForeverTaskActivity.FallBack` gives it up and
   `ForeverAgenda.GiveUp` starts the next task.
3. **The last task.** With nothing above it, a route that does not repeat
   hunts where the character stands for `ForeverAgenda.FallbackMinutes` (3),
   then tries that task again, until the time bound. A repeating route simply
   starts its next lap.

The fights, loot rounds and rests that take over from a task fall back the
same way, each as it knows how; what stops the run is in "When something
fails" in [forever-activities.md](forever-activities.md).

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
| `RoamArea(area, until, pulls = Any, seed = null)` | walks into the area and patrols it: takes what the camera shows, then heads for a mob seen lately, a tracked dot, or the part seen least, looking round at each | `until` holds |
| `HuntHere(until)` | looks round where it stands, takes what it sees, wanders within 25 yards, looks again | `until` holds, or (with `NothingInView`) a full look-around finds nothing |
| `Visit(target, pulls = None)` | finds the place when it begins - `ForeverVisitTarget.Nearest(role, title)`, `.Npc(name)`, `.QuestStart(id)`, `.QuestEnd(id)` - then travels there as `TravelTo` | it arrives |
| `QuestArea(questId, until, pulls = Any, seed = null)` | finds the camp of what the quest asks to kill or gather, nearest the character, when it begins, then roams it as `RoamArea` | `until` holds |

`HuntHere` is what the bounded trials run (`--auto-run`, `--sweep`). `TravelTo`
and `RoamArea` are described in detail in [forever-movement.md](forever-movement.md).

### Places found in the world data

`Visit` and `QuestArea` name their place by who or what is there.
`ForeverDestinations` (SharedLib) finds it in the client's `QuestBook` - every
creature with its roles and the sides it serves (`Creature.FriendlyTo`,
QuestieDB's), every spawn, every quest's places (`PlacesOf`, what the Control
Center draws) - the nearest on the character's continent, and turns it into the
`ForeverTravelTask` or `ForeverRoamAreaTask` the existing runners carry out. The
runners `ForeverVisit` and `ForeverQuestArea` (Core) add only what the screen
says: where the character stands, and its race and class (the side a role's NPC
must serve, the class a `classtrainer` teaches). `ForeverWorldData` reads the
book the first time such a task asks. A quest's camp is its nearest spawn and
those linked to it - within 60 yards, or twice how far its spawns usually stand
apart - at most 15 of them, with 15 yards around.

## Places: `ForeverMapPoint` and `ForeverArea`

Coordinates are a zone map's, 0 to 100, as the game shows them and as
upstream's recorded routes store them. `UIMapId` names the map (1411 is
Durotar, 1413 The Barrens, 1420 Tirisfal Glades).

- `ForeverMapPoint(UIMapId, X, Y)`. `TryToWorld(maps, out world, out
  instanceMapId, out reason)` converts it to world yards through the same
  conversion the live preflight checks against the client's own position. A
  point off the map or on a map the map data does not have is refused with why.
- `ForeverArea(UIMapId, Corners)`: a polygon of three or more corners in
  order. `ForeverArea.Rectangle(uiMapId, a, b)` builds one from two opposite
  corners; `ForeverArea.FromCorners` takes two corners as a rectangle and
  three or more as a polygon, as route files give them. `TryToWorld` gives a
  `ForeverWorldArea` (`Contains`, `RandomPoint`, `Centre`, `Width`, `Height`).

Any zone in the map data can be used, and a task's points must be on the
player's continent and on the baked mesh. They may lie in another zone than
the player's: the mesh is one per continent, so a task crosses zone borders.

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

`Any` takes eligible units in view as the task goes; `OnTheWay` takes only
those standing on the way, within 20 yards of the path still to walk over its
next 80 (`ForeverTravel.OnTheWayYards`, `AheadYards`; `ForeverWalker.IsOnTheWay`),
as a person on their way somewhere kills what stands in it and makes no
detour; `None` fights only what attacks. Units that attack the player or the pet are always fought, whatever
the task says and whatever their level (that is the Threat activity). Before
every pull, an attack the client reports unusable for a reason other than mana
stops the run with why (`ForeverPulling`).
Defaults: travelling `None`, roaming `Any`.

### Which unit: the pull's cost

`ForeverPullOptions` lists every unit the run could pull now (eligible, its
marker on screen between the clickable bottom and top, not set aside, not too
close for every opener) and puts them in order; the best is offered. A click
reaches a plate only in the middle of the screen
(`ForeverTargetPolicy.Clickable`: 0.08 to 0.8 across, 0.33 to 0.94 up from
the bottom): off the edges, where a marker clips and slips out of view as the
camera moves, and clear of the default interface, whose frames take the
click instead (the minimap and objective tracker at the right, the unit
frames, chat and action bars at the bottom). When the best unit stands at the
side of the screen, the run does what a person does: it stops, turns to face
it, and clicks once its marker is in the middle (`ForeverPull.Turning`,
logged once as `Forever pull: turning to face ... first`). A unit too high on
screen, far off near the horizon, comes down into reach as the patrol walks
toward it. The order never refuses a pull, so a unit is
never passed by for being far, out of sight or above the player's level: what
may be pulled at all is the target policy's to say (the profile's level
window, a unit tapped by someone else, kinds such as elites).

1. **Safety first**: the fewer other hostile units that could join the fight
   (alive, fighting no one, within 20 yards of it, `NeighbourYards`), the
   better, whatever the distance. A unit on its own always comes before one
   with company.
2. **Then the effort**, in yards of walking:

   | Part of the effort | Constant | Yards |
   |---|---|---|
   | how far the unit stands | (the estimate) | its yards |
   | the camera cannot see it | `OccludedYards` | 40 |
   | no attack reaches it from here | `OutOfReachYards` | 10 |
   | each level above the player | `LevelAboveYards` | 5 |
   | not the current target (a click) | `ClickYards` | 5 |
   | at the side of the screen (a turn first) | `TurnYards` | 5 |

3. Then the lowest on screen, the nearest on open ground.

So: a unit on its own beats a nearer one with a hostile unit beside it; of a
hostile (red) and a neutral (yellow) unit standing together the hostile one
goes first, since the neutral one does not join a fight it was not drawn into,
and the neutral one, alone after that, next; of two or three hostile units
together one is still pulled when nothing better is in view, on its own (see
below). Levels above count for little: a mob two levels above weighs as one of
the player's level 10 yards farther.

A unit stands where its nameplate places it: along the marker's bearing, at
the middle of the distance band its range flags give (within 8, 8 to 28, or 28
yards to the plate distance), or exactly where the minimap's tracking shows a
dot along that bearing. A plate whose flags say nothing is too vague to count
as a neighbour. Far off the bands are wide, so a unit may look accompanied
from afar; that only puts it later in the order, and the pet is held back on
such a pull anyway.

Each pull logs its options:
`Forever pull: hostile level 12, 18 yd, alone: effort 23; passed over neutral level 12, 20 yd, 1 hostile near: effort 25`.
`ForeverControllerTrial --auto-check` prints the options for where the player
stands, read-only.

A Hunter pulling a unit with a hostile unit near it keeps the pet back: the
opener draws the mob to the player, and the pet goes in once the mob fights,
since the pet's run in would draw the other one too.

## The agenda

`ForeverAgenda(tasks, repeat = false)`:

| Member | Meaning |
|---|---|
| `Current`, `Tasks` | the task now, and all of them |
| `Progress` | the current task's kills and start time (`ForeverTaskProgress`) |
| `Add(task)` | appends a task after those planned |
| `Complete()` | the current task is done; the next starts |
| `GiveUp()` | the current task failed; the next starts, or, after the last of an agenda that does not repeat, hunting here for 3 minutes and then that task again |
| `Advance()` | ends the current task; a repeating agenda starts over after the last |
| `Finished` | no task left (never for a repeating agenda) |
| `Laps` | times a repeating agenda started over |
| `Completed`, `GivenUp` | tasks done, and given up, in this run |
| `Describe()` | "task 2 of 3, lap 2: Roam the area around 52.5, 45 on map 1411 (1 kills in 2.3 minutes)", with ", 1 given up" once one was |

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
walkable ground inside, and each visit's and quest's place, found from where the
run begins, checked as a travel or a roam is. It is asked until it passes once.

## Writing a new kind of task

1. A record deriving from `ForeverTask`, with a factory method in
   `ForeverTasks` (both in `SharedLib/Forever/ForeverTask.cs`).
2. A runner implementing `IForeverTaskRunner`:
   - `Enter(bars, facts)`: first start, and every resume after a fight or a
     rest; resume from where the character is.
   - `Update(bars, facts, progress)`: one frame's work; `Done` when the
     runner's own part is over (the agenda checks `Until` itself), `Failed`
     with a reason once its own fallbacks are spent: the agenda gives the
     task up and goes on (see "When a task fails").
   - `Exit()`: release whatever is held; it is called whenever something
     more urgent takes over.
   - `Allowed` and `Status`: from the runner's own state machine.
3. A case in `ForeverTaskActivity.StartCurrent` that builds the runner, and
   one in `TryPreflight` if the task can be checked before the run moves.
4. For route files, a step kind in `ForeverRouteFile` (`SharedLib/Forever`; see
   [forever-routes.md](forever-routes.md)).
5. Logic checks beside `LogicTest.Travel.cs` and `LogicTest.Roam.cs`.

A runner should move the character only through `ForeverNavigator` and
`ForeverWalker`, so held keys, mouse steering and the per-state allowances
stay the same everywhere (see [forever-movement.md](forever-movement.md)).

Quests arrive this way. Going where a quest is given, done and handed in is here
(`Visit`, `QuestArea`); talking to the NPC (accept, hand in, buy, train) and new
`Until` conditions (a quest's objectives done) are next; see "Quests" in
[forever-port.md](forever-port.md).
