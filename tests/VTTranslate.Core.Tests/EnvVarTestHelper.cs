namespace VTTranslate.Core.Tests;

/// <summary>
/// AppSettings reads the Azure key/region only from environment variables (by design —
/// see AppSettings.cs), checking Process scope, then falling back to User scope. Tests
/// that need to simulate "not configured" must therefore set a Process-scope override
/// that actually blocks the fallback — NOT an empty string. .NET's
/// Environment.SetEnvironmentVariable treats null-or-empty as "delete this override",
/// which would let the fallback chain reach through to a real value now genuinely
/// present at User scope on any machine that has run the app (a real regression this
/// helper caused before this fix — GetEnv() ended up reading the real configured key
/// during a test asserting "not configured"). A single space is blank per
/// string.IsNullOrWhiteSpace (what AppSettings.IsProviderConfigured checks) while still
/// being a non-empty override that actually sets at Process scope.
/// </summary>
internal static class EnvVarTestHelper
{
    public static IDisposable SetTemporarily(string name, string? value)
    {
        var effectiveValue = value == "" ? " " : value;
        var previous = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(name, effectiveValue, EnvironmentVariableTarget.Process);
        return new Restorer(name, previous);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;
        public Restorer(string name, string? previous) { _name = name; _previous = previous; }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous, EnvironmentVariableTarget.Process);
    }
}
