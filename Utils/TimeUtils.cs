namespace Dmart.Utils;

public static class TimeUtils
{
    public static DateTime UtcNow() => DateTime.UtcNow;
    public static long UnixSeconds(DateTime t) => new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds();
}
