using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5. Evaluates three incremental-translation
/// strategies against Azure's OWN, already-produced per-partial translated text (never
/// a new translation API call — see docs/design-notes/incremental-translation-shadow-evaluation.md
/// §2 for why), correlated with Policy A's (<see cref="PrefixStabilityEngine"/>,
/// unmodified) source-stability decisions. Has no events and no reference to
/// translation-dispatch/TTS/playback — structurally incapable of making experimental
/// output user-visible, the same guarantee as <see cref="ShadowStabilityObserver"/> and
/// <see cref="CommitPolicyComparator"/>.
///
/// Strategy A ("naive/independent"): on EVERY partial (not gated by source stability),
/// blindly appends whatever is new in Azure's translated text relative to the previous
/// partial's translated text — including when that "new" text is actually a revision/
/// contradiction, not a clean extension. This deliberately reproduces the exact failure
/// mode the Step 5 objective warns against ("translate independently and concatenate"),
/// so its measured duplicate/contradictory-content rate is the evaluation's baseline for
/// "how bad is the naive approach, concretely."
///
/// Strategy B ("cumulative re-translation, stability-gated"): only when Policy A reports a
/// newly stable source commit (or at Final), takes a fresh, whole snapshot of Azure's
/// current full translated text, replacing the previous candidate outright. Always
/// internally consistent (it's Azure's own single, coherent translation of the whole
/// text-so-far) but never incremental — every stable step re-renders the entire
/// translation from scratch.
///
/// Strategy C ("gated rolling window"): only when Policy A reports a newly stable source
/// commit (or at Final), compares the current translated text against the translated text
/// AS OF THE PREVIOUS STABLE COMMIT (not every partial). A clean extension is appended
/// incrementally; a contradiction is withheld (not appended, mirroring Policy A's own
/// "never retract, wait for agreement" rule) rather than blindly appended as Strategy A
/// does. This is the strategy hypothesized to best balance incrementality and correctness.
/// </summary>
public sealed class AzureAlignedIncrementalTranslationShadow : IIncrementalTranslationShadow
{
    private const string StrategyA = "A_NaiveIndependent";
    private const string StrategyB = "B_CumulativeReTranslation";
    private const string StrategyC = "C_GatedRollingWindow";

    private readonly IDiagnosticLogger _logger;
    private readonly string _sessionTag;
    private readonly int _generation;

    private string? _currentUtteranceId;

    // Strategy A: compares against literally the previous partial's translated text, every step.
    private List<string>? _aPreviousTranslatedTokens;
    private readonly List<string> _aCumulativeTokens = new();
    private int _aRevisionCount;
    private int _aChunkCount;

    // Strategy B: replaced wholesale on each stability-gated step.
    private List<string> _bCumulativeTokens = new();
    private int _bChunkCount;

    // Strategy C: compares against the translated text as of the last stability-gated step only.
    private List<string>? _cReferenceTranslatedTokens;
    private readonly List<string> _cCumulativeTokens = new();
    private int _cRevisionCount; // count of withheld (contradicting) steps
    private int _cChunkCount;

    private DateTimeOffset? _firstStableSourceCommitAt;
    private readonly Dictionary<string, DateTimeOffset?> _firstCandidateAt = new()
    {
        [StrategyA] = null, [StrategyB] = null, [StrategyC] = null,
    };

    public AzureAlignedIncrementalTranslationShadow(IDiagnosticLogger logger, string sessionTag, int generation)
    {
        _logger = logger;
        _sessionTag = sessionTag;
        _generation = generation;
    }

