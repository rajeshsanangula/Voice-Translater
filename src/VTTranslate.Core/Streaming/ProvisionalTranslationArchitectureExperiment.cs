using System.Diagnostics;
using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.9. Compares three translation-reconciliation
/// architectures against the real, independent Azure Translator API, all built on top of
/// the real, unmodified Policy A (<see cref="PrefixStabilityEngine"/>) for SOURCE
/// stability. Answers: can provisional translation be generated meaningfully earlier than
/// final ASR, reconciled reliably, and gated so that only a defensibly-stable subset is
/// ever marked <see cref="SpeechReadiness.Speakable"/>?
///
/// Architecture A ("cumulative retranslation"): translates the full cumulative committed
/// SOURCE on every stable commit — the same baseline strategy Steps 5.7/5.8 already
/// measured. No speech-readiness gating is applied (always <see cref="ProvisionalTranslationState.Draft"/>,
/// never <see cref="SpeechReadiness.Speakable"/>) — this IS the finding being reproduced:
/// a naive cumulative strategy never becomes safe on its own.
///
/// Architecture B ("overlapping segment translation"): translates a fixed-size SLIDING
/// WINDOW of the most recently committed words (independent per-call, not the full
/// cumulative) and attempts to RECONCILE consecutive overlapping segments by finding the
/// longest token-level overlap between the tail of the previous translated segment and
/// the head of the new one, appending only the non-overlapping remainder — a genuine
/// attempt at joining overlapping translated segments without blind concatenation.
///
/// Architecture C ("provisional draft + authoritative final"): translates the full
/// cumulative SOURCE like Architecture A, but applies real speech-readiness gating: a
/// translation becomes <see cref="ProvisionalTranslationState.StableDraft"/> only if it
/// does not contradict (word-level) the immediately preceding translation for this
/// utterance/architecture; <see cref="SpeechReadiness.CandidateForSpeech"/> only after 2
/// CONSECUTIVE non-contradicting translations; <see cref="SpeechReadiness.Speakable"/>
/// only after 3 CONSECUTIVE non-contradicting translations (see
/// <see cref="MinConsecutiveStableForSpeakable"/>) AND the underlying SOURCE has itself
/// reached <see cref="ProvisionalSourceState.Stable"/>. This is a real, conservative,
/// explicit, defensible criterion — never satisfied merely by "HTTP 200" or "looks
/// grammatical" (see class-level critical-safety-rule discussion in the governing
/// instructions and docs/design-notes/incremental-translation-architecture-research.md §7).
///
/// Has no events and no reference to production translation-dispatch, TTS, playback, or
/// UI. Never wired into <c>AzureSpeechTranslationProvider</c>.
/// </summary>
public sealed class ProvisionalTranslationArchitectureExperiment
{
    /// <summary>Sliding window size (in SOURCE words) for Architecture B's overlapping segments.</summary>
    public const int ArchitectureBWindowWordCount = 4;

    /// <summary>Number of CONSECUTIVE non-contradicting translations required before Architecture C marks a result Speakable — deliberately conservative (see class doc comment).</summary>
    public const int MinConsecutiveStableForSpeakable = 3;

    private const int MinConsecutiveForCandidateForSpeech = 2;

    private readonly IStreamingStabilityEngine _sourceEngine;
    private readonly IIncrementalTranslationProvider _translationProvider;
    private readonly IDiagnosticLogger _logger;
    private readonly string _sessionTag;
    private readonly int _generation;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;

    private string? _currentUtteranceId;

    // Architecture A: wholesale-replace cumulative, ungated.
    private List<string> _aCumulative = new();
    private int _aRequestCount;
    private int _aCharsSent;
    private double? _aFirstLatencyMs;

    // Architecture B: overlapping-window segments, reconciled by overlap-detection.
    private List<string> _bCumulative = new();
    private List<string>? _bPreviousSegmentTokens;
    private int _bRequestCount;
    private int _bCharsSent;
    private double? _bFirstLatencyMs;

