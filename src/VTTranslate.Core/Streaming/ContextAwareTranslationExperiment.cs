using System.Diagnostics;
using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.10. Compares two translation-request-construction
/// approaches against the real, independent Azure Translator API — both built on the
/// same base <c>/translate</c> endpoint validated in Step 5.6b; NEITHER approach uses any
/// native context/session/document API parameter, because none exists on that endpoint
/// (see docs/design-notes/context-aware-translation-strategy-research.md §1). Approach A's
/// "context" is application-side string concatenation, explicitly labeled as such
/// throughout — never described as native Translator context.
///
/// Approach A ("base Translator + accumulated source context"): translates
/// "{previously committed source} {newly committed segment}" as ONE request string —
/// the model sees the preceding words only because they are literally present in the
/// input text, not through any API-level context mechanism.
///
/// Approach B ("base Translator + minimal current segment"): translates ONLY the newly
/// committed segment, alone, with no preceding text — the cheapest possible request, used
/// to measure how much cross-segment coherence is lost without any context at all.
///
/// Approach C ("available supported contextual/custom capability") is NOT implemented in
/// this class — per Step 5.10's explicit decision, no provisioned Custom Translator or
/// other contextual Azure capability has been demonstrated for this project's resource,
/// and none was fabricated. See the governing documentation for the full "UNAVAILABLE /
/// NOT VERIFIED IN THIS SESSION" statement.
///
/// Both approaches share the same conservative speech-readiness gate as Step 5.9's
/// Architecture C (word-level non-contradiction across consecutive candidates; 2
/// consecutive agreements → CandidateForSpeech; 3 consecutive → Speakable) — reused by
/// value (same thresholds), not by reference, to keep this experiment fully isolated.
///
/// Has no events and no reference to production translation-dispatch, TTS, playback, or
/// UI. Never wired into <c>AzureSpeechTranslationProvider</c>.
/// </summary>
public sealed class ContextAwareTranslationExperiment
{
    public const int MinConsecutiveForCandidateForSpeech = 2;
    public const int MinConsecutiveForSpeakable = 3;

    private readonly IStreamingStabilityEngine _sourceEngine;
    private readonly IIncrementalTranslationProvider _translationProvider;
    private readonly IDiagnosticLogger _logger;
    private readonly string _sessionTag;
    private readonly int _generation;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;

    private string? _currentUtteranceId;

    // Approach A: accumulated-context + segment, single request string.
    private readonly ApproachTracker _a = new();
    // Approach B: segment-only, no context.
    private readonly ApproachTracker _b = new();

    public ContextAwareTranslationExperiment(
        IStreamingStabilityEngine sourceEngine, IIncrementalTranslationProvider translationProvider,
        IDiagnosticLogger logger, string sessionTag, int generation, string sourceLanguage, string targetLanguage)
    {
        _sourceEngine = sourceEngine;
        _translationProvider = translationProvider;
        _logger = logger;
        _sessionTag = sessionTag;
        _generation = generation;
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
    }

    public async Task<IReadOnlyList<ContextAwareTranslationStepResult>> ObservePartialAsync(
        ContextAwareSourceObservation observation, CancellationToken ct)
    {
        if (_currentUtteranceId != observation.UtteranceId) ResetInternal(observation.UtteranceId);

        var stability = _sourceEngine.ProcessPartial(
            new PartialSourceEvent(observation.UtteranceId, observation.PartialSequence, observation.SourceText, observation.Timestamp));

        var results = new List<ContextAwareTranslationStepResult>();
        if (!stability.ShouldEmit || string.IsNullOrEmpty(stability.NewlyCommittedSegment)) return results;

        // Approach A: previously-committed context (everything before this segment) + the
        // new segment, concatenated into ONE request string — application-side context
        // construction, never a native API parameter.
        var previouslyCommittedContext = stability.CommittedSourceText.Length > stability.NewlyCommittedSegment.Length
            ? stability.CommittedSourceText[..^stability.NewlyCommittedSegment.Length].TrimEnd()
            : "";
        var aRequestText = string.IsNullOrEmpty(previouslyCommittedContext)
            ? stability.NewlyCommittedSegment
            : $"{previouslyCommittedContext} {stability.NewlyCommittedSegment}";
        results.Add(await RunApproachAsync("A_AccumulatedContext", _a, observation, aRequestText,
            previouslyCommittedContext.Length, stability.NewlyCommittedSegment.Length, ct));

        // Approach B: the new segment alone, no context whatsoever.
        results.Add(await RunApproachAsync("B_MinimalSegment", _b, observation, stability.NewlyCommittedSegment,
            0, stability.NewlyCommittedSegment.Length, ct));

        return results;
    }

