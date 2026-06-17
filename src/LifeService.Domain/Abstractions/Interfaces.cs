namespace LifeService.Domain.Abstractions;

/// <summary>
/// API-facing use-case surface (SYSTEM_SPECIFICATION.md §5.1).
/// </summary>
public interface ILifeComputeService
{
    /// <summary>
    /// Uploads an initial board state. Idempotent by content: if a board with an identical set of
    /// live cells already exists, its id is returned without creating a new board
    /// (<see cref="BoardCreationResult.Created"/> is then <c>false</c>). All subsequent operations
    /// act on that board's state set.
    /// </summary>
    Task<BoardCreationResult> UploadInitialStateAsync(
        IReadOnlyCollection<LifeCell> activeCells,
        CancellationToken ct);

    Task<LifeState> GetNextStateAsync(BoardId boardId, CancellationToken ct);

    Task<SolutionSummary> GetFinalStateAsync(BoardId boardId, CancellationToken ct);

    Task<IReadOnlyList<LifeState>> GetNextNStatesAsync(
        BoardId boardId,
        int n,
        CancellationToken ct);

    Task<IReadOnlyList<LifeState>> GetStatesInRangeAsync(
        BoardId boardId,
        long fromLabel,
        long toLabel,
        CancellationToken ct);

    /// <summary>
    /// Lists the first state (label 0, the uploaded initial state) of every stored board, in a
    /// deterministic order, one page at a time. Each item carries the usual board id, label and
    /// active cells.
    /// </summary>
    Task<PagedResult<LifeState>> ListInitialStatesAsync(int page, int pageSize, CancellationToken ct);

    Task ClearQuarantineAsync(BoardId boardId, CancellationToken ct);
}

/// <summary>
/// Deterministic compute provider implementing the map/reduce model
/// (SYSTEM_SPECIFICATION.md §5.2, §8, §9). Implementations must not mutate input state.
/// </summary>
public interface ILifeComputeProvider
{
    Task<LifeState> ComputeNextStateAsync(LifeState current, CancellationToken ct);

    Task<IReadOnlyList<LifeState>> ComputeNextNStatesAsync(
        LifeState current,
        int n,
        CancellationToken ct);

    Task<SolutionSummary> ComputeUntilSteadyOrLimitAsync(
        BoardId boardId,
        LifeState initial,
        int maxStates,
        CancellationToken ct);
}

/// <summary>
/// Persistence boundary for boards, states, solution summaries and quarantine records
/// (SYSTEM_SPECIFICATION.md §5.3). All operations must be idempotent.
/// </summary>
public interface ILifeStorageProvider
{
    /// <summary>
    /// Creates a board from its initial state, or returns the existing board if one with an
    /// identical cell set (per <see cref="LifeService.Domain.BoardFingerprint"/>) was already
    /// created. This makes uploads idempotent by content; <see cref="BoardCreationResult.Created"/>
    /// indicates whether a new board was persisted.
    /// </summary>
    Task<BoardCreationResult> CreateBoardAsync(
        IReadOnlyCollection<LifeCell> initialState,
        CancellationToken ct);

    Task<LifeState?> GetStateAsync(BoardId boardId, LifeStateLabel label, CancellationToken ct);

    Task<IReadOnlyList<LifeState>> GetStatesRangeAsync(
        BoardId boardId,
        LifeStateLabel from,
        LifeStateLabel to,
        CancellationToken ct);

    /// <summary>
    /// Returns a page of the initial state (label 0) of every stored board, ordered deterministically
    /// by board id, along with the total board count. Used to enumerate stored results.
    /// </summary>
    Task<PagedResult<LifeState>> GetInitialStatesAsync(int page, int pageSize, CancellationToken ct);

    Task PersistStateAsync(LifeState state, CancellationToken ct);

    Task<SolutionSummary?> GetSolutionSummaryAsync(BoardId boardId, CancellationToken ct);

    Task PersistSolutionSummaryAsync(SolutionSummary summary, CancellationToken ct);

    Task<QuarantineInfo?> GetQuarantineAsync(BoardId boardId, CancellationToken ct);

    Task PersistQuarantineAsync(QuarantineInfo info, CancellationToken ct);

    Task ClearQuarantineAsync(BoardId boardId, CancellationToken ct);
}
