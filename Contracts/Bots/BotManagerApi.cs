namespace Contracts.Bots;

/// <summary>
/// The Bot Manager's routes and hubs. The manager maps exactly these and its clients call exactly
/// these, so a renamed route breaks the build on both sides.
/// </summary>
public static class BotManagerApi
{
    /// <summary>The contract version of the REST routes and types in this namespace.</summary>
    public const int Version = 1;

    /// <summary>
    /// The agent protocol: what an agent sends in <see cref="AgentHello.Protocol"/>. The manager
    /// refuses an agent that speaks another, and says why.
    /// </summary>
    public const int AgentProtocol = 1;

    /// <summary>Where the manager listens unless told otherwise: the next ten after the Control Center.</summary>
    public const int DefaultPort = 5030;

    public static readonly string DefaultUrl = $"http://localhost:{DefaultPort}";

    public const string Root = "/api/v1";

    /// <summary>
    /// <c>GET</c> every bot as a <see cref="BotInfo"/>; <c>GET</c> and <c>DELETE</c> (an offline bot
    /// only) under <c>{Bots}/{agentId}</c>.
    /// </summary>
    public const string Bots = Root + "/bots";

    /// <summary><c>PUT</c> a <see cref="BotAssignment"/> or <c>DELETE</c> it: <c>{Bots}/{agentId}/{Assignment}</c>.</summary>
    public const string Assignment = "assignment";

    /// <summary><c>POST {Bots}/{agentId}/{Start}</c>: the bot starts its assignment; answers a <see cref="CommandAck"/>.</summary>
    public const string Start = "start";

    /// <summary><c>POST {Bots}/{agentId}/{Stop}</c>: the bot stops its run, releasing input first; answers a <see cref="CommandAck"/>.</summary>
    public const string Stop = "stop";

    /// <summary><c>GET {Bots}/{agentId}/{Log}</c>: the bot's latest <see cref="BotLogLine"/>s, oldest first.</summary>
    public const string Log = "log";

    /// <summary><c>GET {Bots}/{agentId}/{Runs}</c>: the bot's <see cref="RunRecord"/>s, newest first.</summary>
    public const string Runs = "runs";

    /// <summary><c>GET</c> the <see cref="ManagerHealth"/>.</summary>
    public const string Health = Root + "/health";

    /// <summary>The SignalR hub (MessagePack) agents dial out to: <see cref="AgentHubMethods"/>.</summary>
    public const string AgentHub = "/agentHub";

    /// <summary>The SignalR hub (MessagePack) that streams the fleet to the Control Center: <see cref="FleetHubMethods"/>.</summary>
    public const string FleetHub = "/fleetHub";

    /// <summary>The Swagger UI and its OpenAPI document.</summary>
    public const string Swagger = "/swagger";

    /// <summary>The longest time bound a Forever assignment may ask for.</summary>
    public const int MaxMinutes = 600;
}

/// <summary>
/// The agent hub's methods (<see cref="BotManagerApi.AgentHub"/>, MessagePack). An agent dials out,
/// calls <see cref="Register"/> on every connection, then streams; the manager reaches the agent
/// only over that connection.
/// </summary>
public static class AgentHubMethods
{
    /// <summary>From an agent: <c>(AgentHello)</c>, answered with an <see cref="AgentWelcome"/>. Again after every reconnect.</summary>
    public const string Register = "Register";

    /// <summary>From an agent: <c>(BotSnapshot)</c>, about once a second.</summary>
    public const string Snapshot = "Snapshot";

    /// <summary>From an agent: <c>(BotLogLine[])</c>, the lines logged since the last batch.</summary>
    public const string Log = "Log";

    /// <summary>From an agent: <c>(RunReport)</c>, when a run ends; kept while the manager is down and sent after it returns.</summary>
    public const string RunEnded = "RunEnded";

    /// <summary>To an agent: <c>(BotAssignment?)</c>, its assignment changed.</summary>
    public const string Assign = "Assign";

    /// <summary>To an agent: <c>(BotCommand)</c>, answered with a <see cref="CommandAck"/> (a client result).</summary>
    public const string Command = "Command";
}

/// <summary>The fleet hub's methods (<see cref="BotManagerApi.FleetHub"/>, MessagePack), all from the manager.</summary>
public static class FleetHubMethods
{
    /// <summary><c>(BotInfo)</c>: a bot registered, went offline, or its assignment changed.</summary>
    public const string BotChanged = "BotChanged";

    /// <summary><c>(string agentId)</c>: a bot was forgotten.</summary>
    public const string BotRemoved = "BotRemoved";

    /// <summary><c>(BotSnapshot)</c>: a bot's latest state.</summary>
    public const string Snapshot = "Snapshot";

    /// <summary><c>(string agentId, BotLogLine[])</c>: lines a bot logged.</summary>
    public const string Log = "Log";

    /// <summary><c>(RunRecord)</c>: a bot's run ended.</summary>
    public const string RunEnded = "RunEnded";
}
