# HeadlessServer — the bot without a UI, and the PC's resident bot agent

Same bot as `BlazorServer`, minus the web front end, on the Generic Host. A class profile on the
command line runs at once, as it always has; `--agent` keeps the process as this PC's bot agent
(`docs/service-architecture.md`): it dials out to the Bot Manager, runs one assignment at a time
(Classic or WoW: Forever), pulls each run's files from the Pathing Service first, and streams
telemetry. A logon task keeps it running (`install-agent-task.ps1`). It is also the working
directory benchmarks chdir into.

```
Program.cs                    config, Serilog, CommandLineParser, then the host; ends the process
HeadlessArguments.cs          the command line's two readers apart (configuration keys / options)
RunOptions.cs                 the CLI surface
DependencyInjection.cs        StartupConfig options from RunOptions (a Classic run's composition)
Hosting/HeadlessHost.cs       the Generic Host: BotAgent, its data sync, and the runtimes below
Hosting/ResidentHost.cs       runs the host; an agent's is built again after a failure; exit codes
Hosting/HeadlessLogging.cs    Serilog: file, debug, console (behind a queue), and the agent's log stream
Hosting/StopOnSignals.cs      the window closing and the session ending stop the host, held until it stopped
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
run-agent.ps1                 the agent in a loop: started again unless it exits 0 (run-agent.log)
install-agent-task.ps1        the logon task that runs run-agent.ps1; uninstall-agent-task.ps1 removes it
```

## Running

```powershell
.\build.bat
.\run.bat Warrior_1.json -m Local -p <wow-pid>                      # as always: runs, Ctrl+C stops it
.\run.bat --agent --manager http://botbox:5030 --name "PC 1"       # resident agent
.\run.bat --agent --manager http://botbox:5030 --pathing-service http://botbox:5010   # syncing runs first
powershell -ExecutionPolicy Bypass -File .\install-agent-task.ps1    # the agent at every logon
```

The logon task runs `run-agent.ps1`, which runs the Release build's `HeadlessServer.dll` with
`dotnet` in a loop: build first. Its settings are this folder's `headless_appsettings.json`;
`-Manager`, `-PathingService` and `-Name` put them on the agent's command line instead. Ctrl+C in
the agent's window, closing it, or a logoff stops it (keys released first) and ends the loop;
Task Scheduler's End terminates it without that. `uninstall-agent-task.ps1` removes the task and
leaves a running agent alone.

**The class profile is positional, not an option.** There is no `-c`/`--classconfig`; it comes
first and bare, and it is needed unless `--agent` is given. Passing `-c Warrior_1.json` fails to parse.

`run.bat` runs the Release build directly (`dotnet bin\Release\<tfm>\HeadlessServer.dll`), so
**build first** or you run a stale binary. Never through `dotnet run`: it puts the agent in a job
that ends it the moment `dotnet.exe` ends, which closing the window ends first, before the agent
releases the game's keys (known issue 65).

## Options

Long forms take two dashes (`--mode`); `CommandLineParser` rejects `-mode`.

