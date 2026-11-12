// Interim: copied from ai-keep-awake (src/AiKeepAwake/Py.cs). Belongs in background-tools-lib as PyCompat.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MailTriage.Interim;

/// <summary>The few bits of Python's behaviour the port has to reproduce exactly: truthiness and conversions of
/// JSON values, how numbers are printed, and line splitting. Parity with the Python version depends on these,
/// so they're kept in one place and tested.</summary>
public static class Py
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Python's bool(value) for a value from json.loads.</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => Number(v) != 0,
            _ => false,
        },
        _ => false,
    };

    /// <summary>Python's float(value). Throws FormatException (ValueError) or InvalidCastException (TypeError).</summary>
    public static double Float(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => Number(v),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => ParseFloat(v.GetValue<string>()),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => 1,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => 0,
        _ => throw new InvalidCastException("float() argument must be a string or a real number"),
    };

    /// <summary>Python's int(value): whole numbers exactly, decimals truncated, text parsed.</summary>
    public static long Int(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            switch (v.GetValueKind())
            {
                case JsonValueKind.Number:
                    var raw = NumberText(v);
                    if (IsWhole(raw))
                        return long.Parse(raw, NumberStyles.AllowLeadingSign, Inv);
                    var value = Number(v);
                    if (double.IsInfinity(value) || double.IsNaN(value))
                        throw new OverflowException("cannot convert float infinity or NaN to integer");
                    return checked((long)Math.Truncate(value));
                case JsonValueKind.String:
                    var text = v.GetValue<string>().Trim();
                    if (text.Length > 0 && !text.StartsWith('_') && !text.EndsWith('_') && !text.Contains("__"))
                    {
                        var digits = text.Replace("_", "");
                        if (long.TryParse(digits, NumberStyles.AllowLeadingSign, Inv, out var parsed))
                            return parsed;
                    }
                    throw new FormatException($"invalid literal for int() with base 10: {ReprString(v.GetValue<string>())}");
                case JsonValueKind.True:
                    return 1;
                case JsonValueKind.False:
                    return 0;
            }
        }
        throw new InvalidCastException("int() argument must be a string or a real number");
    }

    /// <summary>Python's str(value) for a value from json.loads (None, True, numbers as Python prints them).</summary>
    public static string Str(JsonNode? node) => node switch
    {
        null => "None",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => IsWhole(NumberText(v)) ? WholeText(NumberText(v)) : Repr(Number(v)),
            _ => "None",
        },
        _ => PyJson.Dump(node, null, false),
    };

    internal static string NumberText(JsonValue v)
    {
        if (v.TryGetValue<JsonElement>(out var element))
            return element.GetRawText();
        if (v.TryGetValue<long>(out var l))
            return l.ToString(Inv);
        if (v.TryGetValue<int>(out var i))
            return i.ToString(Inv);
        if (v.TryGetValue<double>(out var d))
            return Repr(d);
        return v.ToJsonString();
    }

    internal static double Number(JsonValue v)
    {
        var raw = NumberText(v);
        if (raw is "inf")
            return double.PositiveInfinity;
        if (raw is "-inf")
            return double.NegativeInfinity;
        if (raw is "nan")
            return double.NaN;
        return double.Parse(raw, NumberStyles.Float, Inv);
    }

    /// <summary>Whether JSON number text is a Python int (no ".", "e" or "E").</summary>
    internal static bool IsWhole(string raw) => raw.IndexOfAny(['.', 'e', 'E', 'n', 'i']) < 0;

    internal static string WholeText(string raw) => raw == "-0" ? "0" : raw;

    private static double ParseFloat(string text)
    {
        var t = text.Trim();
        var lower = t.ToLowerInvariant().TrimStart('+');
        var negative = lower.StartsWith('-');
        var body = negative ? lower[1..] : lower;
        if (body is "inf" or "infinity")
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        if (body == "nan")
            return double.NaN;
        if (t.Length > 0 && !t.Contains("__") && !t.StartsWith('_') && !t.EndsWith('_')
            && t.All(c => char.IsAsciiDigit(c) || c is '.' or 'e' or 'E' or '+' or '-' or '_')
            && double.TryParse(t.Replace("_", ""), NumberStyles.Float, Inv, out var value))
            return value;
        throw new FormatException($"could not convert string to float: {ReprString(text)}");
    }

    /// <summary>Python's repr(float): the shortest digits that round-trip, as Python lays them out
    /// ("1800000900.0", "0.001", "1e+16", "1.5e-07").</summary>
    public static string Repr(double x)
    {
        if (double.IsNaN(x))
            return "nan";
        if (double.IsInfinity(x))
            return x > 0 ? "inf" : "-inf";
        if (x == 0)
            return double.IsNegative(x) ? "-0.0" : "0.0";
        var r = x.ToString("R", Inv);
        var negative = r.StartsWith('-');
        if (negative)
            r = r[1..];
        var exponent = 0;
        var cut = r.IndexOfAny(['E', 'e']);
        if (cut >= 0)
        {
            exponent = int.Parse(r[(cut + 1)..], NumberStyles.AllowLeadingSign, Inv);
            r = r[..cut];
        }
        var dot = r.IndexOf('.');
        var digits = dot >= 0 ? r[..dot] + r[(dot + 1)..] : r;
        var intLength = dot >= 0 ? dot : r.Length;
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0')
        {
            lead++;
            intLength--;
        }
        digits = digits[lead..].TrimEnd('0');
        if (digits.Length == 0)
            digits = "0";
        var e = intLength - 1 + exponent;
        string body;
        if (e is >= -4 and < 16)
        {
            if (e >= digits.Length - 1)
                body = digits + new string('0', e - digits.Length + 1) + ".0";
            else if (e >= 0)
                body = digits[..(e + 1)] + "." + digits[(e + 1)..];
            else
                body = "0." + new string('0', -e - 1) + digits;
        }
        else
        {
            body = digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (e < 0 ? "-" : "+")
                   + Math.Abs(e).ToString("00", Inv);
        }
        return (negative ? "-" : "") + body;
    }

    /// <summary>Python's f"{x:.Nf}": fixed decimals, an exact half rounded to even (as Python does).</summary>
    public static string Fixed(double x, int digits)
    {
        if (double.IsNaN(x))
            return "nan";
        if (double.IsInfinity(x))
            return x > 0 ? "inf" : "-inf";
        var format = "F" + digits.ToString(Inv);
        var scale = Math.Pow(10, digits);
        var scaled = x * scale;
        // An exact tie only if the multiplication was exact and left exactly a half.
        if (Math.FusedMultiplyAdd(x, scale, -scaled) == 0 && Math.Abs(scaled - Math.Truncate(scaled)) == 0.5)
            return (Math.Round(scaled, MidpointRounding.ToEven) / scale).ToString(format, Inv);
        return x.ToString(format, Inv);
    }

    /// <summary>Python's f"{x:g}": six significant digits, no trailing zeros.</summary>
    public static string G(double x) => x.ToString("g6", Inv);

    /// <summary>Python's repr(str), for messages.</summary>
    public static string ReprString(string text)
    {
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        var result = new StringBuilder().Append(quote);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (c == quote)
                        result.Append('\\').Append(c);
                    else if (c < 0x20 || c == 0x7f)
                        result.Append("\\x").Append(((int)c).ToString("x2", Inv));
                    else
                        result.Append(c);
                    break;
            }
        }
        return result.Append(quote).ToString();
    }

    /// <summary>Python's str.splitlines(keepends=True), with every line boundary Python recognises.</summary>
    public static List<string> SplitLinesKeepEnds(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return lines;
    }

    /// <summary>Text as Python's print() wrote it on Windows: each "\n" as "\r\n".</summary>
    public static string Lines(string text) => text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
}
