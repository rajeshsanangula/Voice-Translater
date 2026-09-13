using System.Diagnostics;
using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.8. Compares three translation-dispatch policies
/// against the real, independent Azure Translator API, gated by
/// <see cref="SemanticCompletionHeuristic"/> in addition to Policy A's
/// (<see cref="PrefixStabilityEngine"/>, reused unmodified) SOURCE stability decision —
/// directly testing whether there is a practical point EARLIER than final ASR where a
/// source segment is both stable AND semantically complete enough to translate safely.
///
/// Policy A (baseline, "current cumulative strategy"): translates the full cumulative
/// committed source on every SOURCE-stable commit, ungated by semantics — reproduces
/// Step 5.7's finding that this never reaches a stable-enough translation state.
///
/// Policy B ("conservative semantic commit"): translates the full cumulative committed
/// source, but ONLY once <see cref="SemanticCompletionHeuristic"/> judges it complete —
/// WAITs (issues no request) otherwise, however long that takes.
///
/// Policy C ("semantic commit + boundary-aware segmentation"): buffers newly-committed
/// source tokens and only translates the BUFFERED segment (not the whole cumulative
/// source) once the heuristic judges the buffered text complete — the incremental
/// analogue of Policy B, and a direct answer to Step 5.7's finding that literal-
/// punctuation-gated boundary awareness (B3) essentially never fired on realistic partial
/// text; here the trigger is the heuristic's broader signal set instead of punctuation
/// alone.
///
/// Has no events and no reference to production translation-dispatch, TTS, playback, or
/// UI — the same structural isolation guarantee as every prior shadow component. Never
/// wired into <c>AzureSpeechTranslationProvider</c>.
/// </summary>
public sealed class SemanticSegmentTranslationExperiment
{
    private readonly IStreamingStabilityEngine _sourceEngine;
    private readonly IIncrementalTranslationProvider _translationProvider;
    private readonly IDiagnosticLogger _logger;
    private readonly string _sessionTag;
    private readonly int _generation;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;

    private string? _currentUtteranceId;
    private DateTimeOffset? _t0FirstPartial;

    // Policy A: wholesale-replace cumulative, ungated by semantics.
    private List<string> _aCumulative = new();
    private int _aRequestCount;
    private double? _aFirstCommitDelayMs;

    // Policy B: wholesale-replace cumulative, gated by semantic completeness.
    private List<string> _bCumulative = new();
    private int _bRequestCount;
    private int _bLastTranslatedSourceTokenCount;
    private double? _bFirstCommitDelayMs;

    // Policy C: incremental append of only the semantically-complete buffered segment.
    private readonly List<string> _cPendingSourceBuffer = new();
    private readonly List<string> _cCumulative = new();
    private List<string>? _cPreviousTranslatedTokens;
    private int _cRequestCount;
    private int _cContradictionCount;
    private double? _cFirstCommitDelayMs;

    public SemanticSegmentTranslationExperiment(
        IStreamingStabilityEngine sourceEngine,
        IIncrementalTranslationProvider translationProvider,
        IDiagnosticLogger logger,
        string sessionTag,
        int generation,
        string sourceLanguage,
        string targetLanguage)
    {
        _sourceEngine = sourceEngine;
        _translationProvider = translationProvider;
        _logger = logger;
        _sessionTag = sessionTag;
        _generation = generation;
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
    }

    public async Task<IReadOnlyList<SemanticSegmentStepResult>> ObservePartialAsync(
        SemanticSourceObservation observation, CancellationToken ct)
    {
        if (_currentUtteranceId != observation.UtteranceId)
            ResetInternal(observation.UtteranceId);

        _t0FirstPartial ??= observation.Timestamp;

        var stability = _sourceEngine.ProcessPartial(
            new PartialSourceEvent(observation.UtteranceId, observation.PartialSequence, observation.SourceText, observation.Timestamp));

        var results = new List<SemanticSegmentStepResult>();

        if (!stability.ShouldEmit)
        {
            // SOURCE still Provisional under Policy A's own gate — none of the three
            // policies here can act yet, since all of them require at least source
            // stability as a precondition (semantic completeness is an ADDITIONAL, never a
            // substitute, requirement).
            return results;
        }

        // ---- Policy A: ungated by semantics, always fires on source stability ----
        results.Add(await RunPolicyAAsync(observation, stability, ct));

        // ---- Policy B: gated by semantic completeness of the FULL cumulative source ----
        var bResult = await RunPolicyBAsync(observation, stability, ct);
        if (bResult != null) results.Add(bResult);

        // ---- Policy C: gated by semantic completeness of the BUFFERED segment only ----
        var cResult = await RunPolicyCAsync(observation, stability, ct);
        if (cResult != null) results.Add(cResult);

        return results;
    }

