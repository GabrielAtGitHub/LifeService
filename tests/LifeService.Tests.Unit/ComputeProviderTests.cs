using LifeService.Domain;
using LifeService.Domain.Abstractions;
using LifeService.Domain.Configuration;
using LifeService.Domain.Diagnostics;
using LifeService.Domain.Errors;
using LifeService.Infrastructure.Compute;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LifeService.Tests.Unit;

/// <summary>
/// Unit tests for the deterministic Game of Life rules and the director/worker engine
/// (SYSTEM_SPECIFICATION.md §11 – still lifes, oscillators, spaceships, limits).
/// </summary>
public class ComputeProviderTests
{
    private static LifeComputeProvider CreateProvider(int maxActiveCells = 10_000)
    {
        var limits = Options.Create(new LifeLimitsOptions { MaxActiveCells = maxActiveCells });
        var compute = Options.Create(new LifeComputeOptions());
        return new LifeComputeProvider(
            limits, compute, new LifeMetrics(), NullLogger<LifeComputeProvider>.Instance);
    }

    private static LifeState State(params (int X, int Y)[] cells) =>
        new(BoardId.New(), LifeStateLabel.Initial, cells.Select(c => new LifeCell(c.X, c.Y)).ToList());

    private static HashSet<LifeCell> CellSet(IReadOnlyCollection<LifeCell> cells) => [.. cells];

    [Fact]
    public async Task Block_IsStillLife()
    {
        // 2x2 block is a still life — next generation is identical.
        var block = State((0, 0), (0, 1), (1, 0), (1, 1));

        var next = await CreateProvider().ComputeNextStateAsync(block, CancellationToken.None);

        Assert.Equal(CellSet(block.ActiveCells), CellSet(next.ActiveCells));
        Assert.Equal(1, next.Label.Value);
    }

    [Fact]
    public async Task Blinker_OscillatesWithPeriodTwo()
    {
        // Vertical blinker -> horizontal -> vertical.
        var vertical = State((1, 0), (1, 1), (1, 2));
        var provider = CreateProvider();

        var horizontal = await provider.ComputeNextStateAsync(vertical, CancellationToken.None);
        var backToVertical = await provider.ComputeNextStateAsync(horizontal, CancellationToken.None);

        Assert.Equal(
            CellSet([new(0, 1), new(1, 1), new(2, 1)]),
            CellSet(horizontal.ActiveCells));
        Assert.Equal(CellSet(vertical.ActiveCells), CellSet(backToVertical.ActiveCells));
    }

    [Fact]
    public async Task LoneCell_Dies()
    {
        var single = State((5, 5));

        var next = await CreateProvider().ComputeNextStateAsync(single, CancellationToken.None);

        Assert.Empty(next.ActiveCells);
    }

    [Fact]
    public async Task Glider_TranslatesAfterFourGenerations()
    {
        // A glider returns to its original shape, shifted by (1,1), after 4 generations.
        var glider = State((1, 0), (2, 1), (0, 2), (1, 2), (2, 2));

        var states = await CreateProvider().ComputeNextNStatesAsync(glider, 4, CancellationToken.None);

        var expected = CellSet(glider.ActiveCells.Select(c => new LifeCell(c.X + 1, c.Y + 1)).ToList());
        Assert.Equal(expected, CellSet(states[^1].ActiveCells));
    }

    [Fact]
    public async Task ComputeNext_DoesNotMutateInput()
    {
        var blinker = State((1, 0), (1, 1), (1, 2));
        var snapshot = CellSet(blinker.ActiveCells);

        await CreateProvider().ComputeNextStateAsync(blinker, CancellationToken.None);

        Assert.Equal(snapshot, CellSet(blinker.ActiveCells));
    }

    [Fact]
    public async Task ExceedingActiveCellLimit_Throws()
    {
        // A 3x3 filled square expands its potential set beyond a tiny limit.
        var dense = State((0, 0), (0, 1), (0, 2), (1, 0), (1, 1), (1, 2), (2, 0), (2, 1), (2, 2));
        var provider = CreateProvider(maxActiveCells: 4);

        var ex = await Assert.ThrowsAsync<LifeException>(
            () => provider.ComputeNextStateAsync(dense, CancellationToken.None));
        Assert.Equal(LifeErrorCode.ActiveCellLimitExceeded, ex.Code);
    }

    [Fact]
    public async Task ComputeUntilSteady_DetectsStableBlock()
    {
        var id = BoardId.New();
        var block = new LifeState(id, LifeStateLabel.Initial,
            [new(0, 0), new(0, 1), new(1, 0), new(1, 1)]);

        var summary = await CreateProvider()
            .ComputeUntilSteadyOrLimitAsync(id, block, 100, CancellationToken.None);

        Assert.Equal(SolutionStatus.StableSteadyState, summary.Status);
        Assert.Equal(1, summary.OscillationPeriodLength);
    }

    [Fact]
    public async Task ComputeUntilSteady_DetectsBlinkerOscillation()
    {
        var id = BoardId.New();
        var blinker = new LifeState(id, LifeStateLabel.Initial,
            [new(1, 0), new(1, 1), new(1, 2)]);

        var summary = await CreateProvider()
            .ComputeUntilSteadyOrLimitAsync(id, blinker, 100, CancellationToken.None);

        Assert.Equal(SolutionStatus.OscillationSteadyState, summary.Status);
        Assert.Equal(2, summary.OscillationPeriodLength);
    }

    [Fact]
    public async Task ComputeUntilSteady_EmptyBoardIsStable()
    {
        var id = BoardId.New();
        var empty = new LifeState(id, LifeStateLabel.Initial, []);

        var summary = await CreateProvider()
            .ComputeUntilSteadyOrLimitAsync(id, empty, 10, CancellationToken.None);

        Assert.Equal(SolutionStatus.StableSteadyState, summary.Status);
    }
}
