namespace FieldKit.SharedKernel;

/// <summary>
/// The only sanctioned source of time. All timestamps are UTC (see AT-7 / ADR-0010).
/// Inject this instead of touching <see cref="System.DateTime"/>/<see cref="System.DateTimeOffset"/>
/// statically — the banned-API analyzer fails the build on static time.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The single production <see cref="IClock"/> — the one place static UTC time is allowed.</summary>
public sealed class SystemClock : IClock
{
#pragma warning disable RS0030 // SystemClock is the sanctioned exception to the banned static-time rule.
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
#pragma warning restore RS0030
}
