using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Tests;

/// <summary>Test-only logger that captures entries in memory so tests can assert on them directly.</summary>
internal sealed class InMemoryDiagnosticLogger : IDiagnosticLogger
{
    public List<(string SessionTag, string EventType, string Details)> Entries { get; } = new();

    public void Log(string sessionTag, string eventType, string details) =>
        Entries.Add((sessionTag, eventType, details));
}
