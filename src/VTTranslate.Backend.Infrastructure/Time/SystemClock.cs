using VTTranslate.Backend.Domain.Abstractions;

namespace VTTranslate.Backend.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
