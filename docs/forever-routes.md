# Forever route files

A route file is the WoW: Forever bot's agenda written as data: steps in
order, each a `Travel` or a `Roam`, on a zone map in the coordinates a player
reads. It loads into the same tasks the [`ForeverTasks` API](forever-tasks.md)
builds, so a route written by hand and one built in code run the same way.
Code: `SharedLib/Forever/ForeverRouteFile.cs`, beside the task records it loads
into, so the bot and the Pathing Service read routes with the same rules.

## Where they live

`Json/path/_forever/<name>.json`. `ForeverRouteFile.Resolve(data, name)`
takes a path as given when it exists, else a file of that name in that
folder. Comments (`//`) are allowed. `durotar_example.json` ships as a
commented example.

The Pathing Service lists, checks and serves them (`/api/v1/forever/routes`),
whatever client it serves, and the Control Center's **Forever routes** page
edits them as text: a save keeps the comments, and a route a run would refuse
is not saved, with the step that is wrong.

## Format

```json
{
  "Name": "Durotar example: south of Razor Hill",
  "UIMapId": 1411,
  "Steps": [
    { "Travel": { "To": [52.5, 44.5] } },
    { "Roam": { "Area": [[51.5, 44.0], [53.5, 46.0]], "Until": { "Kills": 3, "Minutes": 10 } } },
    { "Travel": { "To": [52.5, 43.0] } }
  ],
  "Repeat": false
}
```

| Field | Meaning |
|---|---|
| `Name` | shown in logs; the file name when left out |
| `UIMapId` | the map the coordinates are on (1411 Durotar, 1413 The Barrens, 1420 Tirisfal Glades; `--where` prints it) |
| `Steps` | the steps, in order; at least one |
| `Repeat` | `true` starts over after the last step, a farming loop that runs until the time bound |

A point is `[x, y]`, each from 0 to 100. Any zone in the map data will do, and
a route may cross into the next zone of the same continent: give the steps
there that zone's `UIMapId`. Another continent needs a boat or a zeppelin,
which no step takes yet, so its points are refused at start-up.

### Travel

| Field | Meaning | Default |
|---|---|---|
| `To` | where to go | required |
| `Via` | points on the way, in order: `[[x, y], ...]` | none |
| `Pulls` | `"OnTheWay"` takes the units standing on the way (within 20 yards of the path, over the next 80); `"Any"` takes any unit in view; `"None"` fights only what attacks | `"None"` |
| `UIMapId` | this step's map, when not the route's | the route's |

### Roam

| Field | Meaning | Default |
|---|---|---|
| `Area` | two opposite corners `[[x1, y1], [x2, y2]]`, or three or more corners in order | required |
| `Until` | `{ "Kills": n, "Minutes": m, "Level": l }`, any of them, whichever comes first | required |
| `Pulls` | `"None"` roams without taking units | `"Any"` |
| `Seed` | pins the spots it walks between; otherwise a fresh seed is drawn and logged | random |
| `UIMapId` | this step's map, when not the route's | the route's |

How travelling and roaming behave is in [forever-movement.md](forever-movement.md).

## Checks

**When the file loads**, a mistake is refused with the step it is in:

- a step with both kinds or neither;
- a point that is not `[x, y]`, or off the map;
- a map the map data does not have;
- a roam without an `Area` of two or more corners, or without an `Until`
  (it would never end), or with `Kills` or `Level` below 1 or `Minutes` not
  above 0;
- any field the format does not know: a misspelt `"Kils"` is refused with
  where it is (`Steps[1].Roam.Until.Kils`) instead of silently doing nothing.

```
Route refused: Durotar test, step 2: a Roam needs an Until with Kills, Minutes or Level, or it would never end
```

**Before anything moves**, the controller's start-up check needs every travel
point on the player's continent and on the baked mesh, and every roam area
with walkable ground inside.

## Running a route

