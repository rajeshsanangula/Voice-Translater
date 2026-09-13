using System.Diagnostics;
using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.7. Answers: "can we obtain sufficiently stable
/// translated segments, early enough, that a future streaming TTS layer could safely
/// speak them without routinely producing duplicated, contradictory, or obviously
/// incomplete speech?" — using REALISTIC incremental ASR-style source progression (not
/// the 2-segment synthetic splits of Steps 5.5/5.6b), the real, unmodified Policy A
/// (<see cref="PrefixStabilityEngine"/>) for source-stability decisions, and the real,
/// genuine, independent Azure Translator API (via the injected
/// <see cref="IIncrementalTranslationProvider"/> — never a mock/fake for live results,
/// see docs/design-notes/streaming-translation-decision-experiment.md).
///
/// Reuses <see cref="IncrementalTranslationProviderExperiment"/> (Step 5.5/5.6b, unmodified)
/// for the actual B1/B2/B3 translation-strategy mechanics — this class only evaluates and
/// reports B1 and B3 (per instruction, B2's duplicate-inflation problem is already known
/// and it is not treated as a primary candidate), but does not duplicate B1/B2/B3's own
/// logic; it only adds the SOURCE-vs-TRANSLATION state-machine distinction (§4 of the
/// governing instructions) and realistic multi-partial driving on top.
///
/// Has no events and no reference to production translation-dispatch, TTS, playback, or
/// UI — the same structural isolation guarantee as every prior shadow component in this
/// project (Steps 3-5.6b). Never wired into <c>AzureSpeechTranslationProvider</c>.
/// </summary>
public sealed class StreamingTranslationDecisionExperiment
{
    private static readonly string[] ReportedStrategies = { "B1_CumulativeSource", "B3_BoundaryAware" };

    private readonly IStreamingStabilityEngine _sourceEngine;
    private readonly IIncrementalTranslationProvider _translationProvider;
    private readonly IncrementalTranslationProviderExperiment _translationExperiment;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;

    private string? _currentUtteranceId;
    private readonly Dictionary<string, double?> _earliestCandidateLatencyMs = new();

    public StreamingTranslationDecisionExperiment(
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
        _translationExperiment = new IncrementalTranslationProviderExperiment(translationProvider, logger, sessionTag, generation);
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
    }

    /// <summary>
    /// Observes one realistic ASR partial. Runs Policy A's real stability decision; if (and
    /// only if) it produces a newly-stable source commit, issues real B1/B3 translation
    /// requests via the genuine Translator API and reports both SOURCE and TRANSLATION
    /// state for this step. Returns an empty list when the source is still Provisional
    /// (nothing new to translate this step) — this is expected, not an error.
    /// </summary>
    public async Task<IReadOnlyList<StreamingTranslationStepResult>> ObservePartialAsync(
        StreamingSourceObservation observation, CancellationToken ct)
    {
        if (_currentUtteranceId != observation.UtteranceId)
            ResetInternal(observation.UtteranceId);

        var t0 = observation.Timestamp;
        var stability = _sourceEngine.ProcessPartial(
            new PartialSourceEvent(observation.UtteranceId, observation.PartialSequence, observation.SourceText, observation.Timestamp));

        if (!stability.ShouldEmit)
        {
            // SOURCE is still Provisional this step — by design, no translation request is
            // issued for unstable source content (this IS the point being evaluated: does
            // gating on source stability actually avoid wasted/premature translation calls).
            return Array.Empty<StreamingTranslationStepResult>();
        }

        var t1 = DateTimeOffset.UtcNow;
        var candidates = await _translationExperiment.ObserveStableSegmentAsync(
            observation.UtteranceId, observation.PartialSequence,
            stability.NewlyCommittedSegment!, stability.CommittedSourceText,
            _sourceLanguage, _targetLanguage, ct);

        var results = new List<StreamingTranslationStepResult>();
        foreach (var candidate in candidates.Where(c => ReportedStrategies.Contains(c.StrategyName)))
        {
            var translationState = MapTranslationState(candidate);
            if (candidate.CallSucceeded)
            {
                if (!_earliestCandidateLatencyMs.TryGetValue(candidate.StrategyName, out var existing) || existing == null)
                    _earliestCandidateLatencyMs[candidate.StrategyName] = candidate.LatencyMs;
            }

            results.Add(new StreamingTranslationStepResult(
                Strategy: candidate.StrategyName,
                SourceState: SourceCommitState.Stable,
                TranslationState: translationState,
                PartialSequence: observation.PartialSequence,
                HasNewCandidate: candidate.CallSucceeded,
                SourceCommitToTranslationRequestMs: (t1 - t0).TotalMilliseconds,
                TranslationRequestLatencyMs: candidate.LatencyMs,
                CumulativeCandidateTokenCount: candidate.CumulativeCandidateTokenCount));
        }

        return results;
    }

