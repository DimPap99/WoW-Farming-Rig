namespace Contracts;

/// <summary>
/// The shared key the rig's services take from other machines. A request from this machine is
/// trusted; one from another machine is served only with the key, and without a configured key a
/// service serves this machine only, and refuses to start bound beyond it.
/// </summary>
public static class ServiceKey
{
    /// <summary>The request header a client sends the key in, on every call and hub connection.</summary>
    public const string Header = "X-Api-Key";

    /// <summary>The configuration key a service reads its key from, and a client's fallback for its own.</summary>
    public const string ConfigKey = "ApiKey";
}
