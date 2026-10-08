# Phase 3 review - the fixes: plan and handoff

**Status (2026-10-08):** step 1 (this plan, known issues 63 to 77) is written. A max-level review
of Phase 3 (its eleven commits in 4e65fd27..e71c5781: the route library, `ForeverRun`, the Bot
Manager, the agent, the fleet pages and the sync), checked against ef51cc7d, found 16 medium and
about 40 low findings and nothing high, and 17 design findings. None was fixed at ef51cc7d: the
agent, manager and fleet code had not changed since Phase 3. The review's full report (every
verdict, probe and location, line numbers at e71c5781) is outside the repository, at
`~/RiderProjects/WoW-Farming-Rig-review/phase3/phase3-review.md` on the machine it ran on.

## Decisions (2026-10-08)

1. **Scope:** record the findings, fix the bugs, then the design findings, in that order, on
   `fix/phase3-review` (from `feature/forever-world-data` at ef51cc7d, where the findings were
   checked; several fixes touch code only that branch has). One commit per fix, nothing pushed.
2. **Two agents with one id (item 74):** an id online from another machine is refused, naming that
   machine. A reconnect from the same machine still replaces its old connection, and one agent per id
   runs on a PC.
3. **The hosts' copied plumbing (design 1):** a new `ServiceHosting` project, an ASP.NET Core
   library referencing `Contracts` and `ServiceClients`, used by the Pathing Service, the Bot Manager,
   the Control Center and BlazorServer; the root `CLAUDE.md`'s reference rule names it.

Taken as the obvious choice, each open to change:

4. **HeadlessServer never deletes the addon's configuration** (item 66); BlazorServer still does,
   since its configuration page needs it.
5. **A repeating route with nothing left to do ends Complete** (item 69), saying every step is
   already done.
6. **The agent keeps its console window** (item 64): the console's writes go through a queue rather
   than the window being hidden, so a person can still read it and press Ctrl+C.
7. **A name ends in `.json` exactly** (item 70): no file in `Json/path`, `Json/class` or
   `Json/road` ends otherwise.
8. **A registry that cannot be read for an I/O error stops the manager's start** (item 72): only
   unreadable JSON is kept aside and the manager starts without it.

## How to work

As in [phase1-fixes-plan.md](phase1-fixes-plan.md):
- One commit per fix, its message in the log's style, with the co-author line.
- Every bug fix gets a test that fails on the old code, run once against it (or with the fix taken
  out) before the fix goes in. Where the old code crashes the process or hangs, the failing run is a
  probe outside the suite, and the test asserts the fixed behaviour with a deadline.
- `dotnet test` (ServiceTests and AgentTests) and `dotnet build WoWFarmingRig.sln` before each commit.
- Limits are `public const` fields in the class that owns them.
- A fix that lands in code a refactor merges waits for the refactor, so it is written once (item 75
  after `ServiceHosting`; the name parts of items 68 and 70 after the one name rule).
- Windows-only behaviour (window close, logoff, QuickEdit, the probe, DXGI) is built and unit-tested
  here, and goes on the Windows checklist below.
- When a fix lands, its known-issues item moves to "Fixed" with the commit, or is rewritten to say
  what is left.

## Step 2 - the bugs

In this order, most severe first, except where a fix needs another first.

### 2.1 The agent's crash and its silence (item 63)

- **Fix:** `WowProcess` polls on a background thread with a token source of its own, linked to the
  one it is given; `Dispose` cancels it and joins the thread, so the container that built it ends it.
  A poll that throws (the process gone, WMI failing) marks the client not running and polls again.
  `AddWoWProcess` disposes its probe's container instead of cancelling a token, and `ForeverRun`
  disposes the `WowProcess` it makes. `HeadlessServer/Program` logs an unhandled exception (Fatal,
  flushed) again.
