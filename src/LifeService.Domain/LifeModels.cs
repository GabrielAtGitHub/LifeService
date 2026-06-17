using System.Collections.ObjectModel;

namespace LifeService.Domain;

/// <summary>
/// An immutable snapshot of a board at a given generation label.
/// </summary>
public sealed record LifeState
{
    public LifeState(BoardId boardId, LifeStateLabel label, IReadOnlyCollection<LifeCell> activeCells)
    {
        BoardId = boardId;
        Label = label;
        // Defensive copy: callers must never be able to mutate a persisted/returned state.
        ActiveCells = new ReadOnlyCollection<LifeCell>(activeCells?.ToList() ?? []);
    }

    public BoardId BoardId { get; }

    public LifeStateLabel Label { get; }

    public IReadOnlyCollection<LifeCell> ActiveCells { get; }
}

/// <summary>
/// Outcome of an initial-state upload.
/// </summary>
/// <param name="BoardId">The board the initial state belongs to.</param>
/// <param name="Created">
/// <c>true</c> when a new board was created; <c>false</c> when an existing board with an identical
/// initial cell set was found and its id returned (idempotent upload — SYSTEM_SPECIFICATION.md §5.3).
/// </param>
public readonly record struct BoardCreationResult(BoardId BoardId, bool Created);

/// <summary>
/// Classification of a board's long-term behaviour.
/// </summary>
public enum SolutionStatus
{
    /// <summary>The board has not yet reached a steady state within the computed range.</summary>
    Incomplete = 0,

    /// <summary>The board reached a fixed point (still life / empty board); period == 1.</summary>
    StableSteadyState = 1,

    /// <summary>The board entered a repeating cycle; period &gt; 1.</summary>
    OscillationSteadyState = 2,
}

/// <summary>
/// Summary of the computed outcome for a board.
/// </summary>
public sealed record SolutionSummary
{
    public required BoardId BoardId { get; init; }

    public required SolutionStatus Status { get; init; }

    public required LifeStateLabel LastComputedLabel { get; init; }

    /// <summary>Label at which the repeating state was first observed (oscillation/stable only).</summary>
    public LifeStateLabel? OscillationPeriodStart { get; init; }

    /// <summary>Length of the detected cycle in generations (1 for stable, &gt;1 for oscillation).</summary>
    public int? OscillationPeriodLength { get; init; }
}

/// <summary>
/// A single page of results together with the pagination metadata needed to request further pages.
/// </summary>
/// <typeparam name="T">The item type contained in the page.</typeparam>
public sealed record PagedResult<T>
{
    /// <summary>The items on this page (may be empty for pages beyond the end).</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>1-based page number that was returned.</summary>
    public required int Page { get; init; }

    /// <summary>Maximum number of items per page.</summary>
    public required int PageSize { get; init; }

    /// <summary>Total number of items across all pages.</summary>
    public required long TotalCount { get; init; }
}

/// <summary>
/// Records that a board has been quarantined after repeated failures.
/// </summary>
public sealed record QuarantineInfo
{
    public required BoardId BoardId { get; init; }

    public required DateTimeOffset QuarantinedAt { get; init; }

    public required string Reason { get; init; }

    public required int RetryCount { get; init; }
}
