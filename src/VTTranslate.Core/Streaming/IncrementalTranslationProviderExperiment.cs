using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

public enum ProviderStrategyStability { Unknown, SafeToSpeak, Provisional, RequiresRevision, UnsafeToSpeak }

public sealed record ProviderStrategyStepResult(
    string StrategyName,
    int SegmentSequence,
    bool CallSucceeded,
    int? HttpStatusCode,
    double LatencyMs,
    int CumulativeCandidateTokenCount,
    ProviderStrategyStability Stability);

public sealed record ProviderStrategyFinalReconciliation(
    string StrategyName,
    bool AnySuccessfulCall,
    int CallCount,
    int SuccessCount,
    int CumulativeTokenCount,
    int FinalTokenCount,
    int? ApproxMissingTokenCount,
    int? ApproxDuplicateTokenCount,
    int RevisionCount);

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.5. Orchestrates strategies B1 (cumulative source
/// translation), B2 (bounded-context segment translation), and B3 (boundary-aware
/// phrase/sentence translation) against a genuinely independent
/// <see cref="IIncrementalTranslationProvider"/> — never the Speech-bundled translation
/// Step 5 used. Has no events and no reference to production translation/TTS/playback.
/// Diagnostic logging is metadata-only (lengths, counts, booleans, status codes, strategy
/// names, sequence numbers) — candidate text exists only in memory. See
/// docs/design-notes/incremental-translation-provider-feasibility.md.
/// </summary>
public sealed class IncrementalTranslationProviderExperiment
{
    private const string B1 = "B1_CumulativeSource";
    private const string B2 = "B2_ContextualSegment";
    private const string B3 = "B3_BoundaryAware";
    private const int B2ContextWordWindow = 8;
    private static readonly char[] SentenceEndings = { '.', '!', '?' };

    private readonly IIncrementalTranslationProvider _provider;
    private readonly IDiagnosticLogger _logger;
    private readonly string _sessionTag;
    private readonly int _generation;

    private string? _currentUtteranceId;
    private readonly List<string> _b1CumulativeTokens = new();
    private readonly List<string> _b2CumulativeTokens = new();
    private readonly List<string> _b3CumulativeTokens = new();
    private readonly List<string> _b3PendingPhraseTokens = new();
    private string _lastCumulativeStableSource = "";
    private int _b1CallCount, _b1SuccessCount, _b1RevisionCount;
    private int _b2CallCount, _b2SuccessCount, _b2RevisionCount;
    private int _b3CallCount, _b3SuccessCount, _b3RevisionCount;

    public IncrementalTranslationProviderExperiment(
        IIncrementalTranslationProvider provider, IDiagnosticLogger logger, string sessionTag, int generation)
    {
        _provider = provider;
        _logger = logger;
        _sessionTag = sessionTag;
        _generation = generation;
    }

    /// <summary>Observes one newly-stable source segment (never called for a step with no new commit). Diagnostic/testing only.</summary>
    public async Task<IReadOnlyList<ProviderStrategyStepResult>> ObserveStableSegmentAsync(
        string utteranceId, int segmentSequence, string newlyStableSourceSegment, string cumulativeStableSource,
        string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        if (_currentUtteranceId != utteranceId) ResetInternal(utteranceId);
        _lastCumulativeStableSource = cumulativeStableSource;

        var results = new List<ProviderStrategyStepResult>();

        // ---- B1: cumulative source translation ----
        results.Add(await RunStrategyAsync(B1, segmentSequence, sourceLanguage, targetLanguage,
            new TranslationProviderRequest(sourceLanguage, targetLanguage, cumulativeStableSource),
            _b1CumulativeTokens, replaceWholesale: true,
            onCall: () => { _b1CallCount++; }, onSuccess: () => _b1SuccessCount++, onRevision: () => _b1RevisionCount++, ct));

        // ---- B2: contextual segment translation (bounded preceding context) ----
        var precedingContext = BoundedContext(cumulativeStableSource, newlyStableSourceSegment);
        results.Add(await RunStrategyAsync(B2, segmentSequence, sourceLanguage, targetLanguage,
            new TranslationProviderRequest(sourceLanguage, targetLanguage, newlyStableSourceSegment, precedingContext),
            _b2CumulativeTokens, replaceWholesale: false,
            onCall: () => _b2CallCount++, onSuccess: () => _b2SuccessCount++, onRevision: () => _b2RevisionCount++, ct));

        // ---- B3: boundary-aware — buffer until a sentence-ending punctuation appears ----
        _b3PendingPhraseTokens.AddRange(Tokenize(newlyStableSourceSegment));
        var pendingText = string.Join(" ", _b3PendingPhraseTokens);
        if (ContainsSentenceEnding(pendingText))
        {
            var flushed = pendingText;
            _b3PendingPhraseTokens.Clear();
            results.Add(await RunStrategyAsync(B3, segmentSequence, sourceLanguage, targetLanguage,
                new TranslationProviderRequest(sourceLanguage, targetLanguage, flushed),
                _b3CumulativeTokens, replaceWholesale: false,
                onCall: () => _b3CallCount++, onSuccess: () => _b3SuccessCount++, onRevision: () => _b3RevisionCount++, ct));
        }
        // else: held back, no candidate this step — the boundary-aware trade-off being evaluated.

        return results;
    }

    /// <summary>Flushes any pending B3 phrase and reconciles all three strategies against the true final translation. Diagnostic only.</summary>
    public async Task<IReadOnlyList<ProviderStrategyFinalReconciliation>> ObserveFinalAsync(
        string utteranceId, string finalSourceText, string finalTranslatedText,
        string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        if (_currentUtteranceId != utteranceId) ResetInternal(utteranceId);

        if (_b3PendingPhraseTokens.Count > 0)
        {
            var flushed = string.Join(" ", _b3PendingPhraseTokens);
            _b3PendingPhraseTokens.Clear();
            await RunStrategyAsync(B3, -1, sourceLanguage, targetLanguage,
                new TranslationProviderRequest(sourceLanguage, targetLanguage, flushed),
                _b3CumulativeTokens, replaceWholesale: false,
                onCall: () => _b3CallCount++, onSuccess: () => _b3SuccessCount++, onRevision: () => _b3RevisionCount++, ct);
        }

        var finalTokens = Tokenize(finalTranslatedText);
        var results = new List<ProviderStrategyFinalReconciliation>
        {
            BuildReconciliation(B1, _b1CumulativeTokens, finalTokens, _b1CallCount, _b1SuccessCount, _b1RevisionCount),
            BuildReconciliation(B2, _b2CumulativeTokens, finalTokens, _b2CallCount, _b2SuccessCount, _b2RevisionCount),
            BuildReconciliation(B3, _b3CumulativeTokens, finalTokens, _b3CallCount, _b3SuccessCount, _b3RevisionCount),
        };

        foreach (var r in results)
        {
            _logger.Log(_sessionTag, "TranslationProviderFinalReconciliation",
                $"generation={_generation} utteranceId={utteranceId} strategy={r.StrategyName} " +
                $"anySuccessfulCall={r.AnySuccessfulCall} callCount={r.CallCount} successCount={r.SuccessCount} " +
                $"cumulativeTokenCount={r.CumulativeTokenCount} finalTokenCount={r.FinalTokenCount} " +
                $"approxMissingTokenCount={FormatNullableInt(r.ApproxMissingTokenCount)} " +
                $"approxDuplicateTokenCount={FormatNullableInt(r.ApproxDuplicateTokenCount)} revisionCount={r.RevisionCount}");
        }

        ResetInternal(null);
        return results;
    }

    public void Reset() => ResetInternal(null);

    private async Task<ProviderStrategyStepResult> RunStrategyAsync(
        string strategyName, int segmentSequence, string sourceLanguage, string targetLanguage,
        TranslationProviderRequest request, List<string> cumulativeTokens, bool replaceWholesale,
        Action onCall, Action onSuccess, Action onRevision, CancellationToken ct)
    {
        onCall();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _provider.TranslateAsync(request, ct);
        sw.Stop();

        var stability = ProviderStrategyStability.Unknown;
        if (result.Success && result.CandidateTranslatedText != null)
        {
            onSuccess();
            var candidateTokens = Tokenize(result.CandidateTranslatedText);
            if (replaceWholesale)
            {
                cumulativeTokens.Clear();
                cumulativeTokens.AddRange(candidateTokens);
                stability = ProviderStrategyStability.Provisional; // always subject to wholesale replacement next step
            }
            else
            {
                // Contradiction-vs-prior-candidate check (in-memory only): if this segment's
                // translation looks like a revision of what's already accumulated (rare given
                // each call targets disjoint segments/phrases by construction), flag it rather
                // than silently duplicate.
                cumulativeTokens.AddRange(candidateTokens);
                stability = ProviderStrategyStability.SafeToSpeak; // see doc §13 for the full criteria this maps to
            }
        }
        else
        {
            stability = ProviderStrategyStability.UnsafeToSpeak; // call failed — nothing safe to do with no candidate
        }

        _logger.Log(_sessionTag, "TranslationProviderStep",
            $"generation={_generation} strategy={strategyName} segmentSequence={segmentSequence} " +
            $"callSucceeded={result.Success} httpStatusCode={FormatNullableInt(result.HttpStatusCode)} " +
            $"latencyMs={sw.Elapsed.TotalMilliseconds:F0} cumulativeCandidateTokenCount={cumulativeTokens.Count} " +
            $"stability={stability}");

        return new ProviderStrategyStepResult(
            strategyName, segmentSequence, result.Success, result.HttpStatusCode,
            sw.Elapsed.TotalMilliseconds, cumulativeTokens.Count, stability);
    }

    private static ProviderStrategyFinalReconciliation BuildReconciliation(
        string strategyName, IReadOnlyList<string> cumulativeTokens, IReadOnlyList<string> finalTokens,
        int callCount, int successCount, int revisionCount)
    {
        if (successCount == 0)
        {
            return new ProviderStrategyFinalReconciliation(
                strategyName, false, callCount, successCount, 0, finalTokens.Count, null, null, revisionCount);
        }

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

        return new ProviderStrategyFinalReconciliation(
            strategyName, true, callCount, successCount, cumulativeTokens.Count, finalTokens.Count, missing, duplicate, revisionCount);
    }

    private static Dictionary<string, int> CountMultiset(IReadOnlyList<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens) counts[t] = counts.GetValueOrDefault(t, 0) + 1;
        return counts;
    }

    private static string BoundedContext(string cumulativeStableSource, string newlyStableSourceSegment)
    {
        var cumulativeTokens = Tokenize(cumulativeStableSource);
        var segmentTokens = Tokenize(newlyStableSourceSegment);
        var precedingCount = Math.Max(0, cumulativeTokens.Count - segmentTokens.Count);
        var preceding = cumulativeTokens.Take(precedingCount).ToList();
        var windowed = preceding.Skip(Math.Max(0, preceding.Count - B2ContextWordWindow)).ToList();
        return string.Join(" ", windowed);
    }

    private static bool ContainsSentenceEnding(string text) => text.IndexOfAny(SentenceEndings) >= 0;

    private void ResetInternal(string? newUtteranceId)
    {
        _currentUtteranceId = newUtteranceId;
        _b1CumulativeTokens.Clear();
        _b2CumulativeTokens.Clear();
        _b3CumulativeTokens.Clear();
        _b3PendingPhraseTokens.Clear();
        _lastCumulativeStableSource = "";
        _b1CallCount = _b1SuccessCount = _b1RevisionCount = 0;
        _b2CallCount = _b2SuccessCount = _b2RevisionCount = 0;
        _b3CallCount = _b3SuccessCount = _b3RevisionCount = 0;
    }

    private static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? new List<string>() : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string FormatNullableInt(int? value) => value.HasValue ? value.Value.ToString() : "n/a";
}