    public async Task<IReadOnlyList<ContextAwareUtteranceSummary>> ObserveFinalAsync(
        ContextAwareSourceObservation observation, CancellationToken ct)
    {
        if (_currentUtteranceId != observation.UtteranceId) ResetInternal(observation.UtteranceId);

        _sourceEngine.ProcessFinal(
            new FinalSourceEvent(observation.UtteranceId, observation.PartialSequence, observation.SourceText, observation.Timestamp));

        var sw = Stopwatch.StartNew();
        var finalTranslation = await _translationProvider.TranslateAsync(
            new TranslationProviderRequest(_sourceLanguage, _targetLanguage, observation.SourceText), ct);
        sw.Stop();
        var finalTokens = Tokenize(finalTranslation.Success ? finalTranslation.CandidateTranslatedText : "");

        var summaries = new List<ContextAwareUtteranceSummary>
        {
            BuildSummary(observation.UtteranceId, "A_AccumulatedContext", _a, finalTokens, finalTranslation.Success, sw),
            BuildSummary(observation.UtteranceId, "B_MinimalSegment", _b, finalTokens, finalTranslation.Success, sw),
        };

        foreach (var s in summaries)
            _logger.Log(_sessionTag, "ContextAwareFinalReconciliation",
                $"generation={_generation} utteranceId={s.UtteranceId} approach={s.Approach} requests={s.RequestCount} " +
                $"contradictions={s.ContradictionCount} revisions={s.RevisionCount} contextChars={s.TotalContextCharactersSent} " +
                $"segmentChars={s.TotalSegmentCharactersSent} missing={FormatNullableInt(s.ApproxMissingTokenCount)} " +
                $"duplicate={FormatNullableInt(s.ApproxDuplicateTokenCount)} finalSucceeded={s.FinalTranslationSucceeded} " +
                $"anyReachedSpeakable={s.AnyContentReachedSpeakable}");

        ResetInternal(null);
        return summaries;
    }

    public void Reset() => ResetInternal(null);

    private async Task<ContextAwareTranslationStepResult> RunApproachAsync(
        string approachName, ApproachTracker tracker, ContextAwareSourceObservation obs, string requestText,
        int contextChars, int segmentChars, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = await _translationProvider.TranslateAsync(new TranslationProviderRequest(_sourceLanguage, _targetLanguage, requestText), ct);
        sw.Stop();

        tracker.RequestCount++;
        tracker.ContextCharsSent += contextChars;
        tracker.SegmentCharsSent += segmentChars;
        tracker.FirstLatencyMs ??= sw.Elapsed.TotalMilliseconds;

        if (!result.Success)
        {
            tracker.ConsecutiveStableCount = 0;
            LogStep(approachName, obs, TranslationRevisionKind.Incomplete, ContextAwareSpeechReadiness.NotSpeakable, sw.Elapsed.TotalMilliseconds, tracker.CumulativeTokens.Count);
            return new ContextAwareTranslationStepResult(approachName, ContextAwareSourceState.Stable, TranslationRevisionKind.Incomplete,
                ContextAwareSpeechReadiness.NotSpeakable, obs.PartialSequence, tracker.RequestCount, contextChars, segmentChars, sw.Elapsed.TotalMilliseconds, tracker.CumulativeTokens.Count);
        }

        var newTokens = Tokenize(result.CandidateTranslatedText);
        var kind = ClassifyRevision(tracker.PreviousTokens, newTokens);

        ContextAwareSpeechReadiness speechState;
        if (kind is TranslationRevisionKind.Contradiction or TranslationRevisionKind.Replacement or TranslationRevisionKind.Incomplete)
        {
            if (kind != TranslationRevisionKind.Incomplete) tracker.RevisionCount++;
            if (kind == TranslationRevisionKind.Contradiction) tracker.ContradictionCount++;
            tracker.ConsecutiveStableCount = 0;
            speechState = ContextAwareSpeechReadiness.NotSpeakable;
        }
        else
        {
            tracker.ConsecutiveStableCount++;
            if (tracker.ConsecutiveStableCount >= MinConsecutiveForSpeakable)
            {
                speechState = ContextAwareSpeechReadiness.Speakable;
                tracker.AnyReachedSpeakable = true;
            }
            else if (tracker.ConsecutiveStableCount >= MinConsecutiveForCandidateForSpeech)
            {
                speechState = ContextAwareSpeechReadiness.CandidateForSpeech;
                tracker.FirstCandidateForSpeechDelayMs ??= sw.Elapsed.TotalMilliseconds;
            }
            else
            {
                speechState = ContextAwareSpeechReadiness.NotSpeakable;
            }
        }

        if (kind != TranslationRevisionKind.Incomplete)
        {
            tracker.CumulativeTokens = newTokens;
            tracker.PreviousTokens = newTokens;
        }

        LogStep(approachName, obs, kind, speechState, sw.Elapsed.TotalMilliseconds, tracker.CumulativeTokens.Count);

        return new ContextAwareTranslationStepResult(approachName, ContextAwareSourceState.Stable, kind, speechState,
            obs.PartialSequence, tracker.RequestCount, contextChars, segmentChars, sw.Elapsed.TotalMilliseconds, tracker.CumulativeTokens.Count);
    }

