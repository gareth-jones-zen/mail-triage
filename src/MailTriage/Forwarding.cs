using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>The forward action (1.9.0): forward the email with a covering comment, then mark it read and file it.
/// A forward sends mail, so it's never sent twice. Before the POST the email's key is written to the forwarded file as
/// "sending", and only once Graph answers does it become "sent". An email found "sending" had its answer lost (a
/// timeout, a reset, a 5xx after Graph may have acted): it's raised for a person to check Sent Items, never resent.
/// "sent" with the email still in its folder means the forward went but the filing didn't: only the filing is redone.</summary>
public static class Forwarding
{
    /// <summary>"forward to a@x and file to F (marked read)", for logs and dry runs ("would " + it).</summary>
    public static string Describe(Rule rule, bool done = false)
    {
        var to = string.Join(", ", rule.ForwardTo ?? []);
        return (done ? "forwarded to " : "forward to ") + to
               + (rule.To is { } folder ? (done ? " and filed to " : " and file to ") + folder : "")
               + (rule.MarkRead ? " (marked read)" : "");
    }

    /// <summary>Your own domain, from `email` (lower case), or null when the settings have no email.</summary>
    internal static string? Domain(Settings settings) =>
        settings.Email is { } email && email.LastIndexOf('@') is var at and >= 0 && at < email.Length - 1 ? email[(at + 1)..].ToLowerInvariant() : null;

    /// <summary>The addresses outside that domain (1.28.0), for the `forward` preview to flag. A subdomain counts as
    /// outside: only an exact match is yours.</summary>
    internal static List<string> Outside(IEnumerable<string> addresses, string? domain) =>
        domain is null ? [] : addresses.Where(a => !a.Trim().EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Forward, then mark read, then file (marking goes first because a move gives the email a new id).
    /// Returns null when done, or why it wasn't (an earlier forward's answer was lost), for the queue.</summary>
    public static string? Apply(Settings settings, Mailbox mailbox, string key, string messageId, Rule rule)
    {
        var state = StateFiles.LoadObject(settings.ForwardedFile, () => []);
        if (state.Get(key) is JsonObject prior)
        {
            if (prior.Str("state") != "sent")
                return $"a forward to {string.Join(", ", prior.Get("to").Strings())} may already have gone (started "
                       + $"{prior.Fmt("when")}, its answer was lost): check Sent Items, then file it by hand. Never forwarded twice";
        }
        else
        {
            // Only a Live writer records: a journal run sends nothing, and mustn't mark the email as forwarded.
            var live = mailbox.Writer is GraphWrites { Sends: true };
            var entry = new JsonObject { ["state"] = "sending", ["when"] = PyTime.Iso(Clock.Now, "seconds"), ["to"] = J.Array(rule.ForwardTo ?? []) };
            state[key] = entry;
            if (live)
                Save(settings, state);
            mailbox.Forward(messageId, rule.ForwardTo!, rule.Comment!);
            entry["state"] = "sent";
            if (live)
                Save(settings, state);
        }
        if (rule.MarkRead)
            mailbox.MarkRead(messageId);
        if (rule.To is { } to)
            mailbox.File(messageId, mailbox.FolderPath(to));
        return null;
    }

    /// <summary>The "deleted" file's record of a forward, shown by `deleted`.</summary>
    public static JsonObject Entry(string folder, JsonObject message, Rule rule, string when)
    {
        var entry = new JsonObject
        {
            ["when"] = when, ["folder"] = folder, ["received"] = message.GetCopy("receivedDateTime"), ["from"] = Triage.Sender(message),
            ["subject"] = message.GetCopy("subject", ""), ["rule"] = rule.Name, ["forward_to"] = J.Array(rule.ForwardTo ?? []),
            ["did"] = $"forwarded to {string.Join(", ", rule.ForwardTo ?? [])}" + (rule.MarkRead ? ", marked read" : ""),
        };
        if (rule.To is not null)
            entry["to"] = rule.To;
        return entry;
    }

    /// <summary>Kept for 60 days after it was sent, like the deleted file; one still "sending" is kept until a person
    /// has dealt with it (it's what stops a second forward).</summary>
    private static void Save(Settings settings, JsonObject state)
    {
        var cutoff = PyTime.Iso(Clock.Now - TimeSpan.FromDays(Triage.KeepDays), "seconds");
        foreach (var (k, v) in state.ToList())
            if (v is JsonObject o && o.Str("state") == "sent" && PyStr.CompareCodePoints(o.Str("when") ?? "", cutoff) < 0)
                state.Remove(k);
        StateFiles.Save(settings.ForwardedFile, state);
    }
}
