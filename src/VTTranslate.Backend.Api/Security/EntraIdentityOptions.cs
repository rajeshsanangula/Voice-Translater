namespace VTTranslate.Backend.Api.Security;

/// <summary>
/// Bound from the "Identity" configuration section (appsettings / environment
/// variables / secret manager — never a committed value, see appsettings.json's own
/// placeholder comment). Neither field is a secret: <see cref="Authority"/> is the
/// tenant's public OIDC issuer URL and <see cref="Audience"/> is this API's public
/// application/client ID — both are metadata a client needs anyway to acquire a token,
/// not credentials. No client secret is required anywhere in this phase because the API
/// only VALIDATES bearer tokens; it never acquires them.
/// </summary>
public sealed class EntraIdentityOptions
{
    public const string SectionName = "Identity";

    public string? Authority { get; set; }
    public string? Audience { get; set; }
}
