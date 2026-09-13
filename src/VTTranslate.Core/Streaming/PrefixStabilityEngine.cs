namespace VTTranslate.Core.Streaming;

/// <summary>
/// Word-level prefix-stability engine. See docs/design-notes/prefix-stability-engine.md
/// for the full algorithm writeup, state machine, and examples — this comment covers
/// the essentials.
///
/// SOURCE-FIRST: stability decisions are made purely from consecutive partials'
/// SOURCE text (Recognizing events' recognized-language text). Translated text
/// (<see cref="PartialSourceEvent.TranslatedTextForDiagnosticsOnly"/>) is never
/// inspected — per docs/design-notes/streaming-measurement-results.md §4/§9, Azure's
/// per-partial translated text regresses far more often than the source text, so it
/// cannot be trusted as a stability signal; only the source text's own agreement
/// across consecutive partials is used.
///
/// Algorithm, per partial: compute the word-level longest common prefix (LCP) between
/// the current and immediately preceding partial's tokens. Hold back the LAST agreed
/// word (never trust the boundary word yet — it's the one most likely to still be
/// revised). If what remains is longer than what's already committed AND is a true
/// extension of it (not a contradiction), commit the new words. If it contradicts
/// already-committed text, commit nothing new and wait — already-committed text is
/// never retracted (this is a correctness/latency trade-off, not a bug; see "Regression
/// handling" in the doc). On Final, force-complete: whatever of the final text isn't
/// yet committed is emitted as the last segment, guaranteeing no final content is ever
/// lost, even if it contradicts an earlier (now known incorrect) commit.
///
/// Deterministic: no timestamps, randomness, or wall-clock values participate in any
/// decision (only stored on the input records for a future consumer's diagnostics).
///
/// Comparison-only punctuation normalization: token equality checks used for stability
/// decisions ignore a token's trailing punctuation (e.g. "would" and "would," compare
/// equal), so a word that later merely gains trailing punctuation/formatting is not
/// treated as a contradiction. This affects ONLY the internal comparison — emitted text
/// (<see cref="StabilityResult.NewlyCommittedSegment"/>, CommittedSourceText,
/// PendingUnstableText) is always built from the original tokens, verbatim, punctuation
/// and all. This is not semantic normalization: no lowercasing, stemming, or fuzzy
/// matching is performed, and a genuine lexical change (e.g. "Tuesday" vs "Thursday")
/// still compares unequal exactly as before.
/// </summary>
public sealed class PrefixStabilityEngine : IStreamingStabilityEngine
{
    private string? _currentUtteranceId;
    private int _lastProcessedSequence = -1;
    private List<string> _committedTokens = new();
    private List<string>? _previousPartialTokens;
    private List<string> _latestPartialTokens = new();
    private int _commitVersion;

    public StabilityResult ProcessPartial(PartialSourceEvent partial)
    {
        if (_currentUtteranceId != partial.UtteranceId)
        {
            ResetInternal();
            _currentUtteranceId = partial.UtteranceId;
        }

        // Stale/out-of-order protection: a partial whose sequence number doesn't
        // advance past what we've already processed for this utterance is ignored
        // entirely — no state change, nothing committed.
        if (partial.SequenceNumber <= _lastProcessedSequence)
            return BuildResult(StabilityAction.None, newlyCommitted: null);

        _lastProcessedSequence = partial.SequenceNumber;

        var currentTokens = Tokenize(partial.SourceText);

        if (currentTokens.Count == 0)
        {
            // Empty/whitespace-only partial: no information gained. Deliberately do
            // NOT overwrite _previousPartialTokens with an empty list — that would
            // corrupt the next real partial's comparison baseline. _latestPartialTokens
            // also stays as-is, so PendingUnstableText remains accurate.
            return BuildResult(StabilityAction.None, newlyCommitted: null);
        }

        _latestPartialTokens = currentTokens;

        string? newSegment = null;
        var action = StabilityAction.None;

        if (_previousPartialTokens != null)
        {
            var agreedLength = WordLevelCommonPrefixLength(_previousPartialTokens, currentTokens);
            var committableLength = Math.Max(0, agreedLength - 1); // hold back the boundary word

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
                // else: candidate contradicts already-committed content. Do not retract,
                // do not emit — wait for future agreement (or Final) to resolve it. This
                // is the documented self-correction-after-commit trade-off.
            }
        }

        _previousPartialTokens = currentTokens;

        return BuildResult(action, newSegment);
    }

    public StabilityResult ProcessFinal(FinalSourceEvent final)
    {
        if (_currentUtteranceId != final.UtteranceId)
        {
            // A final with no preceding partials for this utterance ID (matches the
            // real, observed Step-1 behavior for short utterances — Azure can go
            // straight to Final with zero Recognizing events). Nothing is committed
            // yet; finalize below handles this correctly as a full-text commit.
            ResetInternal();
            _currentUtteranceId = final.UtteranceId;
        }

        var finalTokens = Tokenize(final.SourceText);
        var agreedWithCommitted = WordLevelCommonPrefixLength(_committedTokens, finalTokens);
        var correction = agreedWithCommitted < _committedTokens.Count;

        // Whether or not there's a correction, the remainder beyond the agreed point is
        // always fully emitted here — this is what guarantees final content is never
        // lost, per the interface's authoritative-final contract.
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
        _previousPartialTokens = null;
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

    // Trailing punctuation/formatting characters stripped ONLY for equality comparisons
    // (stability/commit decisions). Emitted text (NewlyCommittedSegment, CommittedSourceText,
    // PendingUnstableText) always comes from the original, untouched tokens — this method's
    // output is never emitted. This intentionally does not lowercase, stem, or fuzzy-match:
    // it exists solely so a word committed without trailing punctuation (e.g. "would") is not
    // treated as contradicting the same word later carrying trailing punctuation (e.g. "would,"
    // or "would."), which is a formatting artifact, not a real speech-recognition correction.
    private static bool TokensMatchForComparison(string a, string b) =>
        string.Equals(TrimTrailingPunctuation(a), TrimTrailingPunctuation(b), StringComparison.Ordinal);

    private static string TrimTrailingPunctuation(string token) =>
        token.TrimEnd('.', ',', '!', '?', ';', ':');
}
