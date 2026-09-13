namespace VTTranslate.Core.Diagnostics;

/// <summary>
/// Appends timestamped diagnostic lines to a local rolling daily log file under
/// %AppData%\VTTranslate\logs\. Purely additive/observational — never touches Azure
/// keys or recognized/translated speech content (enforced by convention at call
/// sites, since this class has no way to know what a caller passes it; see
/// IDiagnosticLogger's doc comment for the contract).
/// </summary>
public sealed class FileDiagnosticLogger : IDiagnosticLogger
{
    private readonly object _lock = new();
    private readonly string _path;

    public FileDiagnosticLogger(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VTTranslate", "logs", $"diagnostic-{DateTime.UtcNow:yyyy-MM-dd}.log");

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public void Log(string sessionTag, string eventType, string details)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{sessionTag}] {eventType}: {details}";
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostic logging must never crash or destabilize the translation
            // pipeline (e.g. disk full, permissions) — best-effort only.
        }
    }
}
