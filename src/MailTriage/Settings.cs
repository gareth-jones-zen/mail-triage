using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;
using WinBackground;
using LeaveSettings = CalendarDays.LeaveSettings;

namespace MailTriage;

/// <summary>A settings problem: the message says which setting and what's wrong (Python's ValueError).</summary>
public sealed class SettingsException(string message) : Exception(message);

public sealed class Folder(string name, string? since = null, string unmatched = "review")
{
    public string Name { get; } = name;
    public string? Since { get; } = since;
    /// <summary>"leave": only the rules act here; mail no rule names is left alone (an inbox).</summary>
    public string Unmatched { get; set; } = unmatched;
}

public sealed class Rule
{
    public required string Name { get; init; }
    public required string Sender { get; init; }
    public required string Action { get; init; }
    public string? Match { get; init; }
    public string? Check { get; init; }
    public string Why { get; init; } = "";
    public string? To { get; init; }
    public string? Handler { get; init; }
    public List<string>? Folders { get; init; }

    /// <summary>forward: who it goes to, the covering text above the original, and whether it's marked read
    /// (forward only: file leaves mail unread on purpose).</summary>
    public List<string>? ForwardTo { get; init; }
    public string? Comment { get; init; }
    public bool MarkRead { get; init; }

    /// <summary>forward (1.19.0): where the Sent Items copy of each forward is moved (never deleted), when [sent] is on; usually
    /// the folder the original is filed to (`to`), so the two sit together.</summary>
    public string? FileSent { get; init; }

    public bool Matches(string sender, string text) =>
        PyRegex.IsMatch(Sender, sender, true) && (Match is null || PyRegex.IsMatch(Match, text, true));
}

public sealed record FlowCheck(string Flow, string Output, string? Pending = null);

public sealed class InviteRule
{
    public required string Name { get; init; }
    public string Action { get; init; } = "skip";
    public string? Subject { get; init; }
    public string? Organiser { get; init; }
    public bool? AllDay { get; init; }
    public List<string>? ShowAs { get; init; }
    public bool? ViaGroup { get; init; }
    public bool? Optional { get; init; }
    public long? MinPeople { get; init; }
    public string Why { get; init; } = "";
}

public sealed record MovableBlock(string Subject, string? Within = null, string? Before = null);

public sealed class Invites
{
    public const string DefaultLeavePattern = @"^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b";
    /// <summary>days_ahead: how far ahead to look, in days, when no months are given (default 42).</summary>
    public long DaysAhead { get; set; } = 42;

    /// <summary>Whether [invites] gives days_ahead itself: a tool's own setting wins over the shared
    /// calendar_months_ahead.</summary>
    public bool DaysAheadSet { get; set; }

    /// <summary>months_ahead (1.11.0): how far ahead invites, days off and double bookings are looked at, in months;
    /// overrides days_ahead.</summary>
    public int? MonthsAhead { get; set; }

    /// <summary>calendar_months_ahead (1.15.0): the shared look-ahead, in months, usually from the base settings file
    /// (base-config.toml). Used when [invites] gives neither months_ahead nor days_ahead.</summary>
    public int? CalendarMonthsAhead { get; set; }

    /// <summary>The window in months, if it's given in months: [invites] months_ahead, else (unless [invites] gives
    /// days_ahead) the shared calendar_months_ahead.</summary>
    public int? Months => MonthsAhead ?? (DaysAheadSet ? null : CalendarMonthsAhead);

    /// <summary>auto_accept (1.11.0): answer an invite with no clash without asking (markers that hold no time too).</summary>
    public bool AutoAccept { get; set; }

    /// <summary>auto_accept_outside: ...from an organiser outside your organisation too (default: asked).</summary>
    public bool AutoAcceptOutside { get; set; }

    /// <summary>auto_accept_optional: ...where you're optional, or invited through a group, too (default: asked).</summary>
    public bool AutoAcceptOptional { get; set; }

    /// <summary>auto_move_blocks: accept an invite that clashes only with your own blocks or travel at either end of the
    /// day, and move those (they notify no one). A clash with a meeting you organise is still asked.</summary>
    public bool AutoMoveBlocks { get; set; }

    /// <summary>decline_days_off: decline meetings others organise on your leave and bank holidays, each occurrence once,
    /// whether accepted already or a new invite; a series that's free apart from your days off is accepted (with
    /// auto_accept) and those occurrences declined.</summary>
    public bool DeclineDaysOff { get; set; }

    /// <summary>cancel_days_off (1.14.0): cancel a meeting you organise with one other person (your manager's 1:1
    /// included) that falls on your days off, that occurrence, with my_note or bank_holiday_note from [one_to_ones].
    /// Your reports' 1:1s are left to [one_to_ones], which may move them instead.</summary>
    public bool CancelDaysOff { get; set; }

    /// <summary>remove_blocks_days_off (1.14.0): delete your own blocks (no one else invited) on your days off, that
    /// occurrence; no one is told.</summary>
    public bool RemoveBlocksDaysOff { get; set; }

    /// <summary>raise_group_days_off (1.14.0): queue a meeting you organise with two or more others on your days off,
    /// once: it may go ahead without you, or someone else chair it, so it's yours to decide.</summary>
    public bool RaiseGroupDaysOff { get; set; }

    /// <summary>max_declines: never more days-off declines than this in one run.</summary>
    public int MaxDeclines { get; set; } = 20;

    /// <summary>double_bookings: raise a meeting that overlaps another meeting, or a fixed block, once each.</summary>
    public bool DoubleBookings { get; set; }

    /// <summary>How many days ahead to look, from a moment.</summary>
    public long Days(DateTime at) => Months is { } m
        ? DateOnly.FromDateTime(at).AddMonths(m).DayNumber - DateOnly.FromDateTime(at).DayNumber
        : DaysAhead;
    public List<string> FixedBlocks { get; set; } = [];
    public List<string> CommuteBlocks { get; set; } = [];
    public List<MovableBlock> Movable { get; set; } = [];
    public List<string> Protected { get; set; } = [];
    public string LeaveNote { get; set; } = "On annual leave";

    /// <summary>The decline note on a day that's only a bank holiday (1.10.0).</summary>
    public string BankHolidayNote { get; set; } = "Bank holiday";

    /// <summary>Entries of yours in these categories mark a bank holiday (1.10.0); null: [one_to_ones]', else "Holiday".</summary>
    public List<string>? BankHolidayCategories { get; set; }
    public string DateFormat { get; set; } = "%d/%m";
    public (string Start, string End) WorkingHours { get; set; } = ("08:00", "18:00");
    public (string Start, string End) TravelHours { get; set; } = ("06:00", "21:00");
    public List<string> RequestsFolders { get; set; } = [];
    public bool AcceptShortened { get; set; }
    public List<string> ProposalsFolders { get; set; } = [];
    public bool AcceptProposals { get; set; }
    public string LeavePattern { get; set; } = DefaultLeavePattern;
    public List<InviteRule> Rules { get; set; } = [];
}

public sealed class OneToOne(string name, string email, string subject)
{
    public string Name { get; } = name;
    public string Email { get; set; } = email;
    public string Subject { get; } = subject;

    /// <summary>cancel_if_within_working_days on this [[one_to_one]] (1.27.0); null: [one_to_ones]'.</summary>
    public long? CancelIfWithinWorkingDays { get; init; }
}

public sealed class OneToOnes
{
    public const string DefaultLeavePattern = @"^{name}\s+(annual leave|on leave)\b";
    public List<OneToOne> Reports { get; set; } = [];
    public bool Act { get; set; }
    public long MaxActions { get; set; } = 20;
    public double CancelDays { get; set; } = 3;
    public string Midday { get; set; } = "13:00";
    public long? DaysAhead { get; set; }
    public List<string> BankHolidayCategories { get; set; } = ["Holiday"];
    public string LeavePattern { get; set; } = DefaultLeavePattern;
    public string ReportNote { get; set; } = "Cancelling as you're on leave - enjoy the time off";
    public string MyNote { get; set; } = "Cancelling as I'm on leave";
    public string BankHolidayNote { get; set; } = "Cancelling for the bank holiday";

    /// <summary>when_shorter (1.26.0): off for less than cancel_days of the week, "move" the occurrence (the default, as
    /// before) or "ask" (queue it with the reason and a suggestion).</summary>
    public string WhenShorter { get; set; } = "move";

    public static readonly string[] WhenShorterChoices = ["move", "ask"];

    /// <summary>The double-booking settings (1.26.0), off ("ignore") unless set.</summary>
    public DoubleBookedSettings DoubleBooked { get; set; } = new();

    /// <summary>cancel_if_within_working_days (1.27.0): 0, off.</summary>
    public long CancelIfWithinWorkingDays { get; set; }

    /// <summary>The 1:1s' handling, as one group.</summary>
    public LeaveHandling Handling => new()
    {
        Act = Act, CancelDays = CancelDays, WhenShorter = WhenShorter, TheirNote = ReportNote, MyNote = MyNote, BankHolidayNote = BankHolidayNote,
        DoubleBooked = DoubleBooked, CancelIfWithinWorkingDays = CancelIfWithinWorkingDays,
    };

    /// <summary>One report's handling: the group's, with its [[one_to_one]]'s cancel_if_within_working_days if it sets one.</summary>
    public LeaveHandling HandlingFor(OneToOne report) =>
        report.CancelIfWithinWorkingDays is { } own ? Handling with { CancelIfWithinWorkingDays = own } : Handling;
}

/// <summary>When the other person is double-booked at a 1:1 or catch-up you organise (1.26.0): something busy or out of
/// office of theirs overlapping it (not free, not tentative), or they've declined it, within `double_booked_days` of it.
/// "move" (as a leave move, with `double_booked_note` added to the meeting's description), "ask" (queue it), or
/// "ignore" (the default). An occurrence is moved for a clash at most `max_moves_per_occurrence` times, then asked.</summary>
public sealed record DoubleBookedSettings
{
    public static readonly string[] Choices = ["move", "ask", "ignore"];
    public string When { get; init; } = "ignore";
    public string Note { get; init; } = "";
    public long Days { get; init; } = 7;
    public long MaxMoves { get; init; } = 2;

    /// <summary>double_booked_cancel_note (1.27.0): the note when a clash move is cancelled instead, as it would land too
    /// soon after the occurrence before it (cancel_if_within_working_days).</summary>
    public string CancelNote { get; init; } = DefaultCancelNote;

    public const string DefaultCancelNote = "Cancelling this week as you've got a clash - we'll pick up next week";
}

/// <summary>A [[catch_up_skip]] (1.26.0): a recurring meeting that would be a catch-up but is left alone. Subject (a
/// pattern, case ignored, matched anywhere in the subject) and email (the other person's address): either, or both,
/// when both must match.</summary>
public sealed class CatchUpSkip
{
    public static readonly string[] Keys = ["subject", "email", "why"];
    public string? Subject { get; init; }
    public string? Email { get; init; }
    public string Why { get; init; } = "";
}

/// <summary>How one group of meetings (the 1:1s, the catch-ups, or one [[catch_up]]) is treated when someone's off or
/// double-booked (1.26.0).</summary>
public sealed record LeaveHandling
{
    public bool Act { get; init; }
    public double CancelDays { get; init; } = 3;
    public string WhenShorter { get; init; } = "move";
    public string TheirNote { get; init; } = "Cancelling as you're on leave - enjoy the time off";
    public string MyNote { get; init; } = "Cancelling as I'm on leave";
    public string BankHolidayNote { get; init; } = "Cancelling for the bank holiday";
    public DoubleBookedSettings DoubleBooked { get; init; } = new();

    /// <summary>cancel_if_within_working_days (1.27.0): a move (for leave or a clash) whose new date would be this many
    /// working days or fewer after the series' occurrence before it cancels the occurrence instead. 0: off.</summary>
    public long CancelIfWithinWorkingDays { get; init; }

    /// <summary>The most cancel_if_within_working_days can be: two working weeks (and so how far back a plan reads).</summary>
    public const long MaxWithinWorkingDays = 10;
}