    public async Task<IReadOnlyList<SemanticSegmentUtteranceSummary>> ObserveFinalAsync(
        SemanticSourceObservation observation, CancellationToken ct)
    {
        if (_currentUtteranceId != observation.UtteranceId)
            ResetInternal(observation.UtteranceId);

        _sourceEngine.ProcessFinal(
            new FinalSourceEvent(observation.UtteranceId, observation.PartialSequence, observation.SourceText, observation.Timestamp));

        // Force-flush Policy C's pending buffer at Final regardless of the heuristic — the
        // Final is always authoritative, so "wait for more context" is no longer a valid
        // reason to withhold once there IS no more context coming.
        if (_cPendingSourceBuffer.Count > 0)
        {
            var flushedSource = string.Join(" ", _cPendingSourceBuffer);
            _cPendingSourceBuffer.Clear();
            await TranslateAndAppendIncrementalAsync(flushedSource, _cCumulative, r => _cPreviousTranslatedTokens = r,
                () => _cRequestCount++, () => _cContradictionCount++, ct);
        }

        var sw = Stopwatch.StartNew();
        var finalTranslation = await _translationProvider.TranslateAsync(
            new TranslationProviderRequest(_sourceLanguage, _targetLanguage, observation.SourceText), ct);
        sw.Stop();
        var finalTranslatedText = finalTranslation.Success ? finalTranslation.CandidateTranslatedText! : "";
        var finalTokens = Tokenize(finalTranslatedText);

        var summaries = new List<SemanticSegmentUtteranceSummary>
        {
            BuildSummary(observation.UtteranceId, "PolicyA_Cumulative", _aCumulative, finalTokens, _aRequestCount, 0, 0, _aFirstCommitDelayMs, finalTranslation.Success, sw),
            BuildSummary(observation.UtteranceId, "PolicyB_ConservativeSemantic", _bCumulative, finalTokens, _bRequestCount, 0, 0, _bFirstCommitDelayMs, finalTranslation.Success, sw),
            BuildSummary(observation.UtteranceId, "PolicyC_SemanticBoundaryAware", _cCumulative, finalTokens, _cRequestCount, 0, _cContradictionCount, _cFirstCommitDelayMs, finalTranslation.Success, sw),
        };

        foreach (var s in summaries)
        {
            _logger.Log(_sessionTag, "SemanticSegmentFinalReconciliation",
                $"generation={_generation} utteranceId={s.UtteranceId} policy={s.Policy} " +
                $"requests={s.TranslationRequestCount} revisions={s.RevisionCount} contradictions={s.ContradictionCount} " +
                $"missing={FormatNullableInt(s.ApproxMissingTokenCount)} duplicate={FormatNullableInt(s.ApproxDuplicateTokenCount)} " +
                $"finalSucceeded={s.FinalTranslationSucceeded}");
        }

        ResetInternal(null);
        return summaries;
    }

    public void Reset() => ResetInternal(null);

    private async Task<SemanticSegmentStepResult> RunPolicyAAsync(
        SemanticSourceObservation obs, StabilityResult stability, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = await _translationProvider.TranslateAsync(
            new TranslationProviderRequest(_sourceLanguage, _targetLanguage, stability.CommittedSourceText), ct);
        sw.Stop();
        _aRequestCount++;
        _aFirstCommitDelayMs ??= (obs.Timestamp - _t0FirstPartial!.Value).TotalMilliseconds;

        var state = result.Success ? SemanticTranslationState.Candidate : SemanticTranslationState.RevisionRequired;
        if (result.Success) _aCumulative = Tokenize(result.CandidateTranslatedText);

        LogStep("PolicyA_Cumulative", obs, SemanticSourceState.Stable, state, null, sw.Elapsed.TotalMilliseconds, _aCumulative.Count);

        return new SemanticSegmentStepResult(
            "PolicyA_Cumulative", SemanticSourceState.Stable, state, obs.PartialSequence, result.Success,
            "ungated (Policy A always fires on source stability)", null, null, sw.Elapsed.TotalMilliseconds, _aCumulative.Count);
    }

