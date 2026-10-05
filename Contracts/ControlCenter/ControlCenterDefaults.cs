namespace Contracts.ControlCenter;

/// <summary>
/// Where the Control Center listens unless told otherwise. The central services step by ten:
/// the Pathing Service is on <see cref="Pathing.PathingApi.DefaultPort"/>, the Control Center
/// on the next.
/// </summary>
public static class ControlCenterDefaults
{
    public const int DefaultPort = 5020;

    public static readonly string DefaultUrl = $"http://localhost:{DefaultPort}";
}