/// <summary>A [[catch_up]] (1.26.0): one catch-up configured on its own, matched by subject (a pattern, case ignored,
/// anywhere in the subject) and/or the other person's email (both, when both are given); the first that matches wins.
/// Each setting it gives wins; each it leaves out comes from [catch_ups].</summary>
public sealed class CatchUpEntry
{
    public static readonly string[] Keys =
    [
        "subject", "email", "why", "act", "cancel_days", "when_shorter", "note", "my_note", "bank_holiday_note",
        "when_double_booked", "double_booked_note", "double_booked_days", "max_moves_per_occurrence",
        "cancel_if_within_working_days", "double_booked_cancel_note",
    ];
    public string? Subject { get; init; }
    public string? Email { get; init; }
    public string Why { get; init; } = "";
    public required LeaveHandling Handling { get; init; }
}

/// <summary>[catch_ups] (1.26.0): recurring meetings you organise with exactly one other person in your organisation
/// (your address's domain) that aren't a [[one_to_one]], cancelled or moved when either of you is off, as the 1:1s
/// are. Their leave comes from their own calendar, through free/busy. Absent: off. `protected` doesn't exclude a
/// meeting here: it only keeps a meeting from being moved or declined to make room for another.</summary>
public sealed class CatchUps
{
    public static readonly string[] Keys =
    [
        "act", "max_actions", "cancel_days", "when_shorter", "days_ahead", "note", "my_note", "bank_holiday_note",
        "when_double_booked", "double_booked_note", "double_booked_days", "max_moves_per_occurrence",
        "cancel_if_within_working_days", "double_booked_cancel_note",
    ];
    public bool Act { get; set; }
    public long MaxActions { get; set; } = 20;
    public double CancelDays { get; set; } = 3;
    public string WhenShorter { get; set; } = "move";
    public long? DaysAhead { get; set; }

    /// <summary>The cancellation note when they're on leave; default [one_to_ones] report_note.</summary>
    public string Note { get; set; } = "Cancelling as you're on leave - enjoy the time off";
    public string MyNote { get; set; } = "Cancelling as I'm on leave";
    public string BankHolidayNote { get; set; } = "Cancelling for the bank holiday";
    public DoubleBookedSettings DoubleBooked { get; set; } = new();

    /// <summary>cancel_if_within_working_days (1.27.0): 0, off.</summary>
    public long CancelIfWithinWorkingDays { get; set; }
    public List<CatchUpSkip> Skip { get; set; } = [];

    /// <summary>The [[catch_up]] entries, in order.</summary>
    public List<CatchUpEntry> Entries { get; set; } = [];

    /// <summary>The group's own handling (no entry).</summary>
    public LeaveHandling Handling => new()
    {
        Act = Act, CancelDays = CancelDays, WhenShorter = WhenShorter, TheirNote = Note, MyNote = MyNote, BankHolidayNote = BankHolidayNote,
        DoubleBooked = DoubleBooked, CancelIfWithinWorkingDays = CancelIfWithinWorkingDays,
    };
}

/// <summary>A standing answer to one kind of double booking (1.13.0): when one meeting matches Keep and the other
/// matches Other (if given) and has exactly one other person in it, from the group OtherWith (if given), the other is
/// moved (one you organise) or declined (one someone else organises), that occurrence only.</summary>
public sealed class DoubleBookingRule
{
    public static readonly string[] Actions = ["move", "decline"];
    public static readonly string[] Keys = ["name", "keep", "other", "other_with", "action", "note", "why"];

    public required string Name { get; init; }

    /// <summary>A pattern for the subject of the meeting that stays (case is ignored).</summary>
    public required string Keep { get; init; }

    /// <summary>A pattern for the subject of the meeting that goes (case is ignored); null: any.</summary>
    public string? Other { get; init; }

    /// <summary>A [groups] name: the meeting that goes has exactly one person in it besides you, from this group.</summary>
    public string? OtherWith { get; init; }

    /// <summary>move or decline.</summary>
    public required string Action { get; init; }

    /// <summary>decline only: the note sent with the response.</summary>
    public string Note { get; init; } = "";
    public string Why { get; init; } = "";
}

/// <summary>[double_bookings] and its [[double_booking_rule]]s (1.13.0). Like [one_to_ones], it changes your calendar,
/// so it starts with act = false, which only records what it would do.</summary>
public sealed class DoubleBookingRules
{
    public static readonly string[] Keys = ["act", "max_actions"];

    public bool Act { get; set; }
    public long MaxActions { get; set; } = 20;
    public List<DoubleBookingRule> Rules { get; set; } = [];
}

/// <summary>[notes] (1.31.0): whether `deleted` quotes the note in an email answered without asking, and the
/// [[note_rule]]s whose notes are raised in the queue too; and (1.34.0) `ignore`, more boilerplate patterns.</summary>
public sealed class NotesSettings
{
    public static readonly string[] Keys = ["quote_in_deleted", "ignore"];

    /// <summary>On unless switched off: each email answered without asking keeps its sender's note in `deleted`.</summary>
    public bool QuoteInDeleted { get; set; } = true;
    public List<NoteRule> Rules { get; } = [];

    /// <summary>`ignore` (1.34.0): the owner's boilerplate patterns (an organisation's disclaimer), added to Notes.DefaultIgnore.</summary>
    public List<string> Ignore { get; } = [];

    /// <summary>Every boilerplate pattern: the built-in ones, then the owner's.</summary>
    public IReadOnlyList<string> AllIgnore => [.. MailTriage.Notes.DefaultIgnore, .. Ignore];

    /// <summary>Whether a run needs to read notes at all: quoting is on, or a rule may raise one.</summary>
    public bool Wanted => QuoteInDeleted || Rules.Count > 0;
}

/// <summary>A meeting whose notes are raised in the queue as well as quoted in `deleted` (1.31.0): every condition given
/// must hold. subject matches the meeting's subject or the email's; organiser and sender match "name address", as
/// [[invite_rule]]'s organiser does. Patterns ignore case.</summary>
public sealed class NoteRule
{
    public static readonly string[] Conditions = ["subject", "organiser", "sender"];
    public static readonly string[] Keys = ["name", .. Conditions, "why"];

    public required string Name { get; init; }
    public string? Subject { get; init; }
    public string? Organiser { get; init; }
    public string? Sender { get; init; }
    public string Why { get; init; } = "";

    public bool Matches(string meetingSubject, string emailSubject, (string Name, string Address) organiser, (string Name, string Address) sender) =>
        (Subject is null || PyRegex.IsMatch(Subject, meetingSubject, true) || PyRegex.IsMatch(Subject, emailSubject, true))
        && (Organiser is null || (organiser.Address.Length > 0 && PyRegex.IsMatch(Organiser, $"{organiser.Name} {organiser.Address}", true)))
        && (Sender is null || PyRegex.IsMatch(Sender, $"{sender.Name} {sender.Address}", true));
}

/// <summary>[send_draft] (1.36.0): what `send-draft` counts as outside, flags and refuses, and how far back `drafts` lists.
/// All optional; read strictly.</summary>
public sealed class SendDraftSettings
{
    public static readonly string[] Keys = ["internal_domains", "warn_recipients", "max_recipients", "list_days", "warn"];

    /// <summary>Square-bracket gaps, "TBC", and en and em dashes: what a draft left for the owner to finish usually has.</summary>
    public static readonly IReadOnlyList<string> DefaultWarn = [@"\[[^\]]*\]", @"\bTBC\b", "[–—]"];

    /// <summary>Lower case; empty means the domain of `email`. A subdomain is outside: only an exact match is internal.</summary>
    public List<string> InternalDomains { get; } = [];
    public long WarnRecipients { get; set; } = 10;
    public long MaxRecipients { get; set; } = 50;
    public long ListDays { get; set; } = 14;

    /// <summary>Python's dialect, ignoring case, checked on what was written (not the quoted thread). Replaces the default.</summary>
    public List<string> Warn { get; set; } = [.. DefaultWarn];
}

/// <summary>The settings file: which folders to triage, and the rules (config.py). Regular expressions are Python's,
/// and only the part of Python's dialect that .NET reads the same way is accepted (PyRegex).</summary>
public sealed class Settings
{
    public static readonly string[] Actions = ["delete", "review", "check", "file", "handle", "forward"];
    private static readonly Dictionary<string, string> Aliases = new() { ["resolve"] = "delete" };   // the first name for delete
    private const string HoursPattern = "([01]\\d|2[0-3]):[0-5]\\d-([01]\\d|2[0-4]):[0-5]\\d";

    public const string Template = """
        # mail-triage settings. See the README.
        # base = "base-config.toml"   # optional: settings shared with other tools
        # login_hint = "you@example.com"

        # The folders to triage. Point Outlook rules at these, and leave your inbox alone.
        # [[folder]]
        # name = "Newsletters"

        # Rules, first match wins. Anything no rule matches is queued for you, never deleted.
        # [[rule]]
        # name = "Vendor newsletters"
        # sender = '@news\.example\.com$'
        # action = "delete"
        # why = "News, not action."

        """;

    public static Func<string, string?> Env { get; set; } = Environment.GetEnvironmentVariable;

    public static string AppData => PyPath.Pure(Env("APPDATA") is { Length: > 0 } a ? a : PyPath.Home(Env));
    public static string Local => PyPath.Pure(System.IO.Path.Combine(Env("LOCALAPPDATA") is { Length: > 0 } l ? l : PyPath.Home(Env), "mail-triage"));
    public static string DefaultPath => System.IO.Path.Combine(AppData, "mail-triage", "config.toml");

    public required string Path { get; init; }
    public List<Folder> Folders { get; } = [];
    public List<Rule> Rules { get; } = [];
    public List<FlowCheck> FlowChecks { get; } = [];
    public Dictionary<string, Handler> Handlers { get; } = new(StringComparer.Ordinal);
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public string QueueFile { get; init; } = System.IO.Path.Combine(Local, "queue.json");
    public string StateFile { get; init; } = System.IO.Path.Combine(Local, "raised.json");
    public string DeletedFile { get; init; } = System.IO.Path.Combine(Local, "deleted.json");
    public string AcceptedFile { get; init; } = System.IO.Path.Combine(Local, "accepted.json");
    /// <summary>Meetings declined for your days off (1.11.0): each occurrence once only.</summary>
    public string DaysOffFile { get; init; } = System.IO.Path.Combine(Local, "days-off.json");
    public string HandledFile { get; init; } = System.IO.Path.Combine(Local, "handled.json");

    /// <summary>What the double-booking rules did, and (act = false) would have done, to each occurrence (1.13.0): so
    /// none is moved or declined twice, and each preview is recorded once.</summary>
    public string DoubleBookingsFile { get; init; } = System.IO.Path.Combine(Local, "double-bookings.json");

    /// <summary>Each email a forward rule has sent on, by internet message ID: "sending" before the POST, "sent" once
    /// it's answered, so a forward whose answer was lost is never sent twice (1.9.0).</summary>
    public string ForwardedFile { get; init; } = System.IO.Path.Combine(Local, "forwarded.json");

    /// <summary>[sent]'s progress through Sent Items (1.19.0): how far it has read, what it has seen lately, and what waits for
    /// its time (a kind set to after_meeting or after_sending).</summary>
    public string SentFile { get; init; } = System.IO.Path.Combine(Local, "sent.json");

    /// <summary>Each invitation `send-invite` has sent (1.24.0), by a hash of its start, end, subject and people: "sending"
    /// before the create, "sent" once Microsoft answers, so the same invitation is never sent twice, and one whose answer
    /// was lost is never resent. Kept until the meeting has ended.</summary>
    public string SentInvitesFile { get; init; } = System.IO.Path.Combine(Local, "sent-invites.json");

    /// <summary>Each calendar write made by hand whose answer was lost (1.30.0): `move`, `leave --decline`, `invite`,
    /// `respond`, `cancel` and `hold`, by what it was and which event. A later preview or --yes for the same event reads it
    /// again: a change that's visibly there clears the record; one that isn't is skipped (never resent) unless --again.
    /// Runtime state: kept until the meeting has ended.</summary>
    public string UnconfirmedFile { get; init; } = System.IO.Path.Combine(Local, "unconfirmed.json");

