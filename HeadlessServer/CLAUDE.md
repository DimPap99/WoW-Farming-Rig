# HeadlessServer — the bot without a UI, and the PC's resident bot agent

Same bot as `BlazorServer`, minus the web front end, on the Generic Host. A class profile on the
command line runs at once, as it always has; `--agent` keeps the process as this PC's bot agent
(`docs/service-architecture.md`): it dials out to the Bot Manager, runs one assignment at a time
(Classic or WoW: Forever), and streams telemetry. It is also the working directory benchmarks chdir
into.

```
Program.cs                    config, Serilog, CommandLineParser, then the host; ends the process
HeadlessArguments.cs          the command line's two readers apart (configuration keys / options)
RunOptions.cs                 the CLI surface
DependencyInjection.cs        StartupConfig options from RunOptions (a Classic run's composition)
Hosting/HeadlessHost.cs       the Generic Host: BotAgent + the runtimes below
Hosting/HeadlessLogging.cs    Serilog: file, debug, console, and the agent's log stream
Hosting/StartFromCommandLine  a profile given on the command line runs at once
Hosting/AgentProcess.cs       the way out for a run that cannot be torn down (exit code 75)
Runtimes/HeadlessRuntimeFactory   which runtime an assignment needs, refused before attaching
Runtimes/ClassicRuntime.cs    the Classic bot, composed per run (InitState, profile, on; off, dispose)
Runtimes/ForeverRuntime.cs    Core's ForeverRun for a route, with the trial runner's gates
Runtimes/WowGameProbe.cs      which client runs, without attaching
Runtimes/ClassProfileFolder   Json/class, for the manager's Classic assignments
headless_appsettings.json     options (note the prefix - NOT appsettings.json)
data_config.json, addon_config.json, frame_config.json, agent_identity.json   beside it, gitignored
run.bat / rundev.bat / build.bat / install.bat / loadall.bat
```

## Running

```powershell
.\build.bat
.\run.bat Warrior_1.json -m Local -p <wow-pid>                      # as always: runs, Ctrl+C stops it
.\run.bat --agent --manager http://botbox:5030 --name "PC 1"       # resident agent
```

**The class profile is positional, not an option.** There is no `-c`/`--classconfig`; it comes
first and bare, and it is needed unless `--agent` is given. Passing `-c Warrior_1.json` fails to parse.

`run.bat` passes `--no-build --no-restore`, so **build first** or you run a stale binary.

## Options

Long forms take two dashes (`--mode`); `CommandLineParser` rejects `-mode`.

| flag | meaning |
|---|---|
| *(positional, arg 0)* | class profile from `Json\class\` (e.g. `Warrior_1.json`), started at once — needed unless `--agent` |
| `--agent` | stay resident: run what the Bot Manager assigns; a profile given too starts at once |
| `--manager <url>` | the Bot Manager (`BotManager:Url`); none: the agent runs on its own |
| `--name <name>` | what the manager calls this agent (`Agent:Name`); the machine name by default |
| `-m, --mode` | `Local` (default) / `RemoteV1` / `RemoteV3` |
| `-p, --pid` | WoW process id — needed when several clients run |
| `-r, --reader` | screen reader backend |
| `--hostv1/--portv1`, `--hostv3/--portv3` | remote pathing endpoints |
| `-n, --viz` | path visualizer |
| `-g, --gpu` | GPU capture |
| `-t/-s/-v` | targeting / skinning / target-vs-add overlays |
| `--loadonly` | load the class config, report, exit — no bot |

The key for a manager on another PC is never on the command line: `ApiKey` (or `BotManager:ApiKey`)
in `headless_appsettings.json` or the environment.

## Things that will bite you

**Config file is `headless_appsettings.json`, not `appsettings.json`.** Copying settings
from `BlazorServer` into the wrong filename here silently changes nothing.

**Configuration keys and options are two readers, split before either sees them.** A
`--Section:Key=value` (or `--Section:Key value`) argument goes to the configuration only
(`HeadlessArguments`); everything else goes to `CommandLineParser`. Before the split,
CommandLineParser refused such a key (`UnknownOptionError`), so `--Pathing:Engine=Navmesh` never
worked although this file said it did.

**Core is composed per run, never at startup.** `WowProcess` throws without a client, and
`BotController`'s constructor waits until the addon reads the player's class. So the host starts
with no game at all (it runs on Linux, refusing every run: "no WoW client is running"), and each
run composes Core for the client the probe found, then disposes it.

**Stop releases input first.** A stop (a command, Ctrl+C, a logoff, the host stopping) cancels the
run's token; the run turns the bot off, which releases what it holds, and only then is torn down.
A run that has not ended after `Agent:StopGraceMs` (10 s) has its keys released from outside; one
that still does not end is abandoned and the process exits with 75, which the logon task's loop
restarts. See `Game/CLAUDE.md` for why the order matters.

**Forever runs only as an agent assignment.** A Forever route goes through Core's `ForeverRun`
with the trial runner's gates: the verified profile, the preflight, a click into the game window
(the console shows "waiting for a click"), the time bound. A profile on the command line is a
Classic run, and on a Forever client it is refused, as standalone Forever always was.

**The agent's id lives in `agent_identity.json`** beside it (`Agent:IdentityFile`). Delete it and
the manager sees a new bot.

**This is the working directory benchmarks assume.** `NavmeshCorpus.SetWorkingDirectory()`
chdirs here so `DataConfig`'s relative `..\json` resolves. Moving or renaming this
project breaks the benchmark harness.

**One bot per process, one process per WoW client.** `-p` exists because `WowProcess`
otherwise picks the first matching client. The decision for the agent is one bot per PC.
