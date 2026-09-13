using Microsoft.CognitiveServices.Speech;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.12. Real client for Azure Speech's neural TTS via
/// <c>Microsoft.CognitiveServices.Speech.SpeechSynthesizer</c> — the SAME Azure Speech
/// resource/credentials (<c>AZURE_SPEECH_KEY</c>/<c>AZURE_SPEECH_REGION</c>) already used
/// throughout this project for TTS (bundled inside <c>AzureSpeechTranslationProvider</c>'s
/// <c>TranslationRecognizer</c>, and standalone in <c>VTTranslate.LiveTest</c>'s own audio-
/// generation helpers, e.g. <c>GenerateTestAudioAsync</c>) — this is NOT a new/unverified
/// credential path; TTS capability for this resource was already proven working
/// repeatedly across this project's session history before this step began. This class
/// simply wraps the same underlying capability behind the isolated
/// <see cref="IStreamingTtsProvider"/> abstraction, standalone (not bundled with
/// recognition), for this experiment's own use.
///
/// Output format is explicitly set to 16kHz/16-bit/mono PCM (RIFF/WAV container) — see
/// docs/design-notes/streaming-tts-feasibility-experiment.md §Audio format for why this
/// was chosen (consistency with every other WAV file this project already produces) and
/// confirmation it was not assumed.
///
/// Streaming behavior: Azure's <c>Synthesizing</c> event fires once per audio chunk AS IT
/// IS GENERATED, before the final <c>SynthesisCompleted</c> event — a genuine, already-
/// used-in-this-project streaming mechanism (the same one <c>AzureSpeechTranslationProvider</c>'s
/// own <c>Synthesizing</c> handler already relies on for T5 latency measurement since
/// Step 1). This class surfaces that same mechanism through <paramref name="onChunk"/> for
/// timing measurement — never invented, never assumed.
///
/// Never coupled to production playback — has no reference to <c>IAudioOutputSink</c> or
/// any production audio type. Never wired into <c>AzureSpeechTranslationProvider</c>.
/// </summary>
public sealed class AzureStreamingTtsProvider : IStreamingTtsProvider
{
    public const string DefaultOutputFormatDescription = "Riff16Khz16BitMonoPcm (16kHz, 16-bit, mono, PCM/WAV)";

    private readonly string _subscriptionKey;
    private readonly string _region;

    public AzureStreamingTtsProvider(string subscriptionKey, string region)
    {
        _subscriptionKey = subscriptionKey;
        _region = region;
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        string text, string targetLanguage, string voiceName, Action<TtsAudioChunk> onChunk, CancellationToken ct)
    {
        try
        {
            var config = SpeechConfig.FromSubscription(_subscriptionKey, _region);
            config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm);
            config.SpeechSynthesisVoiceName = voiceName;

            using var synthesizer = new SpeechSynthesizer(config, null); // null AudioConfig — no automatic device playback, result captured in-memory only

            var totalBytes = 0;
            var chunkCount = 0;
            DateTimeOffset? firstChunkAt = null;
            var chunkLock = new object();

            synthesizer.Synthesizing += (_, e) =>
            {
                var audio = e.Result.AudioData;
                if (audio is not { Length: > 0 }) return;
                lock (chunkLock)
                {
                    chunkCount++;
                    totalBytes += audio.Length;
                    firstChunkAt ??= DateTimeOffset.UtcNow;
                }
                onChunk(new TtsAudioChunk(audio, chunkCount, DateTimeOffset.UtcNow));
            };

            using var registration = ct.Register(() => { try { synthesizer.StopSpeakingAsync(); } catch { /* best-effort cancellation */ } });

            var result = await synthesizer.SpeakTextAsync(text);
            var completedAt = DateTimeOffset.UtcNow;

            if (ct.IsCancellationRequested)
                return new TtsSynthesisResult(false, totalBytes, chunkCount, "Cancelled", firstChunkAt, completedAt);

            if (result.Reason != ResultReason.SynthesizingAudioCompleted)
            {
                var reason = result.Reason == ResultReason.Canceled
                    ? $"Canceled: {SpeechSynthesisCancellationDetails.FromResult(result).Reason}"
                    : $"Unexpected reason: {result.Reason}";
                return new TtsSynthesisResult(false, totalBytes, chunkCount, reason, firstChunkAt, completedAt);
            }

            if (totalBytes == 0)
                return new TtsSynthesisResult(false, 0, 0, "Empty synthesis — no audio bytes produced", firstChunkAt, completedAt);

            return new TtsSynthesisResult(true, totalBytes, chunkCount, null, firstChunkAt, completedAt);
        }
        catch (OperationCanceledException)
        {
            return new TtsSynthesisResult(false, 0, 0, "Cancelled", null, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            // Never include the input text or any credential value in the failure reason.
            return new TtsSynthesisResult(false, 0, 0, $"{ex.GetType().Name}: synthesis failed", null, DateTimeOffset.UtcNow);
        }
    }
}