    /// <summary>The meeting descriptions seen in the invitation emails mail-triage deleted (1.31.0), by meeting, so the next
    /// update's note can say what changed. Runtime state; an entry not seen for a year is forgotten.</summary>
    public string NotesFile { get; init; } = System.IO.Path.Combine(Local, "notes.json");

    /// <summary>What `keep` took off the queue (1.33.0), each item as it was, so `requeue` can put it back. Kept for
    /// KeepDays (60 days).</summary>
    public string KeptFile { get; init; } = System.IO.Path.Combine(Local, "kept.json");

    /// <summary>Each draft `send-draft` has sent (1.36.0), by its short id: "sending" before the send, "sent" once Microsoft
    /// answers, "unconfirmed" when the answer was lost and it couldn't be settled, so none is ever sent twice. Holds the
    /// addresses (never the body); a sent entry is kept KeepDays, the others until settled.</summary>
    public string SentDraftsFile { get; init; } = System.IO.Path.Combine(Local, "sent-drafts.json");

    /// <summary>[send_draft] (1.36.0). Never null.</summary>
    public SendDraftSettings SendDraft { get; set; } = new();

    /// <summary>The shared activity journal (1.33.0, background-tools-lib's ActivityJournal): one JSON line for each thing
    /// mail-triage did, for "while you were away". Null (the default, no journal_file) writes none. Not --journal, which
    /// is where `--writes journal` records the writes it didn't send.</summary>
    public string? JournalFile { get; init; }

    /// <summary>[notes] and [[note_rule]] (1.31.0): the note quoted in `deleted` for an email answered without asking, and
    /// the meetings whose notes are also raised in the queue. Never null: quoting is on unless switched off.</summary>
    public NotesSettings Notes { get; set; } = new();

    /// <summary>[sent] (1.19.0): what's tidied from Sent Items; null when there's no [sent], and nothing is touched there.</summary>
    public SentSettings? Sent { get; set; }

    /// <summary>[hold] rooms (1.23.0): the meeting rooms `hold --room` may book, the allow-list that is the only way a room
    /// gets onto a hold. Empty when there's no [hold]: then a hold books no room.</summary>
    public List<HoldRoom> HoldRooms { get; } = [];
    public string LogFile { get; init; } = System.IO.Path.Combine(Local, "mail-triage.log");
    public string Notify { get; init; } = "new";
    /// <summary>When the watcher triages: [schedule] (background-tools-lib's shared scheduling settings, 1.16.0), a
    /// watcher's every_minutes being how often it polls. Until 1.16.0 these were every_minutes, hours and weekdays at the
    /// top level, still read (the old form; the watcher logs it once at start).</summary>
    public ScheduleSettings Schedule { get; set; } = DefaultSchedule;

    /// <summary>True when the schedule came from the old top-level keys.</summary>
    public bool ScheduleFromOldKeys { get; init; }

    public static readonly ScheduleSettings DefaultSchedule = new()
    {
        EveryMinutes = 15,
        Hours = (new TimeSpan(7, 0, 0), new TimeSpan(19, 0, 0)),
        Days = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
    };

    public long EveryMinutes => Schedule.EveryMinutes ?? 15;
    public (string Start, string End) Hours => Schedule.Hours is var (from, to) ? (Clock(from), Clock(to)) : ("00:00", "24:00");
    /// <summary>Whether the watcher keeps to Monday to Friday (the old weekdays setting).</summary>
    public bool Weekdays => Schedule.Days is { Count: 5 } d && !d.Contains(DayOfWeek.Saturday) && !d.Contains(DayOfWeek.Sunday);