- **Test:** the poll thread's life as a small class of its own (`Game/WoWProcess`): it runs until
  disposed, outlives the disposal of the token source it was given, survives a tick that throws, and
  is joined by `Dispose` (`AgentTests`). The crash itself is shown by a probe outside the suite (the
  old pattern exits 134, the new one does not).

### 2.2 The console cannot hold the bot (item 64)

- **Fix:** `HeadlessLogging` wraps the console sink in `Serilog.Sinks.Async` (bounded, dropping when
  full); the file, the debugger and the agent's stream are unchanged.
- **Test:** a logger built as `HeadlessLogging` builds it, over a console sink that blocks: a hundred
  lines return at once, and arrive once it unblocks (`AgentTests`).

### 2.3 A clean stop whichever way the agent is stopped (item 65)

- **Fix:**
  - HeadlessServer registers SIGHUP (CTRL_CLOSE on Windows: the window closing) and, on Windows, the
    session ending (`Microsoft.Win32.SystemEvents.SessionEnding`). Each stops the host and holds the
    signal until the host has stopped or a bound passes, so the run releases input before Windows
    ends the process.
  - `run-agent.ps1` and `run.bat` run the built `HeadlessServer.dll` with `dotnet`, not `dotnet run`,
    whose job kills the agent the moment `dotnet.exe` ends.
  - The docs (`HeadlessHost`, `Program`, `ResidentHost`, the scripts, `HeadlessServer/CLAUDE.md`,
    item 21) say what stops cleanly: Ctrl+C, closing the window, a logoff; and that Task Scheduler's
    End is a kill.
- **Test:** the signal handler against a host (`AgentTests`): it stops the host and returns once it
  stopped, or at its bound. Live on Linux: `kill -HUP` ends an agent with exit 0, after "Application
  is shutting down". Windows: the checklist.

### 2.4 A Classic start that never deletes (item 66)

