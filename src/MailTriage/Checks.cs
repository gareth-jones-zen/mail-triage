using System.Globalization;
using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>Checks: an email that may need you, or may not (checks.py). A rule with action = "check" runs one on
/// the email: (nothing to do?, why). True deletes it as a delete rule would; false queues it with the why.
///
/// flow-failures: Power Automate's "flow(s) have failed" digest only matters if the flow is still failing. Each flow
/// needs a [[flow_check]] saying what a good run leaves behind; a flow without one is raised.
///
/// github-runs: a GitHub Actions "Run failed" email only matters if the workflow is still failing on that branch.
/// It asks the GitHub CLI (gh, signed in as you) for the branch's latest finished run of that workflow.
///
/// sharepoint-access (1.12.0): a SharePoint "X wants to access 'Site'" request only matters if no other owner has dealt
/// with it. It finds the request in the site's hidden Access Requests list (by who asked, and when: the email's own
/// AccessRequestID isn't the list item's) and passes when it's approved.</summary>
public static class Checks
{
    public delegate (bool Fine, string Why) Check(JsonObject message, string bodyHtml, Settings settings);

    /// <summary>name -> (needs the body?, the check).</summary>
    public static readonly IReadOnlyDictionary<string, (bool NeedsBody, Check Run)> All =
        new Dictionary<string, (bool, Check)>
        {
            ["flow-failures"] = (true, CheckFlowFailures),
            ["github-runs"] = (false, CheckGitHubRun),
            ["sharepoint-access"] = (true, CheckSharePointAccess),
        };

    /// <summary>Graph, for the checks that read it (sharepoint-access): the run's mailbox sets it. Replaced in tests.</summary>
    public static IGraph? Graph;

    /// <summary>Reading a site's lists needs Sites.Read.All, or Files.ReadWrite.All, which the tenant-wide consent has.</summary>
    public static readonly string[] SiteScopes = ["Files.ReadWrite.All"];

    /// <summary>How far the request's time in the list may be from the email's: they're seconds apart.</summary>
    public static readonly TimeSpan AccessRequestWindow = TimeSpan.FromMinutes(15);

    private static readonly System.Text.RegularExpressions.Regex RequestedResource = new(
        @"(?is)Requested resource:\s*(?:<[^>]*>\s*)*(?<url>https://[^<\s""]+)");

    private static readonly System.Text.RegularExpressions.Regex RequestedBy = new(
        @"(?is)Requested by:\s*(?:<[^>]*>\s*)*(?<name>[^(<]+?)\s*\((?<email>[^)\s<]+@[^)\s<]+)\)");

    /// <summary>(the requested resource's URL, the requester's name, their email) from the request email's body, or null.</summary>
    public static (string Url, string Name, string Email)? ParseAccessRequest(string bodyHtml)
    {
        var resource = RequestedResource.Match(bodyHtml);
        var by = RequestedBy.Match(bodyHtml);
        if (!resource.Success || !by.Success)
            return null;
        return (System.Net.WebUtility.HtmlDecode(resource.Groups["url"].Value), System.Net.WebUtility.HtmlDecode(by.Groups["name"].Value).Trim(),
                by.Groups["email"].Value.Trim());
    }

