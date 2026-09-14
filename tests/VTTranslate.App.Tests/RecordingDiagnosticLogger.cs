using VTTranslate.Core.Diagnostics;

namespace VTTranslate.App.Tests;

/// <summary>Records event TYPES and metadata only — never asserts against speech content (there is none in these tests) — used to verify Phase 7.2 observability events fire and never carry secret-shaped values.</summary>
public sealed class RecordingDiagnosticLogger : IDiagnosticLogger
{
    public List<(string SessionTag, string EventType, string Details)> Events { get; } = new();

    public void Log(string sessionTag, string eventType, string details) =>
        Events.Add((sessionTag, eventType, details));

    public bool Contains(string eventType) => Events.Any(e => e.EventType == eventType);
}