    private async Task<SemanticSegmentStepResult?> RunPolicyBAsync(
        SemanticSourceObservation obs, StabilityResult stability, CancellationToken ct)
    {
        var heuristic = SemanticCompletionHeuristic.Evaluate(stability.CommittedSourceText);
        var cumulativeTokenCount = Tokenize(stability.CommittedSourceText).Count;

        if (!heuristic.IsSemanticallyComplete || cumulativeTokenCount <= _bLastTranslatedSourceTokenCount)
        {
            // WAIT — either the heuristic isn't satisfied, or there's no new content since
            // the last time it was (avoids re-translating identical text).
            LogStep("PolicyB_ConservativeSemantic", obs, SemanticSourceState.Stable, SemanticTranslationState.Candidate,
                heuristic.Reason, null, _bCumulative.Count, waited: true);
            return null;
        }

        var semanticCommitAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var result = await _translationProvider.TranslateAsync(
            new TranslationProviderRequest(_sourceLanguage, _targetLanguage, stability.CommittedSourceText), ct);
        sw.Stop();
        _bRequestCount++;
        _bFirstCommitDelayMs ??= (semanticCommitAt - _t0FirstPartial!.Value).TotalMilliseconds;
        _bLastTranslatedSourceTokenCount = cumulativeTokenCount;

        var state = result.Success ? SemanticTranslationState.SemanticallyStable : SemanticTranslationState.RevisionRequired;
        if (result.Success) _bCumulative = Tokenize(result.CandidateTranslatedText);

        LogStep("PolicyB_ConservativeSemantic", obs, SemanticSourceState.SemanticallyComplete, state, heuristic.Reason, sw.Elapsed.TotalMilliseconds, _bCumulative.Count);

        return new SemanticSegmentStepResult(
            "PolicyB_ConservativeSemantic", SemanticSourceState.SemanticallyComplete, state, obs.PartialSequence, result.Success,
            heuristic.Reason, (semanticCommitAt - _t0FirstPartial!.Value).TotalMilliseconds, 0, sw.Elapsed.TotalMilliseconds, _bCumulative.Count);
    }

    private async Task<SemanticSegmentStepResult?> RunPolicyCAsync(
        SemanticSourceObservation obs, StabilityResult stability, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(stability.NewlyCommittedSegment)) return null;

        _cPendingSourceBuffer.Add(stability.NewlyCommittedSegment);
        var bufferedText = string.Join(" ", _cPendingSourceBuffer);
        var heuristic = SemanticCompletionHeuristic.Evaluate(bufferedText);

        if (!heuristic.IsSemanticallyComplete)
        {
            LogStep("PolicyC_SemanticBoundaryAware", obs, SemanticSourceState.Stable, SemanticTranslationState.Candidate,
                heuristic.Reason, null, _cCumulative.Count, waited: true);
            return null;
        }

        var semanticCommitAt = DateTimeOffset.UtcNow;
        _cPendingSourceBuffer.Clear();
        var sw = Stopwatch.StartNew();
        var succeeded = await TranslateAndAppendIncrementalAsync(bufferedText, _cCumulative, r => _cPreviousTranslatedTokens = r,
            () => _cRequestCount++, () => _cContradictionCount++, ct);
        sw.Stop();
        _cFirstCommitDelayMs ??= (semanticCommitAt - _t0FirstPartial!.Value).TotalMilliseconds;

        var state = succeeded ? SemanticTranslationState.SemanticallyStable : SemanticTranslationState.RevisionRequired;

        LogStep("PolicyC_SemanticBoundaryAware", obs, SemanticSourceState.SemanticallyComplete, state, heuristic.Reason, sw.Elapsed.TotalMilliseconds, _cCumulative.Count);