    /// <summary>
    /// Token-level HEURISTIC classification — NOT a linguistic parse. <see cref="TranslationRevisionKind.Contradiction"/>
    /// if the very first token differs (cheapest, strongest signal); <see cref="TranslationRevisionKind.Extension"/>
    /// if the previous candidate is an exact prefix of the new one; <see cref="TranslationRevisionKind.Identical"/>
    /// if byte-for-byte equal; otherwise the overlap ratio (longest common prefix length ÷
    /// shorter candidate's length) decides <see cref="TranslationRevisionKind.ParaphraseOrRestructuring"/>
    /// (&gt;=50%) vs. <see cref="TranslationRevisionKind.Replacement"/> (&lt;50%).
    /// </summary>
    private static TranslationRevisionKind ClassifyRevision(List<string>? previous, List<string> current)
    {
        if (current.Count == 0) return TranslationRevisionKind.Incomplete;
        if (previous == null) return TranslationRevisionKind.FirstCandidate;
        if (previous.SequenceEqual(current)) return TranslationRevisionKind.Identical;

        var lcp = 0;
        var n = Math.Min(previous.Count, current.Count);
        while (lcp < n && string.Equals(previous[lcp], current[lcp], StringComparison.Ordinal)) lcp++;

        if (lcp == 0) return TranslationRevisionKind.Contradiction;
        if (lcp == previous.Count) return TranslationRevisionKind.Extension;

        var shorterLength = Math.Min(previous.Count, current.Count);
        var overlapRatio = shorterLength == 0 ? 0.0 : (double)lcp / shorterLength;
        return overlapRatio >= 0.5 ? TranslationRevisionKind.ParaphraseOrRestructuring : TranslationRevisionKind.Replacement;
    }

    private ContextAwareUtteranceSummary BuildSummary(
        string utteranceId, string approach, ApproachTracker tracker, IReadOnlyList<string> finalTokens,
        bool finalSucceeded, Stopwatch finalSw)
    {
        if (!finalSucceeded)
            return new ContextAwareUtteranceSummary(utteranceId, approach, tracker.RequestCount, tracker.ContradictionCount,
                tracker.RevisionCount, tracker.ContextCharsSent, tracker.SegmentCharsSent, null, null,
                tracker.FirstLatencyMs, tracker.FirstCandidateForSpeechDelayMs, null, false, tracker.AnyReachedSpeakable);

        if (tracker.RequestCount == 0)
            return new ContextAwareUtteranceSummary(utteranceId, approach, 0, 0, 0, 0, 0, null, null,
                null, null, finalSw.Elapsed.TotalMilliseconds, true, false);

        var finalCounts = CountMultiset(finalTokens);
        var cumulativeCounts = CountMultiset(tracker.CumulativeTokens);
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

        return new ContextAwareUtteranceSummary(utteranceId, approach, tracker.RequestCount, tracker.ContradictionCount,
            tracker.RevisionCount, tracker.ContextCharsSent, tracker.SegmentCharsSent, missing, duplicate,
            tracker.FirstLatencyMs, tracker.FirstCandidateForSpeechDelayMs, finalSw.Elapsed.TotalMilliseconds, true, tracker.AnyReachedSpeakable);
    }

    private static Dictionary<string, int> CountMultiset(IReadOnlyList<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens) counts[t] = counts.GetValueOrDefault(t, 0) + 1;
        return counts;
    }

    private void LogStep(string approach, ContextAwareSourceObservation obs, TranslationRevisionKind kind,
        ContextAwareSpeechReadiness speechState, double latencyMs, int cumulativeTokenCount)
    {
        _logger.Log(_sessionTag, "ContextAwareTranslationStep",
            $"generation={_generation} approach={approach} utteranceId={obs.UtteranceId} partialSequence={obs.PartialSequence} " +
            $"revisionKind={kind} speechState={speechState} latencyMs={latencyMs:F0} cumulativeTokenCount={cumulativeTokenCount}");
    }

    private static string FormatNullableInt(int? value) => value.HasValue ? value.Value.ToString() : "n/a";

    private void ResetInternal(string? newUtteranceId)
    {
        _currentUtteranceId = newUtteranceId;
        _a.Reset();
        _b.Reset();
        _sourceEngine.Reset();
    }

    private static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? new List<string>() : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    private sealed class ApproachTracker
    {
        public List<string> CumulativeTokens = new();
        public List<string>? PreviousTokens;
        public int ConsecutiveStableCount;
        public int RequestCount;
        public int RevisionCount;
        public int ContradictionCount;
        public int ContextCharsSent;
        public int SegmentCharsSent;
        public double? FirstLatencyMs;
        public double? FirstCandidateForSpeechDelayMs;
        public bool AnyReachedSpeakable;

        public void Reset()
        {
            CumulativeTokens = new List<string>();
            PreviousTokens = null;
            ConsecutiveStableCount = 0;
            RequestCount = 0;
            RevisionCount = 0;
            ContradictionCount = 0;
            ContextCharsSent = 0;
            SegmentCharsSent = 0;
            FirstLatencyMs = null;
            FirstCandidateForSpeechDelayMs = null;
            AnyReachedSpeakable = false;
        }
    }
}
