namespace VTTranslate.Core.Diagnostics;

/// <summary>
/// Lightweight structured diagnostic logging for the translation pipeline. Callers
/// must never pass recognized/translated text, Azure keys, or region/endpoint
/// secrets through <paramref name="details"/> — only metadata (counts, lengths,
/// generation IDs, error codes, attempt numbers). See <see cref="FileDiagnosticLogger"/>
/// for the production sink.
/// </summary>
public interface IDiagnosticLogger
{
    /// <param name="sessionTag">A direction/session identifier, e.g. "en-US-&gt;de-DE" — never speech content.</param>
    /// <param name="eventType">A short category, e.g. "Connected", "ReconnectAttempt", "AudioChunksSent".</param>
    /// <param name="details">Metadata only — no recognized/translated text, no secrets.</param>
    void Log(string sessionTag, string eventType, string details);
}
