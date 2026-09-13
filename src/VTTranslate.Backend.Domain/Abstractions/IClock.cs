namespace VTTranslate.Backend.Domain.Abstractions;

/// <summary>Testable time source — every application-layer service that reasons about expiry/grace windows depends on this rather than <see cref="DateTimeOffset.UtcNow"/> directly, so tests can deterministically control "now" (needed for entitlement/grace-period boundary tests).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
