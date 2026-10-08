# ServiceHosting - what the rig's ASP.NET hosts share

The Pathing Service, the Bot Manager, the Control Center and BlazorServer each kept their own copy of
this, and the copies drifted (a fix reached one of two key guards). Plain `net10.0` on ASP.NET Core
(`FrameworkReference Microsoft.AspNetCore.App`); it references `Contracts` and `ServiceClients` only.
**Never reference it from the agent (`BotAgent`, `HeadlessServer`) or the bot**: they would drag
ASP.NET along. The pure rules stay in `ServiceClients/Security` (`ServiceKeyPolicy`, `ForwardPolicy`),
where the clients read them too; this is their one ASP.NET side.

```
ServiceKeyGuard.cs     UseServiceKey (a service: the key, this machine when its request names it,
                       TrustLoopback, no write or WebSocket from another site, no binding beyond
                       localhost without a key); UseHostCheck (a UI: a request over loopback names
                       this machine)
ForwardGuard.cs        a forwarder's door: UseForwardGuard and Forwarding (the key on, the browser's
                       origin headers off, the hop mark)
ServiceLogging.cs      AddServiceLogging: Serilog to the console and a daily file (LogFile)
ServiceProblems.cs     AddServiceProblems: problem details, 400 for a request that cannot be read
ApiResult.cs           ApiResult / ApiResult<T> and their one mapping to HTTP (ToHttp)
HubSendQueue.cs        a hub's one send loop: whoever posts never waits on a socket
```

## Things that will bite you

**This machine is trusted only when its request names it.** A request over loopback (or no remote
address: the in-memory test server) is served without the key when its Host is localhost, a loopback
address or the machine's own name. DNS rebinding points another site's name at 127.0.0.1, and that
page is then same-origin with the service: it is refused unless it carries the key (known issue 75).
`TrustLoopback=false` serves no one without the key, for a service behind a proxy or a tunnel on its
machine; `ASPNETCORE_FORWARDEDHEADERS_ENABLED` with loopback trusted logs a warning at start.

**A WebSocket's opening is a GET, and judged as a write.** SignalR takes a WebSocket without its
negotiate, from any origin; the cross-site rule let GETs through, so any page could open a hub. The
guard reads `Upgrade: websocket` (or HTTP/2's CONNECT) and refuses one from another site.

**Never await a hub send in a call another client waits on.** Post it to `HubSendQueue`: SignalR reads
a connection's next message only once its current call returned (known issue 73).