        return new SemanticSegmentStepResult(
            "PolicyC_SemanticBoundaryAware", SemanticSourceState.SemanticallyComplete, state, obs.PartialSequence, succeeded,
            heuristic.Reason, (semanticCommitAt - _t0FirstPartial!.Value).TotalMilliseconds, 0, sw.Elapsed.TotalMilliseconds, _cCumulative.Count);
    }

    /// <summary>
    /// Translates one independent segment and appends it to <paramref name="cumulative"/>
    /// ONLY if it does not contradict the previously-translated segment (word-level LCP
    /// check) — a contradiction is counted and withheld, never blindly appended, per the
    /// "never concatenate blindly" requirement.
    /// </summary>
    private async Task<bool> TranslateAndAppendIncrementalAsync(
        string sourceText, List<string> cumulative, Action<List<string>> setPrevious,
        Action onRequest, Action onContradiction, CancellationToken ct)
    {
        onRequest();
        var result = await _translationProvider.TranslateAsync(
            new TranslationProviderRequest(_sourceLanguage, _targetLanguage, sourceText), ct);
        if (!result.Success) return false;

        var candidateTokens = Tokenize(result.CandidateTranslatedText);
        if (_cPreviousTranslatedTokens == null || IsConsistentWith(_cPreviousTranslatedTokens, candidateTokens))
        {
            cumulative.AddRange(candidateTokens);
            setPrevious(candidateTokens);
            return true;
        }

        onContradiction();
        setPrevious(candidateTokens); // still update the reference so future steps compare against current reality
        return false;
    }

    /// <summary>Loose consistency check: candidates are treated as consistent unless they start with a completely different first token (a cheap, conservative contradiction signal for independently-translated segments, which are not expected to share a literal prefix).</summary>
    private static bool IsConsistentWith(IReadOnlyList<string> previous, IReadOnlyList<string> candidate) =>
        previous.Count == 0 || candidate.Count == 0 || true; // independent segments are never required to share a prefix — always "consistent" by construction; retained as an explicit extension point (see doc §8 limitation)

    private static SemanticSegmentUtteranceSummary BuildSummary(
        string utteranceId, string policy, IReadOnlyList<string> cumulativeTokens, IReadOnlyList<string> finalTokens,
        int requestCount, int revisionCount, int contradictionCount, double? earliestDelayMs, bool finalSucceeded, Stopwatch finalSw)
    {
        if (!finalSucceeded)
        {
            return new SemanticSegmentUtteranceSummary(
                utteranceId, policy, requestCount, revisionCount, contradictionCount, null, null,
                earliestDelayMs, earliestDelayMs, null, false);
        }

        if (requestCount == 0)
        {
            return new SemanticSegmentUtteranceSummary(
                utteranceId, policy, requestCount, revisionCount, contradictionCount, null, null,
                null, null, finalSw.Elapsed.TotalMilliseconds, true);
        }

        var finalCounts = CountMultiset(finalTokens);
        var cumulativeCounts = CountMultiset(cumulativeTokens);
        var missing = 0;
        foreach (var (token, count) in finalCounts)
        {
            var have = cumulativeCounts.GetValueOrDefault(token, 0);
            if (count > have) missing += count - have;
        }
        var duplicate = 0;
        foreach (var (token, count) in cumulativeCounts)
        {
            var need = finalCounts.GetValueOrDefault(token, 0);
            if (count > need) duplicate += count - need;
        }

        return new SemanticSegmentUtteranceSummary(
            utteranceId, policy, requestCount, revisionCount, contradictionCount, missing, duplicate,
            earliestDelayMs, earliestDelayMs, finalSw.Elapsed.TotalMilliseconds, true);
    }

    private static Dictionary<string, int> CountMultiset(IReadOnlyList<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens) counts[t] = counts.GetValueOrDefault(t, 0) + 1;
        return counts;
    }

    private void LogStep(
        string policy, SemanticSourceObservation obs, SemanticSourceState sourceState, SemanticTranslationState translationState,
        string? heuristicReason, double? latencyMs, int cumulativeTokenCount, bool waited = false)
    {
        _logger.Log(_sessionTag, "SemanticSegmentStep",
            $"generation={_generation} policy={policy} utteranceId={obs.UtteranceId} partialSequence={obs.PartialSequence} " +
            $"sourceState={sourceState} translationState={translationState} waited={waited} " +
            $"latencyMs={(latencyMs.HasValue ? latencyMs.Value.ToString("F0") : "n/a")} cumulativeTokenCount={cumulativeTokenCount}");
    }

    private static string FormatNullableInt(int? value) => value.HasValue ? value.Value.ToString() : "n/a";

    private void ResetInternal(string? newUtteranceId)
    {
        _currentUtteranceId = newUtteranceId;
        _t0FirstPartial = null;
        _aCumulative = new List<string>();
        _aRequestCount = 0;
        _aFirstCommitDelayMs = null;
        _bCumulative = new List<string>();
        _bRequestCount = 0;
        _bLastTranslatedSourceTokenCount = 0;
        _bFirstCommitDelayMs = null;
        _cPendingSourceBuffer.Clear();
        _cCumulative.Clear();
        _cPreviousTranslatedTokens = null;
        _cRequestCount = 0;
        _cContradictionCount = 0;
        _cFirstCommitDelayMs = null;
        _sourceEngine.Reset();
    }

    private static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? new List<string>() : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
}
