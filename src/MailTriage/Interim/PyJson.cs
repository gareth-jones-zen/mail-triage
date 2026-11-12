// Interim: copied from ai-keep-awake (src/AiKeepAwake/PyJson.cs). Belongs in background-tools-lib as PyCompat.
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MailTriage.Interim;

/// <summary>JSON read and written exactly as Python's json module did, so the files this writes (the leases,
/// the state file, Claude Code's settings.json) come out byte for byte as the Python version wrote them.
/// System.Text.Json's own writer escapes differently and drops the ".0" from whole floats.</summary>
public static class PyJson
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 512 };

    /// <summary>json.loads: a duplicate key keeps its first position and takes the last value, as a Python dict
    /// does. Throws JsonException for anything that isn't JSON.</summary>
    public static JsonNode? Parse(string text)
    {
        if (text.Length > 0 && text[0] == '\ufeff')
            throw new JsonException("Unexpected UTF-8 BOM (decode using utf-8-sig)");
        using var document = JsonDocument.Parse(text, Options);
        return Build(document.RootElement);
    }

    private static JsonNode? Build(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                    obj[property.Name] = Build(property.Value);
                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                    array.Add(Build(item));
                return array;
            case JsonValueKind.String:
                return JsonValue.Create(element.GetString()!);
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            case JsonValueKind.Number:
                return JsonValue.Create(element.Clone());
            default:
                return null;
        }
    }

    /// <summary>json.dumps: indent null gives ", " and ": " on one line; an indent gives "," and ": " with
    /// newlines. ensureAscii escapes everything outside printable ASCII as \uXXXX (Python's default).</summary>
    public static string Dump(JsonNode? node, int? indent = null, bool ensureAscii = true)
    {
        var builder = new StringBuilder();
        Write(builder, node, indent, ensureAscii, 0);
        return builder.ToString();
    }

    private static void Write(StringBuilder b, JsonNode? node, int? indent, bool ascii, int level)
    {
        switch (node)
        {
            case null:
                b.Append("null");
                return;
            case JsonObject obj:
                if (obj.Count == 0)
                {
                    b.Append("{}");
                    return;
                }
                b.Append('{');
                var first = true;
                foreach (var (key, value) in obj)
                {
                    Separator(b, indent, level + 1, first);
                    first = false;
                    Quote(b, key, ascii);
                    b.Append(": ");
                    Write(b, value, indent, ascii, level + 1);
                }
                Close(b, indent, level);
                b.Append('}');
                return;
            case JsonArray array:
                if (array.Count == 0)
                {
                    b.Append("[]");
                    return;
                }
                b.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    Separator(b, indent, level + 1, i == 0);
                    Write(b, array[i], indent, ascii, level + 1);
                }
                Close(b, indent, level);
                b.Append(']');
                return;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        Quote(b, value.GetValue<string>(), ascii);
                        return;
                    case JsonValueKind.True:
                        b.Append("true");
                        return;
                    case JsonValueKind.False:
                        b.Append("false");
                        return;
                    case JsonValueKind.Number:
                        b.Append(NumberText(value));
                        return;
                    default:
                        b.Append("null");
                        return;
                }
        }
    }

    private static void Separator(StringBuilder b, int? indent, int level, bool first)
    {
        if (indent is { } size)
        {
            if (!first)
                b.Append(',');
            b.Append('\n').Append(' ', size * level);
        }
        else if (!first)
            b.Append(", ");
    }

    private static void Close(StringBuilder b, int? indent, int level)
    {
        if (indent is { } size)
            b.Append('\n').Append(' ', size * level);
    }

    /// <summary>A number as Python writes it: an int's digits as they were, a float by repr(), and
    /// Infinity / NaN for the values JSON has no word for.</summary>
    private static string NumberText(JsonValue value)
    {
        var raw = Py.NumberText(value);
        if (Py.IsWhole(raw))
            return Py.WholeText(raw);
        var number = Py.Number(value);
        if (double.IsNaN(number))
            return "NaN";
        if (double.IsInfinity(number))
            return number > 0 ? "Infinity" : "-Infinity";
        return Py.Repr(number);
    }

    /// <summary>A JSON number for a Python float: held as its text, so it's written as Python writes it
    /// ("1800000900.0", never "1800000900").</summary>
    public static JsonNode Float(double value) => Raw(Py.Repr(value));

    /// <summary>A JSON number for a Python int.</summary>
    public static JsonNode Int(long value) => Raw(value.ToString(CultureInfo.InvariantCulture));

    private static JsonNode Raw(string text)
    {
        using var document = JsonDocument.Parse(text);
        return JsonValue.Create(document.RootElement.Clone())!;
    }

    /// <summary>A file's text as Python's open(encoding="utf-8") read it: invalid UTF-8 is an error, and a byte
    /// order mark is kept (so json.loads refuses it).</summary>
    public static string ReadStrict(string path) => new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));

    private static void Quote(StringBuilder b, string text, bool ascii)
    {
        b.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                case '\b': b.Append("\\b"); break;
                case '\f': b.Append("\\f"); break;
                default:
                    if (c < 0x20 || (ascii && c > 0x7e))
                        b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        b.Append(c);
                    break;
            }
        }
        b.Append('"');
    }

    /// <summary>Python's == on two values from json.loads: objects ignore key order, 1 == 1.0 == true.</summary>
    public static bool Equal(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a is JsonObject oa)
        {
            if (b is not JsonObject ob || oa.Count != ob.Count)
                return false;
            foreach (var (key, value) in oa)
                if (!ob.TryGetPropertyValue(key, out var other) || !Equal(value, other))
                    return false;
            return true;
        }
        if (a is JsonArray aa)
        {
            if (b is not JsonArray ab || aa.Count != ab.Count)
                return false;
            for (var i = 0; i < aa.Count; i++)
                if (!Equal(aa[i], ab[i]))
                    return false;
            return true;
        }
        if (a is not JsonValue va || b is not JsonValue vb)
            return false;
        var ka = va.GetValueKind();
        var kb = vb.GetValueKind();
        if (ka == JsonValueKind.String || kb == JsonValueKind.String)
            return ka == kb && va.GetValue<string>() == vb.GetValue<string>();
        // Numbers and booleans compare as numbers (True == 1).
        if (Whole(va) is { } ia && Whole(vb) is { } ib)
            return ia == ib;
        return NumberOf(va) == NumberOf(vb);
    }

    private static BigInteger? Whole(JsonValue v) => v.GetValueKind() switch
    {
        JsonValueKind.True => BigInteger.One,
        JsonValueKind.False => BigInteger.Zero,
        JsonValueKind.Number when Py.IsWhole(Py.NumberText(v)) => BigInteger.Parse(Py.NumberText(v), CultureInfo.InvariantCulture),
        _ => null,
    };

    private static double NumberOf(JsonValue v) => v.GetValueKind() switch
    {
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => Py.Number(v),
    };
}
