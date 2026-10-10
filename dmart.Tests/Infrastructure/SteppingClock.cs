namespace Dmart.Tests.Infrastructure;

// A clock that moves only when told to: each reading advances it by Step
// (zero by default, so it stands still).
internal sealed class SteppingClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.Ticks;
    public TimeSpan Step { get; set; } = TimeSpan.Zero;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Add(ref _ticks, Step.Ticks);
    public override DateTimeOffset GetUtcNow() => new(GetTimestamp(), TimeSpan.Zero);
}
