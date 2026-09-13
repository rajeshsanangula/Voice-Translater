namespace VTTranslate.Core.Providers;

/// <summary>
/// A streaming speech-to-speech translation channel: raw audio in, translated text
/// and synthesized speech out. One instance handles one fixed source-to-target
/// language pair for the life of the session (Azure's streaming translation
/// recognizer is bound to a source language per connection).
/// </summary>
public interface ISpeechTranslationProvider : IAsyncDisposable
{
    /// <summary>Raised when a partial (not-yet-final) recognition/translation is available.</summary>
    event EventHandler<TranslationResult>? PartialResult;

    /// <summary>Raised when a segment's recognition and translation are final.</summary>
    event EventHandler<TranslationResult>? FinalResult;

    /// <summary>Raised when synthesized target-language audio is ready to play.</summary>
    event EventHandler<SynthesizedAudio>? AudioSynthesized;

    /// <summary>Raised on a recoverable or fatal provider/network error.</summary>
    event EventHandler<ProviderError>? Error;

    Task StartAsync(string sourceLanguage, string targetLanguage, CancellationToken ct);

    /// <summary>Feed one chunk of 16-bit PCM audio at the provider's expected sample rate.</summary>
    void PushAudio(ReadOnlySpan<byte> pcm16);

    Task StopAsync();
}

public sealed record TranslationResult(
    string SourceText,
    string TranslatedText,
    bool IsFinal,
    TimeSpan Offset);

public sealed record SynthesizedAudio(byte[] Pcm16, int SampleRateHz);

public sealed record ProviderError(string Message, bool IsFatal, Exception? Exception = null);
