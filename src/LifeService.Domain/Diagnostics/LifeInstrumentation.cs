using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LifeService.Domain.Diagnostics;

/// <summary>
/// Shared <see cref="ActivitySource"/> for the engine (CLAUDE.md §5 – Tracing).
/// </summary>
public static class LifeDiagnostics
{
    public const string ActivitySourceName = "GameOfLife.Engine";

    public static readonly ActivitySource Source = new(ActivitySourceName);

    public static Activity? StartOperation(string operation, BoardId boardId)
    {
        var activity = Source.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag("operation", operation);
        activity?.SetTag("boardId", boardId.ToString());
        return activity;
    }
}

/// <summary>
/// OpenTelemetry-compatible metrics for the engine, registered under the
/// "GameOfLife.Engine" meter (CLAUDE.md §5 – Metrics). Registered as a singleton.
/// </summary>
public sealed class LifeMetrics : IDisposable
{
    public const string MeterName = "GameOfLife.Engine";

    private readonly Meter _meter;

    public LifeMetrics()
    {
        _meter = new Meter(MeterName);
        StatesComputed = _meter.CreateCounter<long>("states_computed", unit: "{state}",
            description: "Number of Game of Life states computed.");
        ActiveCells = _meter.CreateUpDownCounter<long>("active_cells", unit: "{cell}",
            description: "Current number of active cells in the most recently computed state.");
        QuarantinedBoards = _meter.CreateCounter<long>("quarantined_boards", unit: "{board}",
            description: "Number of boards placed into quarantine.");
    }

    public Counter<long> StatesComputed { get; }

    public UpDownCounter<long> ActiveCells { get; }

    public Counter<long> QuarantinedBoards { get; }

    public void Dispose() => _meter.Dispose();
}
