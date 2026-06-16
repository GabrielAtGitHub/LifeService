namespace LifeService.Domain.Configuration;

/// <summary>Hard limits that protect the service from unbounded work. Bound from "Life:Limits".</summary>
public sealed class LifeLimitsOptions
{
    public const string SectionName = "Life:Limits";

    public int MaxActiveCells { get; init; } = 10_000;
    public int MaxStatesPerRequest { get; init; } = 1_000;
    public int MaxRetriesPerBoard { get; init; } = 3;
}

/// <summary>Tuning knobs for the map/reduce compute engine. Bound from "Life:Compute".</summary>
public sealed class LifeComputeOptions
{
    public const string SectionName = "Life:Compute";

    public int WorkerMinCellsPerTask { get; init; } = 128;
    public double ThreadPoolFactor { get; init; } = 2.0;
}

/// <summary>Storage provider selection. Bound from "Life:Storage".</summary>
public sealed class LifeStorageOptions
{
    public const string SectionName = "Life:Storage";

    public bool UseRedisQuarantine { get; init; } = true;
    public bool UseRedisSolutionCache { get; init; } = false;
}
