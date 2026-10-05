# BotAgent - the resident agent's machinery, without the bot

What makes a PC's bot an agent (`docs/service-architecture.md`): its identity, the link it dials
out to the Bot Manager over, the run host (one run at a time; stop releases input first), telemetry
and its log stream. Plain `net10.0`, referencing `Contracts` and `ServiceClients` only - no Core, so
`ServiceTests/Agent` runs it on Linux against fake runtimes. `HeadlessServer` composes it with
Core's runtimes (`IBotRuntimeFactory`, `IGameProbe`, `IClassProfileCatalog`, `IAgentProcess`).

```
AgentIdentity.cs           the agent's id (agent_identity.json) and name
AgentOptions.cs            Agent:IdentityFile, Agent:Name, Agent:StopGraceMs, BotManager:Url/ApiKey
AgentRegistration.cs       AddBotAgent: the order hosted services start and stop in
Runtime/IBotRuntime.cs     IBotRuntime, IBotRuntimeFactory, IGameProbe, IClassProfileCatalog,
                           IRunPreparation, IAgentProcess
Runtime/BotRuntimeHost.cs  one run at a time, on its own thread; stop, grace, force, abandon
Link/AgentLink.cs          dial out, back off, register on every connection, obey, report
Telemetry/TelemetryPublisher.cs   a BotSnapshot a second, the log since the last batch
Telemetry/AgentLogSink.cs  the Serilog sink the console's lines come from
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
