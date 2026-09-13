using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using VTTranslate.Core.Diagnostics;
using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Providers;

/// <summary>
/// Real, working streaming speech-to-speech translation using the Azure AI Speech SDK's
/// TranslationRecognizer: one connection does streaming ASR, translation, and neural TTS
/// synthesis of the translated text. This is the only provider implementation shipped —
/// there is no mock/fake path in production code.
///
/// Includes automatic reconnection: a transient network/connection failure (as opposed
/// to a config/auth error) triggers a bounded number of retries with exponential backoff
/// before the pipeline is marked fatally errored.
///
/// Concurrency: recognizer lifecycle (create/start/stop/dispose) is fully serialized
/// through <see cref="_connectionLock"/>. Without this, a pending reconnect racing
/// against an intentional Stop() could either (a) operate on a recognizer instance
/// that Stop() is concurrently disposing, or (b) start a brand-new recognizer stream
/// AFTER Stop() was called, leaking a connection nothing ever shuts down. Both were
/// possible in an earlier version of this class and are now prevented by the lock plus
/// a re-check of <see cref="_stopRequested"/> immediately before starting a new stream.
///
/// Utterance gating: every recognized utterance is evaluated by
/// <see cref="UtteranceEligibilityGate"/> (an utterance-QUALITY gate — not speaker
/// identification, see its doc comment) before its synthesized audio is forwarded via
/// <see cref="AudioSynthesized"/>. This exists because the microphone can pick up real
/// ambient/third-party speech that Azure correctly recognizes; the gate filters
/// low-duration/low-confidence noise misrecognitions but does NOT claim to fully solve
/// third-party speech reaching the far end of a call — see
/// docs/design-notes/utterance-gating-and-push-to-talk.md for the acknowledged residual
/// gap and the planned future mitigation (a user-controlled mute/push-to-talk control).
/// Transcript display (<see cref="FinalResult"/>) is unaffected by this gate either way.
/// </summary>
public sealed class AzureSpeechTranslationProvider : ISpeechTranslationProvider, IReconnectingProvider
{
    private const int MaxReconnectAttempts = 5;

    private readonly string _subscriptionKey;
    private readonly string _region;
    private readonly string _voiceName;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly IDiagnosticLogger _logger;
    private readonly Func<IStreamingStabilityEngine> _stabilityEngineFactory;
    private readonly Func<IStreamingStabilityEngine> _experimentalStabilityEngineFactory;

    private TranslationRecognizer? _recognizer;
    private PushAudioInputStream? _pushStream;
    private string _sourceLanguage = "";
    private string _targetLanguage = "";
    private CancellationTokenSource? _lifetimeCts;
    private volatile bool _stopRequested = true; // true until StartAsync succeeds
    private int _reconnectAttempt;
    private int _startedFlag; // 0 = not started, 1 = started; guards against double-start
    private int _generation; // incremented on every (re)connect; guards against a superseded recognizer's in-flight callback still firing after a newer one has taken over
    private volatile bool _lastUtteranceAccepted = true; // set by Recognized, consumed by the immediately-following Synthesizing for the same utterance

    // Audio-chunk-sent logging is summarized every N chunks rather than per-call —
    // Azure receives a chunk roughly every 100ms, so per-chunk logging would flood the
    // log file without adding diagnostic value.
    private const int AudioLogEveryNChunks = 50;
    private long _totalChunksSent;

    // Part 5 (translation-quality diagnostics): if Azure accepts an utterance (per
    // UtteranceEligibilityGate) but never produces ANY synthesized audio for it within
    // a reasonable window, that's a distinct, attributable signal — "ASR+MT succeeded,
    // TTS did not" — useful for telling a future translation-quality bug apart from an
    // audio-routing bug (routing bugs show up as PushAudio/capture problems instead;
    // see docs/design-notes/translation-quality-diagnostics.md). No speech content is
    // ever involved in this signal, only its absence.
    private const int TtsWatchdogTimeoutMs = 5000;
    private readonly object _watchdogLock = new();
    private Timer? _ttsWatchdog;

