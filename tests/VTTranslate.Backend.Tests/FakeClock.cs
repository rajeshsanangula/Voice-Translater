using VTTranslate.Backend.Domain.Abstractions;

namespace VTTranslate.Backend.Tests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
