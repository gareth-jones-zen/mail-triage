// Interim: belongs in background-tools-lib as part of PyCompat (the regular expression dialect guard).
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace MailTriage.Interim;

/// <summary>A pattern Python's re would accept but this port won't run, because .NET would read it differently
/// (or Python would refuse it, so a rollback to the Python version would break).</summary>
public sealed class PyRegexUnsupportedException(string pattern, string why)
    : Exception($"{Py.ReprString(pattern)} uses {why}, which Python and .NET read differently; it isn't supported");

/// <summary>Python's re, on .NET's Regex. Settings hold user-written patterns that the Python version also runs,
/// so a pattern must mean the same in both. Each pattern is translated from Python's dialect:
/// - \w, \W and \b, \B use Python's word characters (letters, and Nd, Nl and No numbers, and "_"), not .NET's
///   (which add combining marks and other connector punctuation, and leave out Nl and No);
/// - \s and \S add U+001C to U+001F, as Python's do; \Z becomes \z;
/// - global inline flags at the start, (?i) (?m) (?s), become options.
/// Anything the two read differently is refused (the guard): named groups, \p{...}, \Z's .NET meaning, {,n},
/// possessive quantifiers, character-class subtraction, mid-pattern global flags, (?x), (?a), (?L), (?u),
/// backreferences, and escapes one side lacks. Interpreted, never compiled: patterns are user input, and
/// Compiled is ignored under Native AOT. Each match has a time limit; Python's has none.</summary>
public sealed class PyRegex
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private const string Word = @"\p{L}\p{Nd}\p{Nl}\p{No}_";
    private const string WordClass = "[" + Word + "]";
    private const string NotWordClass = "[^" + Word + "]";
    private const string Space = @"\s\x1C-\x1F";
    private const string Boundary = "(?:(?<=" + WordClass + ")(?!" + WordClass + ")|(?<!" + WordClass + ")(?=" + WordClass + "))";
    private const string NotBoundary = "(?:(?<=" + WordClass + ")(?=" + WordClass + ")|(?<!" + WordClass + ")(?!" + WordClass + "))";

    private static readonly ConcurrentDictionary<(string, bool), PyRegex> Cache = new();

    private readonly Regex search;
    private readonly Regex anchored;
    private readonly Regex full;

    private PyRegex(string pattern, bool ignoreCase)
    {
        Pattern = pattern;
        var (translated, options) = Translate(pattern);
        if (ignoreCase)
            options |= RegexOptions.IgnoreCase;
        options |= RegexOptions.CultureInvariant;
        Translated = translated;
        try
        {
            search = new Regex(translated, options, Timeout);
            anchored = new Regex(@"\G(?:" + translated + ")", options, Timeout);
            full = new Regex(@"\G(?:" + translated + @")\z", options, Timeout);
        }
        catch (ArgumentException error)
        {
            throw new PyRegexUnsupportedException(pattern, $"something .NET can't read ({error.Message})");
        }
    }

    public string Pattern { get; }

    /// <summary>The .NET pattern it became.</summary>
    public string Translated { get; }

    /// <summary>The pattern, translated and checked once. Throws PyRegexUnsupportedException.</summary>
    public static PyRegex Get(string pattern, bool ignoreCase = false) =>
        Cache.GetOrAdd((pattern, ignoreCase), key => new PyRegex(key.Item1, key.Item2));

    /// <summary>re.search: the first match anywhere, or null.</summary>
    public Match? Search(string text)
    {
        var m = search.Match(text);
        return m.Success ? m : null;
    }

    public bool IsMatch(string text) => search.IsMatch(text);

    /// <summary>re.match: a match starting at the beginning, or null.</summary>
    public Match? MatchStart(string text)
    {
        var m = anchored.Match(text, 0);
        return m.Success ? m : null;
    }

    /// <summary>re.fullmatch.</summary>
    public bool FullMatch(string text) => full.Match(text, 0).Success;

    /// <summary>re.findall for a pattern with no groups: every match's text.</summary>
    public List<string> FindAll(string text) => search.Matches(text).Select(m => m.Value).ToList();

    /// <summary>re.sub with a literal replacement (no group references).</summary>
    public string Sub(string replacement, string text) => search.Replace(text, replacement.Replace("$", "$$"));

    /// <summary>re.split with no groups.</summary>
    public string[] Split(string text) => search.Split(text);

    // Shortcuts for the calls the Python makes.
    public static Match? Search(string pattern, string text, bool ignoreCase = false) => Get(pattern, ignoreCase).Search(text);

    public static bool IsMatch(string pattern, string text, bool ignoreCase = false) => Get(pattern, ignoreCase).IsMatch(text);

    /// <summary>Checks a pattern as config.py's re.compile did, and the dialect guard. Null if fine, else why not.</summary>
    public static string? Problem(string pattern)
    {
        try
        {
            Get(pattern, true);
            Get(pattern, false);
            return null;
        }
        catch (PyRegexUnsupportedException error)
        {
            return error.Message;
        }
    }

    /// <summary>re.escape, as Python 3.7+ does it: only the special characters, and whitespace.</summary>
    public static string Escape(string text)
    {
        const string special = "()[]{}?*+-|^$\\.&~# \t\n\r\v\f";
        var b = new StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            if (special.Contains(c))
                b.Append('\\');
            b.Append(c);
        }
        return b.ToString();
    }

    /// <summary>The Python pattern as .NET reads it, and the options its leading flags give.</summary>
    public static (string Pattern, RegexOptions Options) Translate(string pattern)
    {
        var options = RegexOptions.None;
        var b = new StringBuilder(pattern.Length * 2);
        var i = 0;
        var atStart = true;
        void Refuse(string why) => throw new PyRegexUnsupportedException(pattern, why);

        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '(' && i + 1 < pattern.Length && pattern[i + 1] == '?')
            {
                var rest = pattern[(i + 2)..];
                if (rest.StartsWith(':') || rest.StartsWith('=') || rest.StartsWith('!'))
                {
                    b.Append(pattern, i, 3);
                    i += 3;
                }
                else if (rest.StartsWith("<=") || rest.StartsWith("<!"))
                {
                    b.Append(pattern, i, 4);
                    i += 4;
                }
                else
                {
                    // Flags: (?ims) at the start, or a scoped (?i:...) / (?-i:...).
                    var j = i + 2;
                    while (j < pattern.Length && (char.IsAsciiLetter(pattern[j]) || pattern[j] == '-'))
                        j++;
                    var flags = pattern[(i + 2)..j];
                    var close = j < pattern.Length ? pattern[j] : '\0';
                    if (flags.Length == 0)
                        Refuse(rest.StartsWith('P') || rest.StartsWith('<') || rest.StartsWith('\'') ? "a named group"
                               : rest.StartsWith('#') ? "a comment group" : rest.StartsWith('(') ? "a conditional group"
                               : rest.StartsWith('>') ? "an atomic group" : "a group .NET reads differently");
                    if (flags.StartsWith('P'))
                        Refuse("a named group");
                    foreach (var f in flags)
                        if (f is not ('i' or 'm' or 's' or '-'))
                            Refuse($"the inline flag {f}");
                    if (close == ')')
                    {
                        if (!atStart || flags.Contains('-'))
                            Refuse("inline flags in the middle of the pattern");
                        foreach (var f in flags)
                            options |= f switch { 'i' => RegexOptions.IgnoreCase, 'm' => RegexOptions.Multiline, _ => RegexOptions.Singleline };
                        i = j + 1;
                        continue;   // still at the start: Python allows several leading flag groups
                    }
                    if (close != ':')
                        Refuse("a group .NET reads differently");
                    b.Append("(?").Append(flags).Append(':');
                    i = j + 1;
                }
                atStart = false;
                continue;
            }
            atStart = false;
            switch (c)
            {
                case '\\':
                    if (i + 1 >= pattern.Length)
                        Refuse("a trailing backslash");
                    b.Append(Escape(pattern, pattern[i + 1], false, Refuse));
                    i += 2;
                    if (pattern[i - 1] is 'x')
                        i = CopyHex(pattern, i, 2, b, Refuse);
                    else if (pattern[i - 1] is 'u')
                        i = CopyHex(pattern, i, 4, b, Refuse);
                    continue;
                case '[':
                    i = Class(pattern, i, b, Refuse);
                    continue;
                case '{':
                    {
                        var close = pattern.IndexOf('}', i);
                        if (close > i)
                        {
                            var inside = pattern[(i + 1)..close];
                            if (inside.StartsWith(',') && inside.Length > 1 && inside[1..].All(char.IsAsciiDigit))
                                Refuse("{,n} (a quantifier in Python, text in .NET)");
                            var isQuantifier = inside.Length > 0 && inside.Split(',').Length <= 2
                                               && inside.Split(',').All(part => part.All(char.IsAsciiDigit)) && inside[0] != ',';
                            if (isQuantifier && close + 1 < pattern.Length && pattern[close + 1] == '+')
                                Refuse("a possessive quantifier");
                        }
                        b.Append(c);
                        i++;
                        continue;
                    }
                case '*' or '+' or '?':
                    b.Append(c);
                    i++;
                    if (i < pattern.Length && pattern[i] == '+')
                        Refuse("a possessive quantifier");
                    continue;
                default:
                    b.Append(c);
                    i++;
                    continue;
            }
        }
        return (b.ToString(), options);
    }

    private static int CopyHex(string pattern, int i, int count, StringBuilder b, Action<string> refuse)
    {
        if (i + count > pattern.Length || !pattern.Substring(i, count).All(char.IsAsciiHexDigit))
            refuse("an incomplete hex escape");
        b.Append(pattern, i, count);
        return i + count;
    }

    /// <summary>One escape, outside or inside a class. Returns what to write (for \x and \u, without the digits).</summary>
    private static string Escape(string pattern, char c, bool inClass, Action<string> refuse)
    {
        switch (c)
        {
            case 'w': return inClass ? Word : WordClass;
            case 'W':
                if (inClass)
                    refuse(@"\W inside a character class");
                return NotWordClass;
            case 's': return inClass ? Space : "[" + Space + "]";
            case 'S':
                if (inClass)
                    refuse(@"\S inside a character class");
                return "[^" + Space + "]";
            case 'd' or 'D': return "\\" + c;
            case 'b': return inClass ? @"\b" : Boundary;
            case 'B':
                if (inClass)
                    refuse(@"\B inside a character class");
                return NotBoundary;
            case 'A':
                if (inClass)
                    refuse(@"\A inside a character class");
                return @"\A";
            case 'Z':
                if (inClass)
                    refuse(@"\Z inside a character class");
                return @"\z";
            case 'n' or 'r' or 't' or 'f' or 'v' or 'a': return "\\" + c;
            case 'x' or 'u': return "\\" + c;
        }
        if (char.IsAsciiDigit(c))
            refuse("a backreference or octal escape");
        if (char.IsAsciiLetter(c))
            refuse($@"\{c}");
        // Any other character escaped stands for itself in both. .NET refuses an escaped word character (such as
        // "_" or "é"), so those are written plain; anything else keeps its backslash (in a class, "\-" must).
        return IsNetWordChar(c) ? c.ToString() : "\\" + c;
    }

    private static bool IsNetWordChar(char c) => c == '_' || char.IsLetterOrDigit(c)
        || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.ConnectorPunctuation;

    /// <summary>A character class from pattern[i] == '['. Returns the index after its ']'.</summary>
    private static int Class(string pattern, int i, StringBuilder b, Action<string> refuse)
    {
        b.Append('[');
        i++;
        if (i < pattern.Length && pattern[i] == '^')
        {
            b.Append('^');
            i++;
        }
        var first = true;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == ']')
            {
                if (!first)
                {
                    b.Append(']');
                    return i + 1;
                }
                b.Append(@"\]");   // a "]" first in the class is a literal one, in Python
                i++;
                first = false;
                continue;
            }
            first = false;
            if (c == '\\')
            {
                if (i + 1 >= pattern.Length)
                    refuse("a trailing backslash");
                var e = pattern[i + 1];
                b.Append(Escape(pattern, e, true, refuse));
                i += 2;
                if (e is 'x')
                    i = CopyHex(pattern, i, 2, b, refuse);
                else if (e is 'u')
                    i = CopyHex(pattern, i, 4, b, refuse);
                continue;
            }
            if (c == '[' && i + 1 < pattern.Length && pattern[i + 1] is ':' or '=' or '.')
                refuse("a POSIX-style class");
            if (c == '-' && i + 1 < pattern.Length && pattern[i + 1] == '[')
                refuse("character-class subtraction");
            if (c == '[')
            {
                b.Append(@"\[");
                i++;
                continue;
            }
            b.Append(c);
            i++;
        }
        refuse("an unterminated character class");
        return i;
    }
}
