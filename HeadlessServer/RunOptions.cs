using CommandLine;

using SharedLib;

namespace HeadlessServer;

public sealed class RunOptions
{
    [Value(0,
        MetaName = "ClassConfig file",
        Required = false,
        HelpText = "ClassConfiguration file found in 'Json\\class\\', started at once; required unless --agent\nexample: Warrior_1.json")]
    public string? ClassConfig { get; set; }

    [Option("agent",
        Required = false,
        Default = false,
        HelpText = "Stay resident as this PC's bot agent: dial out to the Bot Manager (--manager, or BotManager:Url) " +
        "and run what it assigns. A class profile given too starts at once.")]
    public bool Agent { get; set; }

    [Option("manager",
        Required = false,
        HelpText = "The Bot Manager to dial out to (BotManager:Url), for example http://botbox:5030")]
    public string? Manager { get; set; }

    [Option("name",
        Required = false,
        HelpText = "What the Bot Manager calls this agent (Agent:Name); the machine's name unless given")]
    public string? Name { get; set; }

    [Option('m',
        "mode",
        Required = false,
        Default = StartupConfigPathing.Types.Local,
        HelpText = $"Navigation services: \n{nameof(StartupConfigPathing.Types.Local)}\n{nameof(StartupConfigPathing.Types.RemoteV1)}\n{nameof(StartupConfigPathing.Types.RemoteV3)}")]
    public StartupConfigPathing.Types Mode { get; set; }

    [Option('p',
        "pid",
        Required = false,
        Default = -1,
        HelpText = $"World of Warcraft Process Id")]
    public int Pid { get; set; }

    [Option('r',
        "reader",
        Required = false,
        Default = AddonDataProviderType.DXGI,
        HelpText = $"Screen reader backend. " +
        $"'{nameof(AddonDataProviderType.DXGI)}': DirectX based, works from Win8. " +
        $"'{nameof(AddonDataProviderType.WGC)}': Windows Graphics Capture, supports background capture (Win10 2004+).")]
    public AddonDataProviderType Reader { get; set; }

    [Option("hostv1",
        Required = false,
        Default = "localhost",
        HelpText = $"Navigation Remote V1 host")]
    public string? Hostv1 { get; set; }

    [Option("portv1",
        Required = false,
        Default = 5001,
        HelpText = $"Navigation Remote V1 port")]
    public int Portv1 { get; set; }

    [Option("hostv3",
        Required = false,
        Default = "127.0.0.1",
        HelpText = $"Navigation Remote V3 host")]
    public string? Hostv3 { get; set; }

    [Option("portv3",
        Required = false,
        Default = 47111,
        HelpText = $"Navigation Remote V3 port")]
    public int Portv3 { get; set; }

    [Option('d', "diag",
        Required = false,
        Default = false,
        HelpText = $"Capture Screenshot for Diagnostics")]
    public bool Diagnostics { get; set; }

    [Option('o', "overlay",
        Required = false,
        Default = false,
        HelpText = $"Show NpcNameFinder Overlay")]
    public bool OverlayEnabled { get; set; }

    [Option('t', "otargeting",
        Required = false,
        Default = false,
        HelpText = $"Show NpcNameFinder Overlay for Targeting")]
    public bool OverlayTargeting { get; set; }

    [Option('s', "oskinning",
        Required = false,
        Default = false,
        HelpText = $"Show NpcNameFinder Overlay for Skinning")]
    public bool OverlaySkinning { get; set; }

    [Option('v', "otargetvsadd",
        Required = false,
        Default = false,
        HelpText = $"Show NpcNameFinder Overlay for Target vs Add")]
    public bool OverlayTargetVsAdd { get; set; }

    [Option('n', "viz",
        Required = false,
        Default = false,
        HelpText = $"Show path visualization while RemoteV1 is available. " +
        $"Can render RemoteV3 paths as well.")]
    public bool PathVisualizer { get; set; }

    [Option('g', "gpu",
        Required = false,
        Default = false,
        HelpText = "Use GPU compute shader for NPC name finding")]
    public bool UseGpu { get; set; }

    [Option("loadonly",
        Required = false,
        Default = false,
        HelpText = $"Loads the class profile then exists")]
    public bool LoadOnly { get; set; }

    /// <summary>Why these options cannot run, or null: a class profile, or --agent, is needed.</summary>
    public string? Problem => ClassConfig is null && !Agent
        ? "Give a class profile (Warrior_1.json), or --agent to stay resident and run what the Bot Manager assigns."
        : LoadOnly && ClassConfig is null ? "--loadonly needs a class profile." : null;

    /// <summary>What the options set in the configuration: the manager and the agent's name.</summary>
    public IEnumerable<KeyValuePair<string, string?>> ConfigurationValues()
    {
        if (!string.IsNullOrWhiteSpace(Manager))
        {
            yield return new(ServiceClients.Bots.BotManagerOptions.UrlKey, Manager);
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            yield return new(BotAgent.AgentOptions.NameKey, Name);
        }
    }
}
