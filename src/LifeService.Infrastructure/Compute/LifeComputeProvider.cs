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
/// Deterministic director/worker compute engine (SYSTEM_SPECIFICATION.md §8).
///
/// Director phase: build the de-duplicated set of "potential" cells (active cells plus their
/// eight neighbours). Worker phase: partition the potential set into disjoint chunks and, in
/// parallel, evaluate each cell against the Game of Life rules using read-only lookups into the
/// active set. Reduce phase: union the per-worker results. Because chunks are disjoint and each
/// worker only emits cells it owns, no write conflicts are possible and no de-duplication is
/// required.
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

    public async Task<SolutionSummary> ComputeUntilSteadyOrLimitAsync(
        BoardId boardId, LifeState initial, int maxStates, CancellationToken ct)
    {
        using var activity = LifeDiagnostics.StartOperation("ComputeUntilSteadyOrLimit", boardId);

        var detector = new SteadyStateDetector();
        var current = initial;
        detector.Observe(current); // record the starting state at its own label

        for (var i = 0; i < maxStates; i++)
        {
            ct.ThrowIfCancellationRequested();
            current = await ComputeNextAsync(current, ct).ConfigureAwait(false);
            var result = detector.Observe(current);
            if (result.IsSteady)
            {
                activity?.SetTag("status", result.Status.ToString());
                return new SolutionSummary
                {
                    BoardId = boardId,
                    Status = result.Status,
                    LastComputedLabel = current.Label,
                    OscillationPeriodStart = result.PeriodStart,
                    OscillationPeriodLength = result.PeriodLength,
                };
            }
        }

        activity?.SetTag("status", nameof(SolutionStatus.Incomplete));
        return new SolutionSummary
        {
            BoardId = boardId,
            Status = SolutionStatus.Incomplete,
            LastComputedLabel = current.Label,
        };
    }

    /// <summary>Computes the successor of <paramref name="current"/> without mutating it.</summary>
    private async Task<LifeState> ComputeNextAsync(LifeState current, CancellationToken ct)
    {
        using var activity = LifeDiagnostics.StartOperation("ComputeNextState", current.BoardId);

        var active = new HashSet<LifeCell>(current.ActiveCells);
        var potential = BuildPotentialCells(active);

        if (potential.Count > _limits.MaxActiveCells)
        {
            throw LifeException.ActiveCellLimitExceeded(potential.Count, _limits.MaxActiveCells);
        }

        var alive = await EvaluatePotentialCellsAsync(potential, active, ct).ConfigureAwait(false);

        _metrics.StatesComputed.Add(1);
        // active_cells is an UpDownCounter reflecting the delta to the latest computed state.
        _metrics.ActiveCells.Add(alive.Count - active.Count);

        activity?.SetTag("activeCells", alive.Count);
        return new LifeState(current.BoardId, current.Label.Next(), alive);
    }

    /// <summary>Director phase: active cells plus their eight neighbours, de-duplicated.</summary>
    private static HashSet<LifeCell> BuildPotentialCells(HashSet<LifeCell> active)
    {
        var potential = new HashSet<LifeCell>(active.Count * 9);
        foreach (var cell in active)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    potential.Add(new LifeCell(cell.X + dx, cell.Y + dy));
                }
            }
        }

        return potential;
    }

    /// <summary>
    /// Worker + reduce phase: split the potential set across up to
    /// <c>min(ProcessorCount × ThreadPoolFactor, count / WorkerMinCellsPerTask)</c> workers and
    /// evaluate them in parallel. Deriving the worker count from a floor division by
    /// <see cref="LifeComputeOptions.WorkerMinCellsPerTask"/> guarantees every chunk holds at least
    /// that many cells, so small boards don't over-parallelise.
    /// </summary>
    private async Task<List<LifeCell>> EvaluatePotentialCellsAsync(
        HashSet<LifeCell> potential, HashSet<LifeCell> active, CancellationToken ct)
    {
        var cells = new List<LifeCell>(potential);

        var minPerTask = Math.Max(1, _compute.WorkerMinCellsPerTask);
        var maxWorkers = Math.Max(1, (int)(Environment.ProcessorCount * _compute.ThreadPoolFactor));

        // Small boards stay single-threaded to avoid scheduling overhead.
        if (cells.Count / minPerTask <= 1)
        {
            return EvaluateChunk(cells, 0, cells.Count, active, ct);
        }

        var desiredWorkers = Math.Min(maxWorkers, cells.Count / minPerTask);

        // Because desiredWorkers <= cells.Count / minPerTask (floor), ceil(count / desiredWorkers)
        // is necessarily >= minPerTask, so no explicit lower clamp on the chunk size is needed.
        var chunkSize = (cells.Count + desiredWorkers - 1) / desiredWorkers;
        Debug.Assert(chunkSize >= minPerTask, "chunk size must not fall below WorkerMinCellsPerTask");

        var tasks = new List<Task<List<LifeCell>>>(desiredWorkers);
        for (var start = 0; start < cells.Count; start += chunkSize)
        {
            var localStart = start;
            var localEnd = Math.Min(start + chunkSize, cells.Count);
            tasks.Add(Task.Run(() => EvaluateChunk(cells, localStart, localEnd, active, ct), ct));
        }

        var chunks = await Task.WhenAll(tasks).ConfigureAwait(false);

        var alive = new List<LifeCell>(cells.Count);
        foreach (var chunk in chunks)
        {
            alive.AddRange(chunk);
        }

        return alive;
    }

    /// <summary>
    /// Pure Game of Life rule evaluation for a half-open slice of the potential set. Reads only
    /// the shared (immutable for the duration) <paramref name="active"/> set, so it is thread-safe.
    /// Cancellation is observed periodically so large chunks stop promptly.
    /// </summary>
    private static List<LifeCell> EvaluateChunk(
        List<LifeCell> cells, int start, int end, HashSet<LifeCell> active, CancellationToken ct)
    {
        var result = new List<LifeCell>(end - start);
        for (var i = start; i < end; i++)
        {
            // Observe cancellation at the start of the chunk and every 1024 cells thereafter.
            if ((i - start) % 1024 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var cell = cells[i];
            var neighbours = CountLiveNeighbours(cell, active);
            var isAlive = active.Contains(cell);

            // Birth on exactly 3; survival on 2 or 3.
            if ((isAlive && (neighbours == 2 || neighbours == 3)) || (!isAlive && neighbours == 3))
            {
                result.Add(cell);
            }
        }

        return result;
    }

    private static int CountLiveNeighbours(LifeCell cell, HashSet<LifeCell> active)
    {
        var count = 0;
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                if (active.Contains(new LifeCell(cell.X + dx, cell.Y + dy)))
                {
                    count++;
                }
            }
        }

        return count;
    }
}
