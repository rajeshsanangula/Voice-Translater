using System.Diagnostics;
using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.11. Compares five bounded APPLICATION-SIDE CONTEXT
/// CONSTRUCTION strategies (never native Translator context/session — no such capability
/// is demonstrated on the base API used throughout this project; see
/// docs/design-notes/context-window-optimization-experiment.md §2) against the real,
/// independent Azure Translator API, to find the smallest context that materially
/// improves translation-side stability without unbounded request growth.
///
/// C0: current segment only, zero context (Step 5.10's Approach B).
/// C1: current segment + the single immediately-preceding committed source segment.
/// C2: current segment + the last TWO committed source segments.
/// C3: current segment + a bounded recent-context window, <see cref="C3CharacterLimit"/> characters.
/// C4: current segment + a LARGER bounded window, <see cref="C4CharacterLimit"/> characters (for comparison — never unlimited).
///
/// All five share the same real, unmodified Policy A (<see cref="PrefixStabilityEngine"/>)
/// SOURCE-stability decisions and the same conservative speech-readiness gate used in
/// Steps 5.9/5.10 (2 consecutive non-contradicting translations → CandidateForSpeech, 3 →
/// Speakable) — reused by value (same thresholds), not by reference, for isolation.
///
/// Has no events and no reference to production translation-dispatch, TTS, playback, or
/// UI. Never wired into <c>AzureSpeechTranslationProvider</c>.
/// </summary>
public sealed class ContextWindowTranslationExperiment
{
    public static readonly string[] StrategyNames = { "C0_NoContext", "C1_OnePrecedingUnit", "C2_TwoPrecedingUnits", "C3_BoundedSmall", "C4_BoundedLarge" };
    public const int C3CharacterLimit = 30;
    public const int C4CharacterLimit = 80;
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
    private readonly List<string> _committedSegmentHistory = new();
    private readonly Dictionary<string, StrategyTracker> _trackers =
        StrategyNames.ToDictionary(n => n, _ => new StrategyTracker());

