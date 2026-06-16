using FsCheck;
using FsCheck.Xunit;
using LifeService.Domain;
using LifeService.Domain.Configuration;
using LifeService.Domain.Diagnostics;
using LifeService.Infrastructure.Compute;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LifeService.Tests.Unit;

/// <summary>A randomly generated board: a distinct set of live cells in a bounded region.</summary>
public sealed record Board(IReadOnlyList<LifeCell> Cells);

/// <summary>A random 2-D translation, used to assert position-independence properties.</summary>
public sealed record Translation(int Dx, int Dy);

/// <summary>FsCheck generators for <see cref="Board"/> and <see cref="Translation"/>.</summary>
internal static class BoardArbitraries
{
    // Coordinates are kept in a small window so generated boards are dense enough for cells to
    // interact (births/deaths actually happen), which makes the properties meaningful.
    private static Gen<LifeCell> CellGen =>
        from x in Gen.Choose(-6, 6)
        from y in Gen.Choose(-6, 6)
        select new LifeCell(x, y);

    public static Arbitrary<Board> Boards() =>
        Arb.From(from cells in Gen.ArrayOf(CellGen)
                 select new Board(cells.Distinct().ToArray()));

    public static Arbitrary<Translation> Translations() =>
        Arb.From(from dx in Gen.Choose(-1000, 1000)
                 from dy in Gen.Choose(-1000, 1000)
                 select new Translation(dx, dy));
}

/// <summary>
/// Property-based tests for the compute engine. The engine's parallel director/worker output is
/// checked against an independent reference implementation and against algebraic invariants
/// (determinism, no mutation, locality, translation-independence) over randomly generated boards.
/// </summary>
[Properties(Arbitrary = [typeof(BoardArbitraries)], MaxTest = 300)]
public class ComputeProviderPropertyTests
{
    private static LifeComputeProvider CreateProvider(double threadPoolFactor = 2.0) =>
        new(
            Options.Create(new LifeLimitsOptions()),
            Options.Create(new LifeComputeOptions { ThreadPoolFactor = threadPoolFactor }),
            new LifeMetrics(),
            NullLogger<LifeComputeProvider>.Instance);

    private static HashSet<LifeCell> Next(LifeComputeProvider provider, IReadOnlyCollection<LifeCell> cells)
    {
        var state = new LifeState(BoardId.New(), LifeStateLabel.Initial, cells);
        var next = provider.ComputeNextStateAsync(state, CancellationToken.None).GetAwaiter().GetResult();
        return next.ActiveCells.ToHashSet();
    }

    /// <summary>
    /// Independent, obviously-correct oracle: accumulate neighbour counts, then apply the B3/S23
    /// rule. Formulated differently from the engine (which iterates potential cells and looks up
    /// neighbours), so agreement is strong evidence of correctness.
    /// </summary>
    private static HashSet<LifeCell> ReferenceNext(IReadOnlyCollection<LifeCell> cells)
    {
        var active = cells.ToHashSet();
        var counts = new Dictionary<LifeCell, int>();
        foreach (var c in active)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    var neighbour = new LifeCell(c.X + dx, c.Y + dy);
                    counts[neighbour] = counts.GetValueOrDefault(neighbour) + 1;
                }
            }
        }

        var next = new HashSet<LifeCell>();
        foreach (var (cell, n) in counts)
        {
            var alive = active.Contains(cell);
            if ((alive && (n == 2 || n == 3)) || (!alive && n == 3))
            {
                next.Add(cell);
            }
        }

        return next;
    }

    [Property]
    public bool NextState_MatchesReferenceImplementation(Board board) =>
        Next(CreateProvider(), board.Cells).SetEquals(ReferenceNext(board.Cells));

    [Property]
    public bool NextState_IsDeterministicAcrossWorkerCounts(Board board)
    {
        var fewWorkers = Next(CreateProvider(threadPoolFactor: 0.1), board.Cells);   // ~1 worker
        var manyWorkers = Next(CreateProvider(threadPoolFactor: 16.0), board.Cells); // many workers
        return fewWorkers.SetEquals(manyWorkers);
    }

    [Property]
    public bool NextState_DoesNotMutateInput(Board board)
    {
        var cells = board.Cells.ToList();
        var snapshot = cells.ToHashSet();
        _ = Next(CreateProvider(), cells);
        return snapshot.SetEquals(cells) && cells.Count == snapshot.Count;
    }

    [Property]
    public bool NextState_IsSubsetOfPotentialCells(Board board)
    {
        var potential = new HashSet<LifeCell>();
        foreach (var c in board.Cells)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    potential.Add(new LifeCell(c.X + dx, c.Y + dy));
                }
            }
        }

        return Next(CreateProvider(), board.Cells).IsSubsetOf(potential);
    }

    [Property]
    public bool NextNStates_EqualsRepeatedSingleSteps(Board board, byte rawSteps)
    {
        var n = rawSteps % 6; // 0..5 generations
        var provider = CreateProvider();

        var state = new LifeState(BoardId.New(), LifeStateLabel.Initial, board.Cells);
        // FsCheck 2.x has no Testable for Task<bool>; the engine completes synchronously anyway.
        var batch = provider.ComputeNextNStatesAsync(state, n, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (n == 0)
        {
            return batch.Count == 0;
        }

        var cursor = board.Cells.ToHashSet();
        for (var i = 0; i < n; i++)
        {
            cursor = Next(provider, cursor);
        }

        return batch.Count == n && batch[^1].ActiveCells.ToHashSet().SetEquals(cursor);
    }

    [Property]
    public bool Block_IsStillLifeAtAnyOffset(Translation t)
    {
        var block = new[]
        {
            new LifeCell(t.Dx, t.Dy), new LifeCell(t.Dx + 1, t.Dy),
            new LifeCell(t.Dx, t.Dy + 1), new LifeCell(t.Dx + 1, t.Dy + 1),
        };

        return Next(CreateProvider(), block).SetEquals(block.ToHashSet());
    }

    [Property]
    public bool Blinker_HasPeriodTwoAtAnyOffset(Translation t)
    {
        var vertical = new[]
        {
            new LifeCell(t.Dx, t.Dy), new LifeCell(t.Dx, t.Dy + 1), new LifeCell(t.Dx, t.Dy + 2),
        }.ToHashSet();

        var provider = CreateProvider();
        var afterTwo = Next(provider, Next(provider, vertical));
        return afterTwo.SetEquals(vertical);
    }

    [Property]
    public bool Canonical_IsTranslationInvariant(Board board, Translation t)
    {
        var translated = board.Cells.Select(c => new LifeCell(c.X + t.Dx, c.Y + t.Dy)).ToList();
        return SteadyStateDetector.Canonical(board.Cells) == SteadyStateDetector.Canonical(translated);
    }
}
