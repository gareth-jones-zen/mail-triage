using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>An automatic calendar write whose answer was lost (a 5xx, a timeout, or a connection dropped after sending):
/// it may have gone, so it's never sent again. The run carries on, the write counts as done (so the next run doesn't redo
/// it), it's listed in `mail-triage deleted` under a "Couldn't confirm" rule, and the watcher notifies.</summary>
public static class Unconfirmed
{
    public const string Prefix = "Couldn't confirm: ";

    /// <summary>Runs the write (or writes: the first lost answer stops the rest). Null when Microsoft answered; otherwise
    /// what happened, for the record.</summary>
    public static string? Try(Action write)
    {
        try
        {
            write();
            return null;
        }
        catch (M365Graph.GraphDeliveryUnknownException error)
        {
            return error.Message;
        }
    }

    /// <summary>Marks a record of what was done (an `acted` entry) as unconfirmed: its rule gets the prefix and its why says
    /// to check.</summary>
    public static void Mark(JsonObject acted, string unknown, string fallbackRule)
    {
        acted["rule"] = Prefix + (acted.Str("rule") ?? fallbackRule);
        acted["why"] = $"may have gone, check the calendar ({acted.Str("why") ?? ""}): {unknown}";
    }

    /// <summary>Whether a `deleted` record is one of these.</summary>
    public static bool Is(JsonNode? entry) =>
        entry is JsonObject o && (o.Str("rule") ?? "").StartsWith(Prefix, StringComparison.Ordinal);
}
