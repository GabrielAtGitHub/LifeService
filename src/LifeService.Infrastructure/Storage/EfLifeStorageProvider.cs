using System.Text.Json;
using LifeService.Domain;
using LifeService.Domain.Abstractions;
using LifeService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeService.Infrastructure.Storage;

/// <summary>
/// EF Core backed <see cref="ILifeStorageProvider"/> (SQLite for development; relational engines in
/// production). Registered as scoped alongside <see cref="LifeDbContext"/>. All writes are
/// idempotent upserts keyed by natural identity.
/// </summary>
public sealed class EfLifeStorageProvider : ILifeStorageProvider
{
    private readonly LifeDbContext _db;

    public EfLifeStorageProvider(LifeDbContext db) => _db = db;

    public async Task<BoardCreationResult> CreateBoardAsync(
        IReadOnlyCollection<LifeCell> initialState, CancellationToken ct)
    {
        var fingerprint = BoardFingerprint.Compute(initialState);

        // Idempotent upload: return the existing board if the same initial state was uploaded before.
        var existing = await _db.Summaries.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Fingerprint == fingerprint, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return new BoardCreationResult(new BoardId(existing.BoardId), Created: false);
        }

        var id = BoardId.New();
        _db.States.Add(new StateEntity
        {
            BoardId = id.Value,
            Label = LifeStateLabel.Initial.Value,
            CellsJson = Serialize(initialState),
        });
        _db.Summaries.Add(new SummaryEntity
        {
            BoardId = id.Value,
            Status = (int)SolutionStatus.Incomplete,
            LastComputedLabel = LifeStateLabel.Initial.Value,
            Fingerprint = fingerprint,
        });

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent upload of the same state won the unique-fingerprint race; defer to it.
            _db.ChangeTracker.Clear();
            var winner = await _db.Summaries.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Fingerprint == fingerprint, ct)
                .ConfigureAwait(false);
            if (winner is not null)
            {
                return new BoardCreationResult(new BoardId(winner.BoardId), Created: false);
            }

            throw;
        }

        return new BoardCreationResult(id, Created: true);
    }

    public async Task<LifeState?> GetStateAsync(BoardId boardId, LifeStateLabel label, CancellationToken ct)
    {
        var entity = await _db.States.AsNoTracking()
            .FirstOrDefaultAsync(s => s.BoardId == boardId.Value && s.Label == label.Value, ct)
            .ConfigureAwait(false);

        return entity is null ? null : ToState(entity);
    }

    public async Task<IReadOnlyList<LifeState>> GetStatesRangeAsync(
        BoardId boardId, LifeStateLabel from, LifeStateLabel to, CancellationToken ct)
    {
        var entities = await _db.States.AsNoTracking()
            .Where(s => s.BoardId == boardId.Value && s.Label >= from.Value && s.Label <= to.Value)
            .OrderBy(s => s.Label)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.Select(ToState).ToList();
    }

    public async Task<PagedResult<LifeState>> GetInitialStatesAsync(int page, int pageSize, CancellationToken ct)
    {
        // One label-0 row exists per board; order by board id for stable pagination.
        var query = _db.States.AsNoTracking()
            .Where(s => s.Label == LifeStateLabel.Initial.Value)
            .OrderBy(s => s.BoardId);

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        var entities = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<LifeState>
        {
            Items = entities.Select(ToState).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
        };
    }

    public async Task PersistStateAsync(LifeState state, CancellationToken ct)
    {
        var existing = await _db.States
            .FirstOrDefaultAsync(s => s.BoardId == state.BoardId.Value && s.Label == state.Label.Value, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _db.States.Add(new StateEntity
            {
                BoardId = state.BoardId.Value,
                Label = state.Label.Value,
                CellsJson = Serialize(state.ActiveCells),
            });
        }
        else
        {
            existing.CellsJson = Serialize(state.ActiveCells);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<SolutionSummary?> GetSolutionSummaryAsync(BoardId boardId, CancellationToken ct)
    {
        var entity = await _db.Summaries.AsNoTracking()
            .FirstOrDefaultAsync(s => s.BoardId == boardId.Value, ct)
            .ConfigureAwait(false);

        return entity is null ? null : ToSummary(entity);
    }

    public async Task PersistSolutionSummaryAsync(SolutionSummary summary, CancellationToken ct)
    {
        var existing = await _db.Summaries
            .FirstOrDefaultAsync(s => s.BoardId == summary.BoardId.Value, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _db.Summaries.Add(ToEntity(summary));
        }
        else
        {
            existing.Status = (int)summary.Status;
            existing.LastComputedLabel = summary.LastComputedLabel.Value;
            existing.OscillationPeriodStart = summary.OscillationPeriodStart?.Value;
            existing.OscillationPeriodLength = summary.OscillationPeriodLength;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<QuarantineInfo?> GetQuarantineAsync(BoardId boardId, CancellationToken ct)
    {
        var entity = await _db.Quarantines.AsNoTracking()
            .FirstOrDefaultAsync(q => q.BoardId == boardId.Value, ct)
            .ConfigureAwait(false);

        return entity is null ? null : ToQuarantine(entity);
    }

    public async Task PersistQuarantineAsync(QuarantineInfo info, CancellationToken ct)
    {
        var existing = await _db.Quarantines
            .FirstOrDefaultAsync(q => q.BoardId == info.BoardId.Value, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _db.Quarantines.Add(new QuarantineEntity
            {
                BoardId = info.BoardId.Value,
                QuarantinedAt = info.QuarantinedAt,
                Reason = info.Reason,
                RetryCount = info.RetryCount,
            });
        }
        else
        {
            existing.QuarantinedAt = info.QuarantinedAt;
            existing.Reason = info.Reason;
            existing.RetryCount = info.RetryCount;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearQuarantineAsync(BoardId boardId, CancellationToken ct)
    {
        var existing = await _db.Quarantines
            .FirstOrDefaultAsync(q => q.BoardId == boardId.Value, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            _db.Quarantines.Remove(existing);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    // --- mapping ----------------------------------------------------------------------------

    private static string Serialize(IReadOnlyCollection<LifeCell> cells) =>
        JsonSerializer.Serialize(cells);

    private static LifeState ToState(StateEntity e)
    {
        var cells = JsonSerializer.Deserialize<List<LifeCell>>(e.CellsJson) ?? [];
        return new LifeState(new BoardId(e.BoardId), new LifeStateLabel(e.Label), cells);
    }

    private static SolutionSummary ToSummary(SummaryEntity e) => new()
    {
        BoardId = new BoardId(e.BoardId),
        Status = (SolutionStatus)e.Status,
        LastComputedLabel = new LifeStateLabel(e.LastComputedLabel),
        OscillationPeriodStart = e.OscillationPeriodStart is { } p ? new LifeStateLabel(p) : null,
        OscillationPeriodLength = e.OscillationPeriodLength,
    };

    private static SummaryEntity ToEntity(SolutionSummary s) => new()
    {
        BoardId = s.BoardId.Value,
        Status = (int)s.Status,
        LastComputedLabel = s.LastComputedLabel.Value,
        OscillationPeriodStart = s.OscillationPeriodStart?.Value,
        OscillationPeriodLength = s.OscillationPeriodLength,
    };

    private static QuarantineInfo ToQuarantine(QuarantineEntity e) => new()
    {
        BoardId = new BoardId(e.BoardId),
        QuarantinedAt = e.QuarantinedAt,
        Reason = e.Reason,
        RetryCount = e.RetryCount,
    };
}