- **Fix:** `AddWoWProcess(log, out isForever, out problem, keepConfiguration)`. The check becomes a
  function of what it read (the client rect, the install version, the two files) that answers what
  does not match in words a person can act on ("the WoW window is minimized: restore it", "the window
  is 1280x720 and frame_config.json was made at 1920x1080"). BlazorServer deletes on a mismatch as
  before; HeadlessServer (runs and `--loadonly`) never does and refuses with the reason. `--loadonly`
  refuses without both files, through the same check `TryCreate` uses.
- **Test:** the mismatch function: minimized, resized, another addon version, too few frames, a
  missing install (`AgentTests`). That nothing is deleted needs a client: the checklist.

### 2.5 A probe that fails stops nothing (item 67)

- **Fix:** `WowGameProbe` answers null for any failure and keeps the reason, which the factory's
  refusal names ("WoW's executable could not be read: it may run as administrator"). `ReadInfo`
  refuses an empty path instead of reading `Wow.exe` in the working directory. `TelemetryPublisher`
  logs a failed tick (once until it succeeds again) and goes on. The probe's interval is measured with
  `TimeProvider.GetTimestamp`.
- **Test:** an agent whose probe throws `FileNotFoundException` keeps its host and its run, and
  keeps sending snapshots (`ServiceTests/Agent`); a clock set back does not stop the probe.

### 2.6 A repeating route with nothing left to do (item 69)

- **Fix:** `ForeverAgenda.PassDone(isDone)` passes done tasks at most once round the list and says
  whether every task was done; `ForeverTaskActivity.Update` uses it, and a repeating agenda whose
  every task is done ends (`ForeverAgenda.End`), so the run ends Complete with "every step is already
  done". `ForeverRouteFile.TryUntil` takes `Minutes` only as a finite number above 0. `ForeverRun`
  reads the autonomous goal's state before it turns the bot off.
- **Test:** the agenda with a repeating route whose steps are all met ends after one lap; one with a
  step left keeps it current; NaN, Infinity and 1e400 minutes are refused (`AgentTests/Forever`,
  `ServiceTests/Forever`).

### 2.7 One malformed route file (item 70, without the name rule)

- **Fix:** `TryParse` refuses an empty step ("step 2 is empty: a stray comma?"); the library
  describes a file whose parse throws as refused with the exception's message, so the list, a get
  and a check answer, and `Save` refuses it with 400.
- **Test:** `"Steps":[null]`, `[a,,b]` and `[,a]` are refused with the step's number; a library
  folder holding one such file still lists the others (`ServiceTests`).

### 2.8 Case twins (item 71)

- **Fix:** `ForeverRouteLibrary.Save` and `RecordedPathStore.Save` refuse a name that another file of
  the folder has in another case (409, naming it). `DataSync` builds its manifest knowing the twins:
  on a case-insensitive file system a named file with a twin refuses the run, naming both, and an
  unnamed one is skipped with a note. The designer (`ForeverLibrary.razor`) replaces a route under
  its stored name.
- **Test:** the service refuses `route.json` beside `Route.json`; the sync, told the file system
  ignores case, refuses a run on a twin and leaves the PC's file alone (`ServiceTests`).

### 2.9 The registry keeps its bots (item 72)

- **Fix:** `JsonBotRegistry.Read` keeps aside only what does not parse; an I/O error throws. `Save`,
  `Remove` and `AddRun` write first and change memory after. `AddRun` replaces a report with the same
  `RunId`. `BotFleet` logs a registration's or a disconnect's failed write and goes on, and answers a
  failed assignment save with 500 "not saved" and the old assignment kept. `AssignmentRules.IsAgentId`
  refuses Windows device names. Agent text is capped (`BotFleet.MaxMessageLength`) and its line breaks
  escaped in the manager's log.
- **Test:** `bots.json` that is a folder stops the registry's start; a read-only data folder leaves
  memory as it was and the assignment as before; a report sent twice is kept once; `CON` is not an id
  (`ServiceTests/Bots`).

### 2.10 One send loop for the fleet hub (item 73)

- **Fix:** `FleetBroadcast`, one loop that sends what the fleet posts, in order, to the fleet hub;
  `BotFleet` posts under its lock, so the order is the order of the changes, and never awaits a send.
- **Test:** a fleet hub whose send never returns: a snapshot, a registration and a command still
  complete at once (`ServiceTests/Bots`).

### 2.11 One agent per id (item 74)

- **Fix:**
  - `BotFleet.RegisterAsync`: an id online from another machine is refused, naming it; from the same
    machine the new connection takes over and the old one is aborted (its `HubCallerContext`). A
    connection registers one id.
  - A snapshot, a log or a report from a connection that is not the bot's own fails with a
    `HubException`, so `AgentLink` keeps the report for its next connection.
  - A hello without a client keeps the one last seen.
  - HeadlessServer loads its identity once, before its first host, and holds a named mutex for it
    while it runs: a second agent with the same id on this PC ends with an error saying so.
- **Test:** two fake agents with one id on two machines: the second is refused; on one machine, the
  first connection is closed; a report on a displaced connection fails; a mutex claimed twice is
  refused the second time (`ServiceTests/Bots`, `AgentTests`).

### 2.12 The agent's run host (item 76)

- **Fix:**
  - `BotRuntimeHost`: a final read of the runtime before it is disposed, and the runtime taken out of
    telemetry's reach first; `RunEnded` raised before the run's end is signalled; the abandon checks
    again that the run did not end; Start refused once the host is stopping.
  - `AgentOptions.StopGrace` capped (`MaxStopGraceMs`), and the host's shutdown timeout derived from
    it (grace, release, abandon, the last report).
  - `HeadlessHost` starts the command line's profile only in the first host it builds;
    `StartFromCommandLine` records the run's id before it can end.
  - `AgentIdentity` makes a new id only when the file is missing or holds none; an I/O error throws.
  - `AgentLogSink` keeps the cursor of what was sent, as it outlives the hosts.
- **Test:** each in `ServiceTests/Agent` (the fake runtime gains a "disposed" state that `Read`
  honours) and `AgentTests` (`ResidentHostTests`, `HeadlessCommandLineTests`).

### 2.13 A stop while the controller is built (item 23)

- **Fix:** `BotController`'s constructor waits with its token (`Wait.Update(timeout, token)`), so a
  cancelled token ends the wait with `OperationCanceledException`. `ClassicRuntime` cancels the
  composition's token on a stop only while the controller is built; `ForeverRun` already uses one
  token for both.
- **Test:** `Wait.Update` with a cancelled token returns at once (`AgentTests`); the rest needs a
  client: the checklist.

### 2.14 The Classic run's paths (item 68, without the name rule)

- **Fix:** `ClassicRuntime.TryCreate` reads, before it attaches, every path the run needs
  (`ProfilePaths`, the override in place of the first) and refuses one that is missing or not a
  recorded path. `BotController.LoadPathProfile` returns whether it loaded, and the run refuses when
  it did not; `CreateSession` builds the new session before it lets the old one go.
- **Test:** `TryCreate` refuses a missing profile path and a malformed override before anything
  attaches (`AgentTests/Agent/RuntimeRefusalTests`).

### 2.15 Smaller findings (item 77)

Each with a test where one can fail:
- `HeadlessArguments`: a value after `--Section:Key` may start with a single `-`.
- `Program` keeps the window open after a standalone run that failed, when its console is
  interactive.
- `ForeverRuntime` refuses when Forever's map data cannot be read.
- `install-agent-task.ps1` sets priority 4 (normal); `run-agent.ps1` logs with `Add-Content` that
  cannot end the loop, and times runs with a stopwatch.
- `RunOptions`' missing profile shows the usage; the dead logging override goes.
- `BotManagerStatus` takes only the manager's health (a body with its agent protocol);
  `BotCommands` keeps Stop on while a Start is in flight; `BotLog` and `RunHistory` read again when
  their first read failed, at the next status change; `FleetFeed` clears a bot's run on `RunEnded`;
  `AssignmentForm` builds its draft before its first await; `ForeverRoutes` marks saved only the text
  it sent; the fleet's hint names the manager's address as a game PC must dial it; `RunHistory` keeps
  at most `JsonBotRegistry.MaxRuns`; `BotFormat.Place` names a zone only for a bot on the Pathing
  Service's client.
- `DataSync` checks a fetched file's size and hash against the manifest, `ServiceHttp.GetBytesAsync`
  caps an answer (`MaxFileBytes`), and a manifest with a null entry is the service refusing the set.

## Step 3 - the design findings

From the review's design and altitude angles. Each moves or merges code that works, so the existing
tests guard it; a fix that lands in the merged code comes with it.

1. **`ServiceHosting`** (decision 3). The key guard (one copy, and item 75's fixes in it), the
   services' logging (`AddServiceLogging` with SharedLib's one template, and the short source
   computed from the context), one result type with its HTTP mapping (`StoreResult` and
   `FleetResult`, merged as `ApiResult` with `Timeout`), the forward check of both forwarders, and the
   handler that answers 400 to a request that cannot be bound (item 46's last bullet). About 250
   lines go.
2. **One name rule.** The segment rule, the extension (`.json` exactly), the length and `IsUnder` live
   once in `ServiceClients` (`LibraryName`); `PathName`, `ForeverRouteLibrary.TryResolve`,
   `AssignmentRules` and `DataSync.LocalFile` use it. Closes the name parts of items 68 and 70.
3. **One client stack.** `ServiceOptions.Read(configuration, position, defaultUrl)` and one
   `AddServiceClient<TClient, TImplementation>` with the retry rule item 37 gave the pathing client;
   a command is never repeated. Closes item 46's client half.
4. **Delegates for the one-method interfaces.** `IGameProbe`, `IClassProfileCatalog`, `IAgentProcess`,
   `IRunPreparation` and `IBotRuntimeFactory` become delegates in `BotAgent`, the factory's returning
   `(IBotRuntime? Runtime, string? Refusal)`; `AgentProcess` moves into `BotAgent`, so the simulated
   agent restarts the same way; the fleet feed takes a `Func<HubConnection>`; `BotFleet` takes
   `JsonBotRegistry`. `IBotRuntime` and the typed HTTP clients stay interfaces.
5. **One hub loop.** "Dial a hub with the key, stay connected, back off with jitter" as
   `ServiceHubs.Build` and `ServiceHubs.StayConnectedAsync` in `ServiceClients`, for `AgentLink`,
   `FleetFeed` and `WatchFeed`.
6. **`AtomicFile` everywhere.** `JsonBotRegistry` and `AgentIdentity` use it.
7. **The agent's data folder loaded once** and injected (`DataConfig` as a singleton): the sync, the
   runtime factory and the profile list stop reading the working directory per call, and tests can
   give their own.
8. **A run's result as a value.** `ForeverRun.Run` returns a `ForeverRunResult` (exit code and
   message), so `ForeverRuntime` stops reading its own printed lines; a runtime fills `BotRunResult`'s
   progress as it ends, so a report no longer depends on telemetry's last read.
9. **The stuck-run machinery** shrinks with 2.13: what is left of it is for a run that ignores its
   token, not for the controller's wait.
10. **`ResidentHost`** records a fault at `ApplicationStopping` (a background service already faulted
    or cancelled), which replaces the five-flag `Decide` table's guesswork, and the latent canceled
    service (the review's V37) counts as a fault.
11. **The Control Center tracks the manager once.** Online and the counts come from the fleet feed;
    `BotManagerStatus`'s 2 s poll goes, and with it item 30's manager half.
12. **Log lines carry their number.** `BotLogLine` gets the agent's line number and a per-process
    epoch (agent protocol 2), so the manager drops a resent batch and the console's buffer keys lines
    by number instead of time and text.
13. **The assignment push goes.** `AgentWelcome.Assignment`, `AgentHubMethods.Assign` and
    `BotRuntimeHost.Assignment` are write-only: a start carries its assignment (agent protocol 2, with
    12).
14. **`ForeverRunRequest` holds the command line's rules** (a Roam with no kills, a point off its map,
    a field of another mode), so an assignment and the trial runner refuse alike.
15. **One owner per rule:** the 600-minute bound (`BotManagerApi.MaxMinutes`), the Forever client's
    name (`GameClients.Forever`), the route listing.
16. **The client probe is cheap:** the process's path is cached by its id and asked for with
    `WHERE ProcessId = n`; nothing is probed while a run knows its client.
17. **Also:** the unused members (`TelemetryPublisher.Latest`, `AgentLogSink`'s minimum,
    `ForeverRun.Request` and `Trial`); `ExitRefused` means one thing; the three conventions of the
    agent's state files become the contract's JSON.

## Windows checklist additions

For the Phase 3 checklist in [phase3-plan.md](phase3-plan.md), with a game where it says so:

1. **The window closes:** with a Classic run moving, close the agent's window. The character stops
   (keys released), the console shows "Application is shutting down", and the fleet shows the run
   Stopped.
2. **A logoff:** the same with a sign-out (the run is reported Stopped, or nothing is held when
   WoW closes first).
3. **QuickEdit:** click into the agent's window during a run (Windows 10 console). The bot plays on,
   and a Stop from the Control Center stops it at once.
4. **Minimized:** minimize WoW and start a Classic assignment. It is refused with "the WoW window is
   minimized", and `frame_config.json` is still there; restored, the next start runs.
5. **A locked PC:** lock it and start a Classic assignment. It is refused, and the agent stays up (no
   crash, no restart in `run-agent.log`).
6. **As administrator:** WoW run as administrator, the agent not: the agent stays online and refuses a
   run with the reason.