    /// <summary>"every 15 minutes, 07:00-19:00 Mon-Fri", for messages.</summary>
    public string ScheduleText(string unit = "minutes")
    {
        var days = Schedule.Days is null || Schedule.Days.Count == 7 ? ""
            : Weekdays ? " Mon-Fri"
            : " " + string.Join(",", Schedule.Days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3]));
        return $"every {EveryMinutes} {unit}, {Hours.Start}-{Hours.End}{days}";
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 24 ? "24:00" : $"{(int)t.TotalHours:00}:{t.Minutes:00}";
    public string? Email { get; set; }
    public Invites? Invites { get; set; }
    public OneToOnes? OneToOnes { get; set; }

    /// <summary>[catch_ups] and its [[catch_up_skip]]s (1.26.0); null when there's no [catch_ups].</summary>
    public CatchUps? CatchUps { get; set; }

    /// <summary>[leave] (1.17.0): what counts as your leave and your bank holidays, background-tools-lib's shared rules
    /// (CalendarDays.LeaveSettings), the same ones calendar-out-of-office, calendar-work-location and meeting-preps read.</summary>
    public LeaveSettings Leave { get; set; } = new();

    /// <summary>The old settings [leave] was read from (leave_pattern and bank_holiday_categories under [invites] and
    /// [one_to_ones]), if it was; the watcher says so once.</summary>
    public List<string> LeaveOldKeys { get; set; } = [];

    /// <summary>[groups] (1.13.0): named lists of addresses (lower case), for rules to name.</summary>
    public Dictionary<string, List<string>> Groups { get; } = new(StringComparer.Ordinal);

    /// <summary>[double_bookings] and [[double_booking_rule]]s (1.13.0); null when there are none.</summary>
    public DoubleBookingRules? DoubleBookings { get; set; }

    /// <summary>The first rule that matches and applies in this folder. A rule naming folders applies only in them;
    /// a folder with unmatched = "leave" (an inbox) gets only the rules that name it.</summary>
    public Rule? RuleFor(string sender, string text, Folder? folder = null)
    {
        bool Applies(Rule rule)
        {
            if (folder is null)
                return true;
            var named = (rule.Folders ?? []).Select(f => f.ToLowerInvariant()).ToList();
            return named.Count > 0 || folder.Unmatched == "leave" ? named.Contains(folder.Name.ToLowerInvariant()) : true;
        }
        return Rules.FirstOrDefault(rule => Applies(rule) && rule.Matches(sender, text));
    }

    /// <summary>Every top-level setting this file accepts (tables included): the ones a base file can supply.</summary>
    public static readonly string[] KnownKeys =
    [
        "login_hint", "tenant", "msal_cache", "email", "queue_file", "state_file", "deleted_file", "accepted_file", "days_off_file", "handled_file", "forwarded_file",
        "double_bookings_file",
        "log_file", "notify", "every_minutes", "hours", "weekdays", "folder", "handler", "rule", "flow_check", "invites",
        "invite_rule", "movable_block", "one_to_ones", "one_to_one", "groups", "double_bookings", "double_booking_rule", "schedule", "leave",
        "calendar_months_ahead", "sent", "sent_file", "hold", "sent_invites_file", "unconfirmed_file", "catch_ups", "catch_up_skip", "catch_up",
        "notes", "note_rule", "notes_file", "kept_file", "journal_file", "sent_drafts_file", "send_draft",
    ];

    /// <summary>~ and %VARIABLES% expanded, with the variables given (the base's [paths], then the environment).</summary>
    private static string ExpandPath(string value, Func<string, string?>? env = null)
    {
        env ??= Env;
        return PyPath.ExpandVars(PyPath.ExpandUser(value, env), env);
    }

    /// <summary>config._path: ~ and %VARIABLES% expanded; relative to the settings file's folder, resolved.</summary>
    internal static string ToPath(string value, string baseDir, Func<string, string?>? env = null)
    {
        var path = ExpandPath(value, env);
        return PyPath.IsAbsolute(path) ? PyPath.Pure(path) : PyPath.ResolveAgainst(baseDir, path);
    }

    /// <summary>config._pattern: a glob, expanded like a path but not resolved.</summary>
    internal static string ToPattern(string value, string baseDir, Func<string, string?>? env = null)
    {
        var path = ExpandPath(value, env);
        if (PyPath.IsAbsolute(path))
            return PyPath.Pure(path);
        var p = path.Replace('/', '\\');
        if (p.StartsWith('\\'))
            return PyPath.Pure(System.IO.Path.GetPathRoot(baseDir)!.TrimEnd('\\') + p);
        return PyPath.Pure(baseDir + "\\" + path);
    }

    /// <summary>Reads and checks the settings (config.load). Throws FileNotFoundException or SettingsException.</summary>
    public static Settings Load(string? path = null)
    {
        var full = PyPath.Resolve(path is { Length: > 0 } ? path : Env("MAIL_TRIAGE_CONFIG") is { Length: > 0 } e ? e : DefaultPath);
        if (!File.Exists(full))
            throw new FileNotFoundException($"No settings file at {full}. Run `mail-triage config` to create one.");
        TomlTable written;
        var text = File.ReadAllText(full, new UTF8Encoding(false, true));
        try
        {
            written = TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable) ?? [];
        }
        catch (Exception error) when (error is not IOException and not UnauthorizedAccessException)
        {
            throw new SettingsException($"{full} isn't valid TOML: {error.Message}");
        }
        CheckTablesKept(text, written, full);
        // Then read strictly, with the base file it names (if any): the base's [paths] are variables for the paths
        // here, and its other settings this file knows and doesn't set are added.
        SettingsWithBase read;
        try
        {
            read = BaseSettings.Read(text, full, Env, KnownKeys);
        }
        catch (TomlException error)
        {
            throw new SettingsException($"{full} isn't valid TOML: {error.Message}");
        }
        catch (FormatException error)
        {
            throw new SettingsException(error.Message);
        }
        return FromToml(read.Raw, full, read.Env);
    }

    /// <summary>Every [[name]] in the file must have arrived. The TOML reader drops the tables before a break when one
    /// kind is split by another ([[rule]]s, a [[handler]], more [[rule]]s): on 16 Nov 2026 that silently cut a settings
    /// file to its last 10 rules. Missing rules never delete anything, but they do leave mail unhandled.</summary>
    internal static void CheckTablesKept(string text, TomlTable raw, string full)
    {
        var written = System.Text.RegularExpressions.Regex.Matches(text.Replace("\r\n", "\n"), @"^[ \t]*\[\[[ \t]*([A-Za-z0-9_-]+)[ \t]*\]\]",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .GroupBy(m => m.Groups[1].Value);
        foreach (var group in written)
        {
            var read = raw.Get(group.Key) switch { TomlTableArray a => a.Count, TomlArray a => a.Count, _ => 0 };
            if (read != group.Count())
                throw new SettingsException($"{full} has {group.Count()} [[{group.Key}]] tables, but only {read} were read: keep each kind "
                                            + $"together, with no other [[...]] or [...] table between two [[{group.Key}]]s.");
        }
    }

    /// <summary>[hold] (1.23.0): `rooms`, the meeting rooms `hold --room` may book. Each is `{ name = "MTG Room 3.A.1",
    /// address = "MTGRoom3.A.1@example.com" }` (the name is what Outlook shows, and the hold's location), or just the
    /// address (then the name is the address).</summary>
    private static List<HoldRoom> ParseHoldRooms(object value, string full)
    {
        if (value is not TomlTable table)
            throw new SettingsException($"hold in {full} should be a [hold] table.");
        foreach (var key in table.Keys)
            if (key != "rooms")
                throw new SettingsException($"[hold] in {full}: {Py.ReprString(key)} isn't a setting (it takes rooms).");
        if (!table.TryGetValue("rooms", out var list))
            return [];
        IEnumerable<object?> items = list switch
        {
            TomlTableArray tables => tables,
            TomlArray array => array,
            _ => throw new SettingsException($"[hold] rooms in {full} should be a list of rooms."),
        };
        var rooms = new List<HoldRoom>();
        foreach (var item in items)
        {
            var room = item switch
            {
                string address when IsAddress(address) => new HoldRoom(address, address),
                TomlTable t when t.Keys.All(k => k is "name" or "address") && IsAddress(t.Get("address"))
                                 && (t.Get("name") is null || t.Get("name") is string { Length: > 0 }) =>
                    new HoldRoom(t.Get("name") as string ?? (string)t.Get("address")!, (string)t.Get("address")!),
                _ => throw new SettingsException($"[hold] rooms in {full}: each room is an address, or {{ name = \"...\", address = \"...\" }} "
                                                 + $"(got {Py.ReprString(item?.ToString() ?? "nothing")})."),
            };
            if (rooms.Any(r => string.Equals(r.Address, room.Address, StringComparison.OrdinalIgnoreCase)))
                throw new SettingsException($"[hold] rooms in {full}: {room.Address} is listed twice.");
            rooms.Add(room);
        }
        return rooms;
    }

    private static readonly string[] OldScheduleKeys = ["every_minutes", "hours", "weekdays"];

    /// <summary>The watcher's schedule: [schedule] (1.16.0), or the old top-level every_minutes, hours and weekdays
    /// (still read, so a settings file from before keeps working). Both at once is refused.</summary>
    private static (ScheduleSettings Schedule, bool OldKeys) ReadSchedule(TomlTable raw, string full)
    {
        var old = OldScheduleKeys.Where(raw.ContainsKey).ToList();
        if (raw.Get("schedule") is { } table)
        {
            if (old.Count > 0)
                throw new SettingsException($"{full} sets {string.Join(", ", old)} at the top level and has a [schedule]: "
                                            + "keep them in [schedule] only (the top-level keys are the old form).");
            if (table is not TomlTable schedule)
                throw new SettingsException($"schedule in {full} should be a [schedule] table.");
            ScheduleSettings parsed;
            try
            {
                parsed = ScheduleSettings.Parse(schedule, DefaultSchedule, TaskKind.Watcher, $"[schedule] in {full}");
            }
            catch (FormatException error)
            {
                throw new SettingsException(error.Message);
            }
            if (parsed.EveryMinutes is not > 0)
                throw new SettingsException($"[schedule] in {full}: every_minutes (how often the watcher triages) should be at least 1.");
            return (parsed, false);
        }
        if (old.Count == 0)
            return (DefaultSchedule, false);
        var hours = Toml.Str(raw.Get("hours") ?? "07:00-19:00");
        if (!PyRegex.Get(HoursPattern).FullMatch(hours))
            throw new SettingsException($"hours in {full} should look like \"07:00-19:00\".");
        var every = Toml.Int(raw.Get("every_minutes") ?? 15L);
        if (every < 1)
            throw new SettingsException($"every_minutes in {full} should be at least 1.");
        return (DefaultSchedule with
        {
            EveryMinutes = (int)every,
            Hours = ScheduleSettings.ParseHours(hours, full),
            Days = Toml.Truthy(raw.Get("weekdays") ?? true) ? DefaultSchedule.Days : null,
        }, true);
    }

    internal static Settings FromToml(TomlTable raw, string full, Func<string, string?>? env = null)
    {
        var baseDir = System.IO.Path.GetDirectoryName(full)!;
        var (schedule, oldKeys) = ReadSchedule(raw, full);
        var notify = raw.Get("notify") ?? "new";
        if (notify is not ("new" or "errors"))
            throw new SettingsException($"notify in {full} should be \"new\" or \"errors\".");
        string PathOr(string key, string fallback) => Toml.Truthy(raw.Get(key)) ? ToPath(Toml.Str(raw.Get(key)), baseDir, env) : fallback;
        var settings = new Settings
        {
            Path = full,
            LoginHint = raw.Get("login_hint") is { } hint ? Toml.Str(hint) : null,
            Tenant = raw.Get("tenant") is { } tenant ? Toml.Str(tenant) : "organizations",
            MsalCache = Toml.Truthy(raw.Get("msal_cache")) ? ToPath(Toml.Str(raw.Get("msal_cache")), baseDir, env) : null,
            QueueFile = PathOr("queue_file", System.IO.Path.Combine(Local, "queue.json")),
            StateFile = PathOr("state_file", System.IO.Path.Combine(Local, "raised.json")),
            DeletedFile = PathOr("deleted_file", System.IO.Path.Combine(Local, "deleted.json")),
            AcceptedFile = PathOr("accepted_file", System.IO.Path.Combine(Local, "accepted.json")),
            DaysOffFile = PathOr("days_off_file", System.IO.Path.Combine(Local, "days-off.json")),
            HandledFile = PathOr("handled_file", System.IO.Path.Combine(Local, "handled.json")),
            DoubleBookingsFile = PathOr("double_bookings_file", System.IO.Path.Combine(Local, "double-bookings.json")),
            ForwardedFile = PathOr("forwarded_file", System.IO.Path.Combine(Local, "forwarded.json")),
            SentFile = PathOr("sent_file", System.IO.Path.Combine(Local, "sent.json")),
            SentInvitesFile = PathOr("sent_invites_file", System.IO.Path.Combine(Local, "sent-invites.json")),
            UnconfirmedFile = PathOr("unconfirmed_file", System.IO.Path.Combine(Local, "unconfirmed.json")),
            NotesFile = PathOr("notes_file", System.IO.Path.Combine(Local, "notes.json")),
            KeptFile = PathOr("kept_file", System.IO.Path.Combine(Local, "kept.json")),
            SentDraftsFile = PathOr("sent_drafts_file", System.IO.Path.Combine(Local, "sent-drafts.json")),
            JournalFile = Toml.Truthy(raw.Get("journal_file")) ? ToPath(Toml.Str(raw.Get("journal_file")), baseDir, env) : null,
            LogFile = PathOr("log_file", System.IO.Path.Combine(Local, "mail-triage.log")),
            Notify = (string)notify,
            Schedule = schedule,
            ScheduleFromOldKeys = oldKeys,
        };

        foreach (var item in Toml.Tables(raw.Get("folder")))
        {
            if (!Toml.Truthy(item.Get("name")))
                throw new SettingsException($"A [[folder]] in {full} has no name.");
            var name = Toml.Str(item.Get("name"));
            var since = item.Get("since");
            if (since is not null && !PyRegex.Get(@"\d{4}-\d{2}-\d{2}").FullMatch(Toml.Str(since)))
                throw new SettingsException($"since for folder {name} in {full} should be a date such as \"2026-09-17\".");
            var unmatched = item.Get("unmatched") ?? "review";
            if (unmatched is not ("review" or "leave"))
                throw new SettingsException($"unmatched for folder {name} in {full} should be \"review\" or \"leave\".");
            settings.Folders.Add(new Folder(name, Toml.Truthy(since) ? Toml.Str(since!) : null, (string)unmatched));
        }

        foreach (var item in Toml.Tables(raw.Get("handler")))
        {
            if (!Toml.Truthy(item.Get("name")))
                throw new SettingsException($"A [[handler]] in {full} has no name.");
            var name = Toml.Str(item.Get("name"));
            var command = item.Get("command") is { } c ? Toml.Strings(c) : [];
            var queue = item.Get("queue") is { } q ? Toml.Strings(q) : null;
            if (queue is not null && command.Count > 0)
                throw new SettingsException($"Handler {Py.ReprString(name)} in {full} has command and queue: give one (queue puts the command on the job queue).");
            if (queue is not null && (queue.Count == 0 || queue[0].Length == 0))
                throw new SettingsException($"Handler {Py.ReprString(name)} in {full} has an empty queue: give the command to queue, with {{saved}} for the saved email.");
            if (queue is null && (command.Count == 0 || command[0].Length == 0))
                throw new SettingsException($"Handler {Py.ReprString(name)} in {full} needs command, a list such as ['python', 'handler.py'] (or queue, for a job).");
            if (queue is null && Toml.Truthy(item.Get("save_to")))
                throw new SettingsException($"Handler {Py.ReprString(name)} in {full} has save_to without queue: save_to is for a queue handler.");
            var then = item.Get("then") is { } t ? Toml.Str(t) : "delete";
            if (!Handler.Thens.Contains(then))
                throw new SettingsException($"Handler {Py.ReprString(name)} in {full} has then = {Py.ReprString(then)}: use delete, file, leave or review.");
            if (then == "file" && !Toml.Truthy(item.Get("to")))
                throw new SettingsException($"Handler {Py.ReprString(name)} in {full} has then = file but no `to` folder.");
            var timeout = Toml.Int(item.Get("timeout") ?? 120L);
            var attempts = Toml.Int(item.Get("max_attempts") ?? 4L);
            if (timeout < 1 || attempts < 1)
                throw new SettingsException($"Handler {Py.ReprString(name)} in {full}: timeout and max_attempts should be at least 1.");
            if (!settings.Handlers.TryAdd(name, new Handler
                {
                    Name = name, Command = queue ?? command, Queue = queue, Then = then, To = item.Get("to") is { } to ? Toml.Str(to) : null,
                    SaveTo = queue is null ? null : Toml.Truthy(item.Get("save_to")) ? ToPath(Toml.Str(item.Get("save_to")), baseDir, env)
                        : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(PyPath.Resolve(settings.HandledFile))!,
                            "handler-saved", string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'))),
                    Cwd = Toml.Truthy(item.Get("cwd")) ? ToPath(Toml.Str(item.Get("cwd")), baseDir, env) : baseDir,
                    Timeout = timeout, MaxAttempts = attempts, Attachments = Toml.Truthy(item.Get("attachments")),
                }))
                throw new SettingsException($"Two handlers in {full} are called {Py.ReprString(name)}.");
        }

        var i = 0;
        foreach (var item in Toml.Tables(raw.Get("rule")))
        {
            i++;
            var name = item.Get("name") is { } n ? Toml.Str(n) : $"rule {i}";
            if (!Toml.Truthy(item.Get("sender")))
                throw new SettingsException($"Rule {Py.ReprString(name)} in {full} has no sender.");
            var given = item.Get("action") is { } a ? Toml.Str(a) : "";
            var action = Aliases.GetValueOrDefault(given, given);
            if (!Actions.Contains(action))
                throw new SettingsException($"Rule {Py.ReprString(name)} in {full} has action {Toml.Repr(item.Get("action"))}: use delete, review, check, file, handle or forward.");
            if (action == "file" && !Toml.Truthy(item.Get("to")))
                throw new SettingsException($"Rule {Py.ReprString(name)} in {full} has action file but no `to` folder.");
            if (action == "forward")
            {
                if (item.Get("forward_to") is not TomlArray list || list.Count == 0
                    || list.Any(x => x is not string address || !PyRegex.Get(@"^[^@\s]+@[^@\s]+\.[^@\s]+$").FullMatch(address)))
                    throw new SettingsException($"Rule {Py.ReprString(name)} in {full} has action forward: forward_to should be a list of email addresses.");
                if (!(item.Get("comment") is string text && PyStr.Strip(text).Length > 0))
                    throw new SettingsException($"Rule {Py.ReprString(name)} in {full} has action forward but no `comment` (the covering message).");
                if (item.Get("mark_read") is { } mr && mr is not bool)
                    throw new SettingsException($"Rule {Py.ReprString(name)} in {full}: mark_read should be true or false.");
                if (item.Get("file_sent") is { } fs && !(fs is string folderPath && PyStr.Strip(folderPath).Length > 0))
                    throw new SettingsException($"Rule {Py.ReprString(name)} in {full}: file_sent should be a folder path, such as 'Inbox/Suppliers'.");
            }
            if (action != "forward" && item.Get("file_sent") is not null)
                throw new SettingsException($"Rule {Py.ReprString(name)} in {full}: file_sent is only for action forward (where the Sent Items copy of each forward goes).");
            if (action == "check" && !(item.Get("check") is string c && Checks.All.ContainsKey(c)))
                throw new SettingsException($"Rule {Py.ReprString(name)} in {full} needs check = one of {string.Join(", ", Checks.All.Keys.Order(PyStr.Order))}.");
            if (action == "handle" && !(item.Get("handler") is string hn && settings.Handlers.ContainsKey(hn)))
                throw new SettingsException($"Rule {Py.ReprString(name)} in {full} needs handler = the name of a [[handler]]"
                                            + (settings.Handlers.Count > 0 ? $" (one of {string.Join(", ", settings.Handlers.Keys.Order(PyStr.Order))})." : ", and there are none."));
            foreach (var key in new[] { "sender", "match" })
                if (Toml.Truthy(item.Get(key)) && PyRegex.Problem(Toml.Str(item.Get(key))) is { } problem)
                    throw new SettingsException($"Rule {Py.ReprString(name)} in {full}: {key} isn't a valid regular expression ({problem}).");
            settings.Rules.Add(new Rule
            {
                Name = name,
                Sender = Toml.Str(item.Get("sender")),
                Action = action,
                Match = item.Get("match") is { } m ? Toml.Str(m) : null,
                Check = item.Get("check") is { } ch ? Toml.Str(ch) : null,
                Why = item.Get("why") is { } w ? Toml.Str(w) : "",
                To = item.Get("to") is { } to ? Toml.Str(to) : null,
                Handler = action == "handle" ? Toml.Str(item.Get("handler")) : null,
                Folders = Toml.Truthy(item.Get("folders")) ? Toml.Strings(item.Get("folders")) : null,
                ForwardTo = action == "forward" ? Toml.Strings(item.Get("forward_to")) : null,
                Comment = action == "forward" ? Toml.Str(item.Get("comment")) : null,
                MarkRead = action == "forward" && (item.Get("mark_read") is not bool read || read),
                FileSent = action == "forward" && item.Get("file_sent") is string fsPath ? fsPath : null,
            });
        }

        foreach (var item in Toml.Tables(raw.Get("flow_check")))
        {
            if (!Toml.Truthy(item.Get("flow")) || !Toml.Truthy(item.Get("output")))
                throw new SettingsException($"A [[flow_check]] in {full} needs flow and output.");
            var output = ToPattern(Toml.Str(item.Get("output")), baseDir, env);
            var pending = Toml.Truthy(item.Get("pending")) ? ToPattern(Toml.Str(item.Get("pending")), baseDir, env) : null;
            foreach (var pattern in new[] { output, pending })
                if (pattern is not null && System.IO.Path.GetDirectoryName(pattern) is { } dir && (dir.Contains('*') || dir.Contains('?') || dir.Contains('[')))
                    throw new SettingsException($"A [[flow_check]] in {full} has wildcards in a folder name ({pattern}): only the file name may have them.");
            settings.FlowChecks.Add(new FlowCheck(Toml.Str(item.Get("flow")), output, pending));
        }

        var email = Toml.Truthy(raw.Get("email")) ? Toml.Str(raw.Get("email")) : Toml.Truthy(raw.Get("login_hint")) ? Toml.Str(raw.Get("login_hint")) : "";
        settings.Email = email.Length > 0 ? email.ToLowerInvariant() : null;
        if (raw.Get("sent") is { } sentValue)
            settings.Sent = SentSettings.Parse(sentValue, full);
        if (raw.Get("hold") is { } holdValue)
            settings.HoldRooms.AddRange(ParseHoldRooms(holdValue, full));
        if (raw.Get("invites") is { } invitesValue)
        {
            var section = invitesValue as TomlTable ?? throw new SettingsException($"[invites] in {full} should be a table.");
            if (settings.Email is null)
                throw new SettingsException($"[invites] in {full} needs email (or login_hint) set, to tell you from other attendees.");
            var rules = new List<InviteRule>();
            var r = 0;
            foreach (var item in Toml.Tables(raw.Get("invite_rule")))
            {
                r++;
                var name = item.Get("name") is { } n ? Toml.Str(n) : $"invite rule {r}";
                var action = item.Get("action") ?? "skip";
                if (action is not ("skip" or "review"))
                    throw new SettingsException($"Invite rule {Py.ReprString(name)} in {full}: action is skip or review (nothing acts on its own yet).");
                foreach (var key in new[] { "subject", "organiser" })
                    if (Toml.Truthy(item.Get(key)) && PyRegex.Problem(Toml.Str(item.Get(key))) is { } problem)
                        throw new SettingsException($"Invite rule {Py.ReprString(name)} in {full}: {key} isn't a valid regular expression ({problem}).");
                rules.Add(new InviteRule
                {
                    Name = name,
                    Action = (string)action,
                    Subject = item.Get("subject") is { } s ? Toml.Str(s) : null,
                    Organiser = item.Get("organiser") is { } o ? Toml.Str(o) : null,
                    AllDay = item.Get("all_day") is { } ad ? Toml.Truthy(ad) : null,
                    ShowAs = item.Get("show_as") is { } sa ? Toml.Strings(sa) : null,
                    ViaGroup = item.Get("via_group") is { } vg ? Toml.Truthy(vg) : null,
                    Optional = item.Get("optional") is { } op ? Toml.Truthy(op) : null,
                    MinPeople = item.Get("min_people") is { } mp ? Toml.Int(mp) : null,
                    Why = item.Get("why") is { } w ? Toml.Str(w) : "",
                });
            }
            var movable = new List<MovableBlock>();
            foreach (var item in Toml.Tables(raw.Get("movable_block")))
            {
                var within = item.Get("within");
                if (!Toml.Truthy(item.Get("subject")) || within is not (null or "day"))
                    throw new SettingsException($"A [[movable_block]] in {full} needs subject, and within (if given) is \"day\".");
                movable.Add(new MovableBlock(Toml.Str(item.Get("subject")), (string?)within, item.Get("before") is { } b ? Toml.Str(b) : null));
            }
            (string, string) Span(string name, (string, string) fallback)
            {
                var value = Toml.Str(section.Get(name) ?? $"{fallback.Item1}-{fallback.Item2}");
                if (!PyRegex.Get(HoursPattern).FullMatch(value))
                    throw new SettingsException($"{name} in [invites] in {full} should look like \"08:00-18:00\".");
                var parts = value.Split('-');
                return (parts[0], parts[1]);
            }
            var invites = new Invites
            {
                DaysAhead = Toml.Int(section.Get("days_ahead") ?? 42L),
                DaysAheadSet = section.Get("days_ahead") is not null,
                CalendarMonthsAhead = raw.Get("calendar_months_ahead") is { } cma ? (int)Toml.Int(cma) : null,
                MonthsAhead = section.Get("months_ahead") is { } ma ? (int)Toml.Int(ma) : null,
                AutoAccept = Toml.Truthy(section.Get("auto_accept") ?? false),
                AutoAcceptOutside = Toml.Truthy(section.Get("auto_accept_outside") ?? false),
                AutoAcceptOptional = Toml.Truthy(section.Get("auto_accept_optional") ?? false),
                AutoMoveBlocks = Toml.Truthy(section.Get("auto_move_blocks") ?? false),
                DeclineDaysOff = Toml.Truthy(section.Get("decline_days_off") ?? false),
                CancelDaysOff = Toml.Truthy(section.Get("cancel_days_off") ?? false),
                RemoveBlocksDaysOff = Toml.Truthy(section.Get("remove_blocks_days_off") ?? false),
                RaiseGroupDaysOff = Toml.Truthy(section.Get("raise_group_days_off") ?? false),
                MaxDeclines = section.Get("max_declines") is { } md ? (int)Toml.Int(md) : 20,
                DoubleBookings = Toml.Truthy(section.Get("double_bookings") ?? false),
                FixedBlocks = Toml.Strings(section.Get("fixed_blocks")),
                CommuteBlocks = Toml.Strings(section.Get("commute_blocks")),
                Movable = movable,
                Protected = Toml.Strings(section.Get("protected")),
                LeavePattern = section.Get("leave_pattern") is { } lp ? Toml.Str(lp) : Invites.DefaultLeavePattern,
                LeaveNote = section.Get("leave_note") is { } ln ? Toml.Str(ln) : "On annual leave",
                BankHolidayNote = section.Get("bank_holiday_note") is { } bn ? Toml.Str(bn) : "Bank holiday",
                BankHolidayCategories = section.Get("bank_holiday_categories") is { } bc ? Toml.Strings(bc) : null,
                DateFormat = section.Get("date_format") is { } df ? Toml.Str(df) : "%d/%m",
                WorkingHours = Span("working_hours", ("08:00", "18:00")),
                TravelHours = Span("travel_hours", ("06:00", "21:00")),
                RequestsFolders = Toml.Strings(section.Get("requests_folders")),
                AcceptShortened = Toml.Truthy(section.Get("accept_shortened") ?? false),
                ProposalsFolders = Toml.Strings(section.Get("proposals_folders")),
                AcceptProposals = Toml.Truthy(section.Get("accept_proposals") ?? false),
                Rules = rules,
            };
            // Checked here so a pattern .NET would read differently is refused at load, not mid-run (the guard).
            if (!PyTime.Supported(invites.DateFormat))
                throw new SettingsException($"date_format in [invites] in {full} uses a directive this version doesn't support (only %{string.Join(" %", PyTime.Directives.ToCharArray())}).");
            foreach (var (what, pattern) in invites.FixedBlocks.Select(p => ("fixed_blocks", p)).Concat(invites.CommuteBlocks.Select(p => ("commute_blocks", p)))
                         .Concat(invites.Protected.Select(p => ("protected", p))).Concat([("leave_pattern", invites.LeavePattern)])
                         .Concat(movable.Select(m => ("movable_block subject", m.Subject)))
                         .Concat(movable.Where(m => m.Before is not null).Select(m => ("movable_block before", m.Before!))))
                if (PyRegex.Problem(pattern) is { } problem)
                    throw new SettingsException($"{what} in {full} isn't a valid regular expression ({problem}).");
            settings.Invites = invites;
        }
        if (raw.Get("one_to_ones") is { } oneValue)
        {
            var section = oneValue as TomlTable ?? throw new SettingsException($"[one_to_ones] in {full} should be a table.");
            if (settings.Invites is null)
                throw new SettingsException($"[one_to_ones] in {full} needs an [invites] section (your email, working_hours, leave_pattern).");
            var midday = Toml.Str(section.Get("midday") ?? "13:00");
            if (!PyRegex.Get("([01]\\d|2[0-3]):[0-5]\\d").FullMatch(midday))
                throw new SettingsException($"midday in [one_to_ones] in {full} should look like \"13:00\".");
            var form = section.Get("subject_format") is { } sf ? Toml.Str(sf) : "Weekly 1-2-1 - {first}";
            var reports = new List<OneToOne>();
            foreach (var item in Toml.Tables(raw.Get("one_to_one")))
            {
                if (!Toml.Truthy(item.Get("name")) || !Toml.Truthy(item.Get("email")))
                    throw new SettingsException($"A [[one_to_one]] in {full} needs name and email.");
                var name = Toml.Str(item.Get("name"));
                var subject = Toml.Truthy(item.Get("subject")) ? Toml.Str(item.Get("subject"))
                    : PyFormat.Named(form, new Dictionary<string, string> { ["first"] = PyStr.Split(name)[0], ["name"] = name });
                reports.Add(new OneToOne(name, Toml.Str(item.Get("email")).ToLowerInvariant(), subject)
                {
                    CancelIfWithinWorkingDays = ReadWithin(item, $"the [[one_to_one]] for {name}", full),
                });
            }
            var pattern = section.Get("leave_pattern") is { } lp ? Toml.Str(lp) : OneToOnes.DefaultLeavePattern;
            if (!pattern.Contains("{name}"))
                throw new SettingsException($"leave_pattern in [one_to_ones] in {full} needs {{name}}, where the person's name goes.");
            foreach (var report in reports)
                if (PyRegex.Problem(pattern.Replace("{name}", PyRegex.Escape(report.Name))) is { } problem)
                    throw new SettingsException($"leave_pattern in [one_to_ones] in {full} isn't a valid regular expression ({problem}).");
            settings.OneToOnes = new OneToOnes
            {
                Reports = reports,
                Act = Toml.Truthy(section.Get("act") ?? false),
                MaxActions = Toml.Int(section.Get("max_actions") ?? 20L),
                CancelDays = Toml.Float(section.Get("cancel_days") ?? 3L),
                Midday = midday,
                DaysAhead = Toml.Truthy(section.Get("days_ahead")) ? Toml.Int(section.Get("days_ahead")!) : null,
                BankHolidayCategories = section.Get("bank_holiday_categories") is { } bh ? Toml.Strings(bh) : ["Holiday"],
                LeavePattern = pattern,
                ReportNote = section.Get("report_note") is { } rn ? Toml.Str(rn) : "Cancelling as you're on leave - enjoy the time off",
                MyNote = section.Get("my_note") is { } mn ? Toml.Str(mn) : "Cancelling as I'm on leave",
                BankHolidayNote = section.Get("bank_holiday_note") is { } bn ? Toml.Str(bn) : "Cancelling for the bank holiday",
                WhenShorter = WhenShorter(section, "one_to_ones", full),
                DoubleBooked = ReadDoubleBooked(section, "[one_to_ones]", full, new DoubleBookedSettings()),
                CancelIfWithinWorkingDays = ReadWithin(section, "[one_to_ones]", full) ?? 0,
            };
        }
        ReadCatchUps(raw, full, settings);
        ReadLeave(raw, full, settings);
        ReadGroups(raw, full, settings);
        ReadDoubleBookings(raw, full, settings);
        ReadNotes(raw, full, settings);
        ReadSendDraft(raw, full, settings);
        if (settings.Folders.Count == 0 && settings.Invites is null)
            throw new SettingsException($"{full} names no [[folder]] to triage, and no [invites].");
        return settings;
    }

    /// <summary>[leave] (1.17.0): the shared leave rules (CalendarDays.LeaveSettings). The old settings still read, as
    /// aliases feeding the same rules: [invites] leave_pattern (your leave's subjects) and bank_holiday_categories under
    /// [invites] and [one_to_ones]. Both forms at once is an error, and so are the two old category lists disagreeing.
    /// [one_to_ones] leave_pattern isn't one of these: it's how your reports' leave entries are named.</summary>
    private static void ReadLeave(TomlTable raw, string full, Settings settings)
    {
        var invites = raw.Get("invites") as TomlTable;
        var one = raw.Get("one_to_ones") as TomlTable;
        var old = new List<string>();
        if (invites?.ContainsKey("leave_pattern") == true)
            old.Add("[invites] leave_pattern");
        if (invites?.ContainsKey("bank_holiday_categories") == true)
            old.Add("[invites] bank_holiday_categories");
        if (one?.ContainsKey("bank_holiday_categories") == true)
            old.Add("[one_to_ones] bank_holiday_categories");
        if (raw.Get("leave") is { } leaveValue)
        {
            var table = leaveValue as TomlTable ?? throw new SettingsException($"[leave] in {full} should be a table.");
            if (old.Count > 0)
                throw new SettingsException($"{full} has both [leave] and the old {string.Join(", ", old)}: keep [leave] and delete the old.");
            settings.Leave = ParseLeave(table, full);
            return;
        }
        if (old.Count == 0)
        {
            // Neither form: mail-triage's own defaults until 1.17.0 (its leave_pattern, and the "Holiday" category), under
            // the shared rules. A file with [leave] (even empty, filled from a base) takes the shared defaults instead.
            settings.Leave = ParseLeave(new Dictionary<string, object>
            {
                ["subjects"] = Invites.DefaultLeavePattern, ["bank_holiday_categories"] = new List<object> { "Holiday" },
            }, full);
            return;
        }
        var fromInvites = settings.Invites?.BankHolidayCategories;
        var fromOne = one?.ContainsKey("bank_holiday_categories") == true ? settings.OneToOnes?.BankHolidayCategories : null;
        if (fromInvites is not null && fromOne is not null
            && !fromInvites.Select(c => c.ToLowerInvariant()).Order().SequenceEqual(fromOne.Select(c => c.ToLowerInvariant()).Order()))
            throw new SettingsException($"bank_holiday_categories in [invites] and [one_to_ones] in {full} differ: move one list to [leave].");
        var aliases = new Dictionary<string, object>(StringComparer.Ordinal);
        if (invites?.ContainsKey("leave_pattern") == true)
            aliases["subjects"] = settings.Invites!.LeavePattern;
        if ((fromInvites ?? fromOne) is { } categories)
            aliases["bank_holiday_categories"] = categories.Cast<object>().ToList();
        settings.Leave = ParseLeave(aliases, full);
        settings.LeaveOldKeys = old;
    }

    private static LeaveSettings ParseLeave(IDictionary<string, object> table, string full)
    {
        try
        {
            return LeaveSettings.Parse(table, full);
        }
        catch (FormatException error)
        {
            throw new SettingsException(error.Message);
        }
    }

    internal static bool IsAddress(object? value) => value is string s && PyRegex.Get(@"^[^@\s]+@[^@\s]+\.[^@\s]+$").FullMatch(s);

    /// <summary>[groups] (1.13.0): each a list of addresses, kept in lower case.</summary>
    private static void ReadGroups(TomlTable raw, string full, Settings settings)
    {
        if (raw.Get("groups") is not { } value)
            return;
        var table = value as TomlTable ?? throw new SettingsException($"[groups] in {full} should be a table: name = ['someone@example.com', ...].");
        foreach (var (name, list) in table)
        {
            if (list is not TomlArray addresses || addresses.Count == 0 || !addresses.All(IsAddress))
                throw new SettingsException($"Group {Py.ReprString(name)} in [groups] in {full} should be a list of email addresses.");
            settings.Groups[name] = addresses.Cast<string>().Select(a => a.ToLowerInvariant()).ToList();
        }
    }

    /// <summary>when_shorter: "move" (the default) or "ask".</summary>
    private static string? ReadWhenShorter(TomlTable section, string where, string full)
    {
        if (section.Get("when_shorter") is not { } value)
            return null;
        return value is string s && OneToOnes.WhenShorterChoices.Contains(s)
            ? s
            : throw new SettingsException($"when_shorter in {where} in {full} should be \"move\" or \"ask\" (it's {Toml.Repr(value)}).");
    }

    private static string WhenShorter(TomlTable section, string name, string full) => ReadWhenShorter(section, $"[{name}]", full) ?? "move";

    /// <summary>when_double_booked, double_booked_note, double_booked_days and max_moves_per_occurrence (1.26.0), each
    /// falling back to <paramref name="fallback"/>.</summary>
    private static DoubleBookedSettings ReadDoubleBooked(TomlTable section, string where, string full, DoubleBookedSettings fallback)
    {
        var when = fallback.When;
        if (section.Get("when_double_booked") is { } w)
            when = w is string s && DoubleBookedSettings.Choices.Contains(s)
                ? s
                : throw new SettingsException($"when_double_booked in {where} in {full} should be \"move\", \"ask\" or \"ignore\" (it's {Toml.Repr(w)}).");
        var note = fallback.Note;
        if (section.Get("double_booked_note") is { } n)
            note = n as string ?? throw new SettingsException($"double_booked_note in {where} in {full} should be text.");
        var cancelNote = fallback.CancelNote;
        if (section.Get("double_booked_cancel_note") is { } cn)
            cancelNote = cn as string ?? throw new SettingsException($"double_booked_cancel_note in {where} in {full} should be text.");
        long Whole(string key, long current, long least)
        {
            if (section.Get(key) is not { } v)
                return current;
            return v is long l && l >= least ? l : throw new SettingsException($"{key} in {where} in {full} should be a whole number, at least {least}.");
        }
        return new DoubleBookedSettings
        {
            When = when, Note = note, Days = Whole("double_booked_days", fallback.Days, 0), MaxMoves = Whole("max_moves_per_occurrence", fallback.MaxMoves, 0),
            CancelNote = cancelNote,
        };
    }

    /// <summary>cancel_if_within_working_days (1.27.0): a whole number from 0 (off) to LeaveHandling.MaxWithinWorkingDays;
    /// null when not set.</summary>
    private static long? ReadWithin(TomlTable section, string where, string full)
    {
        if (section.Get("cancel_if_within_working_days") is not { } v)
            return null;
        return v is long l and >= 0 and <= LeaveHandling.MaxWithinWorkingDays
            ? l
            : throw new SettingsException($"cancel_if_within_working_days in {where} in {full} should be a whole number from 0 (off) to "
                                          + $"{LeaveHandling.MaxWithinWorkingDays} (it's {Toml.Repr(v)}).");
    }

    /// <summary>The handling keys of [catch_ups] or a [[catch_up]], each falling back to <paramref name="fallback"/>.</summary>
    private static LeaveHandling ReadHandling(TomlTable section, string where, string full, LeaveHandling fallback)
    {
        if (section.Get("act") is { } act && act is not bool)
            throw new SettingsException($"act in {where} in {full} should be true or false.");
        var cancelDays = section.Get("cancel_days") is { } cd ? Toml.Float(cd) : fallback.CancelDays;
        if (cancelDays is <= 0 or > 5)
            throw new SettingsException($"cancel_days in {where} in {full} should be more than 0 and at most 5 (days of a Monday-to-Friday week).");
        string Note(string key, string current) => section.Get(key) is { } v
            ? v as string ?? throw new SettingsException($"{key} in {where} in {full} should be text.")
            : current;
        return new LeaveHandling
        {
            Act = section.Get("act") is bool a ? a : fallback.Act,
            CancelDays = cancelDays,
            WhenShorter = ReadWhenShorter(section, where, full) ?? fallback.WhenShorter,
            TheirNote = Note("note", fallback.TheirNote),
            MyNote = Note("my_note", fallback.MyNote),
            BankHolidayNote = Note("bank_holiday_note", fallback.BankHolidayNote),
            DoubleBooked = ReadDoubleBooked(section, where, full, fallback.DoubleBooked),
            CancelIfWithinWorkingDays = ReadWithin(section, where, full) ?? fallback.CancelIfWithinWorkingDays,
        };
    }

    /// <summary>A subject pattern and/or an email, as [[catch_up_skip]] and [[catch_up]] match a meeting.</summary>
    private static (string? Subject, string? Email) ReadMatch(TomlTable item, Func<string, string> problem)
    {
        var subject = item.Get("subject");
        var email = item.Get("email");
        if (subject is null && email is null)
            throw new SettingsException(problem("it needs subject (a pattern), email, or both."));
        if (subject is not null && (subject is not string text || text.Length == 0))
            throw new SettingsException(problem("subject should be a regular expression."));
        if (subject is string pattern && PyRegex.Problem(pattern) is { } why)
            throw new SettingsException(problem($"subject isn't a valid regular expression ({why})."));
        if (email is not null && !IsAddress(email))
            throw new SettingsException(problem($"email should be an address (it's {Toml.Repr(email)})."));
        return (subject as string, (email as string)?.ToLowerInvariant());
    }

    /// <summary>[catch_ups], [[catch_up_skip]] and [[catch_up]] (1.26.0). Needs [invites] (your email, working_hours); the
    /// notes default to [one_to_ones]'. A [[catch_up]]'s settings win; what it leaves out comes from [catch_ups].</summary>
    private static void ReadCatchUps(TomlTable raw, string full, Settings settings)
    {
        var sectionValue = raw.Get("catch_ups");
        var skips = Toml.Tables(raw.Get("catch_up_skip")).ToList();
        var entries = Toml.Tables(raw.Get("catch_up")).ToList();
        if (sectionValue is null)
        {
            if (skips.Count > 0 || entries.Count > 0)
                throw new SettingsException($"[[catch_up_skip]] and [[catch_up]] in {full} need a [catch_ups] section.");
            return;
        }
        var section = sectionValue as TomlTable ?? throw new SettingsException($"[catch_ups] in {full} should be a table.");
        if (settings.Invites is null || settings.Email is null)
            throw new SettingsException($"[catch_ups] in {full} needs an [invites] section and your email.");
        foreach (var key in section.Keys)
            if (!CatchUps.Keys.Contains(key))
                throw new SettingsException($"[catch_ups] in {full} has {Py.ReprString(key)}: it takes {string.Join(", ", CatchUps.Keys)}.");
        var max = Toml.Int(section.Get("max_actions") ?? 20L);
        if (max < 1)
            throw new SettingsException($"max_actions in [catch_ups] in {full} should be at least 1 (act = false stops it acting).");
        var one = settings.OneToOnes;
        var group = ReadHandling(section, "[catch_ups]", full, new LeaveHandling
        {
            TheirNote = one?.ReportNote ?? "Cancelling as you're on leave - enjoy the time off",
            MyNote = one?.MyNote ?? "Cancelling as I'm on leave",
            BankHolidayNote = one?.BankHolidayNote ?? "Cancelling for the bank holiday",
        });
        var skipList = new List<CatchUpSkip>();
        var i = 0;
        foreach (var item in skips)
        {
            i++;
            var n = i;
            string Problem(string what) => $"[[catch_up_skip]] {n} in {full}: {what}";
            foreach (var key in item.Keys)
                if (!CatchUpSkip.Keys.Contains(key))
                    throw new SettingsException(Problem($"{Py.ReprString(key)} isn't a setting (it takes {string.Join(", ", CatchUpSkip.Keys)})."));
            var (subject, email) = ReadMatch(item, Problem);
            skipList.Add(new CatchUpSkip { Subject = subject, Email = email, Why = item.Get("why") is { } w ? Toml.Str(w) : "" });
        }
        var entryList = new List<CatchUpEntry>();
        i = 0;
        foreach (var item in entries)
        {
            i++;
            var n = i;
            string Problem(string what) => $"[[catch_up]] {n} in {full}: {what}";
            foreach (var key in item.Keys)
                if (!CatchUpEntry.Keys.Contains(key))
                    throw new SettingsException(Problem($"{Py.ReprString(key)} isn't a setting (it takes {string.Join(", ", CatchUpEntry.Keys)})."));
            var (subject, email) = ReadMatch(item, Problem);
            entryList.Add(new CatchUpEntry
            {
                Subject = subject, Email = email, Why = item.Get("why") is { } w ? Toml.Str(w) : "",
                Handling = ReadHandling(item, $"[[catch_up]] {n}", full, group),
            });
        }
        settings.CatchUps = new CatchUps
        {
            Act = group.Act,
            MaxActions = max,
            CancelDays = group.CancelDays,
            WhenShorter = group.WhenShorter,
            DaysAhead = Toml.Truthy(section.Get("days_ahead")) ? Toml.Int(section.Get("days_ahead")!) : null,
            Note = group.TheirNote,
            MyNote = group.MyNote,
            BankHolidayNote = group.BankHolidayNote,
            DoubleBooked = group.DoubleBooked,
            CancelIfWithinWorkingDays = group.CancelIfWithinWorkingDays,
            Skip = skipList,
            Entries = entryList,
        };
    }

    /// <summary>[double_bookings] and [[double_booking_rule]] (1.13.0). Read strictly, unknown keys included: a misspelt
    /// condition would otherwise be dropped, and the rule would act on more than it says.</summary>
    private static void ReadDoubleBookings(TomlTable raw, string full, Settings settings)
    {
        var sectionValue = raw.Get("double_bookings");
        var tables = Toml.Tables(raw.Get("double_booking_rule")).ToList();
        if (sectionValue is null && tables.Count == 0)
            return;
        var section = sectionValue is null ? new TomlTable() : sectionValue as TomlTable ?? throw new SettingsException($"[double_bookings] in {full} should be a table.");
        if (settings.Invites is not { DoubleBookings: true })
            throw new SettingsException($"[double_bookings] and [[double_booking_rule]] in {full} need [invites] with double_bookings = true: "
                                        + "a rule settles a double booking that would otherwise be queued.");
        foreach (var key in section.Keys)
            if (!DoubleBookingRules.Keys.Contains(key))
                throw new SettingsException($"[double_bookings] in {full} has {Py.ReprString(key)}: it takes {string.Join(", ", DoubleBookingRules.Keys)}.");
        if (section.Get("act") is { } act && act is not bool)
            throw new SettingsException($"act in [double_bookings] in {full} should be true or false.");
        var max = Toml.Int(section.Get("max_actions") ?? 20L);
        if (max < 1)
            throw new SettingsException($"max_actions in [double_bookings] in {full} should be at least 1 (act = false stops it acting).");
        var config = new DoubleBookingRules { Act = section.Get("act") is true, MaxActions = max };
        var i = 0;
        foreach (var item in tables)
        {
            i++;
            var name = item.Get("name") is { } n ? Toml.Str(n) : $"double booking rule {i}";
            string Problem(string what) => $"Double booking rule {Py.ReprString(name)} in {full}: {what}";
            foreach (var key in item.Keys)
                if (!DoubleBookingRule.Keys.Contains(key))
                    throw new SettingsException(Problem($"{Py.ReprString(key)} isn't a setting (it takes {string.Join(", ", DoubleBookingRule.Keys)})."));
            if (!(item.Get("keep") is string keep && keep.Length > 0))
                throw new SettingsException(Problem("keep, a pattern for the subject of the meeting that stays, is needed."));
            foreach (var key in new[] { "keep", "other" })
            {
                if (item.Get(key) is not { } pattern)
                    continue;
                if (pattern is not string text || text.Length == 0)
                    throw new SettingsException(Problem($"{key} should be a regular expression."));
                if (PyRegex.Problem(text) is { } why)
                    throw new SettingsException(Problem($"{key} isn't a valid regular expression ({why})."));
            }
            var action = item.Get("action") as string ?? "";
            if (!DoubleBookingRule.Actions.Contains(action))
                throw new SettingsException(Problem($"action is {Toml.Repr(item.Get("action"))}: use move or decline."));
            var with = item.Get("other_with");
            if (with is not null && !(with is string g && settings.Groups.ContainsKey(g)))
                throw new SettingsException(Problem($"other_with = {Toml.Repr(with)} isn't a group in [groups]"
                                                    + (settings.Groups.Count > 0 ? $" (one of {string.Join(", ", settings.Groups.Keys.Order(PyStr.Order))})." : ", and there are none.")));
            var note = item.Get("note");
            if (note is not null and not string)
                throw new SettingsException(Problem("note should be text."));
            if (note is not null && action == "move")
                throw new SettingsException(Problem("note is for decline only: a move sends the attendees an update, with no note."));
            if (config.Rules.Any(r => r.Name == name))
                throw new SettingsException($"Two double booking rules in {full} are called {Py.ReprString(name)}.");
            config.Rules.Add(new DoubleBookingRule
            {
                Name = name, Keep = (string)item.Get("keep")!, Other = item.Get("other") as string, OtherWith = with as string, Action = action,
                Note = note as string ?? "", Why = item.Get("why") is { } w ? Toml.Str(w) : "",
            });
        }
        settings.DoubleBookings = config;
    }

    /// <summary>[notes] and [[note_rule]] (1.31.0). Read strictly, unknown keys included, like the double-booking rules: a
    /// misspelt condition would otherwise be dropped, and the rule would raise more than it says.</summary>
    private static void ReadNotes(TomlTable raw, string full, Settings settings)
    {
        var config = new NotesSettings();
        if (raw.Get("notes") is { } sectionValue)
        {
            var section = sectionValue as TomlTable ?? throw new SettingsException($"[notes] in {full} should be a table.");
            foreach (var key in section.Keys)
                if (!NotesSettings.Keys.Contains(key))
                    throw new SettingsException($"[notes] in {full} has {Py.ReprString(key)}: it takes {string.Join(", ", NotesSettings.Keys)}.");
            if (section.Get("quote_in_deleted") is { } quote)
                config.QuoteInDeleted = quote as bool? ?? throw new SettingsException($"quote_in_deleted in [notes] in {full} should be true or false.");
            if (section.Get("ignore") is { } ignore)
            {
                if (ignore is not TomlArray patterns)
                    throw new SettingsException($"ignore in [notes] in {full} should be a list of regular expressions.");
                foreach (var pattern in patterns)
                {
                    if (pattern is not string text || text.Length == 0)
                        throw new SettingsException($"ignore in [notes] in {full} should be a list of regular expressions.");
                    if (PyRegex.Problem(text) is { } why)
                        throw new SettingsException($"ignore in [notes] in {full}: {Py.ReprString(text)} isn't a valid regular expression ({why}).");
                    config.Ignore.Add(text);
                }
            }
        }
        var i = 0;
        foreach (var item in Toml.Tables(raw.Get("note_rule")))
        {
            i++;
            var name = item.Get("name") is { } n ? Toml.Str(n) : $"note rule {i}";
            string Problem(string what) => $"Note rule {Py.ReprString(name)} in {full}: {what}";
            foreach (var key in item.Keys)
                if (!NoteRule.Keys.Contains(key))
                    throw new SettingsException(Problem($"{Py.ReprString(key)} isn't a setting (it takes {string.Join(", ", NoteRule.Keys)})."));
            foreach (var key in NoteRule.Conditions)
            {
                if (item.Get(key) is not { } pattern)
                    continue;
                if (pattern is not string text || text.Length == 0)
                    throw new SettingsException(Problem($"{key} should be a regular expression."));
                if (PyRegex.Problem(text) is { } why)
                    throw new SettingsException(Problem($"{key} isn't a valid regular expression ({why})."));
            }
            if (!NoteRule.Conditions.Any(item.ContainsKey))
                throw new SettingsException(Problem($"it needs at least one of {string.Join(", ", NoteRule.Conditions)}, or it would raise every note."));
            if (config.Rules.Any(r => r.Name == name))
                throw new SettingsException($"Two note rules in {full} are called {Py.ReprString(name)}.");
            config.Rules.Add(new NoteRule
            {
                Name = name, Subject = item.Get("subject") as string, Organiser = item.Get("organiser") as string, Sender = item.Get("sender") as string,
                Why = item.Get("why") is { } w ? Toml.Str(w) : "",
            });
        }
        settings.Notes = config;
    }

    /// <summary>[send_draft] (1.36.0), read strictly: an unknown key, a bad pattern, a domain that isn't one, or
    /// max_recipients below warn_recipients is an error.</summary>
    private static void ReadSendDraft(TomlTable raw, string full, Settings settings)
    {
        var config = new SendDraftSettings();
        if (raw.Get("send_draft") is { } sectionValue)
        {
            var section = sectionValue as TomlTable ?? throw new SettingsException($"[send_draft] in {full} should be a table.");
            foreach (var key in section.Keys)
                if (!SendDraftSettings.Keys.Contains(key))
                    throw new SettingsException($"[send_draft] in {full} has {Py.ReprString(key)}: it takes {string.Join(", ", SendDraftSettings.Keys)}.");
            if (section.Get("internal_domains") is { } domains)
            {
                if (domains is not TomlArray list || list.Count == 0
                    || list.Any(d => d is not string s || !PyRegex.Get(@"^[a-z0-9-]+(\.[a-z0-9-]+)+$", true).FullMatch(s.Trim())))
                    throw new SettingsException($"internal_domains in [send_draft] in {full} should be a list of domains, such as [\"example.com\"] (no @).");
                config.InternalDomains.AddRange(list.Cast<string>().Select(d => d.Trim().ToLowerInvariant()).Distinct());
            }
            long Number(string key, long fallback, long least)
            {
                if (section.Get(key) is not { } value)
                    return fallback;
                if (value is not long n || n < least)
                    throw new SettingsException($"{key} in [send_draft] in {full} should be a whole number, at least {least}.");
                return n;
            }
            config.WarnRecipients = Number("warn_recipients", config.WarnRecipients, 1);
            config.MaxRecipients = Number("max_recipients", config.MaxRecipients, 1);
            config.ListDays = Number("list_days", config.ListDays, 1);
            if (config.MaxRecipients < config.WarnRecipients)
                throw new SettingsException($"max_recipients ({config.MaxRecipients}) in [send_draft] in {full} is below warn_recipients ({config.WarnRecipients}).");
            if (section.Get("warn") is { } warn)
            {
                if (warn is not TomlArray patterns)
                    throw new SettingsException($"warn in [send_draft] in {full} should be a list of regular expressions.");
                config.Warn = [];
                foreach (var pattern in patterns)
                {
                    if (pattern is not string text || text.Length == 0)
                        throw new SettingsException($"warn in [send_draft] in {full} should be a list of regular expressions.");
                    if (PyRegex.Problem(text) is { } why)
                        throw new SettingsException($"warn in [send_draft] in {full}: {Py.ReprString(text)} isn't a valid regular expression ({why}).");
                    config.Warn.Add(text);
                }
            }
        }
        settings.SendDraft = config;
    }

    /// <summary>config.create_default.</summary>
    public static string CreateDefault()
    {
        var path = DefaultPath;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // CRLF, as Python's text mode wrote it, whatever line ends the source file has.
            File.WriteAllText(path, Template.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        }
        return path;
    }

    /// <summary>The loaded settings as JSON, in the same shape as the Python harness's dump of its Config (parity).</summary>
    public JsonObject Dump()
    {
        JsonArray Strs(IEnumerable<string>? items) => J.Array(items ?? []);
        JsonNode? Opt(string? s) => s is null ? null : JsonValue.Create(s);
        JsonObject Dumped(DoubleBookedSettings d) => new()
        {
            ["when"] = d.When, ["note"] = d.Note, ["days"] = d.Days, ["max_moves"] = d.MaxMoves, ["cancel_note"] = d.CancelNote,
        };
        var o = new JsonObject
        {
            ["path"] = Path,
            ["folders"] = new JsonArray(Folders.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["since"] = Opt(f.Since), ["unmatched"] = f.Unmatched }).ToArray()),
            ["rules"] = new JsonArray(Rules.Select(r =>
            {
                var rule = new JsonObject
                {
                    ["name"] = r.Name, ["sender"] = r.Sender, ["action"] = r.Action, ["match"] = Opt(r.Match), ["check"] = Opt(r.Check),
                    ["why"] = r.Why, ["to"] = Opt(r.To), ["folders"] = r.Folders is null ? null : Strs(r.Folders),
                };
                if (r.Action == "forward")
                {
                    rule["forward_to"] = Strs(r.ForwardTo);
                    rule["comment"] = r.Comment;
                    rule["mark_read"] = r.MarkRead;
                }
                return (JsonNode)rule;
            }).ToArray()),
            ["flow_checks"] = new JsonArray(FlowChecks.Select(f => (JsonNode)new JsonObject { ["flow"] = f.Flow, ["output"] = f.Output, ["pending"] = Opt(f.Pending) }).ToArray()),
            ["login_hint"] = Opt(LoginHint), ["tenant"] = Tenant, ["msal_cache"] = Opt(MsalCache), ["queue_file"] = QueueFile,
            ["state_file"] = StateFile, ["deleted_file"] = DeletedFile, ["accepted_file"] = AcceptedFile, ["days_off_file"] = DaysOffFile, ["log_file"] = LogFile,
            ["notify"] = Notify, ["every_minutes"] = EveryMinutes, ["hours"] = Strs([Hours.Start, Hours.End]), ["weekdays"] = Weekdays,
            ["email"] = Opt(Email),
        };
        if (Invites is { } i)
        {
            o["invites"] = new JsonObject
            {
                ["days_ahead"] = i.DaysAhead, ["months_ahead"] = i.MonthsAhead, ["calendar_months_ahead"] = i.CalendarMonthsAhead, ["auto_accept"] = i.AutoAccept,
                ["auto_accept_outside"] = i.AutoAcceptOutside, ["auto_accept_optional"] = i.AutoAcceptOptional, ["auto_move_blocks"] = i.AutoMoveBlocks,
                ["decline_days_off"] = i.DeclineDaysOff, ["cancel_days_off"] = i.CancelDaysOff,
                ["remove_blocks_days_off"] = i.RemoveBlocksDaysOff, ["raise_group_days_off"] = i.RaiseGroupDaysOff, ["max_declines"] = i.MaxDeclines, ["double_bookings"] = i.DoubleBookings,
                ["fixed_blocks"] = Strs(i.FixedBlocks), ["commute_blocks"] = Strs(i.CommuteBlocks),
                ["movable"] = new JsonArray(i.Movable.Select(m => (JsonNode)new JsonObject { ["subject"] = m.Subject, ["within"] = Opt(m.Within), ["before"] = Opt(m.Before) }).ToArray()),
                ["protected"] = Strs(i.Protected), ["leave_note"] = i.LeaveNote, ["bank_holiday_note"] = i.BankHolidayNote,
                ["bank_holiday_categories"] = i.BankHolidayCategories is null ? null : Strs(i.BankHolidayCategories), ["date_format"] = i.DateFormat,
                ["working_hours"] = Strs([i.WorkingHours.Start, i.WorkingHours.End]), ["travel_hours"] = Strs([i.TravelHours.Start, i.TravelHours.End]),
                ["requests_folders"] = Strs(i.RequestsFolders), ["accept_shortened"] = i.AcceptShortened,
                ["proposals_folders"] = Strs(i.ProposalsFolders), ["accept_proposals"] = i.AcceptProposals, ["leave_pattern"] = i.LeavePattern,
                ["rules"] = new JsonArray(i.Rules.Select(r => (JsonNode)new JsonObject
                {
                    ["name"] = r.Name, ["action"] = r.Action, ["subject"] = Opt(r.Subject), ["organiser"] = Opt(r.Organiser),
                    ["all_day"] = r.AllDay, ["show_as"] = r.ShowAs is null ? null : Strs(r.ShowAs), ["via_group"] = r.ViaGroup,
                    ["optional"] = r.Optional, ["min_people"] = r.MinPeople, ["why"] = r.Why,
                }).ToArray()),
            };
        }
        else
            o["invites"] = null;
        if (OneToOnes is { } t)
        {
            o["one_to_ones"] = new JsonObject
            {
                ["reports"] = new JsonArray(t.Reports.Select(r => (JsonNode)new JsonObject
                {
                    ["name"] = r.Name, ["email"] = r.Email, ["subject"] = r.Subject, ["cancel_if_within_working_days"] = r.CancelIfWithinWorkingDays,
                }).ToArray()),
                ["act"] = t.Act, ["max_actions"] = t.MaxActions, ["cancel_days"] = PyJson.Float(t.CancelDays), ["midday"] = t.Midday,
                ["days_ahead"] = t.DaysAhead, ["bank_holiday_categories"] = Strs(t.BankHolidayCategories), ["leave_pattern"] = t.LeavePattern,
                ["report_note"] = t.ReportNote, ["my_note"] = t.MyNote, ["bank_holiday_note"] = t.BankHolidayNote, ["when_shorter"] = t.WhenShorter,
                ["double_booked"] = Dumped(t.DoubleBooked), ["cancel_if_within_working_days"] = t.CancelIfWithinWorkingDays,
            };
        }
        else
            o["one_to_ones"] = null;
        if (CatchUps is { } c)
        {
            o["catch_ups"] = new JsonObject
            {
                ["act"] = c.Act, ["max_actions"] = c.MaxActions, ["cancel_days"] = PyJson.Float(c.CancelDays), ["when_shorter"] = c.WhenShorter,
                ["days_ahead"] = c.DaysAhead, ["note"] = c.Note, ["my_note"] = c.MyNote, ["bank_holiday_note"] = c.BankHolidayNote,
                ["skip"] = new JsonArray(c.Skip.Select(s => (JsonNode)new JsonObject { ["subject"] = Opt(s.Subject), ["email"] = Opt(s.Email), ["why"] = s.Why }).ToArray()),
                ["double_booked"] = Dumped(c.DoubleBooked), ["cancel_if_within_working_days"] = c.CancelIfWithinWorkingDays,
                ["entries"] = new JsonArray(c.Entries.Select(e => (JsonNode)new JsonObject
                {
                    ["subject"] = Opt(e.Subject), ["email"] = Opt(e.Email), ["why"] = e.Why, ["act"] = e.Handling.Act,
                    ["cancel_days"] = PyJson.Float(e.Handling.CancelDays), ["when_shorter"] = e.Handling.WhenShorter, ["note"] = e.Handling.TheirNote,
                    ["my_note"] = e.Handling.MyNote, ["bank_holiday_note"] = e.Handling.BankHolidayNote, ["double_booked"] = Dumped(e.Handling.DoubleBooked),
                    ["cancel_if_within_working_days"] = e.Handling.CancelIfWithinWorkingDays,
                }).ToArray()),
            };
        }
        if (Groups.Count > 0)
        {
            var groups = new JsonObject();
            foreach (var (name, addresses) in Groups)
                groups[name] = Strs(addresses);
            o["groups"] = groups;
        }
        if (DoubleBookings is { } d)
        {
            o["double_bookings_file"] = DoubleBookingsFile;
            o["double_bookings"] = new JsonObject
            {
                ["act"] = d.Act, ["max_actions"] = d.MaxActions,
                ["rules"] = new JsonArray(d.Rules.Select(r => (JsonNode)new JsonObject
                {
                    ["name"] = r.Name, ["keep"] = r.Keep, ["other"] = Opt(r.Other), ["other_with"] = Opt(r.OtherWith), ["action"] = r.Action,
                    ["note"] = r.Note, ["why"] = r.Why,
                }).ToArray()),
            };
        }
        return o;
    }
}

