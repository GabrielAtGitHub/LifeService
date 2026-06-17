using System.Diagnostics;
using LifeService.Domain;
using LifeService.Domain.Abstractions;
using LifeService.Domain.Configuration;
using LifeService.Domain.Diagnostics;
using LifeService.Domain.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LifeService.Application;

/// <summary>
/// Use-case orchestration for the Game of Life service (SYSTEM_SPECIFICATION.md §5.1).
///
/// Responsibilities: input/limit validation, quarantine gating and retry-driven quarantining,
/// delegation to the compute provider, persistence, and observability (structured logging with
/// boardId/operation/status/durationMs, metrics, and tracing).
/// </summary>
public sealed class LifeComputeService : ILifeComputeService
{
    private readonly ILifeComputeProvider _compute;
    private readonly ILifeStorageProvider _storage;
    private readonly LifeLimitsOptions _limits;
    private readonly LifeMetrics _metrics;
    private readonly ILogger<LifeComputeService> _logger;

    public LifeComputeService(
        ILifeComputeProvider compute,
        ILifeStorageProvider storage,
        IOptions<LifeLimitsOptions> limits,
        LifeMetrics metrics,
        ILogger<LifeComputeService> logger)
    {
        _compute = compute;
        _storage = storage;
        _limits = limits.Value;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<BoardCreationResult> UploadInitialStateAsync(
        IReadOnlyCollection<LifeCell> activeCells, CancellationToken ct)
    {
        if (activeCells.Count > _limits.MaxActiveCells)
        {
            throw LifeException.ActiveCellLimitExceeded(activeCells.Count, _limits.MaxActiveCells);
        }

        // Distinct cells only — duplicate coordinates are not meaningful.
        var distinct = activeCells.Distinct().ToList();

        var sw = Stopwatch.StartNew();
        // Idempotent by content: an identical initial cell set returns the previously created board
        // id instead of creating a new board. Subsequent operations act on that state set.
        var result = await _storage.CreateBoardAsync(distinct, ct).ConfigureAwait(false);

        using (BeginScope(result.BoardId, "UploadInitialState"))
        {
            if (result.Created)
            {
                _metrics.ActiveCells.Add(distinct.Count);
                LogSuccess(result.BoardId, "UploadInitialState", sw.ElapsedMilliseconds, distinct.Count);
            }
            else
            {
                // Duplicate upload: do not double-count active cells; record the dedup outcome.
                _logger.LogInformation(
                    "UploadInitialState for board {BoardId} completed with status {Status} in {DurationMs}ms (activeCells: {ActiveCells})",
                    result.BoardId, "deduplicated", sw.ElapsedMilliseconds, distinct.Count);
            }
        }

        return result;
    }

    public Task<LifeState> GetNextStateAsync(BoardId boardId, CancellationToken ct) =>
        RunGuardedAsync(boardId, "GetNextState", async () =>
        {
            var current = await GetLatestStateAsync(boardId, ct).ConfigureAwait(false);
            var next = await _compute.ComputeNextStateAsync(current, ct).ConfigureAwait(false);
            await _storage.PersistStateAsync(next, ct).ConfigureAwait(false);
            await AdvanceSummaryAsync(boardId, next.Label, ct).ConfigureAwait(false);
            return next;
        }, ct);

    public Task<SolutionSummary> GetFinalStateAsync(BoardId boardId, CancellationToken ct) =>
        RunGuardedAsync(boardId, "GetFinalState", async () =>
        {
            var current = await GetLatestStateAsync(boardId, ct).ConfigureAwait(false);
            var summary = await _compute
                .ComputeUntilSteadyOrLimitAsync(boardId, current, _limits.MaxStatesPerRequest, ct)
                .ConfigureAwait(false);
            await _storage.PersistSolutionSummaryAsync(summary, ct).ConfigureAwait(false);
            return summary;
        }, ct);

    public Task<IReadOnlyList<LifeState>> GetNextNStatesAsync(
        BoardId boardId, int n, CancellationToken ct) =>
        RunGuardedAsync(boardId, "GetNextNStates", async () =>
        {
            if (n < 0 || n > _limits.MaxStatesPerRequest)
            {
                throw LifeException.StatesLimitExceeded(n, _limits.MaxStatesPerRequest);
            }

            var current = await GetLatestStateAsync(boardId, ct).ConfigureAwait(false);
            var states = await _compute.ComputeNextNStatesAsync(current, n, ct).ConfigureAwait(false);
            foreach (var state in states)
            {
                await _storage.PersistStateAsync(state, ct).ConfigureAwait(false);
            }

            if (states.Count > 0)
            {
                await AdvanceSummaryAsync(boardId, states[^1].Label, ct).ConfigureAwait(false);
            }

            return states;
        }, ct);

    public Task<IReadOnlyList<LifeState>> GetStatesInRangeAsync(
        BoardId boardId, long fromLabel, long toLabel, CancellationToken ct) =>
        RunGuardedAsync(boardId, "GetStatesInRange", async () =>
        {
            if (fromLabel < 0 || toLabel < fromLabel ||
                (toLabel - fromLabel) > _limits.MaxStatesPerRequest)
            {
                throw LifeException.InvalidRange(fromLabel, toLabel);
            }

            // Ensure the board exists before returning a (possibly empty) range.
            _ = await GetLatestStateAsync(boardId, ct).ConfigureAwait(false);

            return await _storage.GetStatesRangeAsync(
                boardId, new LifeStateLabel(fromLabel), new LifeStateLabel(toLabel), ct)
                .ConfigureAwait(false);
        }, ct);

    public async Task<PagedResult<LifeState>> ListInitialStatesAsync(
        int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize < 1)
        {
            throw LifeException.InvalidPagination(page, pageSize);
        }

        if (pageSize > _limits.MaxStatesPerRequest)
        {
            throw LifeException.StatesLimitExceeded(pageSize, _limits.MaxStatesPerRequest);
        }

        using (BeginCollectionScope("ListInitialStates"))
        {
            var sw = Stopwatch.StartNew();
            var result = await _storage.GetInitialStatesAsync(page, pageSize, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "{Operation} (page {Page}, pageSize {PageSize}) completed with status {Status} in {DurationMs}ms (returned: {Count}, total: {Total})",
                "ListInitialStates", page, pageSize, "ok", sw.ElapsedMilliseconds,
                result.Items.Count, result.TotalCount);
            return result;
        }
    }

