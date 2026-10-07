using SharedLib.Forever;

using System.Collections.Generic;

namespace Core.Forever;

/// <summary>Session policy, separate from target, movement, combat and loot logic.</summary>
public sealed record ForeverRunOptions
{
    public bool StopWhenNoTargets { get; init; }
    public int? MaxCompletedCycles { get; init; }
    /// <summary>The run's agenda; when empty, the run hunts where it stands (see <see cref="ForeverAgenda.For"/>).</summary>
    public IReadOnlyList<ForeverTask> Tasks { get; init; } = [];
    /// <summary>The agenda starts over after its last task, as a farming loop.</summary>
    public bool RepeatTasks { get; init; }
    /// <summary>Walks plan with the era's authored cost zones, as the run's route asks (<c>"CostZones": "Apply"</c>).</summary>
    public bool ApplyCostZones { get; init; }
}
