using BotAgent;
using BotAgent.Runtime;
using BotAgent.Telemetry;

using Contracts.Bots;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Events;

using ServiceClients.Bots;

using SimAgent;

// A bot agent with nothing to play: it registers with the Bot Manager, streams made-up snapshots and
// log lines, takes assignments and obeys Start and Stop, as HeadlessServer --agent does with a game.
// Run two or three, each with its own identity file and name:
//   dotnet run --project Utilities/SimAgent -- --Agent:Name=Sim-1 --Agent:IdentityFile=sim-1.json
// --BotManager:Url (default http://localhost:5030), --Sim:Client (forever or som, default forever),
// --Sim:ClickSeconds (how long a run "waits for a click", default 5).
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration[BotManagerOptions.UrlKey]))
{
    builder.Configuration[BotManagerOptions.UrlKey] = BotManagerApi.DefaultUrl;
}

AgentLogSink sink = new();
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Sink(sink)
    .CreateLogger();
builder.Logging.ClearProviders().AddSerilog(Log.Logger, dispose: true);

builder.Services.AddSingleton(SimOptions.From(builder.Configuration));
builder.Services.AddBotAgent(AgentOptions.From(builder.Configuration), sink);
builder.Services.AddSingleton<IBotRuntimeFactory, SimRuntimeFactory>();
builder.Services.AddSingleton<IGameProbe, SimGame>();
builder.Services.AddSingleton<IClassProfileCatalog, SimGame>();
builder.Services.AddSingleton<IAgentProcess, SimGame>();

builder.Build().Run();
