// Interim: belongs in background-tools-lib as part of PyCompat.
using System.Globalization;
using System.Text;

namespace MailTriage.Interim;

/// <summary>Python's datetime formatting and parsing, as the Python tools wrote and read times. Times compared as
/// strings (the deleted record, the raised file) depend on these being exact. Naive times are DateTime with Kind
/// Unspecified: a wall-clock time, as Python's naive datetime.</summary>
public static class PyTime
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A time cut to whole microseconds, as Python's datetime holds it.</summary>
    public static DateTime Micro(DateTime t) => new(t.Ticks - t.Ticks % 10, DateTimeKind.Unspecified);

    /// <summary>isoformat(timespec=...): "minutes", "seconds", "microseconds", or "auto" (seconds, plus
    /// microseconds only when they aren't zero).</summary>
    public static string Iso(DateTime t, string timespec = "auto")
    {
        var micro = (int)(t.Ticks % TimeSpan.TicksPerSecond / 10);
        return timespec switch
        {
            "minutes" => t.ToString("yyyy-MM-dd'T'HH:mm", Inv),
            "seconds" => t.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv),
            "microseconds" => t.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv) + "." + micro.ToString("000000", Inv),
            "auto" => t.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv) + (micro != 0 ? "." + micro.ToString("000000", Inv) : ""),
            _ => throw new ArgumentException($"Unknown timespec value: {timespec}"),
        };
    }

    /// <summary>date.isoformat().</summary>
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    /// <summary>The strftime directives the tool uses or accepts in settings. Anything else is refused, so a
    /// format never means something different in .NET.</summary>
    public const string Directives = "aAbBdmyYHIMSp%";

    /// <summary>Whether a strftime format uses only the supported directives.</summary>
    public static bool Supported(string format)
    {
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%')
                continue;
            if (i + 1 >= format.Length || !Directives.Contains(format[i + 1]))
                return false;
            i++;
        }
        return true;
    }

    private static readonly string[] Days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
    private static readonly string[] Months =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>Weekday as Python's: Monday 0 to Sunday 6.</summary>
    public static int Weekday(DateOnly d) => ((int)d.DayOfWeek + 6) % 7;

    public static int Weekday(DateTime t) => Weekday(DateOnly.FromDateTime(t));

    /// <summary>strftime in the C locale (English names), for the supported directives.</summary>
    public static string Strftime(DateTime t, string format)
    {
        var b = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%' || i + 1 >= format.Length)
            {
                b.Append(c);
                continue;
            }
            var d = format[++i];
            switch (d)
            {
                case 'a': b.Append(Days[Weekday(t)][..3]); break;
                case 'A': b.Append(Days[Weekday(t)]); break;
                case 'b': b.Append(Months[t.Month - 1][..3]); break;
                case 'B': b.Append(Months[t.Month - 1]); break;
                case 'd': b.Append(t.Day.ToString("00", Inv)); break;
                case 'm': b.Append(t.Month.ToString("00", Inv)); break;
                case 'y': b.Append((t.Year % 100).ToString("00", Inv)); break;
                case 'Y': b.Append(t.Year.ToString(Inv)); break;
                case 'H': b.Append(t.Hour.ToString("00", Inv)); break;
                case 'I': b.Append((t.Hour % 12 == 0 ? 12 : t.Hour % 12).ToString("00", Inv)); break;
                case 'M': b.Append(t.Minute.ToString("00", Inv)); break;
                case 'S': b.Append(t.Second.ToString("00", Inv)); break;
                case 'p': b.Append(t.Hour < 12 ? "AM" : "PM"); break;
                case '%': b.Append('%'); break;
                default: throw new FormatException($"Unsupported strftime directive %{d}");
            }
        }
        return b.ToString();
    }

    public static string Strftime(DateOnly d, string format) => Strftime(d.ToDateTime(TimeOnly.MinValue), format);

    /// <summary>datetime.fromisoformat for the naive forms the tool meets: a date, or a date and time with "T" or
    /// a space between, with minutes, seconds and up to six decimal places.</summary>
    public static DateTime FromIso(string text)
    {
        string[] formats =
        [
            "yyyy-MM-dd", "yyyy-MM-dd'T'HH", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.f",
            "yyyy-MM-dd'T'HH:mm:ss.ff", "yyyy-MM-dd'T'HH:mm:ss.fff", "yyyy-MM-dd'T'HH:mm:ss.ffff",
            "yyyy-MM-dd'T'HH:mm:ss.fffff", "yyyy-MM-dd'T'HH:mm:ss.ffffff",
        ];
        var t = text.Length > 10 && text[10] == ' ' ? text[..10] + "T" + text[11..] : text;
        if (DateTime.TryParseExact(t, formats, Inv, DateTimeStyles.None, out var value))
            return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        throw new FormatException($"Invalid isoformat string: {Py.ReprString(text)}");
    }

    /// <summary>time.fromisoformat for "HH:MM" (and "HH:MM:SS"). "24:00" is refused, as Python refuses it.</summary>
    public static TimeSpan TimeFromIso(string text)
    {
        if (TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss"], Inv, DateTimeStyles.None, out var value))
            return value.ToTimeSpan();
        throw new FormatException($"Invalid isoformat string: {Py.ReprString(text)}");
    }

    /// <summary>datetime.time.max, as a time of day.</summary>
    public static readonly TimeSpan TimeMax = new(TimeSpan.TicksPerDay - 10);

    /// <summary>A naive local time made aware in a zone, as Python's t.replace(tzinfo=zone) does with zoneinfo:
    /// an hour that happens twice is the first (summer time); an hour that doesn't happen takes the offset from
    /// before the change.</summary>
    public static DateTimeOffset Aware(DateTime local, TimeZoneInfo zone)
    {
        var t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsAmbiguousTime(t))
            offset = zone.GetAmbiguousTimeOffsets(t).Max();
        else if (zone.IsInvalidTime(t))
            offset = zone.GetUtcOffset(t.AddHours(-3));
        else
            offset = zone.GetUtcOffset(t);
        return new DateTimeOffset(t, offset);
    }

    /// <summary>t.astimezone(zone).replace(tzinfo=None).</summary>
    public static DateTime Local(DateTimeOffset t, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(t, zone).DateTime, DateTimeKind.Unspecified);

    /// <summary>The London zone, by its Windows id (IANA ids need ICU, which invariant globalisation leaves out).</summary>
    public static TimeZoneInfo Zone(string windowsId) =>
        windowsId.Equals("UTC", StringComparison.OrdinalIgnoreCase) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(windowsId);
}
