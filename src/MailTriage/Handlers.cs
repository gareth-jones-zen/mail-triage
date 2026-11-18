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

    /// <summary>attachments = true (1.5.0): the email's file attachments are saved to a folder of their own, their
    /// paths given in the input, and the folder removed once the handler has run (kept, for a queue handler).</summary>
    public bool Attachments { get; init; }

    /// <summary>queue (1.8.0): instead of a command run now, the email is saved (as JSON, the same input a command
    /// gets, under SaveTo) and this command is put on the claude-headless job queue, with {saved} replaced by the saved
    /// file's path, to run later from Cwd. The handler is done once it's queued, so then applies at once. Null for a
    /// command handler.</summary>
    public List<string>? Queue { get; init; }

    /// <summary>save_to: the folder a queue handler saves emails in (kept until the job deletes them).</summary>
    public string? SaveTo { get; init; }
}

/// <summary>What a handler made of an email. Result: done (apply then), retry (leave it, ask again next run) or
/// failed (raise it, with the note as the reason).</summary>
public sealed record HandleOutcome(string Result, string Note);

/// <summary>Runs handlers, and remembers what each made of each email (the handled file), so a handler runs once per
/// email: again only after "try again later" (exit 75), never after it handled the email or failed.
///
/// The contract: the email as JSON on stdin (id, key, folder, from, subject, received, body as text, webLink,
/// hasAttachments, rule; and, for a handler with attachments = true, attachments: [{name, path, size, contentType}]).
/// The attachment files are removed once the handler exits, so it copies what it keeps. Exit 0: handled; 75: try again later (up to max_attempts); anything else, or a timeout:
/// raise it. The first line of output is the note. A timeout isn't retried: the handler may have done part of its work.
/// The handler captures what it needs before it exits, because then may move the email (and moving changes its id).</summary>
public sealed class Handlers(Settings settings)
{
    public const int TryAgain = 75;   // EX_TEMPFAIL

    /// <summary>Runs a handler on this input: (exit code, output, timed out?). Replaced in tests.</summary>
    public static Func<Handler, string, (int Exit, string Output, bool TimedOut)> Process = RunProcess;

    /// <summary>Puts a command on the claude-headless job queue, to run from a folder. Replaced in tests.</summary>
    public static Action<IReadOnlyList<string>, string> Enqueue = (argv, cwd) => ClaudeHeadless.JobQueue.Default.Add(argv, cwd);

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
        var input = Input(mailbox, message, folder, rule);
        string? saved = null;
        int exit;
        string output;
        bool timedOut;
        if (handler.Queue is not null)
            return Queue(mailbox, message, key, attempts, input, handler);
        try
        {
            if (handler.Attachments)
            {
                saved = Path.Combine(Path.GetDirectoryName(PyPath.Resolve(settings.HandledFile))!, "handler-attachments", Folder(key));
                try
                {
                    input["attachments"] = SaveAttachments(mailbox, message.Req("id"), saved);
                }
                catch (Exception error) when (error is not (OutOfMemoryException or M365Graph.SignInNeededException))
                {
                    // Graph or the disk failed (a sign-in is the run's to report): nothing was handed over, so it's safe to ask again next run.
                    (exit, output, timedOut) = (TryAgain, $"couldn't fetch the attachments: {PyStr.Head(error.Message, 150)}", false);
                    return Record(handler, message, key, attempts, exit, output, timedOut);
                }
            }
            (exit, output, timedOut) = Process(handler, PyJson.Dump(input, null, false));
        }
        finally
        {
            if (saved is not null)
                RemoveQuietly(saved);
        }
        return Record(handler, message, key, attempts, exit, output, timedOut);
    }

    private HandleOutcome Record(Handler handler, JsonObject message, string key, long attempts, int exit, string output, bool timedOut)
    {
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

    /// <summary>A queue handler (1.8.0): save the email (and its attachments, if asked) under save_to, then queue the
    /// command with {saved} filled in. Saving or queuing failing is "try again later"; nothing has been handed over.</summary>
    private HandleOutcome Queue(Mailbox mailbox, JsonObject message, string key, long attempts, JsonObject input, Handler handler)
    {
        var received = message.Str("receivedDateTime") ?? input.Str("received") ?? "";
        var stem = $"{(received.Length >= 10 ? received[..10] : "undated")}-{Folder(key)[..8]}";
        var path = Path.Combine(handler.SaveTo!, stem + ".json");
        try
        {
            Directory.CreateDirectory(handler.SaveTo!);
            if (handler.Attachments)
                input["attachments"] = SaveAttachments(mailbox, message.Req("id"), Path.Combine(handler.SaveTo!, stem + "-attachments"));
            File.WriteAllText(path, PyJson.Dump(input, 1, false), new UTF8Encoding(false));
            Enqueue(handler.Queue!.Select(a => a.Replace("{saved}", path, StringComparison.Ordinal)).ToList(), handler.Cwd);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or M365Graph.SignInNeededException))
        {
            return Record(handler, message, key, attempts, TryAgain, $"couldn't save and queue it: {PyStr.Head(error.Message, 150)}", false);
        }
        return Record(handler, message, key, attempts, 0, $"queued: {Path.GetFileName(path)}", false);
    }

    /// <summary>A folder name for one email's attachments: its key, hashed (keys hold &lt; &gt; @ and the like).</summary>
    private static string Folder(string key) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();

    /// <summary>The email's file attachments (not inline images or attached emails), each saved under folder:
    /// [{name, path, size, contentType}].</summary>
    public static JsonArray SaveAttachments(Mailbox mailbox, string id, string folder)
    {
        RemoveQuietly(folder);
        Directory.CreateDirectory(folder);
        var found = new JsonArray();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in mailbox.Graph.Pages($"/me/messages/{id}/attachments?$select=id,name,size,contentType,isInline",
                     scopes: mailbox.ReadScopes))
        {
            if (item.Str("@odata.type") is { } type && type != "#microsoft.graph.fileAttachment")
                continue;
            if (item.Get("isInline") is JsonValue inline && inline.TryGetValue<bool>(out var isInline) && isInline)
                continue;
            var full = mailbox.Graph.Request($"/me/messages/{id}/attachments/{item.Req("id")}", scopes: mailbox.ReadScopes);
            if (full.Str("contentBytes") is not { } content)
                continue;
            var name = SafeName(item.Str("name") ?? "attachment");
            for (var i = 2; !used.Add(name); i++)
                name = $"{Path.GetFileNameWithoutExtension(SafeName(item.Str("name") ?? "attachment"))} ({i}){Path.GetExtension(name)}";
            var path = Path.Combine(folder, name);
            var bytes = Convert.FromBase64String(content);
            File.WriteAllBytes(path, bytes);
            found.Add((JsonNode)new JsonObject
            {
                ["name"] = item.Str("name") ?? name, ["path"] = path, ["size"] = bytes.LongLength,
                ["contentType"] = item.Str("contentType"),
            });
        }
        return found;
    }

    private static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(Path.GetFileName(name).Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "attachment" : clean;
    }

    private static void RemoveQuietly(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A file still open (the handler's own child process, say): it goes next time this email is handled.
        }
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
