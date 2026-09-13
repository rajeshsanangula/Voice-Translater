namespace VTTranslate.Core.Providers;

/// <summary>
/// Result of comparing one partial-recognition text against the immediately preceding
/// partial for the same in-progress utterance. Pure, stateless comparison — never
/// stores or logs the actual text, only used to derive metadata (lengths/flags) that
/// callers then log. Comparison is exact (ordinal, case-sensitive): a casing-only
/// change (e.g. "hello" -&gt; "Hello") counts as <see cref="Regressed"/>, not
/// <see cref="Unchanged"/> — documented here rather than silently smoothed over,
/// since this is measurement instrumentation and the point is to observe Azure's
/// actual behavior honestly.
/// </summary>
public sealed record PartialTextComparison(bool IsPrefixExtension, bool Unchanged, bool Regressed);

/// <summary>
/// Step 1 measurement instrumentation (see docs/design-notes/streaming-measurement-results.md):
/// determines whether a new partial result is a pure append onto the previous one
/// (the assumption the proposed prefix-stability streaming design in
/// docs/design-notes/streaming-conversational-engine-design.md depends on), an exact
/// repeat, or a regression (Azure revised/changed earlier text). Does not affect any
/// production behavior — purely descriptive.
/// </summary>
public static class PartialResultAnalyzer
{
    /// <param name="previous">The prior partial's text for this utterance, or null if this is the first partial.</param>
    /// <param name="current">The current partial's text.</param>
    public static PartialTextComparison Compare(string? previous, string current)
    {
        if (previous == null)
            return new PartialTextComparison(IsPrefixExtension: false, Unchanged: false, Regressed: false);

        if (string.Equals(previous, current, StringComparison.Ordinal))
            return new PartialTextComparison(IsPrefixExtension: false, Unchanged: true, Regressed: false);

        var extends = current.StartsWith(previous, StringComparison.Ordinal);
        return new PartialTextComparison(IsPrefixExtension: extends, Unchanged: false, Regressed: !extends);
    }
}
