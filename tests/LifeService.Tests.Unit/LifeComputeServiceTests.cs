using LifeService.Application;
using LifeService.Domain;
using LifeService.Domain.Abstractions;
using LifeService.Domain.Configuration;
using LifeService.Domain.Diagnostics;
using LifeService.Domain.Errors;
using LifeService.Infrastructure.Compute;
using LifeService.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LifeService.Tests.Unit;

/// <summary>
/// Service-level unit tests: limit enforcement, range validation and the quarantine lifecycle
/// (SYSTEM_SPECIFICATION.md §10, §11).
/// </summary>
public class LifeComputeServiceTests
{
    private static LifeComputeService CreateService(
        ILifeComputeProvider? provider = null,
        ILifeStorageProvider? storage = null,
        LifeLimitsOptions? limits = null)
    {
        limits ??= new LifeLimitsOptions();
        storage ??= new InMemoryLifeStorageProvider();
        provider ??= new LifeComputeProvider(
            Options.Create(limits), Options.Create(new LifeComputeOptions()),
            new LifeMetrics(), NullLogger<LifeComputeProvider>.Instance);

        return new LifeComputeService(
            provider, storage, Options.Create(limits),
            new LifeMetrics(), NullLogger<LifeComputeService>.Instance);
    }

    /// <summary>Compute provider that always throws — drives the quarantine path.</summary>
    private sealed class ThrowingProvider : ILifeComputeProvider
    {
        public Task<LifeState> ComputeNextStateAsync(LifeState current, CancellationToken ct) =>
            throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<LifeState>> ComputeNextNStatesAsync(
            LifeState current, int n, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task<SteadyStateResult> ComputeUntilSteadyOrLimitAsync(
            BoardId boardId, LifeState initial, int maxStates, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Upload_ThenGetNext_AdvancesGeneration()
    {
        var service = CreateService();
        var upload = await service.UploadInitialStateAsync(
            [new(1, 0), new(1, 1), new(1, 2)], CancellationToken.None);
        Assert.True(upload.Created);

        var next = await service.GetNextStateAsync(upload.BoardId, CancellationToken.None);

        Assert.Equal(1, next.Label.Value);
        Assert.Equal(3, next.ActiveCells.Count);
    }

    [Fact]
    public async Task GetFinal_PersistsTrajectory_SoSubsequentOperationsSucceed()
    {
        var service = CreateService();
        var id = (await service.UploadInitialStateAsync(
            [new(1, 0), new(1, 1), new(1, 2)], CancellationToken.None)).BoardId;

        // Compute to steady state: this advances LastComputedLabel past the uploaded state.
        var summary = await service.GetFinalStateAsync(id, CancellationToken.None);
        Assert.True(summary.LastComputedLabel.Value > 0);

        // Regression: previously /final advanced the summary without persisting the computed states,
        // so GetLatestState (used by /next, /final, /next-sequence) then threw BoardNotFound.
        var next = await service.GetNextStateAsync(id, CancellationToken.None);
        Assert.Equal(summary.LastComputedLabel.Value + 1, next.Label.Value);

        // The computed trajectory is queryable, and the state at LastComputedLabel exists.
        var history = await service.GetStatesInRangeAsync(
            id, 0, summary.LastComputedLabel.Value, CancellationToken.None);
        Assert.Contains(history, s => s.Label.Value == summary.LastComputedLabel.Value);

        // A second /final from the persisted steady state also succeeds.
        var again = await service.GetFinalStateAsync(id, CancellationToken.None);
        Assert.Equal(SolutionStatus.OscillationSteadyState, again.Status);
    }

    [Fact]
    public async Task Upload_DuplicateState_ReturnsSameBoardWithoutCreating()
    {
        var service = CreateService();

        var first = await service.UploadInitialStateAsync(
            [new(1, 0), new(1, 1), new(1, 2)], CancellationToken.None);
        // Same cells, different order and with a duplicate coordinate — still the same board.
        var second = await service.UploadInitialStateAsync(
            [new(1, 2), new(1, 1), new(1, 0), new(1, 1)], CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.BoardId, second.BoardId);
    }

    [Fact]
    public async Task Upload_DifferentState_CreatesDistinctBoards()
    {
        var service = CreateService();

        var a = await service.UploadInitialStateAsync([new(0, 0)], CancellationToken.None);
        // A translated copy is a distinct state set, not a duplicate.
        var b = await service.UploadInitialStateAsync([new(5, 5)], CancellationToken.None);

        Assert.True(a.Created);
        Assert.True(b.Created);
        Assert.NotEqual(a.BoardId, b.BoardId);
    }

    [Fact]
    public async Task Upload_Duplicate_AdvancesFromExistingStateSet()
    {
        var service = CreateService();
        var first = await service.UploadInitialStateAsync(
            [new(1, 0), new(1, 1), new(1, 2)], CancellationToken.None);

        // Advance the board, then "re-upload" the same initial state.
        await service.GetNextStateAsync(first.BoardId, CancellationToken.None);
        var second = await service.UploadInitialStateAsync(
            [new(1, 0), new(1, 1), new(1, 2)], CancellationToken.None);

        // The dedup returns the same board, and operations continue from its current state set.
        Assert.False(second.Created);
        Assert.Equal(first.BoardId, second.BoardId);
        var next = await service.GetNextStateAsync(second.BoardId, CancellationToken.None);
        Assert.Equal(2, next.Label.Value);
    }

    [Fact]
    public async Task Upload_BeyondActiveCellLimit_Throws()
    {
        var service = CreateService(limits: new LifeLimitsOptions { MaxActiveCells = 2 });

        var ex = await Assert.ThrowsAsync<LifeException>(() =>
            service.UploadInitialStateAsync([new(0, 0), new(1, 1), new(2, 2)], CancellationToken.None));
        Assert.Equal(LifeErrorCode.ActiveCellLimitExceeded, ex.Code);
    }

    [Fact]
    public async Task GetNextN_BeyondLimit_Throws()
    {
        var service = CreateService(limits: new LifeLimitsOptions { MaxStatesPerRequest = 5 });
        var id = (await service.UploadInitialStateAsync([new(0, 0)], CancellationToken.None)).BoardId;

        var ex = await Assert.ThrowsAsync<LifeException>(() =>
            service.GetNextNStatesAsync(id, 6, CancellationToken.None));
        Assert.Equal(LifeErrorCode.StatesLimitExceeded, ex.Code);
    }

    [Fact]
    public async Task GetStatesInRange_InvalidRange_Throws()
    {
        var service = CreateService();
        var id = (await service.UploadInitialStateAsync([new(0, 0)], CancellationToken.None)).BoardId;

        var ex = await Assert.ThrowsAsync<LifeException>(() =>
            service.GetStatesInRangeAsync(id, 10, 1, CancellationToken.None));
        Assert.Equal(LifeErrorCode.InvalidRange, ex.Code);
    }

    [Fact]
    public async Task ListInitialStates_ReturnsLabelZeroOfEachBoard_InCreationOrder_Paginated()
    {
        var service = CreateService();
        var a = (await service.UploadInitialStateAsync([new(0, 0)], CancellationToken.None)).BoardId;
        var b = (await service.UploadInitialStateAsync([new(5, 5)], CancellationToken.None)).BoardId;
        var c = (await service.UploadInitialStateAsync([new(9, 9)], CancellationToken.None)).BoardId;

        // Advance one board: listing must still return its *first* state (label 0), not the latest.
        await service.GetNextStateAsync(a, CancellationToken.None);

        var page1 = await service.ListInitialStatesAsync(1, 2, CancellationToken.None);
        var page2 = await service.ListInitialStatesAsync(2, 2, CancellationToken.None);

        Assert.Equal(3, page1.TotalCount);
        Assert.All(page1.Items, s => Assert.Equal(0, s.Label.Value));
        Assert.All(page2.Items, s => Assert.Equal(0, s.Label.Value));

        // Boards are returned in creation order, split across pages.
        Assert.Equal([a, b], page1.Items.Select(s => s.BoardId).ToArray());
        Assert.Equal([c], page2.Items.Select(s => s.BoardId).ToArray());

        // Creation timestamps are non-decreasing in listing order.
        var all = page1.Items.Concat(page2.Items).ToList();
        for (var i = 1; i < all.Count; i++)
        {
            Assert.True(all[i].CreatedAt >= all[i - 1].CreatedAt);
        }

        // The label-0 cells of an advanced board are unchanged (the uploaded state).
        var first = all.Single(s => s.BoardId == a);
        Assert.Equal([new LifeCell(0, 0)], first.ActiveCells);
    }

    [Fact]
    public async Task ListInitialStates_BeyondLastPage_ReturnsEmpty()
    {
        var service = CreateService();
        await service.UploadInitialStateAsync([new(0, 0)], CancellationToken.None);

        var page = await service.ListInitialStatesAsync(5, 10, CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(1, page.TotalCount);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(-1, 10)]
    public async Task ListInitialStates_InvalidPagination_Throws(int page, int pageSize)
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<LifeException>(() =>
            service.ListInitialStatesAsync(page, pageSize, CancellationToken.None));
        Assert.Equal(LifeErrorCode.InvalidRange, ex.Code);
    }

    [Fact]
    public async Task ListInitialStates_PageSizeBeyondLimit_Throws()
    {
        var service = CreateService(limits: new LifeLimitsOptions { MaxStatesPerRequest = 5 });

        var ex = await Assert.ThrowsAsync<LifeException>(() =>
            service.ListInitialStatesAsync(1, 6, CancellationToken.None));
        Assert.Equal(LifeErrorCode.StatesLimitExceeded, ex.Code);
    }

    [Fact]
    public async Task GetNext_OnUnknownBoard_ThrowsBoardNotFound()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<LifeException>(() =>
            service.GetNextStateAsync(BoardId.New(), CancellationToken.None));
        Assert.Equal(LifeErrorCode.BoardNotFound, ex.Code);
    }

    [Fact]
    public async Task RepeatedFailures_QuarantineBoard_ThenClearReinstates()
    {
        var storage = new InMemoryLifeStorageProvider();
        var limits = new LifeLimitsOptions { MaxRetriesPerBoard = 2 };
        var service = CreateService(new ThrowingProvider(), storage, limits);
        var id = (await storage.CreateBoardAsync([new(0, 0)], CancellationToken.None)).BoardId;

        // First failure: tracked, surfaced as InternalError, not yet quarantined.
        var first = await Assert.ThrowsAsync<LifeException>(() =>
            service.GetNextStateAsync(id, CancellationToken.None));
        Assert.Equal(LifeErrorCode.InternalError, first.Code);

        // Second failure reaches the retry threshold and quarantines the board.
        await Assert.ThrowsAsync<LifeException>(() =>
            service.GetNextStateAsync(id, CancellationToken.None));

        // Subsequent calls are rejected up-front as quarantined.
        var blocked = await Assert.ThrowsAsync<LifeException>(() =>
            service.GetNextStateAsync(id, CancellationToken.None));
        Assert.Equal(LifeErrorCode.BoardQuarantined, blocked.Code);

        // Clearing quarantine reinstates the board.
        await service.ClearQuarantineAsync(id, CancellationToken.None);
        Assert.Null(await storage.GetQuarantineAsync(id, CancellationToken.None));
    }
}
