using System.Text.Json.Nodes;
using M365Graph;

namespace MailTriage;

/// <summary>The Graph calls the tool makes: m365_graph's request() and pages(). Synchronous, as the Python was.</summary>
public interface IGraph
{
    /// <summary>One request; a relative path or an absolute nextLink. Returns the JSON object ({} for an empty reply).</summary>
    JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null);

    /// <summary>Every item across @odata.nextLink pages, the same headers on every page. Lazy, as Python's generator.</summary>
    IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null);
}

/// <summary>A write was asked of a Graph that only reads: a bug, never a decision. Nothing was sent.</summary>
public sealed class WriteRefusedException(string what) : Exception($"Refused a write ({what}): this run may only read.");

/// <summary>The second fence, under the write door: every read goes through this, which refuses any method but GET,
/// and the one POST that only reads (getSchedule). The mail and calendar code reads only through it.</summary>
public sealed class ReadOnlyGraph(IGraph inner) : IGraph
{
    public const string SchedulePath = "/me/calendar/getSchedule";

    public static bool Allowed(string url, string method) =>
        method == "GET" || (method == "POST" && url == SchedulePath);

    public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        if (!Allowed(url, method))
            throw new WriteRefusedException($"{method} {url}");
        return inner.Request(url, method, body, scopes, headers);
    }

    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null) =>
        inner.Pages(url, scopes, headers);
}

/// <summary>The real Graph, through background-tools-lib's M365Graph (the shared sign-in, timeouts, retries, IPv4
/// first). The calls go through a small transport so the mapping is tested without a network.</summary>
public sealed class M365GraphAdapter : IGraph
{
    private readonly Func<string, HttpMethod, JsonNode?, IReadOnlyList<string>?, IReadOnlyDictionary<string, string>?, Task<JsonObject>> request;
    private readonly Func<string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string>?, IAsyncEnumerable<JsonNode?>> pages;

    public M365GraphAdapter(GraphClient client)
        : this((url, method, body, scopes, headers) => client.RequestAsync(url, method, body, scopes, headers),
               (url, scopes, headers) => client.PagesAsync(url, scopes, headers))
    {
        Client = client;
    }

    internal M365GraphAdapter(
        Func<string, HttpMethod, JsonNode?, IReadOnlyList<string>?, IReadOnlyDictionary<string, string>?, Task<JsonObject>> request,
        Func<string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string>?, IAsyncEnumerable<JsonNode?>> pages)
    {
        this.request = request;
        this.pages = pages;
    }

    public GraphClient? Client { get; }

    public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        request(url, new HttpMethod(method), body, scopes, headers).GetAwaiter().GetResult();

    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        foreach (var item in pages(url, scopes, headers).ToBlockingEnumerable())
            if (item is JsonObject o)
                yield return o;
    }

    /// <summary>A client as cli.py's client(): the settings' cache, tenant and login hint, never a browser when
    /// interactive is false.</summary>
    public static GraphClient Create(Settings settings, bool? interactive, Action<string>? log = null) =>
        new(new GraphClientOptions
        {
            CachePath = settings.MsalCache ?? M365Graph.Graph.DefaultCachePath,
            Tenant = settings.Tenant,
            LoginHint = settings.LoginHint,
            Scopes = Mailbox.Scopes,
            Interactive = interactive,
            SignInHint = "At a console, run: mail-triage sign-in",
            Log = log,
        });
}
