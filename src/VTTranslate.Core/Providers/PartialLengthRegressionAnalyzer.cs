namespace VTTranslate.Core.Providers;

/// <summary>
/// Phase 15B result of comparing consecutive partial-result LENGTHS only. Distinct from
/// <see cref="PartialTextComparison.Regressed"/> (which flags any non-append change,
/// including same-or-greater-length revisions such as a casing or mid-text correction):
/// this specifically answers Phase 15A's follow-up question — did a later partial
/// actually become SHORTER than the one before it? Pure, stateless, and takes only
/// integer lengths, so it is structurally incapable of touching recognized/translated
/// text content.
/// </summary>
public readonly record struct PartialLengthRegression(bool IsRegression, int MagnitudeChars);

public static class PartialLengthRegressionAnalyzer
{
    /// <param name="previousLength">The prior partial's text length for this utterance, or null if this is the first partial.</param>
    /// <param name="currentLength">The current partial's text length.</param>
    public static PartialLengthRegression Check(int? previousLength, int currentLength)
    {
        if (previousLength is null || currentLength >= previousLength.Value)
            return new PartialLengthRegression(IsRegression: false, MagnitudeChars: 0);

        return new PartialLengthRegression(IsRegression: true, MagnitudeChars: previousLength.Value - currentLength);
    }
}
