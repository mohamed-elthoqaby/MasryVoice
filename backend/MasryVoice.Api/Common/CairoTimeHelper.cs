namespace MasryVoice.Api.Common;

public static class CairoTimeHelper
{
    private static readonly TimeZoneInfo CairoTimeZone;

    static CairoTimeHelper()
    {
        try
        {
            // Windows standard ID
            CairoTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Egypt Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            // Fallback for Linux / IANA
            CairoTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
        }
    }

    public static TimeZoneInfo TimeZone => CairoTimeZone;

    public static DateTime UtcToCairo(DateTime utcDateTime)
    {
        var utc = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, CairoTimeZone);
    }

    public static DateTime CairoToUtc(DateTime cairoDateTime)
    {
        var unspecified = DateTime.SpecifyKind(cairoDateTime, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, CairoTimeZone);
    }

    public static DateTime NowCairo => UtcToCairo(DateTime.UtcNow);

    public static bool IsBusinessHours(DateTime cairoTime)
    {
        // Egyptian business week: Sunday through Thursday, 09:00 to 17:00
        var day = cairoTime.DayOfWeek;
        var isWorkday = day is DayOfWeek.Sunday
            or DayOfWeek.Monday
            or DayOfWeek.Tuesday
            or DayOfWeek.Wednesday
            or DayOfWeek.Thursday;

        if (!isWorkday) return false;

        var hour = cairoTime.Hour;
        var minute = cairoTime.Minute;
        var totalMinutes = hour * 60 + minute;

        return totalMinutes >= 9 * 60 && totalMinutes < 17 * 60;
    }

    public static string FormatCairoFriendly(DateTime utcDateTime)
    {
        var cairo = UtcToCairo(utcDateTime);
        var dayNameAr = cairo.DayOfWeek switch
        {
            DayOfWeek.Sunday => "الأحد",
            DayOfWeek.Monday => "الإثنين",
            DayOfWeek.Tuesday => "الثلاثاء",
            DayOfWeek.Wednesday => "الأربعاء",
            DayOfWeek.Thursday => "الخميس",
            DayOfWeek.Friday => "الجمعة",
            DayOfWeek.Saturday => "السبت",
            _ => ""
        };

        var time12 = cairo.ToString("hh:mm");
        var period = cairo.Hour < 12 ? "صباحاً" : "مساءً";
        return $"{dayNameAr} {cairo:yyyy/MM/dd} الساعة {time12} {period} بتوقيت القاهرة";
    }
}