    public async Task ClearQuarantineAsync(BoardId boardId, CancellationToken ct)
    {
        using (BeginScope(boardId, "ClearQuarantine"))
        {
            var sw = Stopwatch.StartNew();
            await _storage.ClearQuarantineAsync(boardId, ct).ConfigureAwait(false);
            LogSuccess(boardId, "ClearQuarantine", sw.ElapsedMilliseconds, null);
        }
    }

    // --- helpers -----------------------------------------------------------------------------

    private async Task<LifeState> GetLatestStateAsync(BoardId boardId, CancellationToken ct)
    {
        var summary = await _storage.GetSolutionSummaryAsync(boardId, ct).ConfigureAwait(false)
            ?? throw LifeException.BoardNotFound(boardId);

        return await _storage.GetStateAsync(boardId, summary.LastComputedLabel, ct).ConfigureAwait(false)
            ?? throw LifeException.BoardNotFound(boardId);
    }

    private async Task AdvanceSummaryAsync(BoardId boardId, LifeStateLabel label, CancellationToken ct)
    {
        var summary = await _storage.GetSolutionSummaryAsync(boardId, ct).ConfigureAwait(false);
        if (summary is null || label.Value <= summary.LastComputedLabel.Value)
        {
            return;
        }

        await _storage.PersistSolutionSummaryAsync(
            summary with { LastComputedLabel = label }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a compute operation behind the quarantine gate, applies observability, and on
    /// unexpected failures increments the retry count — quarantining the board once
    /// <see cref="LifeLimitsOptions.MaxRetriesPerBoard"/> is reached (SYSTEM_SPECIFICATION.md §10).
    /// </summary>
    private async Task<T> RunGuardedAsync<T>(
        BoardId boardId, string operation, Func<Task<T>> action, CancellationToken ct)
    {
        using var activity = LifeDiagnostics.StartOperation(operation, boardId);
        using var scope = BeginScope(boardId, operation);
        var sw = Stopwatch.StartNew();

        await EnsureNotQuarantinedAsync(boardId, ct).ConfigureAwait(false);

        try
        {
            var result = await action().ConfigureAwait(false);
            activity?.SetTag("status", "ok");
            LogSuccess(boardId, operation, sw.ElapsedMilliseconds, null);
            return result;
        }
        catch (LifeException ex)
        {
            // Domain errors are expected, deterministic outcomes — not failures to retry.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Code.ToString());
            _logger.LogWarning(ex,
                "Operation {Operation} for board {BoardId} rejected with {Code} ({Status}) in {DurationMs}ms",
                operation, boardId, ex.Code, "rejected", sw.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await QuarantineOnFailureAsync(boardId, operation, ex, ct).ConfigureAwait(false);
            throw new LifeException(LifeErrorCode.InternalError,
                $"Operation {operation} failed for board {boardId}.", ex);
        }
    }

    private async Task EnsureNotQuarantinedAsync(BoardId boardId, CancellationToken ct)
    {
        var quarantine = await _storage.GetQuarantineAsync(boardId, ct).ConfigureAwait(false);

        // A record with RetryCount below the threshold is interim failure-tracking, not an active
        // quarantine; only block requests once the board is genuinely quarantined.
        if (quarantine is not null && quarantine.RetryCount >= _limits.MaxRetriesPerBoard)
        {
            _logger.LogWarning(
                "Board {BoardId} is quarantined (reason: {Reason}, retries: {RetryCount})",
                boardId, quarantine.Reason, quarantine.RetryCount);
            throw LifeException.BoardQuarantined(boardId, quarantine.Reason);
        }
    }

    private async Task QuarantineOnFailureAsync(
        BoardId boardId, string operation, Exception ex, CancellationToken ct)
    {
        var existing = await _storage.GetQuarantineAsync(boardId, ct).ConfigureAwait(false);
        var retryCount = (existing?.RetryCount ?? 0) + 1;

        if (retryCount >= _limits.MaxRetriesPerBoard)
        {
            var info = new QuarantineInfo
            {
                BoardId = boardId,
                QuarantinedAt = DateTimeOffset.UtcNow,
                Reason = $"{operation} failed: {ex.Message}",
                RetryCount = retryCount,
            };
            await _storage.PersistQuarantineAsync(info, ct).ConfigureAwait(false);
            _metrics.QuarantinedBoards.Add(1);
            _logger.LogError(ex,
                "Board {BoardId} quarantined after {RetryCount} failures during {Operation}",
                boardId, retryCount, operation);
        }
        else
        {
            // Track the failure count without quarantining yet.
            var info = new QuarantineInfo
            {
                BoardId = boardId,
                QuarantinedAt = DateTimeOffset.UtcNow,
                Reason = $"{operation} failed: {ex.Message}",
                RetryCount = retryCount,
            };
            await _storage.PersistQuarantineAsync(info, ct).ConfigureAwait(false);
            _logger.LogWarning(ex,
                "Operation {Operation} failed for board {BoardId} (failure {RetryCount}/{Max})",
                operation, boardId, retryCount, _limits.MaxRetriesPerBoard);
        }
    }

    private IDisposable? BeginScope(BoardId boardId, string operation) =>
        _logger.BeginScope(new Dictionary<string, object>
        {
            ["boardId"] = boardId.ToString(),
            ["operation"] = operation,
        });

    /// <summary>Logging scope for collection-level operations that are not scoped to a single board.</summary>
    private IDisposable? BeginCollectionScope(string operation) =>
        _logger.BeginScope(new Dictionary<string, object>
        {
            ["operation"] = operation,
        });

    private void LogSuccess(BoardId boardId, string operation, long durationMs, int? activeCells)
    {
        _logger.LogInformation(
            "{Operation} for board {BoardId} completed with status {Status} in {DurationMs}ms (activeCells: {ActiveCells})",
            operation, boardId, "ok", durationMs, activeCells);
    }
}
