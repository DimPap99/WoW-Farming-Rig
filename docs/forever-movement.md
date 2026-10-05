# Forever movement: walking, travelling and roaming

How the WoW: Forever bot gets from one place to another and hunts an area,
the way a person at the keyboard would: forward held, steered with the mouse,
a camera that turns at a steady speed, and no tapping. Code lives in
`Core/Forever/ForeverNavigator.cs` and `Core/Forever/Tasks/` (`ForeverWalker`,
`ForeverTravel`, `ForeverRoamArea`, `ForeverSpotPicker`), with `ForeverArea` in
`SharedLib/Forever/`. The
task API that uses it is in [forever-tasks.md](forever-tasks.md).

## The navigator's primitives

`ForeverNavigator` owns the keys and the camera. Every movement goes through
it, so held keys, mouse steering and the per-state allowances stay the same
everywhere.

| Member | Does |
|---|---|
| `WalkToward(bars, point)` | one frame of walking toward a nearby world point: a short navmesh path, forward held, the mouse steering onto it; a hop when held movement stalls (where allowed) |
| `TryFindPath(bars, destination, out path, out reason)` | the whole way to a world point over baked tiles, or why not |
| `IsOnMesh(bars, point)` | the baked mesh covers the point |
| `TrySnapWalkable(bars, hint, out spot)` | the walkable point nearest a hint, on the ground under it (no roof or canopy) |
| `Sidestep(bars, side, out reason)` | strafe held toward whichever side is walkable |
| `BackOut(bars, rate, out reason)` | move-backward held while the camera turns |
| `TurnInPlace(bars, rate, out reason)` | turns standing, as when looking round |
| `Roam(bars, out reason)` | the short wander of hunting here, within 25 yards |
| `StopPursuit()` | releases forward, strafe, move-backward and the camera |
| `Halt()` | releases everything and refuses further input (the controller stopped) |

Every key comes from the client's own bindings, read from the frame it is
pressed on: move forward, strafe left and right, move backward (binding grid
index 3, a plain key without modifiers to be held) and jump. A right-button
hold for turning always begins on empty ground (`ForeverDragPoint`).

## The walker

`ForeverWalker` walks to one world point and gets itself unstuck on the way.
Travelling and roaming are both built on it.

```csharp
walker.Begin(to: spot, reach: 4, mayGiveUp: true, name: "a spot in the area");
switch (walker.Update(bars, out string reason))
{
    case ForeverWalkResult.Arrived:  walker.Stop(); break;   // or Begin the next point to flow through
    case ForeverWalkResult.Skipped:  /* pick another destination */ break;
    case ForeverWalkResult.Failed when walker.NoProgress: /* walk again, or give the place up */ break;
    case ForeverWalkResult.Failed:   /* fail with reason; the run falls back */ break;
}
```

- `Begin(to, reach, mayGiveUp, name)` sets off; a walk already under way
  turns toward the new point without stopping, so it flows through points on
  the way. A failed walk begins afresh.
- `NoProgress` tells a walk that ran out of progress (another walk from where
  the character stands may get through) from any other failure, such as the
  game losing focus, which is never walked again.
- `Arrived` leaves forward held: the caller either begins the next point or
  calls `Stop()`.
- `Resume()` after something else ran: the way is asked for afresh from where
  the character stands, with a fresh progress window.
- `Stop()` releases held movement.

### The way

A full path over the baked Durotar mesh costs 0.15 to 2 ms within 400 yards,
but 20 to 26 ms around 1,000 yards (10 to 80 ms while tiles load). So the
walker asks for the whole way every 5 seconds, or when it strays more than 8
yards off it, and on each frame walks toward the point 20 yards along it with
`WalkToward`, whose own short path costs a fraction of a millisecond. A path
point within 2 yards counts as passed, and a walk that cut a corner is found
past the points before it.

### Which floor

The client reports no height for the player (Forever's `UnitPosition` gives
Z = 0), and a column of the mesh can hold more than one walkable surface: a
mountain top over the ground below, a rock top or a ledge beside a trail. So
the navigator asks for paths without a height, and the pathfinder's column
scan picks the floor: outdoors the highest surface, and the player's own kept
from one request to the next within 80 yards. Where a path from that floor
does not hold, it is asked for again from the surface nearest height 0, the
other likely floor (`FindOnEitherFloor`). A walk along a route needs neither
guess: the route's points carry the height of the floor it runs on, and each
step passes the route's height at the player and at the point ahead.

### Walking toward an estimate

