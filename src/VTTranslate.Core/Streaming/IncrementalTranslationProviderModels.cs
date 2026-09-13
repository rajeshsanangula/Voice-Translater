namespace VTTranslate.Core.Streaming;

/// <summary>
/// Step 5.5 — EXPERIMENTAL, SHADOW-ONLY. One request to an independent translation
/// provider (as opposed to Step 5's reuse of Azure Speech's bundled translated output).
/// See docs/design-notes/incremental-translation-provider-feasibility.md.
/// </summary>
public sealed record TranslationProviderRequest(
    string SourceLanguage,
    string TargetLanguage,
    string TextToTranslate,
    string? BoundedContext = null);

/// <summary>
/// Result of one translation-provider call. <see cref="CandidateTranslatedText"/> exists
/// ONLY in memory for comparison/testing — callers must never pass it to a logger (see
/// the diagnostic wrapper in <see cref="IncrementalTranslationProviderExperiment"/>, which
/// logs only lengths/booleans/status codes).
/// </summary>
public sealed record TranslationProviderResult(
    bool Success,
    string? CandidateTranslatedText,
    string? FailureReason,
    int? HttpStatusCode);

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY abstraction over a genuinely independent (not Speech-SDK-bundled)
/// translation call. Has no events and no reference to TTS/playback/production translation —
/// same structural isolation guarantee as every other shadow component in this project.
/// </summary>
public interface IIncrementalTranslationProvider
{
    Task<TranslationProviderResult> TranslateAsync(TranslationProviderRequest request, CancellationToken ct);
}
