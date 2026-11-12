using System.Globalization;
using System.Text.Json.Nodes;
using MailTriage.Interim;

namespace MailTriage;

/// <summary>Checks: an email that may need you, or may not (checks.py). A rule with action = "check" runs one on
/// the email: (nothing to do?, why). True deletes it as a delete rule would; false queues it with the why.
///
/// flow-failures: Power Automate's "flow(s) have failed" digest only matters if the flow is still failing. Each flow
/// needs a [[flow_check]] saying what a good run leaves behind; a flow without one is raised.</summary>
public static class Checks
{
    public delegate (bool Fine, string Why) Check(JsonObject message, string bodyHtml, Settings settings);

    /// <summary>name -> (needs the body?, the check).</summary>
    public static readonly IReadOnlyDictionary<string, (bool NeedsBody, Check Run)> All =
        new Dictionary<string, (bool, Check)> { ["flow-failures"] = (true, CheckFlowFailures) };

    /// <summary>[(flow name, failure count)] from the digest's table of flows.</summary>
    public static List<(string Name, long Count)> FlowFailures(string bodyHtml)
    {
        var flows = new List<(string, long)>();
        foreach (var row in PyRegex.Get("(?is)<tr.*?</tr>").FindAll(bodyHtml))
        {
            var cells = PyRegex.Get("(?is)<td.*?</td>").FindAll(row)
                .Select(cell => PyText.Strip(PyRegex.Get(@"\s+").Sub(" ", PyRegex.Get("<[^>]+>").Sub(" ", cell)))).ToList();
            if (cells.Count == 2 && cells[0].Length > 0 && PyText.IsDigit(cells[1]))
                flows.Add((cells[0], PyText.Int(cells[1])));   // "²" passes isdigit and fails int(), as in Python
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
