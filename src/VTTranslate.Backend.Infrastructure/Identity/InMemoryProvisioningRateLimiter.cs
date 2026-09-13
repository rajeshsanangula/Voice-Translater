using System.Collections.Concurrent;
using VTTranslate.Backend.Application.Identity;
using VTTranslate.Backend.Domain.Abstractions;

namespace VTTranslate.Backend.Infrastructure.Identity;

/// <summary>
/// PRODUCTION LIMITATION (see docs/phase-7.0-production-identity-and-account-lifecycle.md
/// §15/§23): this is a single-process, in-memory sliding-window limiter. It is correct
/// and sufficient for local development, the deterministic test suite, and a genuinely
/// single-instance deployment — it is NOT a production-grade limiter for a
/// multi-instance deployment, since each instance tracks attempts independently and the
/// effective limit becomes (configured limit × instance count). A multi-instance
/// production deployment MUST replace this registration with a distributed
/// implementation of <see cref="IProvisioningRateLimiter"/> (e.g. backed by Redis) —
/// this class exists to define the correct application-level contract now, not to be
/// the production implementation.
///
/// Stores only the caller-supplied key and attempt timestamps — never an IP address or
/// any other caller-identifying data beyond what the caller already chose as the key
/// (Phase 7.0's own caller uses the external identity's (provider, subject) pair, never
/// an IP — see <c>AccountProvisioningService</c>).
/// </summary>
public sealed class InMemoryProvisioningRateLimiter(IClock clock) : IProvisioningRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _attempts = new();

    public Task<bool> TryAcquireAsync(string key, int maxAttempts, TimeSpan window, CancellationToken ct)
    {
        // Fail closed on invalid configuration — never silently treat as "unlimited."
        if (maxAttempts <= 0 || window <= TimeSpan.Zero)
            return Task.FromResult(false);

        var now = clock.UtcNow;
        var queue = _attempts.GetOrAdd(key, _ => new Queue<DateTimeOffset>());

        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > window)
                queue.Dequeue();

            if (queue.Count >= maxAttempts)
                return Task.FromResult(false);

            queue.Enqueue(now);
            return Task.FromResult(true);
        }
    }
}
