namespace Core.Session;

// The bounded Forever trial has no XP source or long-running grind session.
public sealed class NoGrindSessionHandler : IGrindSessionHandler
{
    public void Start(string path) { }
    public void Stop(string reason, bool active) { }
}