    /// <summary>The Graph path of the site a SharePoint URL is in: "/sites/host:/sites/Name", or the root site's.</summary>
    public static string? SitePath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
            return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].ToLowerInvariant() is "sites" or "teams" or "personal"
            ? $"/sites/{uri.Host}:/{parts[0]}/{parts[1]}"
            : $"/sites/{uri.Host}";
    }

    public static (bool, string) CheckSharePointAccess(JsonObject message, string bodyHtml, Settings settings)
    {
        if (ParseAccessRequest(bodyHtml) is not var (url, name, _))
            return (false, "couldn't read the requested resource and who asked from the email");
        if (SitePath(url) is not { } sitePath)
            return (false, $"{url} isn't a SharePoint site, so can't look the request up");
        if (Graph is not { } graph)
            return (false, "no Graph to look the request up with");
        if (!DateTime.TryParse(message.Str("receivedDateTime"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var received))
            return (false, "the email has no received time to match the request by");
        JsonObject? best = null;
        var bestGap = TimeSpan.MaxValue;
        try
        {
            var siteId = graph.Request($"{sitePath}?$select=id", scopes: SiteScopes).Req("id");
            var listId = graph.Request($"/sites/{siteId}/lists/{Uri.EscapeDataString("Access Requests")}?$select=id", scopes: SiteScopes).Req("id");
            foreach (var item in graph.Pages($"/sites/{siteId}/lists/{listId}/items?$expand=fields&$select=id,lastModifiedDateTime,lastModifiedBy&$top=200", SiteScopes))
            {
                var fields = item["fields"] as JsonObject;
                var who = fields?["RequestedForDisplayNameDisp"]?.ToString() ?? fields?["RequestedByDisplayNameDisp"]?.ToString() ?? "";
                if (!string.Equals(who.Trim(), name, StringComparison.OrdinalIgnoreCase)
                    || !DateTime.TryParse(fields?["RequestDate"]?.ToString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var asked))
                    continue;
                var gap = (asked - received).Duration();
                if (gap <= AccessRequestWindow && gap < bestGap)
                    (best, bestGap) = (item, gap);
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or M365Graph.SignInNeededException))
        {
            return (false, $"couldn't read the site's access requests ({error.Message}), so can't tell whether it's been dealt with");
        }
        if (best is null)
            return (false, $"{name}'s request isn't in the site's access requests, so can't tell whether it's been dealt with");
        var status = best["fields"]?["StatusDisp"]?.ToString() ?? "";
        var by = best["lastModifiedBy"]?["user"]?["displayName"]?.ToString() ?? "someone";
        var when = DateTime.TryParse(best["lastModifiedDateTime"]?.ToString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var changed)
            ? TimeZoneInfo.ConvertTimeFromUtc(changed, Clock.Provider.LocalTimeZone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture) : "";
        return status switch
        {
            "1" => (true, $"already approved by {by}{(when.Length > 0 ? $" ({when})" : "")}"),
            "0" or "" => (false, "still waiting for approval"),
            _ => (false, $"its status in the site's access requests is {status} (not approved), changed by {by}"),
        };
    }

    /// <summary>Runs gh with these arguments: (exit code, stdout). Replaced in tests.</summary>
    public static Func<IReadOnlyList<string>, (int Exit, string Output)> Gh = RunGh;

    private static (int, string) RunGh(IReadOnlyList<string> args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("gh")
        {
            UseShellExecute = false, CreateNoWindow = true,   // the watcher is windowless: no console flashing up
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(true);
                return (-1, "");
            }
            return (process.ExitCode, output.Result);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "");   // gh isn't installed or isn't on PATH
        }
    }

    private static readonly System.Text.RegularExpressions.Regex RunFailed = new(
        @"^\[(?<repo>[\w.-]+/[\w.-]+)\] Run failed: (?<workflow>.+) - (?<branch>\S+) \((?<sha>[0-9a-f]{7,40})\)\s*$");

    /// <summary>(repo, workflow, branch, sha) from "[owner/repo] Run failed: build - main (6474d40)", or null.</summary>
    public static (string Repo, string Workflow, string Branch, string Sha)? ParseRunFailed(string subject)
    {
        var m = RunFailed.Match(subject.Trim());
        return m.Success ? (m.Groups["repo"].Value, m.Groups["workflow"].Value, m.Groups["branch"].Value, m.Groups["sha"].Value) : null;
    }

    public static (bool, string) CheckGitHubRun(JsonObject message, string bodyHtml, Settings settings)
    {
        var subject = message.Has("subject") ? Py.Str(message.Get("subject")) : "";
        if (ParseRunFailed(subject) is not var (repo, workflow, branch, _))
            return (false, "couldn't read the repository, workflow and branch from the subject");
        var (exit, output) = Gh(["run", "list", "-R", repo, "--branch", branch, "--workflow", workflow, "-L", "10",
                                 "--json", "status,conclusion,headSha,createdAt"]);
        if (exit != 0)
            return (false, "couldn't ask GitHub (is gh installed and signed in?), so can't tell whether it has passed since");
        JsonArray? runs;
        try
        {
            runs = JsonNode.Parse(output) as JsonArray;
        }
        catch (System.Text.Json.JsonException)
        {
            runs = null;
        }
        var latest = runs?.OfType<JsonObject>().FirstOrDefault(r => (string?)r["status"] == "completed");
        if (latest is null)
            return (false, $"no finished {workflow} run on {branch} found, so can't tell whether it has passed since");
        var conclusion = (string?)latest["conclusion"] ?? "unknown";
        var headSha = (string?)latest["headSha"] ?? "";
        var sha = headSha[..Math.Min(7, headSha.Length)];
        var when = (string?)latest["createdAt"] ?? "";
        return conclusion == "success"
            ? (true, $"it has passed since: the latest {workflow} run on {branch} ({sha}, {when}) succeeded")
            : (false, $"still failing: the latest {workflow} run on {branch} ({sha}, {when}) ended '{conclusion}'");
    }

    /// <summary>[(flow name, failure count)] from the digest's table of flows.</summary>
    public static List<(string Name, long Count)> FlowFailures(string bodyHtml)
    {
        var flows = new List<(string, long)>();
        foreach (var row in PyRegex.Get("(?is)<tr.*?</tr>").FindAll(bodyHtml))
        {
            var cells = PyRegex.Get("(?is)<td.*?</td>").FindAll(row)
                .Select(cell => PyStr.Strip(PyRegex.Get(@"\s+").Sub(" ", PyRegex.Get("<[^>]+>").Sub(" ", cell)))).ToList();
            if (cells.Count == 2 && cells[0].Length > 0 && PyStr.IsDigit(cells[1]))
                flows.Add((cells[0], PyStr.Int(cells[1])));   // "²" passes isdigit and fails int(), as in Python
        }
        return flows;
    }

    /// <summary>The newest modified time (as a Unix timestamp) of the files a glob matches, or null.</summary>
    internal static double? Newest(string pattern)
    {
        var times = Glob(pattern).Select(MTime).ToList();
        return times.Count > 0 ? times.Max() : null;
    }

    internal static double MTime(string path) =>
        (File.GetLastWriteTimeUtc(path) - DateTime.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond;

    /// <summary>glob.glob, for a pattern with wildcards in the file name only (checked at load). Case-insensitive,
    /// as Windows is. Hidden files (a leading ".") aren't matched by "*", as in Python.</summary>
    internal static List<string> Glob(string pattern)
    {
        var dir = Path.GetDirectoryName(pattern);
        var name = Path.GetFileName(pattern);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return [];
        if (name.IndexOfAny(['*', '?', '[']) < 0)
            return File.Exists(pattern) || Directory.Exists(pattern) ? [pattern] : [];
        var regex = PyRegex.Get(FnmatchToPython(name), true);
        return Directory.EnumerateFileSystemEntries(dir)
            .Where(path => Path.GetFileName(path) is var n && (!n.StartsWith('.') || name.StartsWith('.')) && regex.FullMatch(n))
            .ToList();
    }

    /// <summary>fnmatch.translate's idea of *, ? and [...], as a Python pattern.</summary>
    internal static string FnmatchToPython(string name)
    {
        var b = new System.Text.StringBuilder("(?s)");
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '*')
                b.Append(".*");
            else if (c == '?')
                b.Append('.');
            else if (c == '[' && name.IndexOf(']', i + 1) is var close and > 0)
            {
                var inside = name[(i + 1)..close];
                b.Append('[').Append(inside.StartsWith('!') ? "^" + inside[1..].Replace("\\", "\\\\") : inside.Replace("\\", "\\\\")).Append(']');
                i = close;
            }
            else
                b.Append(PyRegex.Escape(c.ToString()));
        }
        return b.ToString();
    }

    private static string When(double timestamp) =>
        PyTime.Strftime(DateTime.SpecifyKind(DateTime.UnixEpoch.AddTicks((long)(timestamp * TimeSpan.TicksPerSecond)).ToLocalTime(), DateTimeKind.Unspecified), "%d %b %H:%M");

    /// <summary>Has the flow worked since? With pending: if nothing pending is newer than the newest output. Without:
    /// if its output is newer than the digest itself.</summary>
    public static (bool, string) FlowRecovered(FlowCheck check, DateTimeOffset received)
    {
        var latest = Newest(check.Output);
        if (latest is not { } newest)
            return (false, "nothing it writes has ever been found");
        var when = When(newest);
        if (check.Pending is not null)
        {
            var waiting = Glob(check.Pending).Where(path => MTime(path) > newest).ToList();
            if (waiting.Count > 0)
                return (false, $"{waiting.Count} request(s) since its last success ({when}) haven't been picked up");
            return (true, $"it has worked since: its output last came back {when}");
        }
        if (newest > received.ToUnixTimeMilliseconds() / 1000.0)
            return (true, $"it has worked since the digest: its output was last written {when}");
        return (false, $"its output was last written {when}, before the digest");
    }

    public static (bool, string) CheckFlowFailures(JsonObject message, string bodyHtml, Settings settings)
    {
        var flows = FlowFailures(bodyHtml);
        if (flows.Count == 0)
            return (false, "couldn't read the digest's list of flows");
        var receivedText = message.Has("receivedDateTime") ? Py.Str(message.Get("receivedDateTime")) : "1970-01-01T00:00:00Z";
        var received = DateTimeOffset.Parse(receivedText.Replace("Z", "+00:00"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var findings = new List<string>();
        var fine = true;
        foreach (var (name, count) in flows)
        {
            var check = settings.FlowChecks.FirstOrDefault(c => name.Contains(c.Flow, StringComparison.OrdinalIgnoreCase));
            var (ok, why) = check is not null ? FlowRecovered(check, received)
                : (false, "no [[flow_check]] for this flow, so can't tell whether it has worked since");
            fine = fine && ok;
            findings.Add($"{name} ({count} failures): {why}");
        }
        return (fine, string.Join("; ", findings));
    }
}
