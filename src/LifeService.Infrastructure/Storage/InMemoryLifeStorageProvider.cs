using System.Collections.Concurrent;
using LifeService.Domain;
using LifeService.Domain.Abstractions;

namespace LifeService.Infrastructure.Storage;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="ILifeStorageProvider"/>. Used as the
/// default development/test provider; production deployments swap in an EF Core / Redis backed
/// implementation (see docs/persistence.md). All write operations are idempotent upserts.
/// </summary>
public sealed class InMemoryLifeStorageProvider : ILifeStorageProvider
{
    private sealed class BoardRecord
    {
        public ConcurrentDictionary<long, LifeState> States { get; } = new();
        public SolutionSummary? Summary { get; set; }
        public QuarantineInfo? Quarantine { get; set; }
    }

    private readonly ConcurrentDictionary<BoardId, BoardRecord> _boards = new();

    public Task<BoardId> CreateBoardAsync(
        IReadOnlyCollection<LifeCell> initialState, CancellationToken ct)
    {
        var id = BoardId.New();
        var record = new BoardRecord();
        var initial = new LifeState(id, LifeStateLabel.Initial, initialState);
        record.States[LifeStateLabel.Initial.Value] = initial;
        record.Summary = new SolutionSummary
        {
            BoardId = id,
            Status = SolutionStatus.Incomplete,
            LastComputedLabel = LifeStateLabel.Initial,
        };
        _boards[id] = record;
        return Task.FromResult(id);
    }

    public Task<LifeState?> GetStateAsync(BoardId boardId, LifeStateLabel label, CancellationToken ct)
    {
        if (_boards.TryGetValue(boardId, out var record) &&
            record.States.TryGetValue(label.Value, out var state))
        {
            return Task.FromResult<LifeState?>(state);
        }

        return Task.FromResult<LifeState?>(null);
    }

    public Task<IReadOnlyList<LifeState>> GetStatesRangeAsync(
        BoardId boardId, LifeStateLabel from, LifeStateLabel to, CancellationToken ct)
    {
        if (!_boards.TryGetValue(boardId, out var record))
        {
            return Task.FromResult<IReadOnlyList<LifeState>>([]);
        }

        var states = record.States.Values
            .Where(s => s.Label.Value >= from.Value && s.Label.Value <= to.Value)
            .OrderBy(s => s.Label.Value)
            .ToList();

        return Task.FromResult<IReadOnlyList<LifeState>>(states);
    }

    public Task PersistStateAsync(LifeState state, CancellationToken ct)
    {
        var record = _boards.GetOrAdd(state.BoardId, _ => new BoardRecord());
        record.States[state.Label.Value] = state; // idempotent upsert
        return Task.CompletedTask;
    }

    public Task<SolutionSummary?> GetSolutionSummaryAsync(BoardId boardId, CancellationToken ct)
    {
        _boards.TryGetValue(boardId, out var record);
        return Task.FromResult(record?.Summary);
    }

    public Task PersistSolutionSummaryAsync(SolutionSummary summary, CancellationToken ct)
    {
        var record = _boards.GetOrAdd(summary.BoardId, _ => new BoardRecord());
        record.Summary = summary;
        return Task.CompletedTask;
    }

    public Task<QuarantineInfo?> GetQuarantineAsync(BoardId boardId, CancellationToken ct)
    {
        _boards.TryGetValue(boardId, out var record);
        return Task.FromResult(record?.Quarantine);
    }

    public Task PersistQuarantineAsync(QuarantineInfo info, CancellationToken ct)
    {
        var record = _boards.GetOrAdd(info.BoardId, _ => new BoardRecord());
        record.Quarantine = info;
        return Task.CompletedTask;
    }

    public Task ClearQuarantineAsync(BoardId boardId, CancellationToken ct)
    {
        if (_boards.TryGetValue(boardId, out var record))
        {
            record.Quarantine = null;
        }

        return Task.CompletedTask;
    }
}