A body or a unit out of sight has no exact position: its nameplate places it
along a bearing, or the minimap's tracking places it to a few yards. `WalkToward`
takes an end slack for such a point (5 yards for a body), or
`ForeverNavigator.AsCloseAsReachable`, which walks to wherever the mesh comes
closest and is `Blocked` once it comes no closer; a point over a hole in the
mesh is drawn back toward the player until there is ground under it.

### Turning

A person turns standing at an unhurried pace. No standing turn goes faster
than 0.6 of full mouse speed, however far it turns; looking further round for
a target out of view runs at 0.35; the calm look-around of hunting and
roaming at 0.25. Facing a mob in a fight follows its marker as before.

### Getting unstuck

Borrowed from upstream's `StuckDetector`, redone with held keys and mouse
turns. `WalkToward`'s own hop comes first when held movement stalls. Then each
time the walk stalls (forward held without progress for 3 seconds, no way from
where it stands, or turning standing 2 seconds without a walkable way ahead) it
climbs one step:

| Stall | Step |
|---|---|
| 1st | step aside: strafe held for 0.7 to 1.2 s toward a walkable side |
| 2nd | back out: move-backward held for 0.8 to 1.3 s while the camera turns at half speed |
| 3rd | give the destination up, where the caller allows it (a point on the way, a roam spot) |
| then | step aside and back out in turn |

Without a move-backward key the walker only steps aside. After each step the
way is asked for afresh.

Before each step the navigator checks that the next 3 yards along the facing
are walkable, and turns standing while they are not. The mesh keeps a
character's width away from walls and objects, so brushing past one can leave
the character standing just off it, from where the check fails every way:
within 2 yards of the mesh (`OffMeshYards`) the check is made from the
nearest walkable point instead, and turning standing for 2 seconds
(`WayAheadTurnMs`) without a walkable way ahead is a stall, not more turning;
a shorter turn is left alone, since on a narrow way a slightly better facing
is all it needs (a first version stalled at once when facing within 0.2
radians, and sidestepped where a little more turning would have done). Live on
2026-10-04 a trip stood 20 seconds on one spot in Ratchet, turning, before
either was in place.

### Progress

As upstream: only a new closest approach counts, a yard closer along the path,
and it resets the ladder. Without one for 20 seconds the walk fails with why
("walking to ... made no progress for 20 seconds: ...") and `NoProgress`: a
travel walks again from there, a roam gives the place up.

## Travelling

`ForeverTravel` runs a `TravelTo` task: the points on the way, then the
destination, each with the walker.

- Within 6 yards of a point on the way it flows on to the next; within 3
  yards of the destination it stops, and the task is done.
- A walk that stalls, or makes no progress, skips a point on the way. The
  destination is walked again from where the walk stalled, 3 walks in all
  (`ForeverTravel.LegTries`, logged `Forever travel: ...; walking again from
  here, walk 2 of 3`); after that the travel fails and the agenda gives it up
  (see "When a task fails" in [forever-tasks.md](forever-tasks.md)).
- With `Pulls: Any` it takes units in view as it goes; units that attack are
  always fought, and the travel resumes from wherever the fight left it.
- When the task starts, every point must be on a map the map data has, on the
  player's continent and on the baked mesh, or the run stops with why. The
  point may be in another zone: the mesh is one per continent, so the walk
  crosses zone borders without noticing them.

## Roaming an area

`ForeverRoamArea` runs a `RoamArea` task as a patrol. Its steps are `Walk` and
`Look`:

1. **Entering.** Inside the area it begins by looking round; outside, it draws
   a few spots inside, takes the nearest one that is reachable and walks
   there.
2. **Look.** A calm full circle standing (a quarter of full turning speed),
   taking units in view. The client draws a nameplate only for a unit the
   camera sees, so looking round is how the camera checks all about.
3. **Walk** to where the patrol leads (below), taking units in view on the way;
   on arrival, look again.
4. After a kill it looks round first, so what stands near is taken before it
   walks on; after anything else (a failed click, a rest) it goes on toward
   the same place, back into the area when a fight led out of it. There is no
   leash yet: a pursuit may follow a unit outside the area.
5. It ends when the task's `Until` holds. A walk that makes no progress gives
   its place up, as a stall does. Four places in a row given up, or three
   draws that find no spot, fail the roam with why, and the agenda goes on
   with the next task.

A place counts as reached within 4 yards. With `Pulls: None` it walks and
looks without taking units.

### Where the patrol leads