/// <summary>str.format with named fields only: "{first}", and "{{" / "}}" for braces. Anything else in braces
/// (an index, a format spec, an unknown name) is refused, as Python would refuse it or read it differently.</summary>
public static class PyFormat
{
    public static string Named(string format, IReadOnlyDictionary<string, string> values)
    {
        var b = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '{')
            {
                if (i + 1 < format.Length && format[i + 1] == '{')
                {
                    b.Append('{');
                    i++;
                    continue;
                }
                var close = format.IndexOf('}', i);
                if (close < 0)
                    throw new SettingsException($"Single '{{' encountered in format string {Py.ReprString(format)}");
                var name = format[(i + 1)..close];
                if (!values.TryGetValue(name, out var value))
                    throw new SettingsException($"subject_format {Py.ReprString(format)} uses {{{name}}}: only {{first}} and {{name}} are known.");
                b.Append(value);
                i = close;
            }
            else if (c == '}')
            {
                if (i + 1 < format.Length && format[i + 1] == '}')
                {
                    b.Append('}');
                    i++;
                    continue;
                }
                throw new SettingsException($"Single '}}' encountered in format string {Py.ReprString(format)}");
            }
            else
                b.Append(c);
        }
        return b.ToString();
    }
}

/// <summary>Python's view of TOML values (tomllib): str(), bool(), int() and float() as Python applies them.</summary>
internal static class Toml
{
    public static object? Get(this TomlTable table, string key) => table.TryGetValue(key, out var value) ? value : null;