    // ---- Step 1 measurement instrumentation (docs/design-notes/streaming-measurement-results.md) ----
    // Purely additive/observational: does not alter recognition, translation, TTS,
    // eligibility, reconnect, or any existing control flow. Exists to gather real
    // evidence about Azure partial-result behavior before any streaming-translation
    // work begins (per docs/design-notes/streaming-conversational-engine-design.md,
    // Step 1 of the recommended implementation sequence). Logs metadata only — text
    // LENGTHS and boolean comparisons, never recognized/translated text itself.
    private readonly object _instrumentationLock = new();
    private DateTimeOffset _utteranceCaptureStartedAt; // T0 for the utterance currently being captured
    private bool _awaitingCaptureForInstrumentation = true;
    private int _partialSequenceInUtterance;
    private string? _previousPartialText;
    private string? _previousPartialTranslatedText;
    private int _partialRevisionCount;
    private int _translatedPartialMissingCount;
    private DateTimeOffset _t0ForPendingSynthesisTiming; // snapshot of T0 taken at Final, consumed by the next Synthesizing
    private bool _firstSynthesisLoggedForUtterance = true; // true = "nothing pending to log yet"

    /// <summary>Direction/session tag for log lines only — e.g. "en-US-&gt;de-DE". Never used for control flow.</summary>
    private string SessionTag => $"{_sourceLanguage}->{_targetLanguage}";

    public event EventHandler<TranslationResult>? PartialResult;
    public event EventHandler<TranslationResult>? FinalResult;
    public event EventHandler<SynthesizedAudio>? AudioSynthesized;
    public event EventHandler<ProviderError>? Error;
    public event EventHandler<string>? StatusChanged;

    /// <param name="voiceName">
    /// Target-language neural voice, e.g. "de-DE-KatjaNeural" or "en-US-JennyNeural".
    /// </param>
    /// <param name="logger">
    /// Optional diagnostic logger. Defaults to a no-op — logging is purely additive
    /// and opt-in, never required for correct operation.
    /// </param>
    /// <param name="stabilityEngineFactory">
    /// Step 3 — SHADOW/OBSERVATION only. Creates one <see cref="IStreamingStabilityEngine"/>
    /// per recognizer generation, used exclusively by <see cref="ShadowStabilityObserver"/>
    /// to compute (and log metadata about) what the engine WOULD commit from real Azure
    /// partials. Never wired into translation/TTS/playback — see
    /// docs/design-notes/streaming-shadow-integration.md. Defaults to
    /// <see cref="PrefixStabilityEngine"/>; injectable so this provider (and the shadow
    /// path specifically) stays unit-testable without a real Azure connection.
    /// </param>
    /// <param name="experimentalStabilityEngineFactory">
    /// Step 4 — SHADOW/OBSERVATION only, "Policy B" in
    /// docs/design-notes/commit-policy-shadow-evaluation.md. Creates one experimental
    /// <see cref="IStreamingStabilityEngine"/> per recognizer generation, run side-by-side
    /// with (never in place of) the Policy A engine via <see cref="CommitPolicyComparator"/>.
    /// Never wired into translation/TTS/playback. Defaults to
    /// <see cref="BestPartialStabilityEngine"/>; injectable for the same testability reason
    /// as <paramref name="stabilityEngineFactory"/>.
    /// </param>
    public AzureSpeechTranslationProvider(
        string subscriptionKey,
        string region,
        string voiceName,
        IDiagnosticLogger? logger = null,
        Func<IStreamingStabilityEngine>? stabilityEngineFactory = null,
        Func<IStreamingStabilityEngine>? experimentalStabilityEngineFactory = null)
    {
        _subscriptionKey = subscriptionKey;
        _region = region;
        _voiceName = voiceName;
        _logger = logger ?? NullDiagnosticLogger.Instance;
        _stabilityEngineFactory = stabilityEngineFactory ?? (() => new PrefixStabilityEngine());
        _experimentalStabilityEngineFactory = experimentalStabilityEngineFactory ?? (() => new BestPartialStabilityEngine());
    }

