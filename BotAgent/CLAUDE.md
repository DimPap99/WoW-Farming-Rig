# BotAgent - the resident agent's machinery, without the bot

What makes a PC's bot an agent (`docs/service-architecture.md`): its identity, the link it dials
out to the Bot Manager over, the run host (one run at a time; stop releases input first), the data
sync that pulls a run's files from the Pathing Service first, telemetry and its log stream. Plain
`net10.0`, referencing `Contracts`, `ServiceClients` and `DataConfig` (the folders the sync writes
to) - no Core, so `ServiceTests/Agent` runs it on Linux against fake runtimes. `HeadlessServer`
composes it with Core's runtimes (`IBotRuntimeFactory`, `IGameProbe`, `IClassProfileCatalog`,
`IAgentProcess`).

```
AgentIdentity.cs           the agent's id (agent_identity.json) and name
AgentOptions.cs            Agent:IdentityFile, Agent:Name, Agent:StopGraceMs, BotManager:Url/ApiKey
AgentRegistration.cs       AddBotAgent: the order hosted services start and stop in; AddDataSync
Runtime/IBotRuntime.cs     IBotRuntime, IBotRuntimeFactory, IGameProbe, IClassProfileCatalog,
                           IRunPreparation, IAgentProcess
Runtime/BotRuntimeHost.cs  one run at a time, on its own thread; stop, grace, force, abandon
Link/AgentLink.cs          dial out, back off, register on every connection, obey, report
Telemetry/TelemetryPublisher.cs   a BotSnapshot a second, the log since the last batch
Telemetry/AgentLogSink.cs  the Serilog sink the console's lines come from
Sync/DataSync.cs           IRunPreparation: a run's files from the Pathing Service, before it starts
Sync/ProfilePaths.cs       the paths a class profile names, read from its JSON without the bot
Sync/SyncState.cs          .sync/state.json: what was last synced, so a local change is told apart
```

## Things that will bite you

**A stop is a cancellation, never a kill.** The host cancels the run's token and waits: the run
releases input and returns. Only after `StopGrace` does the host call `ForceRelease` (keys only:
it must not block on the run), and after half the grace again it abandons the run: reported
`Abandoned`, then `IAgentProcess.Restart`. Never call into a run's controller from the host; two
threads toggling a bot race it back on.

**The run host stops first.** `AddBotAgent` registers it last, so the host stops it before the
telemetry and the link: the report of the run it just stopped still goes out.

**Nothing calls the agent.** `AgentLink` dials out; commands arrive as client results on its own
connection. It registers again after every reconnect, since the manager may have restarted, and
keeps the reports of runs that ended while the manager was away until one is taken.

**A refused agent waits.** A manager speaking another protocol refuses the hello with why; the link
logs it and tries again only after `RefusedRetry` (5 min).

**Telemetry reads, never writes.** `IBotRuntime.Read` is called from the publisher's thread once a
second; a runtime answers from read-only taps and must tolerate being read while it ends.

**Log lines are numbered.** The publisher sends a batch only when the link is up and advances its
cursor only once the batch went, so a console catches up after the manager was down (2000 lines
kept). Lines below Information stay in the file log.

**A run syncs before it starts.** `DataSync` is the run's preparation (state `Syncing`): a Forever
run needs its route, a Classic run its override and every path its profile names (path groups
included), and both the cost zones of the era of the client running (none without a client: the
runtime refuses next anyway). It compares each file's `SyncHash` with the service's manifest and
fetches only what differs; line endings do not count, so a Windows checkout is never rewritten for
its CRLFs. Its one line goes to the log and the console.

**The sync never deletes, and keeps what it would overwrite.** A local copy that differs from what
`.sync/state.json` says was last synced (or that was never synced) is copied to
`<data root>/.sync/backup/<time>/<set>/...` before it is replaced. A file the service does not have
stays as it is.

**Only the file an assignment names refuses a run.** With the service down, or none set
(`PathingService:Url`), a run uses this PC's copies and says so. The Forever route or the path
override missing then refuses the run with why; a profile's own paths are reported, and the bot's
loader has the last word. A stop while syncing ends the run "stopped before it started".

**A profile is read without the bot.** `ProfilePaths` takes every `PathFilename`,
`OverridePathFilename` and `PathsFilenames` at any depth, in any letter case (Newtonsoft's matching),
and reads `_pack\1-20\x.json` or `\_pack\x.json` as the library's `_pack/1-20/x.json`.
`AgentTests` pins those names to Core's properties; `ServiceTests` reads every profile in the
repository and checks every path it names exists.

**The sync's HTTP client is quieted by the host.** Without `System.Net.Http.HttpClient: Warning` and
`Polly: Fatal` in its logging, every request, and every retry and failed attempt with its stack
trace, streams to the console. Polly logs its last failed attempt at Error, so Error is not enough.