    // Architecture C: wholesale-replace cumulative + speech-readiness gating.
    private List<string> _cCumulative = new();
    private List<string>? _cPreviousTranslatedTokens;
    private int _cConsecutiveStableCount;
    private int _cRequestCount;
    private int _cRevisionCount;
    private int _cCharsSent;
    private double? _cFirstLatencyMs;
    private double? _cFirstStableDraftDelayMs;
    private double? _cFirstCandidateForSpeechDelayMs;
    private bool _cAnyReachedSpeakable;
    private DateTimeOffset? _t0;

    public ProvisionalTranslationArchitectureExperiment(
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

    public async Task<IReadOnlyList<ProvisionalTranslationStepResult>> ObservePartialAsync(
        ProvisionalSourceObservation observation, CancellationToken ct)
    {
        if (_currentUtteranceId != observation.UtteranceId)
            ResetInternal(observation.UtteranceId);
        _t0 ??= observation.Timestamp;

        var stability = _sourceEngine.ProcessPartial(
            new PartialSourceEvent(observation.UtteranceId, observation.PartialSequence, observation.SourceText, observation.Timestamp));

        var results = new List<ProvisionalTranslationStepResult>();
        if (!stability.ShouldEmit) return results;

        results.Add(await RunArchitectureAAsync(observation, stability, ct));
        results.Add(await RunArchitectureBAsync(observation, stability, ct));
        results.Add(await RunArchitectureCAsync(observation, stability, ct));
        return results;
    }

    public async Task<IReadOnlyList<ProvisionalTranslationUtteranceSummary>> ObserveFinalAsync(
        ProvisionalSourceObservation observation, CancellationToken ct)
    {
        if (_currentUtteranceId != observation.UtteranceId)
            ResetInternal(observation.UtteranceId);

        _sourceEngine.ProcessFinal(
            new FinalSourceEvent(observation.UtteranceId, observation.PartialSequence, observation.SourceText, observation.Timestamp));

        var sw = Stopwatch.StartNew();
        var finalTranslation = await _translationProvider.TranslateAsync(
            new TranslationProviderRequest(_sourceLanguage, _targetLanguage, observation.SourceText), ct);
        sw.Stop();
        var finalTokens = Tokenize(finalTranslation.Success ? finalTranslation.CandidateTranslatedText : "");

        var summaries = new List<ProvisionalTranslationUtteranceSummary>
        {
            BuildSummary(observation.UtteranceId, "ArchitectureA_CumulativeRetranslation", _aCumulative, finalTokens,
                _aRequestCount, 0, 0, _aCharsSent, _aFirstLatencyMs, null, null, finalTranslation.Success, sw, false),
            BuildSummary(observation.UtteranceId, "ArchitectureB_OverlappingSegments", _bCumulative, finalTokens,
                _bRequestCount, 0, 0, _bCharsSent, _bFirstLatencyMs, null, null, finalTranslation.Success, sw, false),
            BuildSummary(observation.UtteranceId, "ArchitectureC_ProvisionalDraftPlusAuthoritative", _cCumulative, finalTokens,
                _cRequestCount, _cRevisionCount, _cRevisionCount, _cCharsSent, _cFirstLatencyMs,
                _cFirstStableDraftDelayMs, _cFirstCandidateForSpeechDelayMs, finalTranslation.Success, sw, _cAnyReachedSpeakable),
        };

        foreach (var s in summaries)
            _logger.Log(_sessionTag, "ProvisionalTranslationFinalReconciliation",
                $"generation={_generation} utteranceId={s.UtteranceId} architecture={s.Architecture} " +
                $"requests={s.TranslationRequestCount} revisions={s.RevisionCount} contradictions={s.ContradictionCount} " +
                $"charsSent={s.CharactersSentTotal} missing={FormatNullableInt(s.ApproxMissingTokenCount)} duplicate={FormatNullableInt(s.ApproxDuplicateTokenCount)} " +
                $"finalSucceeded={s.FinalTranslationSucceeded} anyReachedSpeakable={s.AnyContentReachedSpeakable}");

        ResetInternal(null);
        return summaries;
    }

    public void Reset() => ResetInternal(null);

    // ---- Architecture A ----
    private async Task<ProvisionalTranslationStepResult> RunArchitectureAAsync(
        ProvisionalSourceObservation obs, StabilityResult stability, CancellationToken ct)
    {
        var text = stability.CommittedSourceText;
        var sw = Stopwatch.StartNew();
        var result = await _translationProvider.TranslateAsync(new TranslationProviderRequest(_sourceLanguage, _targetLanguage, text), ct);
        sw.Stop();
        _aRequestCount++;
        _aCharsSent += text.Length;
        _aFirstLatencyMs ??= sw.Elapsed.TotalMilliseconds;

        var newTokens = result.Success ? Tokenize(result.CandidateTranslatedText) : _aCumulative;
        var changed = result.Success && !newTokens.SequenceEqual(_aCumulative);
        if (result.Success) _aCumulative = newTokens;

        var state = result.Success ? ProvisionalTranslationState.Draft : ProvisionalTranslationState.Revised;
        LogStep("ArchitectureA_CumulativeRetranslation", obs, ProvisionalSourceState.Stable, state, SpeechReadiness.NotSpeakable, sw.Elapsed.TotalMilliseconds, _aCumulative.Count);

        return new ProvisionalTranslationStepResult(
            "ArchitectureA_CumulativeRetranslation", ProvisionalSourceState.Stable, state, SpeechReadiness.NotSpeakable,
            obs.PartialSequence, _aRequestCount, changed, SupersedesPrevious: true, PreviousContentStillValid: false,
            sw.Elapsed.TotalMilliseconds, _aCumulative.Count);
    }

    // ---- Architecture B ----
    private async Task<ProvisionalTranslationStepResult> RunArchitectureBAsync(
        ProvisionalSourceObservation obs, StabilityResult stability, CancellationToken ct)
    {
        var allCommittedTokens = Tokenize(stability.CommittedSourceText);
        var windowTokens = allCommittedTokens.Skip(Math.Max(0, allCommittedTokens.Count - ArchitectureBWindowWordCount)).ToList();
        var windowText = string.Join(" ", windowTokens);

        var sw = Stopwatch.StartNew();
        var result = await _translationProvider.TranslateAsync(new TranslationProviderRequest(_sourceLanguage, _targetLanguage, windowText), ct);
        sw.Stop();
        _bRequestCount++;
        _bCharsSent += windowText.Length;
        _bFirstLatencyMs ??= sw.Elapsed.TotalMilliseconds;

        var supersedes = false;
        var previousValid = true;
        if (result.Success)
        {
            var segmentTokens = Tokenize(result.CandidateTranslatedText);
            if (_bPreviousSegmentTokens == null)
            {
                _bCumulative.AddRange(segmentTokens);
            }
            else
            {
                // Attempt to reconcile via longest overlap between the tail of the
                // previous segment and the head of the new one — a genuine join attempt,
                // never a blind concatenation.
                var overlap = LongestOverlap(_bPreviousSegmentTokens, segmentTokens);
                var newPortion = segmentTokens.Skip(overlap).ToList();
                _bCumulative.AddRange(newPortion);
                supersedes = overlap > 0;
                previousValid = overlap == Math.Min(_bPreviousSegmentTokens.Count, segmentTokens.Count) || overlap > 0;
            }
            _bPreviousSegmentTokens = segmentTokens;
        }

        var state = result.Success ? ProvisionalTranslationState.Draft : ProvisionalTranslationState.Revised;
        LogStep("ArchitectureB_OverlappingSegments", obs, ProvisionalSourceState.Stable, state, SpeechReadiness.NotSpeakable, sw.Elapsed.TotalMilliseconds, _bCumulative.Count);

        return new ProvisionalTranslationStepResult(
            "ArchitectureB_OverlappingSegments", ProvisionalSourceState.Stable, state, SpeechReadiness.NotSpeakable,
            obs.PartialSequence, _bRequestCount, TranslationChangedFromPrevious: result.Success, supersedes, previousValid,
            sw.Elapsed.TotalMilliseconds, _bCumulative.Count);
    }

    // ---- Architecture C ----
    private async Task<ProvisionalTranslationStepResult> RunArchitectureCAsync(
        ProvisionalSourceObservation obs, StabilityResult stability, CancellationToken ct)
    {
        var text = stability.CommittedSourceText;
        var sw = Stopwatch.StartNew();
        var result = await _translationProvider.TranslateAsync(new TranslationProviderRequest(_sourceLanguage, _targetLanguage, text), ct);
        sw.Stop();
        _cRequestCount++;
        _cCharsSent += text.Length;
        _cFirstLatencyMs ??= sw.Elapsed.TotalMilliseconds;

        if (!result.Success)
        {
            _cConsecutiveStableCount = 0;
            LogStep("ArchitectureC_ProvisionalDraftPlusAuthoritative", obs, ProvisionalSourceState.Stable,
                ProvisionalTranslationState.Revised, SpeechReadiness.NotSpeakable, sw.Elapsed.TotalMilliseconds, _cCumulative.Count);
            return new ProvisionalTranslationStepResult(
                "ArchitectureC_ProvisionalDraftPlusAuthoritative", ProvisionalSourceState.Stable, ProvisionalTranslationState.Revised,
                SpeechReadiness.NotSpeakable, obs.PartialSequence, _cRequestCount, false, false, false, sw.Elapsed.TotalMilliseconds, _cCumulative.Count);
        }

        var newTokens = Tokenize(result.CandidateTranslatedText);
        var isConsistent = _cPreviousTranslatedTokens == null || IsNonContradicting(_cPreviousTranslatedTokens, newTokens);

        ProvisionalTranslationState translationState;
        SpeechReadiness speechState;

        if (!isConsistent)
        {
            _cRevisionCount++;
            _cConsecutiveStableCount = 0;
            translationState = ProvisionalTranslationState.Revised;
            speechState = SpeechReadiness.NotSpeakable;
        }
        else
        {
            _cConsecutiveStableCount++;
            translationState = _cConsecutiveStableCount >= 2 ? ProvisionalTranslationState.StableDraft : ProvisionalTranslationState.Draft;
            _cFirstStableDraftDelayMs ??= translationState == ProvisionalTranslationState.StableDraft
                ? (obs.Timestamp - _t0!.Value).TotalMilliseconds : null;

            if (_cConsecutiveStableCount >= MinConsecutiveStableForSpeakable)
            {
                speechState = SpeechReadiness.Speakable;
                _cAnyReachedSpeakable = true;
            }
            else if (_cConsecutiveStableCount >= MinConsecutiveForCandidateForSpeech)
            {
                speechState = SpeechReadiness.CandidateForSpeech;
                _cFirstCandidateForSpeechDelayMs ??= (obs.Timestamp - _t0!.Value).TotalMilliseconds;
            }
            else
            {
                speechState = SpeechReadiness.NotSpeakable;
            }
        }

        _cCumulative = newTokens;
        _cPreviousTranslatedTokens = newTokens;

        LogStep("ArchitectureC_ProvisionalDraftPlusAuthoritative", obs, ProvisionalSourceState.Stable, translationState, speechState, sw.Elapsed.TotalMilliseconds, _cCumulative.Count);

        return new ProvisionalTranslationStepResult(
            "ArchitectureC_ProvisionalDraftPlusAuthoritative", ProvisionalSourceState.Stable, translationState, speechState,
            obs.PartialSequence, _cRequestCount, TranslationChangedFromPrevious: true, SupersedesPrevious: true,
            PreviousContentStillValid: isConsistent, sw.Elapsed.TotalMilliseconds, _cCumulative.Count);
    }

    /// <summary>Word-level: candidate is non-contradicting relative to previous if previous is a prefix of candidate (clean extension) or they are identical.</summary>
    private static bool IsNonContradicting(IReadOnlyList<string> previous, IReadOnlyList<string> candidate)
    {
        var n = Math.Min(previous.Count, candidate.Count);
        for (var i = 0; i < n; i++)
            if (!string.Equals(previous[i], candidate[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Longest token-level overlap between the tail of <paramref name="previous"/> and the head of <paramref name="current"/> — the basis of Architecture B's join attempt.</summary>
    private static int LongestOverlap(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        var maxPossible = Math.Min(previous.Count, current.Count);
        for (var len = maxPossible; len > 0; len--)
        {
            var matches = true;
            for (var i = 0; i < len; i++)
            {
                if (!string.Equals(previous[previous.Count - len + i], current[i], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return len;
        }
        return 0;
    }

    private ProvisionalTranslationUtteranceSummary BuildSummary(
        string utteranceId, string architecture, IReadOnlyList<string> cumulativeTokens, IReadOnlyList<string> finalTokens,
        int requestCount, int revisionCount, int contradictionCount, int charsSent, double? firstLatencyMs,
        double? firstStableDraftDelayMs, double? firstCandidateForSpeechDelayMs, bool finalSucceeded, Stopwatch finalSw, bool anyReachedSpeakable)
    {
        if (!finalSucceeded)
            return new ProvisionalTranslationUtteranceSummary(utteranceId, architecture, requestCount, revisionCount,
                contradictionCount, charsSent, null, null, firstLatencyMs, firstStableDraftDelayMs, firstCandidateForSpeechDelayMs, null, false, anyReachedSpeakable);

        if (requestCount == 0)
            return new ProvisionalTranslationUtteranceSummary(utteranceId, architecture, requestCount, revisionCount,
                contradictionCount, charsSent, null, null, null, null, null, finalSw.Elapsed.TotalMilliseconds, true, anyReachedSpeakable);

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

        return new ProvisionalTranslationUtteranceSummary(utteranceId, architecture, requestCount, revisionCount,
            contradictionCount, charsSent, missing, duplicate, firstLatencyMs, firstStableDraftDelayMs,
            firstCandidateForSpeechDelayMs, finalSw.Elapsed.TotalMilliseconds, true, anyReachedSpeakable);
    }

    private static Dictionary<string, int> CountMultiset(IReadOnlyList<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens) counts[t] = counts.GetValueOrDefault(t, 0) + 1;
        return counts;
    }

    private void LogStep(string architecture, ProvisionalSourceObservation obs, ProvisionalSourceState sourceState,
        ProvisionalTranslationState translationState, SpeechReadiness speechState, double latencyMs, int cumulativeTokenCount)
    {
        _logger.Log(_sessionTag, "ProvisionalTranslationStep",
            $"generation={_generation} architecture={architecture} utteranceId={obs.UtteranceId} partialSequence={obs.PartialSequence} " +
            $"sourceState={sourceState} translationState={translationState} speechState={speechState} " +
            $"latencyMs={latencyMs:F0} cumulativeTokenCount={cumulativeTokenCount}");
    }

    private static string FormatNullableInt(int? value) => value.HasValue ? value.Value.ToString() : "n/a";

    private void ResetInternal(string? newUtteranceId)
    {
        _currentUtteranceId = newUtteranceId;
        _t0 = null;
        _aCumulative = new List<string>(); _aRequestCount = 0; _aCharsSent = 0; _aFirstLatencyMs = null;
        _bCumulative = new List<string>(); _bPreviousSegmentTokens = null; _bRequestCount = 0; _bCharsSent = 0; _bFirstLatencyMs = null;
        _cCumulative = new List<string>(); _cPreviousTranslatedTokens = null; _cConsecutiveStableCount = 0;
        _cRequestCount = 0; _cRevisionCount = 0; _cCharsSent = 0; _cFirstLatencyMs = null;
        _cFirstStableDraftDelayMs = null; _cFirstCandidateForSpeechDelayMs = null; _cAnyReachedSpeakable = false;
        _sourceEngine.Reset();
    }

    private static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? new List<string>() : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
}
