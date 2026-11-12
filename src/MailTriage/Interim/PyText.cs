// Interim: belongs in background-tools-lib as part of PyCompat (with Py, PyJson and PyPath).
using System.Globalization;
using System.Text;

namespace MailTriage.Interim;

/// <summary>Python's str methods where .NET's differ: slices count code points (not UTF-16 units), strip() and
/// split() use Python's idea of whitespace, isdigit() is Unicode's Digit or Decimal, and strings sort by code
/// point.</summary>
public static class PyText
{
    /// <summary>text[:count], counting code points.</summary>
    public static string Head(string text, int count)
    {
        if (count <= 0)
            return "";
        var i = 0;
        var seen = 0;
        while (i < text.Length && seen < count)
        {
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            seen++;
        }
        return text[..i];
    }

    /// <summary>str.isspace() for one character: Unicode White_Space-ish as CPython defines it (bidi WS, B or S,
    /// or category Zs). Includes U+001C to U+001F, which .NET's char.IsWhiteSpace leaves out.</summary>
    public static bool IsSpace(char c) => c switch
    {
        '\t' or '\n' or '\v' or '\f' or '\r' or '\x1c' or '\x1d' or '\x1e' or '\x1f' or ' ' or '\x85' => true,
        _ => c > 0x7f && (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.SpaceSeparator
                              or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator),
    };

    /// <summary>str.strip() with no argument.</summary>
    public static string Strip(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
            start++;
        while (end > start && IsSpace(text[end - 1]))
            end--;
        return text[start..end];
    }

    /// <summary>str.split() with no argument: runs of whitespace separate, none empty.</summary>
    public static List<string> Split(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (IsSpace(c))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
                current.Append(c);
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    /// <summary>str.isdigit(): not empty, and every code point a Unicode Digit or Decimal (so "²" is a digit,
    /// although int("²") fails).</summary>
    public static bool IsDigit(string text)
    {
        if (text.Length == 0)
            return false;
        foreach (var rune in text.EnumerateRunes())
            if (CharUnicodeInfo.GetDigitValue(rune.ToString(), 0) < 0)
                return false;
        return true;
    }

    /// <summary>int(text) for text that is digits: Decimal digits of any script, optional sign and surrounding
    /// whitespace, as Python's int() accepts. Anything else is a FormatException (Python's ValueError).</summary>
    public static long Int(string text)
    {
        var t = Strip(text);
        var negative = false;
        if (t.StartsWith('+') || t.StartsWith('-'))
        {
            negative = t[0] == '-';
            t = t[1..];
        }
        if (t.Length == 0)
            throw new FormatException($"invalid literal for int() with base 10: {Py.ReprString(text)}");
        long value = 0;
        var previousUnderscore = true;
        foreach (var rune in t.EnumerateRunes())
        {
            if (rune.Value == '_' && !previousUnderscore)
            {
                previousUnderscore = true;
                continue;
            }
            var digit = CharUnicodeInfo.GetDecimalDigitValue(rune.ToString(), 0);
            if (digit < 0)
                throw new FormatException($"invalid literal for int() with base 10: {Py.ReprString(text)}");
            value = checked(value * 10 + digit);
            previousUnderscore = false;
        }
        if (previousUnderscore)
            throw new FormatException($"invalid literal for int() with base 10: {Py.ReprString(text)}");
        return negative ? -value : value;
    }

    /// <summary>Strings in Python's order: by code point. Ordinal order differs only where a surrogate pair
    /// (an emoji) meets a character from U+E000 to U+FFFF.</summary>
    public static readonly IComparer<string> Order = Comparer<string>.Create(CompareCodePoints);

    public static int CompareCodePoints(string? a, string? b)
    {
        if (ReferenceEquals(a, b))
            return 0;
        if (a is null)
            return -1;
        if (b is null)
            return 1;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x == y)
                continue;
            // A surrogate stands for a code point above U+FFFF, so it sorts after every other BMP character.
            var sx = char.IsSurrogate(x);
            var sy = char.IsSurrogate(y);
            if (sx != sy)
                return sx ? 1 : -1;
            return x.CompareTo(y);
        }
        return a.Length.CompareTo(b.Length);
    }
}
