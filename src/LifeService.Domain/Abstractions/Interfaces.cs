namespace LifeService.Domain.Abstractions;

/// <summary>
/// API-facing use-case surface (SYSTEM_SPECIFICATION.md §5.1).
/// </summary>
public interface ILifeComputeService
{
    Task<BoardId> UploadInitialStateAsync(
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

    Task ClearQuarantineAsync(BoardId boardId, CancellationToken ct);
}

/// <summary>
/// Deterministic compute provider implementing the director/worker model
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
    Task<BoardId> CreateBoardAsync(
        IReadOnlyCollection<LifeCell> initialState,
        CancellationToken ct);

    Task<LifeState?> GetStateAsync(BoardId boardId, LifeStateLabel label, CancellationToken ct);

    Task<IReadOnlyList<LifeState>> GetStatesRangeAsync(
        BoardId boardId,
        LifeStateLabel from,
        LifeStateLabel to,
        CancellationToken ct);

    Task PersistStateAsync(LifeState state, CancellationToken ct);

    Task<SolutionSummary?> GetSolutionSummaryAsync(BoardId boardId, CancellationToken ct);

    Task PersistSolutionSummaryAsync(SolutionSummary summary, CancellationToken ct);

    Task<QuarantineInfo?> GetQuarantineAsync(BoardId boardId, CancellationToken ct);

    Task PersistQuarantineAsync(QuarantineInfo info, CancellationToken ct);

    Task ClearQuarantineAsync(BoardId boardId, CancellationToken ct);
}
