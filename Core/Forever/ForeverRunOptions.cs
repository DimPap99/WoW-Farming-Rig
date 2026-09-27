namespace Core.Forever;

/// <summary>Session policy, separate from target, movement, combat and loot logic.</summary>
public sealed record ForeverRunOptions
{
    public bool StopWhenNoTargets { get; init; }
    public int? MaxCompletedCycles { get; init; }
}
