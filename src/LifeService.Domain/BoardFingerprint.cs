using System.Text;

namespace LifeService.Domain;

/// <summary>
/// Computes a deterministic, content-addressing fingerprint for an initial board state.
///
/// The fingerprint identifies a board by the <em>exact</em> set of live coordinates (it is
/// order-independent and dedupes repeated coordinates, but is NOT translation-invariant): two
/// boards that differ only by a translation are genuinely distinct state sets and evolve to
/// translated — not identical — results. It backs idempotent uploads: re-uploading an identical
/// cell set returns the previously created <see cref="BoardId"/> (SYSTEM_SPECIFICATION.md §5.3, §8).
/// </summary>
public static class BoardFingerprint
{
    /// <summary>
    /// Produces a canonical key for the cell set: distinct coordinates, sorted by (X, Y), then
    /// serialised. Equal inputs (ignoring order and duplicates) always produce the same key, so it
    /// is safe to use as a persistence/lookup key. An empty set maps to the empty string.
    /// </summary>
    public static string Compute(IReadOnlyCollection<LifeCell>? cells)
    {
        if (cells is null || cells.Count == 0)
        {
            return string.Empty;
        }

        var ordered = cells
            .Distinct()
            .OrderBy(c => c.X)
            .ThenBy(c => c.Y);

        var sb = new StringBuilder();
        foreach (var c in ordered)
        {
            sb.Append(c.X).Append(',').Append(c.Y).Append(';');
        }

        return sb.ToString();
    }
}