    public ContextWindowTranslationExperiment(
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

    public async Task<IReadOnlyList<ContextWindowStepResult>> ObservePartialAsync(
        ContextWindowSourceObservation observation, CancellationToken ct)
    {
        if (_currentUtteranceId != observation.UtteranceId) ResetInternal(observation.UtteranceId);

        var t0 = observation.Timestamp;
        var stability = _sourceEngine.ProcessPartial(
            new PartialSourceEvent(observation.UtteranceId, observation.PartialSequence, observation.SourceText, observation.Timestamp));

        var results = new List<ContextWindowStepResult>();
        if (!stability.ShouldEmit || string.IsNullOrEmpty(stability.NewlyCommittedSegment)) return results;

        var segment = stability.NewlyCommittedSegment;

        foreach (var strategyName in StrategyNames)
        {
            var context = BuildContext(strategyName, segment);
            var requestText = string.IsNullOrEmpty(context) ? segment : $"{context} {segment}";
            var result = await RunStrategyAsync(strategyName, observation, requestText, segment.Length, context.Length, t0, ct);
            results.Add(result);
        }

        _committedSegmentHistory.Add(segment);
        return results;
    }

    public async Task<IReadOnlyList<ContextWindowUtteranceSummary>> ObserveFinalAsync(
        ContextWindowSourceObservation observation, CancellationToken ct)
    {
        if (_currentUtteranceId != observation.UtteranceId) ResetInternal(observation.UtteranceId);

        _sourceEngine.ProcessFinal(
            new FinalSourceEvent(observation.UtteranceId, observation.PartialSequence, observation.SourceText, observation.Timestamp));

        var sw = Stopwatch.StartNew();
        var finalTranslation = await _translationProvider.TranslateAsync(
            new TranslationProviderRequest(_sourceLanguage, _targetLanguage, observation.SourceText), ct);
        sw.Stop();
        var finalTokens = Tokenize(finalTranslation.Success ? finalTranslation.CandidateTranslatedText : "");

        var c0CumulativeChars = _trackers["C0_NoContext"].CumulativeCharsSent;
        var summaries = new List<ContextWindowUtteranceSummary>();
        foreach (var strategyName in StrategyNames)
        {
            var summary = BuildSummary(observation.UtteranceId, strategyName, _trackers[strategyName], finalTokens,
                finalTranslation.Success, sw, c0CumulativeChars);
            summaries.Add(summary);
            _logger.Log(_sessionTag, "ContextWindowFinalReconciliation",
                $"generation={_generation} utteranceId={summary.UtteranceId} strategy={summary.Strategy} requests={summary.RequestCount} " +
                $"contradictions={summary.ContradictionCount} revisions={summary.RevisionCount} cumulativeChars={summary.CumulativeCharactersSent} " +
                $"maxRequestChars={summary.MaxRequestCharacters} amplificationVsC0={summary.AmplificationVsC0:F2} " +
                $"missing={FormatNullableInt(summary.ApproxMissingTokenCount)} duplicate={FormatNullableInt(summary.ApproxDuplicateTokenCount)} " +
                $"finalSucceeded={summary.FinalTranslationSucceeded} anyReachedSpeakable={summary.AnyContentReachedSpeakable}");
        }

        ResetInternal(null);
        return summaries;
    }

    public void Reset() => ResetInternal(null);

    private string BuildContext(string strategyName, string currentSegment)
    {
        return strategyName switch
        {
            "C0_NoContext" => "",
            "C1_OnePrecedingUnit" => _committedSegmentHistory.Count == 0 ? "" : _committedSegmentHistory[^1],
            "C2_TwoPrecedingUnits" => string.Join(" ", _committedSegmentHistory.Skip(Math.Max(0, _committedSegmentHistory.Count - 2))),
            "C3_BoundedSmall" => BoundedTail(string.Join(" ", _committedSegmentHistory), C3CharacterLimit),
            "C4_BoundedLarge" => BoundedTail(string.Join(" ", _committedSegmentHistory), C4CharacterLimit),
            _ => throw new ArgumentOutOfRangeException(nameof(strategyName)),
        };
    }

    private static string BoundedTail(string fullContext, int characterLimit) =>
        fullContext.Length <= characterLimit ? fullContext : fullContext[^characterLimit..];

    private async Task<ContextWindowStepResult> RunStrategyAsync(
        string strategyName, ContextWindowSourceObservation obs, string requestText,
        int segmentChars, int contextChars, DateTimeOffset t0, CancellationToken ct)
    {
        var tracker = _trackers[strategyName];
        var sourceToRequestMs = (DateTimeOffset.UtcNow - t0).TotalMilliseconds;

        var sw = Stopwatch.StartNew();
        var result = await _translationProvider.TranslateAsync(new TranslationProviderRequest(_sourceLanguage, _targetLanguage, requestText), ct);
        sw.Stop();

        tracker.RequestCount++;
        var totalChars = requestText.Length;
        tracker.CumulativeCharsSent += totalChars;
        tracker.MaxRequestChars = Math.Max(tracker.MaxRequestChars, totalChars);
        tracker.FirstLatencyMs ??= sw.Elapsed.TotalMilliseconds;

        ContextRevisionKind kind;
        ContextWindowTranslationState translationState;
        ContextWindowSpeechReadiness speechState;

        if (!result.Success)
        {
            kind = ContextRevisionKind.Incomplete;
            translationState = ContextWindowTranslationState.Revised;
            speechState = ContextWindowSpeechReadiness.NotSpeakable;
            tracker.ConsecutiveStableCount = 0;
        }
        else
        {
            var newTokens = Tokenize(result.CandidateTranslatedText);
            kind = ClassifyRevision(tracker.PreviousTokens, newTokens);

            if (kind is ContextRevisionKind.Contradiction or ContextRevisionKind.Replacement or ContextRevisionKind.Incomplete)
            {
                if (kind != ContextRevisionKind.Incomplete) tracker.RevisionCount++;
                if (kind == ContextRevisionKind.Contradiction) tracker.ContradictionCount++;
                tracker.ConsecutiveStableCount = 0;
                translationState = ContextWindowTranslationState.Revised;
                speechState = ContextWindowSpeechReadiness.NotSpeakable;
            }
            else
            {
                tracker.ConsecutiveStableCount++;
                translationState = tracker.ConsecutiveStableCount >= 2 ? ContextWindowTranslationState.StableDraft : ContextWindowTranslationState.Draft;
                if (tracker.ConsecutiveStableCount >= MinConsecutiveForSpeakable)
                {
                    speechState = ContextWindowSpeechReadiness.Speakable;
                    tracker.AnyReachedSpeakable = true;
                }
                else if (tracker.ConsecutiveStableCount >= MinConsecutiveForCandidateForSpeech)
                {
                    speechState = ContextWindowSpeechReadiness.CandidateForSpeech;
                    tracker.FirstCandidateForSpeechDelayMs ??= sw.Elapsed.TotalMilliseconds;
                }
                else
                {
                    speechState = ContextWindowSpeechReadiness.NotSpeakable;
                }

                if (kind != ContextRevisionKind.Incomplete)
                {
                    tracker.CumulativeTokens = newTokens;
                    tracker.PreviousTokens = newTokens;
                }
            }
        }

        LogStep(strategyName, obs, kind, translationState, speechState, sw.Elapsed.TotalMilliseconds, sourceToRequestMs);

        return new ContextWindowStepResult(strategyName, ContextWindowSourceState.Stable, translationState, kind, speechState,
            obs.PartialSequence, tracker.RequestCount, segmentChars, contextChars, totalChars, sourceToRequestMs, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Token-level HEURISTIC classification — see docs for exact semantics; identical shape to Step 5.10's classifier plus a distinct GrammaticalRestructuring bucket for a mid-range overlap ratio.</summary>
    private static ContextRevisionKind ClassifyRevision(List<string>? previous, List<string> current)
    {
        if (current.Count == 0) return ContextRevisionKind.Incomplete;
        if (previous == null) return ContextRevisionKind.FirstCandidate;
        if (previous.SequenceEqual(current)) return ContextRevisionKind.Identical;

        var lcp = 0;
        var n = Math.Min(previous.Count, current.Count);
        while (lcp < n && string.Equals(previous[lcp], current[lcp], StringComparison.Ordinal)) lcp++;

        if (lcp == 0) return ContextRevisionKind.Contradiction;
        if (lcp == previous.Count) return ContextRevisionKind.Extension;

        var shorterLength = Math.Min(previous.Count, current.Count);
        var overlapRatio = shorterLength == 0 ? 0.0 : (double)lcp / shorterLength;
        if (overlapRatio >= 0.7) return ContextRevisionKind.ParaphraseOrRestructuring;
        if (overlapRatio >= 0.35) return ContextRevisionKind.GrammaticalRestructuring;
        return ContextRevisionKind.Replacement;
    }

    private ContextWindowUtteranceSummary BuildSummary(
        string utteranceId, string strategy, StrategyTracker tracker, IReadOnlyList<string> finalTokens,
        bool finalSucceeded, Stopwatch finalSw, int c0CumulativeChars)
    {
        var amplification = c0CumulativeChars > 0 ? (double)tracker.CumulativeCharsSent / c0CumulativeChars : (tracker.RequestCount == 0 ? 0.0 : 1.0);

        if (!finalSucceeded)
            return new ContextWindowUtteranceSummary(utteranceId, strategy, tracker.RequestCount, tracker.ContradictionCount,
                tracker.RevisionCount, tracker.CumulativeCharsSent, tracker.MaxRequestChars, amplification, null, null,
                tracker.FirstLatencyMs, tracker.FirstCandidateForSpeechDelayMs, null, false, tracker.AnyReachedSpeakable);

        if (tracker.RequestCount == 0)
            return new ContextWindowUtteranceSummary(utteranceId, strategy, 0, 0, 0, 0, 0, 0.0, null, null,
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

        return new ContextWindowUtteranceSummary(utteranceId, strategy, tracker.RequestCount, tracker.ContradictionCount,
            tracker.RevisionCount, tracker.CumulativeCharsSent, tracker.MaxRequestChars, amplification, missing, duplicate,
            tracker.FirstLatencyMs, tracker.FirstCandidateForSpeechDelayMs, finalSw.Elapsed.TotalMilliseconds, true, tracker.AnyReachedSpeakable);
    }

    private static Dictionary<string, int> CountMultiset(IReadOnlyList<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens) counts[t] = counts.GetValueOrDefault(t, 0) + 1;
        return counts;
    }

    private void LogStep(string strategy, ContextWindowSourceObservation obs, ContextRevisionKind kind,
        ContextWindowTranslationState translationState, ContextWindowSpeechReadiness speechState, double latencyMs, double sourceToRequestMs)
    {
        _logger.Log(_sessionTag, "ContextWindowStep",
            $"generation={_generation} strategy={strategy} utteranceId={obs.UtteranceId} partialSequence={obs.PartialSequence} " +
            $"revisionKind={kind} translationState={translationState} speechState={speechState} " +
            $"sourceToRequestMs={sourceToRequestMs:F0} latencyMs={latencyMs:F0}");
    }

    private static string FormatNullableInt(int? value) => value.HasValue ? value.Value.ToString() : "n/a";

    private void ResetInternal(string? newUtteranceId)
    {
        _currentUtteranceId = newUtteranceId;
        _committedSegmentHistory.Clear();
        foreach (var tracker in _trackers.Values) tracker.Reset();
        _sourceEngine.Reset();
    }

    private static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? new List<string>() : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    private sealed class StrategyTracker
    {
        public List<string> CumulativeTokens = new();
        public List<string>? PreviousTokens;
        public int ConsecutiveStableCount;
        public int RequestCount;
        public int RevisionCount;
        public int ContradictionCount;
        public int CumulativeCharsSent;
        public int MaxRequestChars;
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
            CumulativeCharsSent = 0;
            MaxRequestChars = 0;
            FirstLatencyMs = null;
            FirstCandidateForSpeechDelayMs = null;
            AnyReachedSpeakable = false;
        }
    }
}
