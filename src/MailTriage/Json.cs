using System.Text.Json;
using System.Text.Json.Nodes;
using MailTriage.Interim;

namespace MailTriage;

/// <summary>Python dict access on Graph's JSON and the state files, so the port reads like the original:
/// .get(key) is null when missing or null; .get(key, default) keeps a JSON null (which Python formats as "None").</summary>
internal static class J
{
    /// <summary>d.get(key).</summary>
    public static JsonNode? Get(this JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var value) ? value : null;

    /// <summary>key in d.</summary>
    public static bool Has(this JsonNode? node, string key) => node is JsonObject o && o.ContainsKey(key);

    /// <summary>d.get(key) as a string, or null (missing, null, or not a string).</summary>
    public static string? Str(this JsonNode? node, string key) =>
        node.Get(key) is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>d.get(key) or "": Python's (d.get(key) or "") for a string field.</summary>
    public static string StrOr(this JsonNode? node, string key) => node.Str(key) is { Length: > 0 } s ? s : "";

    /// <summary>bool(d.get(key)).</summary>
    public static bool Truthy(this JsonNode? node, string key) => Py.Truthy(node.Get(key));

    /// <summary>d[key] as a string, failing as Python would if it's missing (KeyError) or not text.</summary>
    public static string Req(this JsonNode? node, string key)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var value))
            throw new KeyNotFoundException(Py.ReprString(key));
        return value is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>()
            : throw new InvalidCastException($"{key} isn't text");
    }

    /// <summary>d.get(key, "") formatted by an f-string: the text, "None" for a JSON null, "" when missing.</summary>
    public static string Fmt(this JsonNode? node, string key, string missing = "") =>
        node.Has(key) ? Py.Str(node.Get(key)) : missing;

    /// <summary>The node itself for storing elsewhere (a JsonNode has one parent).</summary>
    public static JsonNode? Copy(this JsonNode? node) => node?.DeepClone();

    /// <summary>d.get(key, default) as a node to store: a copy of the value if the key is there (even null).</summary>
    public static JsonNode? GetCopy(this JsonNode? node, string key, JsonNode? missing = null) =>
        node.Has(key) ? node.Get(key).Copy() : missing;

    public static JsonArray Array(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var item in items)
            a.Add((JsonNode?)JsonValue.Create(item));
        return a;
    }

    public static IEnumerable<JsonObject> Objects(this JsonNode? node) =>
        node is JsonArray a ? a.OfType<JsonObject>() : [];

    public static List<string> Strings(this JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : Py.Str(x)).ToList() : [];
}