    public IReadOnlyList<StrategyCandidateResult> ObservePartial(IncrementalTranslationInput input)
    {
        if (_currentUtteranceId != input.UtteranceId)
            ResetInternal(input.UtteranceId);

        var results = new List<StrategyCandidateResult>();
        var stabilityGated = input.NewlyStableSourceSegment != null;
        if (stabilityGated) _firstStableSourceCommitAt ??= DateTimeOffset.UtcNow;

        var currentTranslatedTokens = Tokenize(input.LatestPartialTranslatedText);

        // ---- Strategy A: every partial, ungated, blind append ----
        if (_aPreviousTranslatedTokens != null && currentTranslatedTokens.Count > 0)
        {
            var agreedLen = WordLevelCommonPrefixLength(_aPreviousTranslatedTokens, currentTranslatedTokens);
            var delta = currentTranslatedTokens.Skip(agreedLen).ToList();
            if (delta.Count > 0)
            {
                var relation = agreedLen == _aPreviousTranslatedTokens.Count
                    ? IncrementalCandidateRelation.Extends
                    : IncrementalCandidateRelation.Revises;
                if (relation == IncrementalCandidateRelation.Revises) _aRevisionCount++;
                _aCumulativeTokens.AddRange(delta); // blind append — the point being evaluated
                _aChunkCount++;
                _firstCandidateAt[StrategyA] ??= DateTimeOffset.UtcNow;
                results.Add(new StrategyCandidateResult(
                    StrategyA, string.Join(" ", delta), input.SegmentSequence,
                    _aCumulativeTokens.Count, string.Join(" ", _aCumulativeTokens), relation));
            }
        }
        _aPreviousTranslatedTokens = currentTranslatedTokens;

        // ---- Strategy B: stability-gated wholesale replace ----
        if (stabilityGated && currentTranslatedTokens.Count > 0)
        {
            _bCumulativeTokens = currentTranslatedTokens;
            _bChunkCount++;
            _firstCandidateAt[StrategyB] ??= DateTimeOffset.UtcNow;
            results.Add(new StrategyCandidateResult(
                StrategyB, string.Join(" ", currentTranslatedTokens), input.SegmentSequence,
                _bCumulativeTokens.Count, string.Join(" ", _bCumulativeTokens), IncrementalCandidateRelation.Replaces));
        }

        // ---- Strategy C: stability-gated, contradiction-aware incremental ----
        if (stabilityGated && currentTranslatedTokens.Count > 0)
        {
            if (_cReferenceTranslatedTokens == null)
            {
                _cReferenceTranslatedTokens = currentTranslatedTokens;
                if (currentTranslatedTokens.Count > 0)
                {
                    _cCumulativeTokens.AddRange(currentTranslatedTokens);
                    _cChunkCount++;
                    _firstCandidateAt[StrategyC] ??= DateTimeOffset.UtcNow;
                    results.Add(new StrategyCandidateResult(
                        StrategyC, string.Join(" ", currentTranslatedTokens), input.SegmentSequence,
                        _cCumulativeTokens.Count, string.Join(" ", _cCumulativeTokens), IncrementalCandidateRelation.Extends));
                }
            }
            else
            {
                var agreedLen = WordLevelCommonPrefixLength(_cReferenceTranslatedTokens, currentTranslatedTokens);
                if (agreedLen == _cReferenceTranslatedTokens.Count)
                {
                    var delta = currentTranslatedTokens.Skip(agreedLen).ToList();
                    if (delta.Count > 0)
                    {
                        _cCumulativeTokens.AddRange(delta);
                        _cChunkCount++;
                        _firstCandidateAt[StrategyC] ??= DateTimeOffset.UtcNow;
                        results.Add(new StrategyCandidateResult(
                            StrategyC, string.Join(" ", delta), input.SegmentSequence,
                            _cCumulativeTokens.Count, string.Join(" ", _cCumulativeTokens), IncrementalCandidateRelation.Extends));
                    }
                    _cReferenceTranslatedTokens = currentTranslatedTokens;
                }
                else
                {
                    // Contradiction relative to the last stable reference — withhold, do not
                    // append (mirrors Policy A's own regression-safety rule), but DO update
                    // the reference so the next stable step compares against current reality.
                    _cRevisionCount++;
                    _cReferenceTranslatedTokens = currentTranslatedTokens;
                }
            }
        }

        LogPartialStep(input, stabilityGated, results);
        return results;
    }

    public IReadOnlyList<StrategyFinalReconciliation> ObserveFinal(IncrementalTranslationInput input)
    {
        if (_currentUtteranceId != input.UtteranceId)
            ResetInternal(input.UtteranceId);

        var finalTokens = Tokenize(input.FinalTranslatedText);
        var results = new List<StrategyFinalReconciliation>
        {
            BuildReconciliation(StrategyA, _aCumulativeTokens, finalTokens, _aRevisionCount, _aChunkCount),
            BuildReconciliation(StrategyB, _bCumulativeTokens, finalTokens, 0, _bChunkCount),
            BuildReconciliation(StrategyC, _cCumulativeTokens, finalTokens, _cRevisionCount, _cChunkCount),
        };

        LogFinalReconciliation(input, results);
        ResetInternal(null);
        return results;
    }

    public void Reset() => ResetInternal(null);

    private void ResetInternal(string? newUtteranceId)
    {
        _currentUtteranceId = newUtteranceId;
        _aPreviousTranslatedTokens = null;
        _aCumulativeTokens.Clear();
        _aRevisionCount = 0;
        _aChunkCount = 0;
        _bCumulativeTokens = new List<string>();
        _bChunkCount = 0;
        _cReferenceTranslatedTokens = null;
        _cCumulativeTokens.Clear();
        _cRevisionCount = 0;
        _cChunkCount = 0;
        _firstStableSourceCommitAt = null;
        _firstCandidateAt[StrategyA] = null;
        _firstCandidateAt[StrategyB] = null;
        _firstCandidateAt[StrategyC] = null;
    }

