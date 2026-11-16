using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;

namespace MailTriage;

/// <summary>A [[handler]]: a command a rule with action = "handle" gives an email to, and what happens to the email
/// once the command has handled it (then: delete, file, leave or review).</summary>
public sealed class Handler
{
    public static readonly string[] Thens = ["delete", "file", "leave", "review"];

    public required string Name { get; init; }
    public required List<string> Command { get; init; }
    public required string Cwd { get; init; }
    public string Then { get; init; } = "delete";
    public string? To { get; init; }
    public long Timeout { get; init; } = 120;
    public long MaxAttempts { get; init; } = 4;
}

/// <summary>What a handler made of an email. Result: done (apply then), retry (leave it, ask again next run) or
/// failed (raise it, with the note as the reason).</summary>
public sealed record HandleOutcome(string Result, string Note);

/// <summary>Runs handlers, and remembers what each made of each email (the handled file), so a handler runs once per
/// email: again only after "try again later" (exit 75), never after it handled the email or failed.
///
/// The contract: the email as JSON on stdin (id, key, folder, from, subject, received, body as text, webLink,
/// hasAttachments, rule). Exit 0: handled; 75: try again later (up to max_attempts); anything else, or a timeout:
/// raise it. The first line of output is the note. A timeout isn't retried: the handler may have done part of its work.
/// The handler captures what it needs before it exits, because then may move the email (and moving changes its id).</summary>
public sealed class Handlers(Settings settings)
{
    public const int TryAgain = 75;   // EX_TEMPFAIL

    /// <summary>Runs a handler on this input: (exit code, output, timed out?). Replaced in tests.</summary>
    public static Func<Handler, string, (int Exit, string Output, bool TimedOut)> Process = RunProcess;

    private JsonObject? state;

    private JsonObject State => state ??= StateFiles.LoadObject(settings.HandledFile, () => []);

    /// <summary>The record for this email, if a handler has seen it.</summary>
    public JsonObject? Record(string key) => State[key] as JsonObject;

    /// <summary>Gives the email to the handler, unless it has already handled it (then only then is left to do).</summary>
    public HandleOutcome Handle(Mailbox mailbox, JsonObject message, string folder, Rule? rule, Handler handler)
    {
        var key = Triage.Key(message);
        var previous = Record(key);
        if (previous?.Str("result") == "done")
            return new("done", previous.Str("note") ?? "");
        var attempts = (previous?.Get("attempts") is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0) + 1;
        var (exit, output, timedOut) = Process(handler, PyJson.Dump(Input(mailbox, message, folder, rule), null, false));
        var first = PyStr.Head(output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "", 200);
        string result, note;
        if (timedOut)
            (result, note) = ("failed", $"{handler.Name} timed out after {handler.Timeout}s" + (first.Length > 0 ? $": {first}" : ""));
        else if (exit == 0)
            (result, note) = ("done", first);
        else if (exit == TryAgain && attempts < handler.MaxAttempts)
            (result, note) = ("retry", first);
        else if (exit == TryAgain)
            (result, note) = ("failed", $"{handler.Name} still not ready after {attempts} tries" + (first.Length > 0 ? $": {first}" : ""));
        else
            (result, note) = ("failed", $"{handler.Name} failed (exit {exit})" + (first.Length > 0 ? $": {first}" : ""));
        State[key] = new JsonObject
        {
            ["handler"] = handler.Name, ["result"] = result, ["note"] = note, ["attempts"] = attempts,
            ["when"] = PyTime.Iso(Clock.Now, "seconds"), ["subject"] = message.GetCopy("subject", ""),
        };
        Save();   // at once: a failure after this (applying then) must never run the handler a second time
        return new(result, note);
    }

    private void Save()
    {
        var cutoff = PyTime.Iso(Clock.Now - TimeSpan.FromDays(Triage.KeepDays));
        foreach (var key in State.Select(p => p.Key).ToList())
            if (State[key] is JsonObject o && PyStr.CompareCodePoints(o.Str("when") ?? "", cutoff) < 0)
                State.Remove(key);
        StateFiles.Save(settings.HandledFile, State);
    }

    /// <summary>The email as the handler sees it. The body comes as text, fetched for this email only.</summary>
    public static JsonObject Input(Mailbox mailbox, JsonObject message, string folder, Rule? rule)
    {
        var id = message.Req("id");
        var full = mailbox.Graph.Request($"/me/messages/{id}?$select=body,hasAttachments", scopes: mailbox.ReadScopes,
            headers: new Dictionary<string, string> { ["Prefer"] = "outlook.body-content-type=\"text\"" });
        return new JsonObject
        {
            ["id"] = id, ["key"] = Triage.Key(message), ["folder"] = folder, ["from"] = Triage.Sender(message),
            ["subject"] = message.GetCopy("subject", ""), ["received"] = message.GetCopy("receivedDateTime"),
            ["body"] = full.Get("body") is JsonObject b ? b.GetCopy("content", "") : "",
            ["webLink"] = message.GetCopy("webLink"), ["hasAttachments"] = full.Get("hasAttachments") is JsonValue h && h.TryGetValue<bool>(out var has) && has,
            ["rule"] = rule?.Name,
        };
    }

    private static (int, string, bool) RunProcess(Handler handler, string input)
    {
        var start = new ProcessStartInfo(handler.Command[0])
        {
            UseShellExecute = false, CreateNoWindow = true,   // the watcher is windowless: no console flashing up
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = handler.Cwd,
        };
        foreach (var a in handler.Command.Skip(1))
            start.ArgumentList.Add(a);
        start.Environment["MAIL_TRIAGE_HANDLER"] = handler.Name;
        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It exited without reading its input: its exit code says what happened.
            }
            if (!process.WaitForExit((int)Math.Min(int.MaxValue, handler.Timeout * 1000)))
            {
                process.Kill(true);
                return (-1, Finished(output), true);
            }
            // A process the handler started that kept its output open would hold these: don't wait for it.
            Task.WaitAll([output, errors], 5_000);
            var text = Finished(output);
            return (process.ExitCode, text.Trim().Length > 0 || process.ExitCode == 0 ? text : LastLine(Finished(errors)), false);
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            return (-1, $"couldn't start {handler.Command[0]}: {error.Message}", false);
        }
    }

    private static string Finished(Task<string> task) => task.IsCompletedSuccessfully ? task.Result : "";

    private static string LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
}