    /// <summary>
    /// Observes the Final. Obtains the real, authoritative Translator translation of the
    /// true final source (one additional genuine API call — this is the ground truth used
    /// for reconciliation; never fabricated, never reused from an earlier partial's
    /// candidate). Reconciles B1/B3 against it and resets for the next utterance.
    /// </summary>
    public async Task<IReadOnlyList<StreamingTranslationUtteranceSummary>> ObserveFinalAsync(
        StreamingSourceObservation observation, CancellationToken ct)
    {
        if (_currentUtteranceId != observation.UtteranceId)
            ResetInternal(observation.UtteranceId);

        _sourceEngine.ProcessFinal(
            new FinalSourceEvent(observation.UtteranceId, observation.PartialSequence, observation.SourceText, observation.Timestamp));

        var sw = Stopwatch.StartNew();
        var finalTranslation = await _translationProvider.TranslateAsync(
            new TranslationProviderRequest(_sourceLanguage, _targetLanguage, observation.SourceText), ct);
        sw.Stop();

        // Never fabricate ground truth: if the authoritative call itself fails, reconcile
        // against an empty string so every strategy's cumulative candidate correctly shows
        // as 100% "missing" rather than silently comparing against nothing meaningful — and
        // FinalTranslationSucceeded=false makes that failure visible, not hidden.
        var finalTranslatedText = finalTranslation.Success ? finalTranslation.CandidateTranslatedText! : "";

        var reconciliations = await _translationExperiment.ObserveFinalAsync(
            observation.UtteranceId, observation.SourceText, finalTranslatedText, _sourceLanguage, _targetLanguage, ct);

        var summaries = new List<StreamingTranslationUtteranceSummary>();
        foreach (var r in reconciliations.Where(r => ReportedStrategies.Contains(r.StrategyName)))
        {
            summaries.Add(new StreamingTranslationUtteranceSummary(
                UtteranceId: observation.UtteranceId,
                Strategy: r.StrategyName,
                TranslationRequestCount: r.CallCount,
                RevisionCount: r.RevisionCount,
                ApproxMissingTokenCount: r.AnySuccessfulCall ? r.ApproxMissingTokenCount : null,
                ApproxDuplicateTokenCount: r.AnySuccessfulCall ? r.ApproxDuplicateTokenCount : null,
                EarliestCandidateLatencyMs: _earliestCandidateLatencyMs.GetValueOrDefault(r.StrategyName),
                FinalTranslationLatencyMs: finalTranslation.Success ? sw.Elapsed.TotalMilliseconds : null,
                FinalTranslationSucceeded: finalTranslation.Success));
        }

        ResetInternal(null);
        return summaries;
    }

    public void Reset() => ResetInternal(null);

    private void ResetInternal(string? newUtteranceId)
    {
        _currentUtteranceId = newUtteranceId;
        _earliestCandidateLatencyMs.Clear();
        _sourceEngine.Reset();
    }

    /// <summary>
    /// Maps a per-step B1/B3 result onto the TRANSLATION state machine (§4): B1 is always
    /// "Candidate" (its own design wholesale-replaces on every stable step, so it is never
    /// itself safe to treat as final — see StreamingTranslationModels.cs doc comment). B3's
    /// successful incremental append is "StableCandidate". Any failed call (the Translator
    /// API returning an error, timing out, etc.) maps to "RevisionRequired" — the closest
    /// fit in the required 4-state enum for "this candidate cannot currently be trusted and
    /// needs to be retried/resolved," made explicit here rather than silently dropped.
    /// </summary>
    private static TranslationCommitState MapTranslationState(ProviderStrategyStepResult candidate)
    {
        if (!candidate.CallSucceeded) return TranslationCommitState.RevisionRequired;
        return candidate.StrategyName switch
        {
            "B1_CumulativeSource" => TranslationCommitState.Candidate,
            "B3_BoundaryAware" => TranslationCommitState.StableCandidate,
            _ => TranslationCommitState.Candidate,
        };
    }
}
