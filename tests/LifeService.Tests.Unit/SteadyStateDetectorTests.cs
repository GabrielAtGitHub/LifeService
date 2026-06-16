using LifeService.Domain;
using LifeService.Infrastructure.Compute;

namespace LifeService.Tests.Unit;

/// <summary>Unit tests for the canonical-state steady-state detector (SYSTEM_SPECIFICATION.md §9).</summary>
public class SteadyStateDetectorTests
{
    private static LifeState State(long label, params (int X, int Y)[] cells) =>
        new(BoardId.New(), new LifeStateLabel(label),
            cells.Select(c => new LifeCell(c.X, c.Y)).ToList());

    [Fact]
    public void Canonical_IsTranslationInvariant()
    {
        var a = SteadyStateDetector.Canonical([new(0, 0), new(0, 1)]);
        var b = SteadyStateDetector.Canonical([new(5, 5), new(5, 6)]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Canonical_EmptyBoardIsEmptyString()
    {
        Assert.Equal(string.Empty, SteadyStateDetector.Canonical([]));
    }

    [Fact]
    public void RepeatedState_WithPeriodOne_IsStable()
    {
        var detector = new SteadyStateDetector();

        Assert.False(detector.Observe(State(0, (0, 0), (1, 1))).IsSteady);
        var result = detector.Observe(State(1, (0, 0), (1, 1)));

        Assert.True(result.IsSteady);
        Assert.Equal(SolutionStatus.StableSteadyState, result.Status);
        Assert.Equal(1, result.PeriodLength);
    }

    [Fact]
    public void RepeatedState_WithLongerPeriod_IsOscillation()
    {
        var detector = new SteadyStateDetector();

        detector.Observe(State(0, (1, 0), (1, 1), (1, 2)));      // vertical
        detector.Observe(State(1, (0, 1), (1, 1), (2, 1)));      // horizontal
        var result = detector.Observe(State(2, (1, 0), (1, 1), (1, 2))); // vertical again

        Assert.True(result.IsSteady);
        Assert.Equal(SolutionStatus.OscillationSteadyState, result.Status);
        Assert.Equal(2, result.PeriodLength);
    }
}
