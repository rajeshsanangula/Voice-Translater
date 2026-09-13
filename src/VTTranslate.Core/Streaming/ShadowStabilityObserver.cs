using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// Step 3 — SHADOW/OBSERVATION integration boundary. Wraps one
/// <see cref="IStreamingStabilityEngine"/> instance plus the bookkeeping needed to log
/// privacy-safe diagnostics about what it WOULD commit, given real partial/final source
/// text. This class has NO events, NO reference to translation/TTS/playback, and no way
/// to influence anything outside itself — its only two outputs are (1) diagnostic log
/// lines (metadata only, never recognized/translated text) and (2) the
/// <see cref="StabilityResult"/> values returned directly to its caller, which
/// <see cref="Providers.AzureSpeechTranslationProvider"/> discards after logging. See
/// docs/design-notes/streaming-shadow-integration.md for the full integration design.
///
/// One instance is scoped to exactly one Azure recognizer "generation" (one connection
/// lifetime) by its caller — a fresh instance (with a fresh, isolated engine) is created
/// per (re)connect, so a stale event from a superseded generation can never reach this
/// generation's engine, and this generation's state can never leak into the next.
///
/// Thread-safety: this class is not internally synchronized — it relies on its caller
/// invoking <see cref="BeginUtterance"/>/<see cref="ObservePartial"/>/<see cref="ObserveFinal"/>
/// from within a lock the caller already holds for other per-utterance bookkeeping (see
/// the call sites in AzureSpeechTranslationProvider, which call in from inside the
/// existing Step 1 instrumentation lock).
/// </summary>
public sealed class ShadowStabilityObserver
{
    private readonly IDiagnosticLogger _logger;
    private readonly string _sessionTag;
    private readonly int _generation;
    private readonly IStreamingStabilityEngine _engine;

    private string? _currentUtteranceId;
    private int _cumulativeCommittedTokenCount;
    private DateTimeOffset _t0;
    private bool _firstPartialLoggedForUtterance;
    private bool _firstShadowCommitLoggedForUtterance;

    public ShadowStabilityObserver(IDiagnosticLogger logger, string sessionTag, int generation, IStreamingStabilityEngine engine)
    {
        _logger = logger;
        _sessionTag = sessionTag;
        _generation = generation;
        _engine = engine;
    }

    /// <summary>
    /// Marks the start of a new utterance for this observer. <paramref name="t0"/> should
    /// be the same measurement origin used by the existing Step 1 instrumentation (per
    /// "reuse the existing measurement origin where valid") so T0-relative shadow timings
    /// are directly comparable to the existing PartialMeasurement/UtteranceSummary log
    /// lines, not a separate/competing clock.
    /// </summary>
    public void BeginUtterance(string utteranceId, DateTimeOffset t0)
    {
        _currentUtteranceId = utteranceId;
        _t0 = t0;
        _cumulativeCommittedTokenCount = 0;
        _firstPartialLoggedForUtterance = false;
        _firstShadowCommitLoggedForUtterance = false;
    }

    /// <summary>Observes one Recognizing (partial) event. Diagnostic-only: the return value here is informational for tests; production callers discard it.</summary>
    public StabilityResult ObservePartial(int sequenceNumber, string sourceText, DateTimeOffset timestamp)
    {
        if (_currentUtteranceId is null)
            throw new InvalidOperationException("ObservePartial called before BeginUtterance.");

        if (!_firstPartialLoggedForUtterance)
        {
            _firstPartialLoggedForUtterance = true;
            var elapsedT0ToFirstPartialMs = (timestamp - _t0).TotalMilliseconds;
            _logger.Log(_sessionTag, "ShadowFirstPartial",
                $"generation={_generation} utteranceId={_currentUtteranceId} elapsedT0ToFirstPartialMs={elapsedT0ToFirstPartialMs:F0}");
        }

        var result = _engine.ProcessPartial(new PartialSourceEvent(_currentUtteranceId, sequenceNumber, sourceText, timestamp));

        var proposedTokenCount = CountTokens(result.NewlyCommittedSegment);
        var proposedCharCount = result.NewlyCommittedSegment?.Length ?? 0;
        if (result.ShouldEmit)
        {
            _cumulativeCommittedTokenCount += proposedTokenCount;
            if (!_firstShadowCommitLoggedForUtterance)
            {
                _firstShadowCommitLoggedForUtterance = true;
                var elapsedT0ToFirstShadowCommitMs = (timestamp - _t0).TotalMilliseconds;
                _logger.Log(_sessionTag, "ShadowFirstCommit",
                    $"generation={_generation} utteranceId={_currentUtteranceId} elapsedT0ToFirstShadowCommitMs={elapsedT0ToFirstShadowCommitMs:F0}");
            }
        }

        var elapsedFromT0Ms = (timestamp - _t0).TotalMilliseconds;
        _logger.Log(_sessionTag, "ShadowPartialObserved",
            $"generation={_generation} utteranceId={_currentUtteranceId} partialSequence={sequenceNumber} " +
            $"elapsedFromT0Ms={elapsedFromT0Ms:F0} shouldEmit={result.ShouldEmit} action={result.Action} " +
            $"proposedCommitTokenCount={proposedTokenCount} proposedCommitCharCount={proposedCharCount} " +
            $"cumulativeCommittedTokenCount={_cumulativeCommittedTokenCount} commitVersion={result.CommitVersion}");

        return result;
    }

    /// <summary>Observes the Recognized (final) event, finalizes the shadow engine, and resets for the next utterance. Diagnostic-only.</summary>
    public StabilityResult ObserveFinal(int sequenceNumber, string sourceText, DateTimeOffset timestamp)
    {
        if (_currentUtteranceId is null)
            throw new InvalidOperationException("ObserveFinal called before BeginUtterance.");

        var utteranceId = _currentUtteranceId;
        var result = _engine.ProcessFinal(new FinalSourceEvent(utteranceId, sequenceNumber, sourceText, timestamp));

        var proposedTokenCount = CountTokens(result.NewlyCommittedSegment);
        var proposedCharCount = result.NewlyCommittedSegment?.Length ?? 0;
        if (result.ShouldEmit) _cumulativeCommittedTokenCount += proposedTokenCount;

        var elapsedT0ToFinalShadowFlushMs = (timestamp - _t0).TotalMilliseconds;
        _logger.Log(_sessionTag, "ShadowFinalized",
            $"generation={_generation} utteranceId={utteranceId} finalized=true " +
            $"elapsedT0ToFinalShadowFlushMs={elapsedT0ToFinalShadowFlushMs:F0} shouldEmit={result.ShouldEmit} " +
            $"proposedCommitTokenCount={proposedTokenCount} proposedCommitCharCount={proposedCharCount} " +
            $"cumulativeCommittedTokenCount={_cumulativeCommittedTokenCount} finalizedWithCorrection={result.FinalizedWithCorrection}");

        // Ready for the next utterance — caller must call BeginUtterance again.
        _currentUtteranceId = null;

        return result;
    }

    private static int CountTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
