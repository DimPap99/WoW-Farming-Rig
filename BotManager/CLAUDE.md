# BotManager - bots, assignments, commands and live state

The Bot Manager from `docs/service-architecture.md`: the registry of bots (one per game PC, each its
resident agent), their assignments and run history, the hub agents dial out to, and the live state
it fans out to the Control Center. It plays nothing and plans nothing. Plain `net10.0`; it references
`Contracts` and `ServiceClients` only - **never `Core`, `Game`, `WinAPI`, `Frontend` or `PPather`**.

## Run

```bash
BotManager/run.sh                                    # http://localhost:5030, Swagger UI at /swagger
ApiKey=<secret> BotManager/run.sh --urls=http://0.0.0.0:5030   # for agents on other PCs
```

The port is `Contracts.Bots.BotManagerApi.DefaultPort` (5030). Its data lives in `data/` beside it
(`Data`, relative to the content root); `--LogFile=` (empty) turns the log file off.

## Layout

```
Program.cs                     host (a class in a namespace, so tests can host it beside the Pathing Service)
Hosting/BotManagerRegistration AddBotManager / UseBotManager - the composition root
Hosting/ServiceKeyGuard.cs     the key: ServiceClients' ServiceKeyPolicy, as the Pathing Service applies it
Hosting/BotManagerLogging.cs   Serilog to console + daily file
Hosting/HubSendQueue.cs        the fleet hub's one send loop: no agent waits on a listener
Registry/IBotRegistry.cs       what is kept between connections: BotRecord, run history
Registry/JsonBotRegistry.cs    data/bots.json and data/runs/<agentId>.json, written then renamed
Bots/BotFleet.cs               registry + who is online over which connection + snapshots, logs, commands
Bots/BotFleetOptions.cs        CommandTimeoutSeconds
Bots/FleetResult.cs            outcomes and their one mapping to HTTP
Bots/BotEndpoints.cs           /api/v1/health, /api/v1/bots/...
Agents/AgentHub.cs             /agentHub: Register, Snapshot, Log, RunEnded (Contracts' AgentHubMethods)
Fleet/FleetHub.cs              /fleetHub: what the Control Center follows (FleetHubMethods)
```

## Things that will bite you

**Nothing calls an agent except over the connection it opened.** A command is a SignalR client
result on the agent's connection (`Command` answers a `CommandAck`). A bot whose agent is not
connected is offline: commands to it answer 409 and are not queued. No answer within
`CommandTimeoutSeconds` (10) answers 504. SignalR reports an invocation cancelled by its token as a
`HubException` ("Invocation canceled by the server"), not as a cancellation, so `BotFleet` tells a
timeout by its own token.

**No agent call waits on the fleet's listeners.** SignalR reads an agent's next message, the answer
to a command included, only once its current hub call returned; a call that awaited the send to
every Control Center froze every agent's connection while one of them did not read (known issue 73).
`BotFleet` posts every change to `HubSendQueue`, under its lock, so listeners get the changes in the
order they happened; never `await fleet.Clients...` in an agent's call.

**An agent registers on every connection, and the newest connection wins.** A reconnect can arrive
before the old connection's end; the old connection's disconnect is then ignored. Anything an agent
sends before it registers is ignored, and the connection, not the message, says which bot sent a
snapshot.

**Live state is memory; the registry is files.** Online, the latest snapshot and the last 500 log
lines per bot vanish with the process; agents register again when it comes back, a running bot
reporting its run. The registry is written on registration, disconnect, assignment and run end,
never per snapshot, and before memory changes: a write that fails changes nothing. An assignment or a
forget whose write fails answers 500 and keeps what was; a registration or a disconnect whose write
fails is logged and counts anyway (`Keep`), written with the next save that succeeds. A run report is
kept once per run id: agents send them at least once. A file that does not parse is renamed aside
(`.unreadable-<time>`) and the manager starts without it; one that cannot be read at all (a lock, its
rights) stops the start, since starting empty would write over every other bot (known issue 72).
Agent text is capped (`MaxMessageLength`), and the manager's log escapes its line breaks.

**The key is the Pathing Service's rules.** This machine (loopback, or no remote address at all, as
an in-memory test server has) is always served. Another machine needs `ApiKey` in the `X-Api-Key`
header, and without a configured key it is refused. A manager bound beyond localhost without a key
stops right after it starts. The rules are `ServiceClients/Security/ServiceKeyPolicy`, shared by every
service and client.

**The wire is MessagePack.** Contracts' records go through it as they are: arrays, not lists, and
positional records. An agent's hello and log batches exceed SignalR's 32 KB default, so the hub takes
1 MB (`MaxAgentMessageBytes`).

**Tests host it in memory.** `ServiceTests/Bots`: `BotManagerFactory` puts the data in a temporary
folder, `FakeAgent` is an agent over the test server (long polling, MessagePack) and `FleetListener`
follows the fleet hub. `RemoteAddress` makes a request look as if it came from another machine.
