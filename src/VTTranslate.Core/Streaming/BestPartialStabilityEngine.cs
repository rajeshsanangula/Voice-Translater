namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL — Step 4, Policy B. Shadow/observation only; never wired to translation,
/// TTS, or playback. See docs/design-notes/commit-policy-shadow-evaluation.md for the
/// full evaluation. <see cref="PrefixStabilityEngine"/> (Policy A) is NOT modified by
/// this class and remains the only production-tested candidate.
///
/// Differs from Policy A in exactly one respect: instead of comparing each partial
/// against the literal immediately-preceding partial, it compares against a "reference"
/// partial that is only replaced when a new partial is STRICTLY LONGER than the current
/// reference AND does not contradict already-committed content. A shorter, equal-length,
/// or contradicting partial is still used normally for THIS step's commit decision (so
/// contradictions/regressions are still detected and withheld exactly as in Policy A —
/// this is NOT "longest partial wins," it is "longest CONSISTENT partial becomes the new
/// comparison baseline") but it does NOT itself become the new reference. This means a
/// single no-op or regressed partial can't degrade the comparison baseline for every
/// partial that follows it, the way Policy A's unconditional "always adopt current as
/// the new previous" rule can (see the commit-delay finding in
/// docs/design-notes/prefix-stability-test-failure-analysis.md).
///
/// Duplicate prevention, monotonic commitment, and the trailing-word buffer are all
/// otherwise identical to Policy A — committed tokens only ever grow via an
/// extension-verified append, and the same comparison-only trailing-punctuation
/// normalization (V-1, see PrefixStabilityEngine) is applied so a difference in policy
/// comparison results is never just an artifact of differing punctuation handling.
/// </summary>
public sealed class BestPartialStabilityEngine : IStreamingStabilityEngine
{
    private string? _currentUtteranceId;
    private int _lastProcessedSequence = -1;
    private List<string> _committedTokens = new();
    private List<string>? _referencePartialTokens;
    private List<string> _latestPartialTokens = new();
    private int _commitVersion;

    public StabilityResult ProcessPartial(PartialSourceEvent partial)
    {
        if (_currentUtteranceId != partial.UtteranceId)
        {
            ResetInternal();
            _currentUtteranceId = partial.UtteranceId;
        }

        if (partial.SequenceNumber <= _lastProcessedSequence)
            return BuildResult(StabilityAction.None, newlyCommitted: null);

        _lastProcessedSequence = partial.SequenceNumber;

        var currentTokens = Tokenize(partial.SourceText);
        if (currentTokens.Count == 0)
            return BuildResult(StabilityAction.None, newlyCommitted: null);

        _latestPartialTokens = currentTokens;

        string? newSegment = null;
        var action = StabilityAction.None;

        if (_referencePartialTokens != null)
        {
            var agreedLength = WordLevelCommonPrefixLength(_referencePartialTokens, currentTokens);
            var committableLength = Math.Max(0, agreedLength - 1); // same trailing-word buffer as Policy A

            if (committableLength > _committedTokens.Count)
            {
                var candidateTokens = currentTokens.Take(committableLength).ToList();
                if (IsExtension(_committedTokens, candidateTokens))
                {
                    var newTokens = candidateTokens.Skip(_committedTokens.Count).ToList();
                    _committedTokens = candidateTokens;
                    newSegment = string.Join(" ", newTokens);
                    _commitVersion++;
                    action = StabilityAction.Committed;
                }
                // else: candidate contradicts already-committed content — same as Policy A,
                // do not retract, do not emit.
            }
        }

        // Reference-update rule — the sole divergence from Policy A.
        if (_referencePartialTokens == null)
        {
            _referencePartialTokens = currentTokens;
        }
        else if (currentTokens.Count > _referencePartialTokens.Count)
        {
            var agreedWithCommitted = WordLevelCommonPrefixLength(_committedTokens, currentTokens);
            if (agreedWithCommitted >= _committedTokens.Count)
                _referencePartialTokens = currentTokens; // strictly longer AND consistent — adopt
            // else: longer but contradicts already-committed content — do NOT adopt; keep the
            // last good reference so this contradiction doesn't degrade future comparisons.
        }
        // else (same length or shorter): never adopted as the new reference — this is what
        // prevents a no-op/regressed partial from resetting the comparison baseline.

        return BuildResult(action, newSegment);
    }

    public StabilityResult ProcessFinal(FinalSourceEvent final)
    {
        // Finalization is identical in shape to Policy A's: the Final is always
        // authoritative regardless of which comparison strategy produced the commits
        // leading up to it.
        if (_currentUtteranceId != final.UtteranceId)
        {
            ResetInternal();
            _currentUtteranceId = final.UtteranceId;
        }

        var finalTokens = Tokenize(final.SourceText);
        var agreedWithCommitted = WordLevelCommonPrefixLength(_committedTokens, finalTokens);
        var correction = agreedWithCommitted < _committedTokens.Count;

        var newTokens = finalTokens.Skip(agreedWithCommitted).ToList();
        var committedFullText = string.Join(" ", finalTokens);
        var newSegment = newTokens.Count > 0 ? string.Join(" ", newTokens) : null;
        if (newSegment != null) _commitVersion++;

        var result = new StabilityResult(
            Action: StabilityAction.Finalized,
            CommittedSourceText: committedFullText,
            NewlyCommittedSegment: newSegment,
            PendingUnstableText: string.Empty,
            CommitVersion: _commitVersion,
            ShouldEmit: newSegment != null,
            FinalizedWithCorrection: correction);

        ResetInternal();
        return result;
    }

    public void Reset() => ResetInternal();

    private void ResetInternal()
    {
        _currentUtteranceId = null;
        _lastProcessedSequence = -1;
        _committedTokens = new List<string>();
        _referencePartialTokens = null;
        _latestPartialTokens = new List<string>();
        _commitVersion = 0;
    }

    private StabilityResult BuildResult(StabilityAction action, string? newlyCommitted)
    {
        var pending = _latestPartialTokens.Count > _committedTokens.Count
            ? string.Join(" ", _latestPartialTokens.Skip(_committedTokens.Count))
            : string.Empty;

        return new StabilityResult(
            Action: action,
            CommittedSourceText: string.Join(" ", _committedTokens),
            NewlyCommittedSegment: newlyCommitted,
            PendingUnstableText: pending,
            CommitVersion: _commitVersion,
            ShouldEmit: newlyCommitted != null);
    }

    private static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static int WordLevelCommonPrefixLength(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var n = Math.Min(a.Count, b.Count);
        var i = 0;
        while (i < n && TokensMatchForComparison(a[i], b[i])) i++;
        return i;
    }

    private static bool IsExtension(IReadOnlyList<string> committed, IReadOnlyList<string> candidate)
    {
        if (candidate.Count < committed.Count) return false;
        for (var i = 0; i < committed.Count; i++)
            if (!TokensMatchForComparison(committed[i], candidate[i])) return false;
        return true;
    }

    // Same comparison-only trailing-punctuation normalization as Policy A's V-1 fix —
    // duplicated intentionally rather than shared, so Policy A's file is never touched by
    // this experimental engine (see class doc comment).
    private static bool TokensMatchForComparison(string a, string b) =>
        string.Equals(TrimTrailingPunctuation(a), TrimTrailingPunctuation(b), StringComparison.Ordinal);

    private static string TrimTrailingPunctuation(string token) =>
        token.TrimEnd('.', ',', '!', '?', ';', ':');
}