    public static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => Py.Repr(d),
        TomlDateTime t when t.Kind == TomlDateTimeKind.LocalDate => t.DateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TomlDateTime t => t.ToString(),
        _ => value.ToString() ?? "",
    };

    public static string Repr(object? value) => value is null ? "None" : value is string s ? Py.ReprString(s) : Str(value);

    public static bool Truthy(object? value) => value switch
    {
        null => false,
        string s => s.Length > 0,
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        TomlArray a => a.Count > 0,
        TomlTableArray a => a.Count > 0,
        TomlTable t => t.Count > 0,
        _ => true,
    };

    public static long Int(object value) => value switch
    {
        long l => l,
        double d when double.IsFinite(d) => (long)Math.Truncate(d),
        bool b => b ? 1 : 0,
        string s => PyStr.Int(s),
        _ => throw new SettingsException($"{Repr(value)} isn't a whole number."),
    };

    public static double Float(object value) => value switch
    {
        long l => l,
        double d => d,
        bool b => b ? 1 : 0,
        string s when double.TryParse(PyStr.Strip(s), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => throw new SettingsException($"{Repr(value)} isn't a number."),
    };

    public static List<string> Strings(object? value) => value switch
    {
        null => [],
        TomlArray a => a.Select(x => x is null ? "None" : Str(x)).ToList(),
        string s => s.Select(c => c.ToString()).ToList(),   // list("abc"), as Python would make it
        _ => throw new SettingsException($"{Repr(value)} should be a list."),
    };

    public static IEnumerable<TomlTable> Tables(object? value) => value switch
    {
        null => [],
        TomlTableArray a => a,
        TomlArray a => a.OfType<TomlTable>(),
        _ => throw new SettingsException($"{Repr(value)} should be a list of tables."),
    };
}

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;
