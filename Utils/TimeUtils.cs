namespace Dmart.Utils;

public static class TimeUtils
{
    // Local wall-clock now. Mirrors Python dmart_plain's `datetime.now()`
    // (naive local) for entity timestamps, log timestamps, OTP/session
    // TTL stamps, and the x-server-time header. Npgsql legacy timestamp
    // behavior (Program.cs sets EnableLegacyTimestampBehavior=true) accepts
    // a Kind=Local DateTime against TIMESTAMPTZ and converts it to UTC for
    // storage, so the stored instant stays correct regardless.
    public static DateTime Now() => DateTime.Now;

    public static DateTime UtcNow() => DateTime.UtcNow;
    public static long UnixSeconds(DateTime t) => new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds();
}