| flag | meaning |
|---|---|
| *(positional, arg 0)* | class profile from `Json\class\` (e.g. `Warrior_1.json`), started at once — needed unless `--agent` |
| `--agent` | stay resident: run what the Bot Manager assigns; a profile given too starts at once |
| `--manager <url>` | the Bot Manager (`BotManager:Url`); none: the agent runs on its own |
| `--name <name>` | what the manager calls this agent (`Agent:Name`); the machine name by default |
| `--pathing-service <url>` | the Pathing Service runs sync from first (`PathingService:Url`); none: this PC's copies |
| `-m, --mode` | `Local` (default) / `RemoteV1` / `RemoteV3` |
| `-p, --pid` | WoW process id — needed when several clients run |
| `-r, --reader` | screen reader backend |
| `--hostv1/--portv1`, `--hostv3/--portv3` | remote pathing endpoints |
| `-n, --viz` | path visualizer |
| `-g, --gpu` | GPU capture |
| `-t/-s/-v` | targeting / skinning / target-vs-add overlays |
| `--loadonly` | load the class config, report, exit — no bot |

The key for a manager or a Pathing Service on another PC is never on the command line: `ApiKey` (or
`BotManager:ApiKey`, `PathingService:ApiKey`) in `headless_appsettings.json` or the environment.

## Things that will bite you

**Config file is `headless_appsettings.json`, not `appsettings.json`.** Copying settings
from `BlazorServer` into the wrong filename here silently changes nothing.

**Configuration keys and options are two readers, split before either sees them.** A
`--Section:Key=value` (or `--Section:Key value`) argument goes to the configuration only
(`HeadlessArguments`); everything else goes to `CommandLineParser`. After a key, a negative number
(`-1`, `-.5`) is its value, and anything else starting with `-` is an option (known issue 77). Before the split,
CommandLineParser refused such a key (`UnknownOptionError`), so `--Pathing:Engine=Navmesh` never
worked although this file said it did.

**HeadlessServer never deletes the addon's configuration.** `AddWoWProcess`'s probe deletes
`frame_config.json` (and the addon's config) when it does not match the client, which BlazorServer
needs to open its configuration page; HeadlessServer passes `keepConfiguration: true` and refuses the
run with what does not match, since nobody is at the PC and a minimized window is gone the next time
(known issue 66).

**Core is composed per run, never at startup.** `WowProcess` throws without a client, and
`BotController`'s constructor waits until the addon reads the player's class. So the host starts
with no game at all (it runs on Linux, refusing every run: "no WoW client is running"), and each
run composes Core for the client the probe found, then disposes it.

**Stop releases input first.** A stop (a command, Ctrl+C, the window closing, a logoff, the host
stopping) cancels the run's token; the run turns the bot off, which releases what it holds, and only then is torn down.
A run that has not ended after `Agent:StopGraceMs` (10 s, at most 60) has its keys released from outside; one
that still does not end is abandoned and the process exits with 75, which `run-agent.ps1` starts
again. See `Game/CLAUDE.md` for why the order matters.

**The console is written behind a queue.** A click into the Windows 10 console starts a selection
that blocks every write to it, which held the bot's threads in their log calls (known issue 64), so
`HeadlessLogging` queues the console's lines (`Queued`) and drops them past 10,000; the file is
written as before. Keep a new console sink behind it.

**An agent's host is built again; its process is started again.** `ResidentHost` decides after each
host: a stop (Ctrl+C, the window closing, a logoff) ends the process with the host's code; a failure
(the host threw before it was asked to stop, or a background service faulted) builds a new host after
3 s in the same process, or, with a run still under way, ends it with 75, since only a new process
takes a run's threads. On .NET 10 a faulted `BackgroundService` stops the host and `Run()` returns
as after a stop, exit code 0 (.NET 11 changes this), so faults are read from the services'
`ExecuteTask`; without that, a broken link would end the agent with 0 and `run-agent.ps1` would not
start it again. A profile run without `--agent` is never built again: a failure exits 1.

**Every run syncs first, when a Pathing Service is set.** With `PathingService:Url` (or
`--pathing-service`), a run, an assignment or a profile on the command line alike, pulls its files
into `../Json` before it starts (BotAgent's `DataSync`): in a git checkout they then show as local
changes. `Json/.sync/` (gitignored) holds what was synced and the local changes kept aside. The
logging overrides for `System.Net.Http.HttpClient` and `Polly` in `headless_appsettings.json` keep
its HTTP client out of the console; keep them when copying settings around.

**Forever runs only as an agent assignment.** A Forever route goes through Core's `ForeverRun`
with the trial runner's gates: the verified profile, the preflight, a click into the game window
(the console shows "waiting for a click"), the time bound. A profile on the command line is a
Classic run, and on a Forever client it is refused, as standalone Forever always was.

**The agent's id lives in `agent_identity.json`** beside it (`Agent:IdentityFile`). Delete it and
the manager sees a new bot. One process holds an id on a PC (`AgentIdentity.Claim`, a machine-wide
mutex): a second `--agent` with it ends with an error, and a profile run beside an agent runs
without the manager (known issue 74). A folder copied to another PC brings the id with it, and the
manager refuses the second PC while the first is online: delete the file there.

**This is the working directory benchmarks assume.** `NavmeshCorpus.SetWorkingDirectory()`
chdirs here so `DataConfig`'s relative `..\json` resolves. Moving or renaming this
project breaks the benchmark harness.

**One bot per process, one process per WoW client.** `-p` exists because `WowProcess`
otherwise picks the first matching client. The decision for the agent is one bot per PC.
