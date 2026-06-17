using System.Diagnostics;
using LifeService.Domain;
using LifeService.Domain.Abstractions;
using LifeService.Domain.Configuration;
using LifeService.Domain.Diagnostics;
using LifeService.Domain.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LifeService.Infrastructure.Compute;

/// <summary>
/// Deterministic map/reduce compute engine (SYSTEM_SPECIFICATION.md §8).
///
/// Map (scatter) phase: partition the active cells across workers; each worker scatters
/// live-neighbour counts into its own local map. Reduce phase: merge the maps by summing counts
/// per cell. Rule phase: each candidate cell is alive next per the B3/S23 rule. Because workers
/// write only to local maps and count summation is order independent, there are no write conflicts
/// and the result is independent of the partitioning.
/// </summary>
public sealed class LifeComputeProvider : ILifeComputeProvider
{
    private readonly LifeLimitsOptions _limits;
    private readonly LifeComputeOptions _compute;
    private readonly LifeMetrics _metrics;
    private readonly ILogger<LifeComputeProvider> _logger;

    public LifeComputeProvider(
        IOptions<LifeLimitsOptions> limits,
        IOptions<LifeComputeOptions> compute,
        LifeMetrics metrics,
        ILogger<LifeComputeProvider> logger)
    {
        _limits = limits.Value;
        _compute = compute.Value;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<LifeState> ComputeNextStateAsync(LifeState current, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var next = await ComputeNextAsync(current, ct).ConfigureAwait(false);
        return next;
    }

    public async Task<IReadOnlyList<LifeState>> ComputeNextNStatesAsync(
        LifeState current, int n, CancellationToken ct)
    {
        if (n < 0)
        {
            throw LifeException.InvalidRange(0, n);
        }

        var results = new List<LifeState>(n);
        var cursor = current;
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            cursor = await ComputeNextAsync(cursor, ct).ConfigureAwait(false);
            results.Add(cursor);
        }

        return results;
    }

    public async Task<SteadyStateResult> ComputeUntilSteadyOrLimitAsync(
        BoardId boardId, LifeState initial, int maxStates, CancellationToken ct)
    {
        using var activity = LifeDiagnostics.StartOperation("ComputeUntilSteadyOrLimit", boardId);

        var detector = new SteadyStateDetector();
        var current = initial;
        detector.Observe(current); // record the starting state at its own label

        // Track the trajectory so the caller can persist it; LastComputedLabel must reference a
        // state that was actually stored.
        var computed = new List<LifeState>();

        for (var i = 0; i < maxStates; i++)
        {
            ct.ThrowIfCancellationRequested();
            current = await ComputeNextAsync(current, ct).ConfigureAwait(false);
            computed.Add(current);
            var result = detector.Observe(current);
            if (result.IsSteady)
            {
                activity?.SetTag("status", result.Status.ToString());
                return new SteadyStateResult
                {
                    Summary = new SolutionSummary
                    {
                        BoardId = boardId,
                        Status = result.Status,
                        LastComputedLabel = current.Label,
                        OscillationPeriodStart = result.PeriodStart,
                        OscillationPeriodLength = result.PeriodLength,
                    },
                    ComputedStates = computed,
                };
            }
        }

        activity?.SetTag("status", nameof(SolutionStatus.Incomplete));
        return new SteadyStateResult
        {
            Summary = new SolutionSummary
            {
                BoardId = boardId,
                Status = SolutionStatus.Incomplete,
                LastComputedLabel = current.Label,
            },
            ComputedStates = computed,
        };
    }

    /// <summary>Computes the successor of <paramref name="current"/> without mutating it.</summary>
    private async Task<LifeState> ComputeNextAsync(LifeState current, CancellationToken ct)
    {
        using var activity = LifeDiagnostics.StartOperation("ComputeNextState", current.BoardId);

        var active = new HashSet<LifeCell>(current.ActiveCells);

        // Map + reduce: live-neighbour counts for every candidate cell.
        var counts = await AccumulateNeighbourCountsAsync(active, ct).ConfigureAwait(false);

        if (counts.Count > _limits.MaxActiveCells)
        {
            throw LifeException.ActiveCellLimitExceeded(counts.Count, _limits.MaxActiveCells);
        }

        var alive = ApplyRules(counts, active);

        _metrics.StatesComputed.Add(1);
        // active_cells is an UpDownCounter reflecting the delta to the latest computed state.
        _metrics.ActiveCells.Add(alive.Count - active.Count);

        activity?.SetTag("activeCells", alive.Count);
        return new LifeState(current.BoardId, current.Label.Next(), alive);
    }