From the Control Center, through the game PC's bot agent (see
[the central services](pathing-api.md#the-bot-manager-and-the-agents)):

1. Start the Pathing Service, the Bot Manager and the Control Center. On the game PC, start
   the agent: `HeadlessServer\run.bat --agent --manager http://<central-pc>:5030
   --pathing-service http://<central-pc>:5010`, or install its logon task.
2. On **Fleet**, open the bot's console. Choose WoW: Forever, the route and its time bound
   (1 to 600 minutes), then Save and Start.
3. The agent first pulls the route, and Forever's cost zones, from the Pathing Service into
   its own `Json`. The run then lists its steps, waits for the preflight, and asks for a
   click into the game window (the console says so), as below.
4. Stop releases the keys and the mouse first. The run's lines are in the console, in its
   run history, and in `local/logs/agent-<route>-<time>.log` when that folder exists.

The route is checked as the run loads it, so a route the Forever routes page shows as refused
is refused here too, with the step.

From BlazorServer, until the agent has passed its Windows checks (the page then goes, known
issue 24): start BlazorServer (`BlazorServer\build.bat`, then
`BlazorServer\run.bat`) and open **Forever runs** at `http://localhost:5000`.
It lists the route files with their steps (or why one is refused), starts one
with a time bound, shows the run's activity, progress and output as it goes,
and stops it. The server itself stays read-only: `ForeverRunHost` starts the
same trial runner as below on the client the server watches (`--pid`), as a child
process with `--listen` (a "stop" line on its input stops it as Ctrl+C does,
and the server waits for it to let go before anything else), and keeps its output in
`local/logs/web-<route>-<time>.log` when that folder exists. After Start, click
into the game; since the bot steers with the mouse, clicking anywhere else on
the PC stops the run, so watch from another screen or device. Build
`Utilities/ForeverControllerTrial` first: the page runs its newest build.

From a terminal:

```powershell
dotnet run --project Utilities/ForeverControllerTrial -- --route durotar_example.json
dotnet run --project Utilities/ForeverControllerTrial -- --route my_loop.json --minutes 30
```

- The file is read and checked before anything attaches to the game; the
  steps are listed.
- The run waits for the preflight, then for the game window to be active
  (click into it), then runs the steps in order. Fights, loot rounds and
  rests take over whenever needed, and the step resumes afterwards.
- A step that fails is given up and the next one starts; the last step of a
  route that does not repeat is tried again after three minutes of hunting
  where the character stands, until the time bound ("When a task fails" in
  [forever-tasks.md](forever-tasks.md)).
- Each status change is printed with the agenda's progress:
  `Autonomous phase: Task/Roam the area around 52.5, 45 on map 1411/Look; ...; task 2 of 3: ... (1 kills in 2.3 minutes)`.
- The time bound is 20 minutes, or `--minutes <n>` (1 to 600).
- Exit code 0 when the route completes, or when a repeating route reaches its
  time bound without failing; 3 when it fails, stops or errs after it started, or a
  non-repeating route runs out of time; 2 when the game, the frame, the preflight
  or a missing click refused it before anything moved;
  1 for a usage mistake or a route file refused when it loads.

## Writing a route

1. Stand where a stop, a point on the way or an area's corner should be and
   read the position:

   ```powershell
   dotnet run --project Utilities/ForeverVisualReader -- --where
   ```

   It prints the zone and `[x, y]` whenever the position changes (60 seconds,
   or `--where <seconds>` up to 600), ready to paste.
2. Keep areas where the mobs you want stand, away from towns and guards, and
   give each roam a sensible `Until`.
3. Run it once without `Repeat`, then turn `Repeat` on for a farming loop.

A farming loop between two areas, pulling on the way:

```json
{
  "Name": "Durotar loop",
  "UIMapId": 1411,
  "Steps": [
    { "Roam": { "Area": [[51.5, 44.0], [53.5, 46.0]], "Until": { "Kills": 5 } } },
    { "Travel": { "Via": [[54.0, 46.5]], "To": [55.5, 48.0], "Pulls": "Any" } },
    { "Roam": { "Area": [[54.5, 47.0], [57.0, 47.0], [57.0, 50.0], [54.5, 50.0]], "Until": { "Kills": 5, "Minutes": 8 } } },
    { "Travel": { "To": [52.5, 45.0] } }
  ],
  "Repeat": true
}
```

The coordinates above are illustrations; read real ones with `--where`.

## Later

Quests are expected to add step kinds (talk to an NPC at given coordinates,
accept, hand in) and `Until` conditions (a quest's objectives done) to this
same format; see "Quests (later)" in [forever-port.md](forever-port.md).
