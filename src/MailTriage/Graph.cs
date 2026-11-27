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

    /// <summary>A write that must never be applied twice, whatever its method (sending an invitation): sent once, and never
    /// again after a 5xx, a timeout or a dropped connection that may have reached Microsoft (only a 429, or a request that
    /// never connected, is retried). A refusal (4xx) throws GraphRequestException; a 5xx or an answer lost after sending
    /// throws GraphDeliveryUnknownException. Request sends a POST or PATCH the same way; this says so at the call. A fake
    /// Graph's Request stands in.</summary>
    JsonObject RequestOnce(string url, string method, JsonNode? body, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        Request(url, method, body, scopes, headers);

    /// <summary>Whether a token for these scopes can be had silently (1.36.0: send-draft's Permission line): true, false
    /// (a sign-in would be needed), or null when it can't be told (a fake, or the check itself failed). Never opens a
    /// browser and calls nothing on Graph.</summary>
    bool? Granted(IReadOnlyList<string> scopes) => null;
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

    public JsonObject RequestOnce(string url, string method, JsonNode? body, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        throw new WriteRefusedException($"{method} {url}");

    public bool? Granted(IReadOnlyList<string> scopes) => inner.Granted(scopes);
}

/// <summary>The real Graph, through background-tools-lib's M365Graph (the shared sign-in, timeouts, retries, IPv4
/// first). The calls go through a small transport so the mapping is tested without a network.
/// <para>Retries follow M365Graph's policy by method: a GET or DELETE is retried after a 5xx or a dropped connection; a
/// POST or PATCH is sent once, and an answer lost after sending (a 5xx, a timeout, a reset) throws
/// GraphDeliveryUnknownException, never resent. The one POST that only reads (getSchedule) is retried as a GET is.</para></summary>
public sealed class M365GraphAdapter : IGraph
{
    private readonly Func<string, HttpMethod, JsonNode?, IReadOnlyList<string>?, IReadOnlyDictionary<string, string>?, GraphRetryPolicy?, Task<JsonObject>> request;
    private readonly Func<string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string>?, IAsyncEnumerable<JsonNode?>> pages;

    public M365GraphAdapter(GraphClient client)
        : this((url, method, body, scopes, headers, policy) => client.RequestAsync(url, method, body, scopes, headers, policy: policy),
               (url, scopes, headers) => client.PagesAsync(url, scopes, headers))
    {
        Client = client;
    }

    internal M365GraphAdapter(
        Func<string, HttpMethod, JsonNode?, IReadOnlyList<string>?, IReadOnlyDictionary<string, string>?, GraphRetryPolicy?, Task<JsonObject>> request,
        Func<string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string>?, IAsyncEnumerable<JsonNode?>> pages)
    {
        this.request = request;
        this.pages = pages;
    }

    public GraphClient? Client { get; }

    /// <summary>The policy a call gets: retried as a read for getSchedule (a POST that only reads); otherwise M365Graph's
    /// default for the method (null).</summary>
    internal static GraphRetryPolicy? PolicyFor(string url, string method) =>
        method == "POST" && url == ReadOnlyGraph.SchedulePath ? GraphRetryPolicy.Read : null;

    public JsonObject Request(string url, string method = "GET", JsonNode? body = null, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        request(url, new HttpMethod(method), body, scopes, headers, PolicyFor(url, method)).GetAwaiter().GetResult();

    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        foreach (var item in pages(url, scopes, headers).ToBlockingEnumerable())
            if (item is JsonObject o)
                yield return o;
    }

    /// <summary>With M365Graph's Write policy whatever the method: only a 429, or a request that never connected, is sent
    /// again; a 5xx or an answer lost after sending throws GraphDeliveryUnknownException.</summary>
    public JsonObject RequestOnce(string url, string method, JsonNode? body, IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        request(url, new HttpMethod(method), body, scopes, headers, GraphRetryPolicy.Write).GetAwaiter().GetResult();

    /// <summary>A silent token request (interactive false): never a browser; nothing is sent to Graph with it.</summary>
    public bool? Granted(IReadOnlyList<string> scopes)
    {
        if (Client is null)
            return null;
        try
        {
            Client.TokenAsync(scopes, false).GetAwaiter().GetResult();
            return true;
        }
        catch (SignInNeededException)
        {
            return false;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
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