What the camera shows always comes first: every frame of the walk and the
look offers the best pull in view ([forever-tasks.md](forever-tasks.md)),
until nothing is left in view. Only then, at the end of a look round, does
`ForeverPatrol.TryNext` choose where to walk, in this order:

1. **A mob seen lately** (`TryRemembered`), in or within 15 yards of the area
   (`EdgeYards`), one the run may pull, set aside by nothing, and off screen
   now: the nearest. `ForeverMobMemory` keeps every plate the camera showed
   for 2 minutes, where its bearing and range band (or its tracked dot) placed
   it; it forgets a unit reported dead, and one that a look round passing
   within 40 yards of its place did not see again. Any class has it.
2. **A dot of the minimap's tracking** (`TryTracked`), in or around the area
   and more than 35 yards off (`SightYards`; a dot nearer than that showed no
   plate worth a pull in the look round: a critter, or a unit the profile
   does not take): the nearest. Only where the class tracks.
3. **The part of the area seen least** (`TryLeastCovered`). The area is split
   into 25-yard cells (`CellYards`); a cell counts as seen when a look round
   ends within 40 yards of it (`LookYards`), or the walk passes within 20
   (`PassYards`). The nearest cell never seen goes first; once every cell is
   seen, the one seen longest ago.

A place that is off the mesh or out of reach is passed over, and so is one a
walk to a mob or a dot reached without taking anything, for 90 seconds
(`VisitedMs`, within 10 yards). Each walk says why:
`Forever roam: nothing in view; heading for a tracked dot in the area around ..., 80 yards off (5 of 12 cells seen)`.
With no lead at all it draws a spot as below.

### Drawing spots

`ForeverSpotPicker` draws the spot that walks into the area from outside, and
the fallback when the patrol leads nowhere, the way upstream's generated
Wander routes pick their stops:

1. A random point of the area, drawn from the task's seed.
2. Snapped onto the ground under it (`TrySnapWalkable`).
3. Kept only if still inside the area,
4. at least `Spacing` from the last spot (25 yards, or 40% of the area's
   narrower side in a small area),
5. and reachable from where the player stands (a complete path).

Up to 40 draws per spot. `Nearest(here, ...)` is the entry from outside: it
asks whether a spot is reachable only for the nearest of six, nearest first,
since a long path costs. The seed is logged ("Forever roam of the area around
...: seed 123, spots at least 25 yards apart"); a task's `Seed` pins it, and
the same seed draws the same spots.

## Tuning

| Value | Where | Default |
|---|---|---|
| destination reached | `ForeverTravel.ArriveYards` | 3 yd |
| point on the way passed | `ForeverTravel.ViaYards` | 6 yd |
| roam spot reached | `ForeverRoamArea.SpotYards` | 4 yd |
| heading point along the path | `ForeverWalker.CarrotYards` | 20 yd |
| progress needed | `ForeverWalker.ProgressYards` | 1 yd |
| give up without progress | `ForeverWalker.GiveUpMs` | 20 s |
| walks to a travel's destination before it fails | `ForeverTravel.LegTries` | 3 |
| spot spacing | `ForeverSpotPicker.MaxSpacingYards` | 25 yd |
| draws per spot | `ForeverSpotPicker.MaxAttempts` | 40 |
| patrol cell side | `ForeverPatrol.CellYards` | 25 yd |
| seen by a look round / by the walk | `ForeverPatrol.LookYards` / `PassYards` | 40 / 20 yd |
| a tracked dot nearer is passed over | `ForeverPatrol.SightYards` | 35 yd |
| leads past the area's edge | `ForeverPatrol.EdgeYards` | 15 yd |
| a place found empty is passed over | `ForeverPatrol.VisitedMs` within `VisitedYards` | 90 s, 10 yd |
| a mob is remembered | `ForeverMobMemory.RememberMs` | 2 min |

## Checks

Offline (`--logic-test`): map conversion, path following on straight and
cut-corner paths, the off-path distance, the ladder's order, the progress
rule, a walker following a real 190-yard Durotar path, area containment, the
spot picker on stand-ins and ten spots around Razor Hill on the baked mesh,
and the patrol: remembering and forgetting mobs, tracked dots, the cells seen
least, and the order of the leads.
Live: `ForeverControllerTrial --travel <x> <y>` and
`--roam <x1> <y1> <x2> <y2> <kills>` (see the testing handover). On
2026-10-04 in The Barrens, travel passed over 60 yards of open ground and over
a 402-yard route through hilly ground, fighting an attacker on the way and
travelling on after it.
