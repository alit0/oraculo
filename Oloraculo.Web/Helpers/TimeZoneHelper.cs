namespace Oloraculo.Web.Helpers;

public static class TimeZoneHelper
{
    private static readonly TimeZoneInfo _argTz =
        TimeZoneInfo.FindSystemTimeZoneById("Argentina Standard Time");

    public static DateTimeOffset ToArgentina(this DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, _argTz);

    public static string FormatArgentina(this DateTimeOffset utc, string format = "dd MMM HH:mm") =>
        utc.ToArgentina().ToString(format);
}
