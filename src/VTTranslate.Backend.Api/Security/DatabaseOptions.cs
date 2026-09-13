namespace VTTranslate.Backend.Api.Security;

/// <summary>
/// Phase 6.5 database configuration boundary. <see cref="ConnectionString"/> is the ONLY
/// setting — it is a secret (carries a database password) and must never be committed;
/// it is supplied via environment variable / secret manager in every real deployment
/// (see appsettings.json's own placeholder comment, and
/// <see cref="ConfigurationSecretSafetyTests"/> equivalent guard in the test suite,
/// which asserts the committed file never contains one).
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string? ConnectionString { get; set; }
}
