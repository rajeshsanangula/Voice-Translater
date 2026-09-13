namespace VTTranslate.Core.Session;

public enum SessionDirection
{
    EnglishMicToGerman,
    GermanRemoteToEnglish
}

public enum SessionStatus
{
    Idle,
    Starting,
    Running,
    Error,
    Stopped
}

public sealed class TranslationSession
{
    public Guid SessionId { get; } = Guid.NewGuid();
    public required SessionDirection Direction { get; init; }
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public SessionStatus Status { get; set; } = SessionStatus.Idle;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public string? LastError { get; set; }
}
