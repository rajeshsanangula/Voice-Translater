namespace VTTranslate.Core.Diagnostics;

/// <summary>Default no-op logger — used when a caller doesn't supply one, so logging is always opt-in.</summary>
public sealed class NullDiagnosticLogger : IDiagnosticLogger
{
    public static readonly NullDiagnosticLogger Instance = new();
    private NullDiagnosticLogger() { }
    public void Log(string sessionTag, string eventType, string details) { }
}
