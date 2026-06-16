using System.Text;
using LifeService.Domain;

namespace LifeService.Infrastructure.Compute;

/// <summary>
/// Detects steady states using the canonical-state algorithm (SYSTEM_SPECIFICATION.md §9).
///
/// A translation-normalised, ordered string is computed for each state and stored against the
/// label at which it was first observed. When a canonical key recurs:
///   * period == 1  → stable steady state (still life or empty board),
///   * period &gt; 1 → oscillation, with the period equal to (currentLabel − firstSeenLabel).
/// </summary>
public sealed class SteadyStateDetector
{
    private readonly Dictionary<string, long> _seen = new();

    public readonly record struct Result(
        bool IsSteady,
        SolutionStatus Status,
        LifeStateLabel? PeriodStart,
        int? PeriodLength);

    /// <summary>
    /// Observe a state. Returns whether a steady state has been reached at this label.
    /// </summary>
    public Result Observe(LifeState state)
    {
        var key = Canonical(state.ActiveCells);
        var label = state.Label.Value;

        if (_seen.TryGetValue(key, out var firstSeen))
        {
            var period = (int)(label - firstSeen);
            var status = period == 1
                ? SolutionStatus.StableSteadyState
                : SolutionStatus.OscillationSteadyState;
            return new Result(true, status, new LifeStateLabel(firstSeen), period);
        }

        _seen[key] = label;
        return new Result(false, SolutionStatus.Incomplete, null, null);
    }

    /// <summary>
    /// Translation-invariant canonical representation: cells are shifted so the minimum X/Y is 0,
    /// then sorted, then serialised. Two boards differing only by translation share a key.
    /// </summary>
    public static string Canonical(IReadOnlyCollection<LifeCell> cells)
    {
        if (cells.Count == 0)
        {
            return string.Empty;
        }

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        foreach (var c in cells)
        {
            if (c.X < minX) minX = c.X;
            if (c.Y < minY) minY = c.Y;
        }

        var normalised = cells
            .Select(c => (X: c.X - minX, Y: c.Y - minY))
            .OrderBy(c => c.X)
            .ThenBy(c => c.Y);

        var sb = new StringBuilder();
        foreach (var c in normalised)
        {
            sb.Append(c.X).Append(',').Append(c.Y).Append(';');
        }

        return sb.ToString();
    }
}