    /// <summary>
    /// Map (scatter) + reduce phase: partition the active cells across up to
    /// <c>min(ProcessorCount × ThreadPoolFactor, activeCount / WorkerMinCellsPerTask)</c> workers.
    /// Each worker scatters live-neighbour counts into a local map (no shared writes); the maps are
    /// then merged by summing counts. Summation is associative/commutative, so the merged result is
    /// independent of how the active cells were partitioned.
    /// </summary>
    private async Task<Dictionary<LifeCell, int>> AccumulateNeighbourCountsAsync(
        HashSet<LifeCell> active, CancellationToken ct)
    {
        var cells = new List<LifeCell>(active);

        var minPerTask = Math.Max(1, _compute.WorkerMinCellsPerTask);
        var maxWorkers = Math.Max(1, (int)(Environment.ProcessorCount * _compute.ThreadPoolFactor));

        // Small boards stay single-threaded to avoid scheduling overhead (also guards the empty case).
        if (cells.Count / minPerTask <= 1)
        {
            return ScatterChunk(cells, 0, cells.Count, ct);
        }

        var desiredWorkers = Math.Min(maxWorkers, cells.Count / minPerTask);

        // Because desiredWorkers <= cells.Count / minPerTask (floor), ceil(count / desiredWorkers)
        // is necessarily >= minPerTask, so no explicit lower clamp on the chunk size is needed.
        var chunkSize = (cells.Count + desiredWorkers - 1) / desiredWorkers;
        Debug.Assert(chunkSize >= minPerTask, "chunk size must not fall below WorkerMinCellsPerTask");

        var tasks = new List<Task<Dictionary<LifeCell, int>>>(desiredWorkers);
        for (var start = 0; start < cells.Count; start += chunkSize)
        {
            var localStart = start;
            var localEnd = Math.Min(start + chunkSize, cells.Count);
            tasks.Add(Task.Run(() => ScatterChunk(cells, localStart, localEnd, ct), ct));
        }

        var partials = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Reduce: sum the per-worker count maps into the first one.
        var merged = partials[0];
        for (var i = 1; i < partials.Length; i++)
        {
            foreach (var (cell, count) in partials[i])
            {
                merged[cell] = merged.GetValueOrDefault(cell) + count;
            }
        }

        return merged;
    }

    /// <summary>
    /// Worker: scatter live-neighbour counts from a half-open slice of the active cells into a local
    /// map. Writes only to its own dictionary, so it is thread-safe. Cancellation is observed
    /// periodically so large chunks stop promptly.
    /// </summary>
    private static Dictionary<LifeCell, int> ScatterChunk(
        List<LifeCell> cells, int start, int end, CancellationToken ct)
    {
        var counts = new Dictionary<LifeCell, int>((end - start) * 8);
        for (var i = start; i < end; i++)
        {
            // Observe cancellation at the start of the chunk and every 1024 cells thereafter.
            if ((i - start) % 1024 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var cell = cells[i];
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    var neighbour = new LifeCell(cell.X + dx, cell.Y + dy);
                    counts[neighbour] = counts.GetValueOrDefault(neighbour) + 1;
                }
            }
        }

        return counts;
    }

    /// <summary>
    /// Rule phase: a cell is alive next iff it is currently alive with 2 or 3 live neighbours, or
    /// currently dead with exactly 3. Cells with no live neighbours never appear in the count map
    /// and correctly die. One active-set lookup per candidate.
    /// </summary>
    private static List<LifeCell> ApplyRules(Dictionary<LifeCell, int> counts, HashSet<LifeCell> active)
    {
        var alive = new List<LifeCell>(counts.Count);
        foreach (var (cell, neighbours) in counts)
        {
            var isAlive = active.Contains(cell);
            // Birth on exactly 3; survival on 2 or 3.
            if ((isAlive && (neighbours == 2 || neighbours == 3)) || (!isAlive && neighbours == 3))
            {
                alive.Add(cell);
            }
        }

        return alive;
    }
}
