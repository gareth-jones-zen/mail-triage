using System.Text.Json.Nodes;
using MailTriage.Interim;

namespace MailTriage;

/// <summary>The few Graph calls triage makes. Reads go through a ReadOnlyGraph; writes only through the write door.</summary>
public sealed class Mailbox
{
    public static readonly string[] Scopes = ["Mail.ReadWrite"];
    public static readonly string[] ReadOnlyScopes = ["Mail.Read"];   // a dry run only reads, so it works before Mail.ReadWrite is granted
    public const string Fields = "id,internetMessageId,subject,from,receivedDateTime,isRead,bodyPreview,webLink";

    public Mailbox(IGraph graph, IMailWriter writer, bool dryRun = false)
    {
        Graph = graph as ReadOnlyGraph ?? new ReadOnlyGraph(graph);
        Writer = writer;
        ReadScopes = dryRun ? ReadOnlyScopes : Scopes;
    }

    /// <summary>For reads only.</summary>
    public IGraph Graph { get; }

    public IMailWriter Writer { get; }

    public IReadOnlyList<string> ReadScopes { get; }

    /// <summary>By display name, searching the whole tree, as triage folders are often nested.</summary>
    public string FolderId(string name)
    {
        var waiting = new List<string> { "/me/mailFolders?$top=100&includeHiddenFolders=false" };
        while (waiting.Count > 0)
        {
            var url = waiting[^1];
            waiting.RemoveAt(waiting.Count - 1);
            foreach (var folder in Graph.Pages(url, ReadScopes))
            {
                if (folder.Req("displayName").ToLowerInvariant() == name.ToLowerInvariant())
                    return folder.Req("id");
                if (folder.Truthy("childFolderCount"))
                    waiting.Add($"/me/mailFolders/{folder.Req("id")}/childFolders?$top=100");
            }
        }
        throw new LookupException($"No mail folder called {Py.ReprString(name)}");
    }

    /// <summary>A folder by its path from the top, such as "Inbox/Projects" (/ or \ between).</summary>
    public string FolderPath(string path)
    {
        var parts = PyRegex.Get(@"[/\\]").Split(path).Where(p => p.Length > 0).ToList();
        var url = "/me/mailFolders?$top=100";
        string? found = null;
        foreach (var part in parts)
        {
            found = Graph.Pages(url, ReadScopes).Where(f => f.Req("displayName").ToLowerInvariant() == part.ToLowerInvariant())
                .Select(f => f.Req("id")).FirstOrDefault();
            if (found is null)
                throw new LookupException($"No mail folder {Py.ReprString(path)} (no {Py.ReprString(part)})");
            url = $"/me/mailFolders/{found}/childFolders?$top=100";
        }
        return found!;
    }

    public void File(string messageId, string folderId) => Writer.File(messageId, folderId);

    public List<JsonObject> Messages(string folderId, string? since)
    {
        var query = new List<(string, string)>();
        if (since is not null)
            query.Add(("$filter", $"receivedDateTime ge {since}T00:00:00Z"));
        query.Add(("$select", Fields));
        query.Add(("$orderby", "receivedDateTime desc"));
        query.Add(("$top", "50"));
        return Graph.Pages($"/me/mailFolders/{folderId}/messages?{Url.Encode(query)}", ReadScopes).ToList();
    }

    public string Body(string messageId) =>
        Graph.Request($"/me/messages/{messageId}?$select=body", scopes: ReadScopes).Get("body").Req("content");

    public void Delete(string messageId) => Writer.Delete(messageId);
}

/// <summary>Not found (Python's LookupError): a folder, a queue number, an id.</summary>
public sealed class LookupException(string message) : Exception(message);

/// <summary>urllib.parse.urlencode, as Python quotes: spaces as "+", and only letters, digits and _.-~ left as they are.</summary>
public static class Url
{
    public static string Encode(IEnumerable<(string Key, string Value)> pairs) =>
        string.Join("&", pairs.Select(p => QuotePlus(p.Key) + "=" + QuotePlus(p.Value)));

    public static string QuotePlus(string text)
    {
        var b = new System.Text.StringBuilder();
        foreach (var by in System.Text.Encoding.UTF8.GetBytes(text))
        {
            var c = (char)by;
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~')
                b.Append(c);
            else if (c == ' ')
                b.Append('+');
            else
                b.Append('%').Append(by.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return b.ToString();
    }
}