    public async Task StartAsync(string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _startedFlag, 1, 0) != 0)
            throw new InvalidOperationException("StartAsync was already called on this provider instance. Create a new instance per session.");

        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
        _stopRequested = false;
        _reconnectAttempt = 0;
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await _connectionLock.WaitAsync();
        try
        {
            await CreateAndStartRecognizerLockedAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Must be called while holding <see cref="_connectionLock"/>.</summary>
    private async Task CreateAndStartRecognizerLockedAsync()
    {
        var config = SpeechTranslationConfig.FromSubscription(_subscriptionKey, _region);
        config.SpeechRecognitionLanguage = _sourceLanguage;
        var targetTwoLetter = ToTwoLetter(_targetLanguage);
        config.AddTargetLanguage(targetTwoLetter);
        config.VoiceName = _voiceName;
        config.SetProperty(PropertyId.SpeechServiceConnection_SynthLanguage, targetTwoLetter);
        // Ask for the detailed-format response so UtteranceEligibilityGate can use
        // Azure's own confidence score when the service actually includes one for a
        // translation result — see AzureConfidenceParser's doc comment for why this
        // isn't guaranteed, and how a missing value degrades gracefully rather than
        // being invented.
        config.SetProperty(PropertyId.SpeechServiceResponse_RequestDetailedResultTrueFalse, "true");

        _pushStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        var audioConfig = AudioConfig.FromStreamInput(_pushStream);

        DisposeRecognizerLocked();
        var recognizer = new TranslationRecognizer(config, audioConfig);
        var myGeneration = Interlocked.Increment(ref _generation);

        // ---- Step 3/4 shadow stability observation (diagnostic/measurement only) ----
        // One CommitPolicyComparator (running Policy A = PrefixStabilityEngine, unmodified,
        // and Policy B = the experimental BestPartialStabilityEngine, side-by-side against
        // the identical real partial/final stream) per recognizer generation, captured in
        // this closure only — a superseded generation's Recognizing/Recognized callbacks
        // are already dropped by the `myGeneration != _generation` guard below BEFORE they
        // ever reach this comparator, so a stale partial can never contaminate the current
        // generation's shadow state, and this generation's shadow state can never leak into
        // the next. Neither policy's output is ever connected to translation/TTS/playback —
        // results are logged (metadata only) and discarded. See
        // docs/design-notes/commit-policy-shadow-evaluation.md.
        var shadowComparator = new CommitPolicyComparator(
            _logger, SessionTag, myGeneration, _stabilityEngineFactory(), _experimentalStabilityEngineFactory());
        var shadowUtteranceIndex = 0;

        // ---- Step 5 shadow incremental-translation evaluation (diagnostic only) ----
        // Reads Policy A's per-step source-stability decision (from shadowComparator above)
        // plus Azure's OWN already-produced translated text for the current partial/final —
        // no new translation API call is ever made. Never wired to translation-dispatch,
        // TTS, or playback. See docs/design-notes/incremental-translation-shadow-evaluation.md.
        var translationShadow = new AzureAlignedIncrementalTranslationShadow(_logger, SessionTag, myGeneration);

        recognizer.Recognizing += (_, e) =>
        {
            if (myGeneration != _generation) return; // superseded by a later (re)connect — drop, don't duplicate
            // Log only that a recognition event happened and its text LENGTH — never the
            // recognized/translated text itself (that's sensitive speech content).
            _logger.Log(SessionTag, "RecognitionEvent", $"kind=partial generation={myGeneration} textLength={e.Result.Text.Length}");
            var translated = e.Result.Translations.TryGetValue(targetTwoLetter, out var t) ? t : "";

            // Step 1 instrumentation — see field group doc comment above. Never logs the
            // actual text: PartialResultAnalyzer.Compare takes it in-memory only to
            // derive booleans, which are what gets logged.
            var translatedAvailable = e.Result.Translations.ContainsKey(targetTwoLetter);
            lock (_instrumentationLock)
            {
                var seq = ++_partialSequenceInUtterance;
                var elapsedFromT0Ms = (DateTimeOffset.UtcNow - _utteranceCaptureStartedAt).TotalMilliseconds;
                var textCmp = PartialResultAnalyzer.Compare(_previousPartialText, e.Result.Text);
                var translatedCmp = PartialResultAnalyzer.Compare(_previousPartialTranslatedText, translated);
                if (textCmp.Regressed) _partialRevisionCount++;
                if (!translatedAvailable) _translatedPartialMissingCount++;

                _logger.Log(SessionTag, "PartialMeasurement",
                    $"sequence={seq} elapsedFromT0Ms={elapsedFromT0Ms:F0} textLength={e.Result.Text.Length} " +
                    $"translatedLength={translated.Length} translatedAvailable={translatedAvailable} " +
                    $"isPrefixExtension={textCmp.IsPrefixExtension} unchanged={textCmp.Unchanged} regressed={textCmp.Regressed} " +
                    $"translatedIsPrefixExtension={translatedCmp.IsPrefixExtension} translatedUnchanged={translatedCmp.Unchanged} translatedRegressed={translatedCmp.Regressed}");

                _previousPartialText = e.Result.Text;
                _previousPartialTranslatedText = translated;

                // Step 3 shadow observation: SOURCE text only (never the translated text —
                // see ShadowStabilityObserver/PrefixStabilityEngine's source-first design),
                // computed and logged here but never forwarded to PartialResult/translation/
                // TTS/playback below. Reuses this same T0 origin (_utteranceCaptureStartedAt)
                // as the existing Step 1 instrumentation, per "reuse the existing measurement
                // origin where valid" — not a separate/competing clock.
                var utteranceId = $"gen{myGeneration}-utt{shadowUtteranceIndex}";
                if (seq == 1)
                    shadowComparator.BeginUtterance(utteranceId, _utteranceCaptureStartedAt);
                var (policyAResult, _) = shadowComparator.ObservePartial(seq, e.Result.Text, DateTimeOffset.UtcNow);

                // Step 5: correlate Policy A's source-stability decision with Azure's own
                // per-partial translated text — SOURCE text still never used for the
                // translation-shadow's own comparisons (only the segment boundaries are
                // read from `policyAResult`); `translated` here is Azure's already-produced
                // value, not a new API call.
                translationShadow.ObservePartial(new IncrementalTranslationInput(
                    utteranceId, seq, _sourceLanguage, _targetLanguage,
                    policyAResult.NewlyCommittedSegment, policyAResult.CommittedSourceText,
                    translated, IsFinal: false));
            }

            PartialResult?.Invoke(this, new TranslationResult(e.Result.Text, translated, false, TimeSpan.FromTicks(e.Result.OffsetInTicks)));
        };

        recognizer.Recognized += (_, e) =>
        {
            if (myGeneration != _generation) return;
            if (e.Result.Reason != ResultReason.TranslatedSpeech) return;
            _logger.Log(SessionTag, "RecognitionEvent", $"kind=final generation={myGeneration} textLength={e.Result.Text.Length}");
            var translated = e.Result.Translations.TryGetValue(targetTwoLetter, out var t) ? t : "";

            // Utterance-quality gate: decides whether the audio Azure is about to
            // synthesize for THIS utterance should be forwarded downstream. This is a
            // quality filter, not speaker identification — see UtteranceEligibilityGate's
            // doc comment. Transcript display (FinalResult below) is unaffected either way.
            var detailedJson = e.Result.Properties.GetProperty(PropertyId.SpeechServiceResponse_JsonResult);
            var confidence = AzureConfidenceParser.TryParse(detailedJson);
            var decision = UtteranceEligibilityGate.Evaluate(e.Result.Duration, e.Result.Text, confidence);
            _lastUtteranceAccepted = decision.Accepted;
            _logger.Log(SessionTag, "UtteranceEvaluated",
                $"generation={myGeneration} durationMs={e.Result.Duration.TotalMilliseconds:F0} " +
                $"confidence={(confidence.HasValue ? confidence.Value.ToString("F2") : "null")} " +
                $"accepted={decision.Accepted} reason=\"{decision.Reason}\"");

            if (decision.Accepted)
                ArmTtsWatchdog(myGeneration);

            // Step 1 instrumentation: summarize this utterance's partial-result behavior
            // (T3 = now), then reset per-utterance state for the next one. T0 is
            // snapshotted for the Synthesizing handler (T5) before being cleared here,
            // since Synthesizing arrives after this reset would otherwise have happened.
            lock (_instrumentationLock)
            {
                var elapsedT0ToFinalMs = (DateTimeOffset.UtcNow - _utteranceCaptureStartedAt).TotalMilliseconds;
                _logger.Log(SessionTag, "UtteranceSummary",
                    $"generation={myGeneration} partialCount={_partialSequenceInUtterance} revisionCount={_partialRevisionCount} " +
                    $"translatedPartialMissingCount={_translatedPartialMissingCount} finalDurationMs={e.Result.Duration.TotalMilliseconds:F0} " +
                    $"elapsedT0ToFinalMs={elapsedT0ToFinalMs:F0} eligibilityAccepted={decision.Accepted}");

                // Step 3 shadow observation: finalize the shadow engine with the true
                // final SOURCE text (SOURCE only, per the engine's source-first design —
                // the translated text `translated`, computed below, is never passed in).
                // Diagnostic-only: this result is logged and discarded, never forwarded
                // to FinalResult/translation/TTS/playback. Handles the zero-partial case
                // (Step-1-observed short utterances that go straight to Final) by
                // beginning the shadow utterance here if no partial ever started one.
                var utteranceId = $"gen{myGeneration}-utt{shadowUtteranceIndex}";
                if (_partialSequenceInUtterance == 0)
                    shadowComparator.BeginUtterance(utteranceId, _utteranceCaptureStartedAt);
                shadowComparator.ObserveFinal(_partialSequenceInUtterance + 1, e.Result.Text, DateTimeOffset.UtcNow);

                // Step 5: finalize the translation shadow with the true final SOURCE and
                // translated text (both already produced by Azure — no new API call).
                translationShadow.ObserveFinal(new IncrementalTranslationInput(
                    utteranceId, _partialSequenceInUtterance + 1, _sourceLanguage, _targetLanguage,
                    null, "", translated, IsFinal: true,
                    FinalSourceText: e.Result.Text, FinalTranslatedText: translated));

                shadowUtteranceIndex++;

                _t0ForPendingSynthesisTiming = _utteranceCaptureStartedAt;
                _firstSynthesisLoggedForUtterance = false;

                _partialSequenceInUtterance = 0;
                _previousPartialText = null;
                _previousPartialTranslatedText = null;
                _partialRevisionCount = 0;
                _translatedPartialMissingCount = 0;
                _awaitingCaptureForInstrumentation = true;
            }

            FinalResult?.Invoke(this, new TranslationResult(e.Result.Text, translated, true, TimeSpan.FromTicks(e.Result.OffsetInTicks)));
        };

        recognizer.Synthesizing += (_, e) =>
        {
            if (myGeneration != _generation) return;
            var audio = e.Result.GetAudio();
            if (audio is not { Length: > 0 }) return;

            DisarmTtsWatchdog(); // TTS produced something — cancel the "missing" watchdog regardless of downstream mute/gate suppression

            // Step 1 instrumentation: T5 = first synthesized chunk for this utterance,
            // logged once (Azure can stream several chunks per utterance; only the
            // first is the meaningful "first TTS audio" measurement).
            lock (_instrumentationLock)
            {
                if (!_firstSynthesisLoggedForUtterance)
                {
                    var elapsedT0ToFirstSynthesisMs = (DateTimeOffset.UtcNow - _t0ForPendingSynthesisTiming).TotalMilliseconds;
                    _logger.Log(SessionTag, "SynthesisTiming", $"generation={myGeneration} elapsedT0ToFirstSynthesisMs={elapsedT0ToFirstSynthesisMs:F0}");
                    _firstSynthesisLoggedForUtterance = true;
                }
            }

            if (!_lastUtteranceAccepted)
            {
                _logger.Log(SessionTag, "AudioSuppressed", $"generation={myGeneration} bytes={audio.Length}");
                return;
            }

            AudioSynthesized?.Invoke(this, new SynthesizedAudio(audio, 16000));
        };

        recognizer.Canceled += OnCanceled;

        _recognizer = recognizer;
        await recognizer.StartContinuousRecognitionAsync();
        _reconnectAttempt = 0; // successful (re)connect resets the backoff counter
        _logger.Log(SessionTag, "Connected", $"generation={myGeneration}");
        StatusChanged?.Invoke(this, "Connected");
    }

    private void OnCanceled(object? sender, TranslationRecognitionCanceledEventArgs e)
    {
        // Ignore events from a recognizer instance we've already moved past (e.g. the
        // old instance's own StopContinuousRecognitionAsync firing Canceled after a
        // reconnect already swapped _recognizer for a new instance).
        if (!ReferenceEquals(sender, _recognizer)) return;
        if (_stopRequested) return; // expected shutdown, not a failure

        var isTransient = IsTransientFailure(e);
        _logger.Log(SessionTag, "Disconnected", $"reason={e.Reason} errorCode={e.ErrorCode} transient={isTransient}");

        // Step 1 instrumentation: was an utterance actively in progress (at least one
        // partial seen, no Final yet) at the moment of disconnect? If so, that
        // utterance's content is lost — the reconnect starts a new recognizer/generation
        // with no way to resume mid-utterance. This directly answers "does reconnect
        // cause an in-progress utterance to disappear."
        lock (_instrumentationLock)
        {
            if (_partialSequenceInUtterance > 0)
            {
                _logger.Log(SessionTag, "ReconnectDuringUtterance",
                    $"partialsSoFar={_partialSequenceInUtterance} lastPartialTextLength={_previousPartialText?.Length ?? 0} " +
                    "note=utterance in progress at disconnect; content lost, no mid-utterance resume");
                _partialSequenceInUtterance = 0;
                _previousPartialText = null;
                _previousPartialTranslatedText = null;
                _partialRevisionCount = 0;
                _translatedPartialMissingCount = 0;
                _awaitingCaptureForInstrumentation = true;
            }
        }

        if (isTransient && _reconnectAttempt < MaxReconnectAttempts)
        {
            _reconnectAttempt++;
            var delay = TimeSpan.FromSeconds(Math.Pow(2, _reconnectAttempt)); // 2s,4s,8s,16s,32s
            var attempt = _reconnectAttempt;
            _logger.Log(SessionTag, "ReconnectAttempt", $"attempt={attempt}/{MaxReconnectAttempts} delayMs={delay.TotalMilliseconds:F0}");
            StatusChanged?.Invoke(this, $"Connection lost ({e.ErrorDetails}). Reconnecting, attempt {attempt}/{MaxReconnectAttempts} in {delay.TotalSeconds:F0}s...");
            Error?.Invoke(this, new ProviderError($"Transient error: {e.ErrorDetails} — reconnecting ({attempt}/{MaxReconnectAttempts})", IsFatal: false));

            var lifetimeToken = _lifetimeCts?.Token ?? CancellationToken.None;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, lifetimeToken);

                    await _connectionLock.WaitAsync(lifetimeToken);
                    try
                    {
                        // Re-check INSIDE the lock: Stop() may have been called and
                        // completed while we were waiting for the delay/lock, and
                        // must win — never start a new stream after shutdown.
                        if (_stopRequested) return;
                        await CreateAndStartRecognizerLockedAsync();
                    }
                    finally
                    {
                        _connectionLock.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    // shutting down while a reconnect was pending — not an error
                }
                catch (ObjectDisposedException)
                {
                    // DisposeAsync tore down the connection lock/recognizer while this
                    // reconnect was in flight — shutdown won the race, which is correct.
                }
                catch (Exception ex)
                {
                    _logger.Log(SessionTag, "ReconnectResult", $"attempt={attempt}/{MaxReconnectAttempts} outcome=failed error={ex.GetType().Name}");
                    Error?.Invoke(this, new ProviderError($"Reconnect attempt {attempt} failed: {ex.Message}", IsFatal: attempt >= MaxReconnectAttempts));
                }
            });
        }
        else
        {
            var reason = isTransient ? "Exhausted reconnect attempts" : "Non-transient error (config/auth)";
            _logger.Log(SessionTag, "ReconnectResult", $"outcome=fatal reason={reason}");
            Error?.Invoke(this, new ProviderError($"{reason}: {e.Reason} — {e.ErrorDetails}", IsFatal: true));
        }
    }

    /// <summary>
    /// Distinguishes a network/service hiccup (worth retrying) from a config/auth problem
    /// (retrying would just fail the same way forever — e.g. a bad key or invalid region).
    /// </summary>
    private static bool IsTransientFailure(TranslationRecognitionCanceledEventArgs e)
    {
        if (e.Reason != CancellationReason.Error) return false;
        return e.ErrorCode is CancellationErrorCode.ConnectionFailure
            or CancellationErrorCode.ServiceTimeout
            or CancellationErrorCode.ServiceUnavailable;
    }

    public void PushAudio(ReadOnlySpan<byte> pcm16)
    {
        if (_stopRequested) return; // don't touch a stream that's being/been torn down

        try
        {
            // Step 1 instrumentation: T0 = first forwarded chunk since the last
            // utterance boundary. Independent of DirectionPipeline's own T0 tracking
            // (LatencyBreakdown) — this provider has no reference to that instance, by
            // design (keeps the provider stateless w.r.t. its caller); this is a
            // self-contained, provider-local T0 used only for this measurement's logs.
            lock (_instrumentationLock)
            {
                if (_awaitingCaptureForInstrumentation)
                {
                    _utteranceCaptureStartedAt = DateTimeOffset.UtcNow;
                    _awaitingCaptureForInstrumentation = false;
                }
            }

            _pushStream?.Write(pcm16.ToArray());
            var total = Interlocked.Increment(ref _totalChunksSent);
            if (total % AudioLogEveryNChunks == 0)
                _logger.Log(SessionTag, "AudioChunksSent", $"totalChunks={total} lastChunkBytes={pcm16.Length}");
        }
        catch (Exception ex)
        {
            if (!_stopRequested) // a close-during-shutdown race is expected, not an error
                Error?.Invoke(this, new ProviderError("Failed to push audio to provider.", false, ex));
        }
    }

    public async Task StopAsync()
    {
        _logger.Log(SessionTag, "Shutdown", "StopAsync called");
        _stopRequested = true;
        _lifetimeCts?.Cancel();
        DisarmTtsWatchdog();

        await _connectionLock.WaitAsync();
        try
        {
            if (_recognizer != null)
            {
                try { await _recognizer.StopContinuousRecognitionAsync(); }
                catch { /* already stopped/canceled — fine during shutdown */ }
            }
            _pushStream?.Close();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Must be called while holding <see cref="_connectionLock"/>.</summary>
    private void DisposeRecognizerLocked()
    {
        if (_recognizer == null) return;
        _recognizer.Canceled -= OnCanceled;
        _recognizer.Dispose();
        _recognizer = null;
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { /* best-effort shutdown */ }

        await _connectionLock.WaitAsync();
        try
        {
            DisposeRecognizerLocked();
            _pushStream?.Dispose();
        }
        finally
        {
            _connectionLock.Release();
        }

        _lifetimeCts?.Dispose();
        _connectionLock.Dispose();
        _logger.Log(SessionTag, "Shutdown", "DisposeAsync completed");
    }

    private void ArmTtsWatchdog(int forGeneration)
    {
        lock (_watchdogLock)
        {
            _ttsWatchdog?.Dispose();
            _ttsWatchdog = new Timer(_ =>
            {
                // Fires only if no Synthesizing event arrived in time, and only if we're
                // still the same generation (a reconnect/new utterance already moved on).
                if (forGeneration == _generation && !_stopRequested)
                    _logger.Log(SessionTag, "TtsMissing",
                        $"generation={forGeneration} timeoutMs={TtsWatchdogTimeoutMs} " +
                        "note=an accepted utterance produced no synthesized audio within the timeout");
            }, null, TtsWatchdogTimeoutMs, Timeout.Infinite);
        }
    }

    private void DisarmTtsWatchdog()
    {
        lock (_watchdogLock)
        {
            _ttsWatchdog?.Dispose();
            _ttsWatchdog = null;
        }
    }

    private static string ToTwoLetter(string bcp47) => bcp47.Split('-')[0];
}
