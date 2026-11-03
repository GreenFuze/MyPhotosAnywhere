namespace Mpa.Processor.Core;

/// <summary>Supervisor stop thresholds, in addition to bounded decoder/model allocations.
/// RSS is sampled; this is not an operating-system hard allocation quota.</summary>
public sealed record WorkerResourcePolicy(long ResidentMemoryStopBytes, TimeSpan MaximumLifetime)
{
    public static WorkerResourcePolicy Default { get; } = new(1024L * 1024 * 1024, TimeSpan.FromMinutes(30));
    internal void Validate()
    {
        if (ResidentMemoryStopBytes is < 16 * 1024 * 1024 or > 16L * 1024 * 1024 * 1024 ||
            MaximumLifetime < TimeSpan.FromMilliseconds(100) || MaximumLifetime > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(WorkerResourcePolicy), "Worker resource policy exceeds supported bounds.");
    }
}

public sealed class WorkerResourceLimitException(string resource, long observed, long allowed)
    : IOException("Worker exceeded its " + resource + " stop threshold; retain checkpoints and investigate before retry.")
{
    public string Resource { get; } = resource;
    public long Observed { get; } = observed;
    public long Allowed { get; } = allowed;
}