    private static StrategyFinalReconciliation BuildReconciliation(
        string strategyName, IReadOnlyList<string> cumulativeTokens, IReadOnlyList<string> finalTokens,
        int revisionCount, int chunkCount)
    {
        // Structural (token-multiset) approximation ONLY — not a semantic/LLM judgment.
        // "Missing" = tokens present (by count) in final more than in cumulative;
        // "duplicate" = tokens present in cumulative more than in final. Order-insensitive,
        // deliberately simple and auditable, consistent with this project's no-LLM constraint.
        var finalCounts = CountMultiset(finalTokens);
        var cumulativeCounts = CountMultiset(cumulativeTokens);

        var missing = 0;
        foreach (var (token, finalCount) in finalCounts)
        {
            var have = cumulativeCounts.GetValueOrDefault(token, 0);
            if (finalCount > have) missing += finalCount - have;
        }

        var duplicate = 0;
        foreach (var (token, cumCount) in cumulativeCounts)
        {
            var need = finalCounts.GetValueOrDefault(token, 0);
            if (cumCount > need) duplicate += cumCount - need;
        }

        return new StrategyFinalReconciliation(
            strategyName, cumulativeTokens.Count, finalTokens.Count, missing, duplicate, revisionCount, chunkCount);
    }

    private static Dictionary<string, int> CountMultiset(IReadOnlyList<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens) counts[t] = counts.GetValueOrDefault(t, 0) + 1;
        return counts;
    }

    private void LogPartialStep(IncrementalTranslationInput input, bool stabilityGated, IReadOnlyList<StrategyCandidateResult> results)
    {
        var elapsedFirstStableToFirstCandidateMsA = ElapsedOrNull(_firstStableSourceCommitAt, _firstCandidateAt[StrategyA]);
        var elapsedFirstStableToFirstCandidateMsB = ElapsedOrNull(_firstStableSourceCommitAt, _firstCandidateAt[StrategyB]);
        var elapsedFirstStableToFirstCandidateMsC = ElapsedOrNull(_firstStableSourceCommitAt, _firstCandidateAt[StrategyC]);

        _logger.Log(_sessionTag, "TranslationShadowPartial",
            $"generation={_generation} utteranceId={input.UtteranceId} segmentSequence={input.SegmentSequence} " +
            $"stabilityGated={stabilityGated} candidateCount={results.Count} " +
            $"aCumulativeTokens={_aCumulativeTokens.Count} aChunkCount={_aChunkCount} aRevisionCount={_aRevisionCount} " +
            $"bCumulativeTokens={_bCumulativeTokens.Count} bChunkCount={_bChunkCount} " +
            $"cCumulativeTokens={_cCumulativeTokens.Count} cChunkCount={_cChunkCount} cRevisionCount={_cRevisionCount} " +
            $"elapsedFirstStableToFirstCandidateMsA={FormatMs(elapsedFirstStableToFirstCandidateMsA)} " +
            $"elapsedFirstStableToFirstCandidateMsB={FormatMs(elapsedFirstStableToFirstCandidateMsB)} " +
            $"elapsedFirstStableToFirstCandidateMsC={FormatMs(elapsedFirstStableToFirstCandidateMsC)}");
    }

    private void LogFinalReconciliation(IncrementalTranslationInput input, IReadOnlyList<StrategyFinalReconciliation> results)
    {
        foreach (var r in results)
        {
            _logger.Log(_sessionTag, "TranslationShadowFinalReconciliation",
                $"generation={_generation} utteranceId={input.UtteranceId} strategy={r.StrategyName} " +
                $"cumulativeTokenCount={r.CumulativeTokenCount} finalTokenCount={r.FinalTokenCount} " +
                $"approxMissingTokenCount={r.ApproxMissingTokenCount} approxDuplicateTokenCount={r.ApproxDuplicateTokenCount} " +
                $"revisionCount={r.RevisionCount} chunkCount={r.ChunkCount}");
        }
    }

    private static double? ElapsedOrNull(DateTimeOffset? from, DateTimeOffset? to) =>
        from.HasValue && to.HasValue ? (to.Value - from.Value).TotalMilliseconds : null;

    private static string FormatMs(double? ms) => ms.HasValue ? ms.Value.ToString("F0") : "n/a";

    private static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static int WordLevelCommonPrefixLength(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var n = Math.Min(a.Count, b.Count);
        var i = 0;
        while (i < n && string.Equals(a[i], b[i], StringComparison.Ordinal)) i++;
        return i;
    }
}
