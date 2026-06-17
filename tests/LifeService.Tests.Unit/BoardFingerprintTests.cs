using LifeService.Domain;

namespace LifeService.Tests.Unit;

/// <summary>
/// Unit tests for the content-addressing fingerprint that backs idempotent uploads
/// (SYSTEM_SPECIFICATION.md §5.3, §8).
/// </summary>
public class BoardFingerprintTests
{
    [Fact]
    public void Compute_IsOrderIndependent()
    {
        var a = BoardFingerprint.Compute([new(1, 0), new(1, 1), new(1, 2)]);
        var b = BoardFingerprint.Compute([new(1, 2), new(1, 0), new(1, 1)]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_IgnoresDuplicateCoordinates()
    {
        var a = BoardFingerprint.Compute([new(1, 1), new(2, 2)]);
        var b = BoardFingerprint.Compute([new(2, 2), new(1, 1), new(1, 1), new(2, 2)]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DistinguishesDifferentCellSets()
    {
        var a = BoardFingerprint.Compute([new(0, 0), new(0, 1)]);
        var b = BoardFingerprint.Compute([new(0, 0), new(1, 0)]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_IsNotTranslationInvariant()
    {
        // Two boards differing only by a translation are distinct state sets.
        var a = BoardFingerprint.Compute([new(0, 0), new(0, 1), new(0, 2)]);
        var b = BoardFingerprint.Compute([new(5, 5), new(5, 6), new(5, 7)]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_EmptyAndNull_MapToEmptyKey()
    {
        Assert.Equal(string.Empty, BoardFingerprint.Compute([]));
        Assert.Equal(string.Empty, BoardFingerprint.Compute(null));
    }
}
