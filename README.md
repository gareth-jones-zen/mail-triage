# mail-triage

> **This is the `dotnet` branch: a port to .NET 11 Native AOT, in progress and not yet in use.** The Python
> version, which is what runs today, is on `main` and at tag `v0.9.1`. The port makes the same decisions (checked
> against the Python version on generated scenarios, settings, patterns and state files); it isn't switched over
> until it has also matched the Python version on real data. Build: `dotnet test -c Release`, with
> [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib) checked out beside this repo.
> Offline parity checks: `tools/parity/parity.py` (Python side) and `tools/Parity` (.NET side).

Standing rules for your mail, and advice on your meeting invites. Outlook rules file the noise into folders; mail-triage goes through
those folders, deletes what your rules say is routine, and queues the rest for you to decide, each
email once. **Anything no rule names is queued, never deleted.**

```
mail-triage run          # 4 deleted, 2 new to review
mail-triage list
  1. 2026-11-10  newsletter@vendor.example
     Agentic AI in telco operations: webinar, 3 Dec
     [Vendor webinars]
  2. 2026-11-10  PowerAutomateNoReply@microsoft.com
     Some of your flow(s) have failed
     [Power Automate failure digests] Weekly report (3 failures): 2 request(s) since its last success haven't been picked up
mail-triage delete 1     # mark read, move to Deleted Items
mail-triage keep 2       # off the queue, left in the folder
mail-triage file 2 --to Inbox/Projects   # move it to a folder, left unread
mail-triage deleted      # what the rules deleted since you last cleared the queue, by rule
```

It works on Microsoft 365 mail through Microsoft Graph, and runs on Windows (anything else with
Python 3.11+ works too, without autostart and notifications).

## Install

```
pipx install --preinstall git+https://github.com/gareth-jones-zen/m365-graph --preinstall git+https://github.com/gareth-jones-zen/win-background git+https://github.com/gareth-jones-zen/mail-triage
```

[m365-graph](https://github.com/gareth-jones-zen/m365-graph) does the sign-in and Graph requests;
[win-background](https://github.com/gareth-jones-zen/win-background) does the scheduled task,
notifications, the run lock and the log. Neither is on PyPI, so both are installed first.

**It needs Mail.ReadWrite** on Microsoft's "Graph Command Line Tools" app, to mark mail read and
move it. Many organisations need an admin to approve that: the first `sign-in` shows an "Approval
required" screen, where you can ask. A dry run needs only Mail.Read.

## Getting started

```
mail-triage config        # create the settings file and say where it is
mail-triage sign-in       # once: a browser opens to sign in to Microsoft
mail-triage run --dry-run # what it would do; nothing changes
mail-triage run
mail-triage autostart on  # a watcher: at once, then every 15 minutes in working hours
```

## Settings

`%APPDATA%\mail-triage\config.toml` by default (`--config` or `MAIL_TRIAGE_CONFIG` for another).

```toml
login_hint = "you@example.com"
every_minutes = 15        # the watcher runs this often...
hours = "07:00-19:00"     # ...between these times, and once at start whatever the time...
weekdays = true           # ...Monday to Friday
notify = "new"            # "new": the watcher notifies new emails to review; "errors": only problems

[[folder]]
name = "Newsletters"      # display name, found anywhere in your folder tree
since = "2026-09-17"      # optional: never touch older mail, which you may be keeping

[[folder]]
name = "Inbox"
unmatched = "leave"       # only rules that name this folder act here; nothing else is touched or queued

[[rule]]
name = "Meeting acceptances"
sender = '@example\.com$'
match = '^Accepted: '
action = "delete"
folders = ["Inbox"]       # optional: only in these folders

[[rule]]                  # first match wins, top to bottom
name = "Vendor newsletters"
sender = '@news\.example\.com$'          # regular expression on the sender's address, ignoring case
match = 'newsletter|digest'              # optional: on the subject and preview
action = "delete"                        # delete | review | check | file
# to = 'Inbox/Projects'                 # for file: the folder, as a path from the top
why = "News, not action."                # say why: it's what keeps a rule list maintainable

[[rule]]
name = "Power Automate failure digests"
sender = 'PowerAutomateNoReply@microsoft\.com'
match = 'flow\(s\) have failed'
action = "check"
check = "flow-failures"

[[flow_check]]
flow = "Weekly report"                   # within the flow's name in the digest
output = '~/OneDrive/Reports/report-*.html'      # what a good run writes
pending = '~/OneDrive/Reports/requests/*.txt'    # optional: what a run consumes
```

Write regular expressions in TOML's single-quoted literal strings, so backslashes stay as they
are. `mail-triage which <sender> "<subject>"` says which rule an email would meet, which is the
quickest way to check a new rule. Also: `msal_cache` (share another tool's sign-in), `tenant`,
`queue_file`, `state_file`, `log_file`.

## How it works, and why

- **Delete** marks the email read and moves it to Deleted Items, so it's recoverable.
- **Review** leaves it where it is and queues it.
- **Your inbox can take a few rules without being triaged.** A folder with `unmatched = "leave"` gets only the rules that name it in `folders`, and mail no rule names is left alone, never queued: tidy the noise (meeting acceptances) without every email becoming a question. A rule with `folders` acts only there; one without acts in every folder except those.
- **File** moves it to the folder in `to` (a path such as `Inbox/Projects`), left unread: for mail you keep but needn't decide on. `deleted` lists what was filed too.
- **Check** runs a test on the email and deletes it only if there's nothing to do; otherwise it's queued with what the test found.
- **Each email is raised once.** Every email ever queued is recorded by its internet message ID (Graph's own id changes when an email moves), so one you've seen and kept in the folder doesn't come back. The first version re-queued everything still in the folders on every run, and the same six emails were put to their owner twice. **What's waiting stays in the queue until you `delete`, `keep` or `clear` it**, and an email deleted in Outlook drops out by itself.
- **The test for a rule is "does this need me to do something?"** A notification that only says something happened should be a delete rule. Hence the flow-failures check: a failure digest only matters if the flow is still failing.
- **flow-failures never guesses that a flow is fine.** Power Automate doesn't say whether a flow has worked since, so each flow needs a `[[flow_check]]` saying what a good run leaves behind. With `pending`, a flow has recovered if nothing pending is newer than its newest output. Without it, if its output is newer than the digest. A flow without a check is raised.
- **One run at a time**, so the watcher and a run started by hand can't both act on the same email. `clear` and `keep` take the same lock, so a run in progress can't write back a queue you've just cleared.
- **Unattended deletes stay visible.** Everything the rules delete is recorded, and `mail-triage deleted` lists what went since you last cleared the queue, grouped by rule (`--all` for the last 60 days). `list` says how many. A rule that starts catching the wrong mail shows up there, and the email is still in Deleted Items.
- **The watcher runs once at start, then every `every_minutes` within `hours`**, so logging on in the evening still clears the day's mail, and checking Outlook in the afternoon finds the folders already tidy. One watcher per settings file; it logs `alive` hourly and notifies a failure once, until a run succeeds.
- **The watcher never opens a browser.** A lapsed sign-in is notified, and `mail-triage sign-in` at a console fixes it.

## Meeting invites

With an `[invites]` section, each run also looks at your calendar for the next `days_ahead` days and
queues every invite you haven't answered, with a recommendation. **Who sent an invite says little about
whether to go**, so unlike mail it's judged on time and context. On three months of one person's calendar,
every decline was either leave (10) or a clash with something already accepted (17); with no clash, 78 of
100 invites were accepted.

```
mail-triage list
  4. 2026-11-17T00:00  HR Team
     Pay changes due: Tue 17 Nov 00:00–00:00
     [invite] ACCEPT: holds no time (all-day or shown as free): a marker, not a meeting (you're invited through a group)
  5. 2026-11-25T14:00  HR Team
     Wellbeing webinar: Wed 25 Nov 14:00–15:00
     [invite] DECLINE: clashes with Design review (accepted, Wed 25 Nov 13:30–15:20) (you're invited through a group)
  6. 2026-11-26T17:00  Sam Jones
     Team social: Thu 26 Nov 17:00–19:00
     [invite] ACCEPT AND MOVE: clashes only with things you can move: Travel (travel, Thu 26 Nov 17:00–18:00 -> Thu 26 Nov 19:00–20:00)
mail-triage invite decline 5 --note "Clashes, sorry"   # the instance; --series for the whole series
mail-triage invite accept 6 --move                     # accept, and move the travel to 19:00
mail-triage move <event id> 2026-11-11T16:00           # move one of your own meetings or blocks by hand
mail-triage leave                                      # meetings in your leave you haven't declined
mail-triage leave --decline                            # decline them, each with the leave note
```

| What it finds | Recommends |
|---|---|
| Falls in your leave (your own entries matching `leave_pattern`: Holiday, Annual leave, Bank holiday) | Decline, noting "On annual leave", or "On annual leave 16/12 to 31/12" when it's longer than a day |
| Clashes with something fixed: a meeting you've accepted, anything `protected`, a block in `fixed_blocks`, an out-of-office block, travel in the middle of the day | Decline (or propose another time) |
| Clashes only with things you can move: your own blocks, meetings you organise, travel at either end of the day | Accept, and move them: each comes with the nearest free slot within its limits. **With no such slot, it's fixed** |
| A series where only some occurrences clash, or fall in leave | Accept, and decline those |
| All-day, or shown as free (a deadline marker, a notice) | Accept: it holds no time, so nothing clashes |
| No clash | Accept, noting a recurring series, being optional or invited through a group, over 50 people, or an outside organiser |

- **Almost nothing answers on its own.** The recommendation is advice; `invite accept|tentative|decline <n>` answers. Rules that act come one class of invite at a time, once the advice has proved itself. The first:
- **`accept_shortened = true`: a meeting you'd accepted that has only been shortened is accepted without asking.** Changing a meeting's time resets your response, so the update arrives as an unanswered invite. To know you'd accepted it, each run remembers the meetings you've accepted and their times (`accepted_file`, default `%LOCALAPPDATA%\mail-triage\accepted.json`); the first run only remembers. Every remembered occurrence must now sit inside its old slot, with at least one shorter. New occurrences after the last remembered one (a series extended) are fine; anything moved or lengthened, or a new occurrence among the old ones, is raised as usual. Each one accepted is listed by `deleted`, under "Accepted: already accepted, only shortened", so it can be reviewed.
- **`accept_proposals = true`, with `proposals_folders = ["Inbox"]`: a new time someone proposes for your meeting is accepted if you're free.** When someone declines (or tentatively accepts) one of your meetings and proposes another time, that occurrence moves to it (they get the update) and the proposal is deleted, but only when it's your meeting, they're the only other person in it, the new time is in the future and within `working_hours`, and nothing that holds time (busy, out of office, tentative) is there. Anything else is queued, once, saying why (a clash, others who would move too, outside hours), with the move it would make. Without `accept_proposals`, every proposal is queued with that advice. Each one moved is listed by `deleted`, under "Accepted: new time proposed, and free".
- **A decline is for the instance, never the series**, unless you add `--series`: declining a series removes every future occurrence. Declining a queued series declines the occurrences the recommendation names (leave or a fixed clash), or else the first one. A series is accepted as a whole; if its recommendation also declines some occurrences, it stays queued until you decline them.
- **No response is sent where the organiser didn't ask for one** (Graph refuses an answer that sends one).
- **Answered invitation emails are tidied up.** Outlook deletes an invitation email when you respond in Outlook, but a response through Graph leaves it behind. With `requests_folders = ["Invites"]` in `[invites]`, each run deletes the invitation emails there whose meeting is answered, cancelled or gone (declining removes it), however you answered; unanswered ones stay.
- **`[[invite_rule]]` with `action = "skip"`** drops invites that need no response at all, such as a colleague's leave sent as an invite. Every condition given must hold.
- **Blocked-out time is assumed movable**, within `working_hours`, to the nearest free slot on the same day or the next week. Limit it with a `[[movable_block]]`: `within = "day"` keeps it on its day; `before = '<subject>'` keeps it before the next meeting of that name. Name blocks that never move in `fixed_blocks`. A meeting you organise counts as movable too (moving it sends attendees an update), so put any you never move, such as your own manager's 1:1 if you send it, in `protected`.
- **Travel (`commute_blocks`) moves only at either end of the day**, within `travel_hours`: morning travel earlier (come in earlier), evening travel later (leave later). Travel in the middle of the day, and any block shown as out of office, never moves.
- **Each invite is raised once:** a recurring series once as a whole, a one-off once per start time, so a moved meeting comes back. **So does one week of a series that the organiser changed on its own** (an exception), once the rest of the series is answered: it's raised and answered on its own. Answering the series doesn't answer such a week, so accepting a new series also accepts any changed weeks in it. An invite answered in Outlook drops off the queue by itself.
- **`delete` refuses an invite**; answer it instead. **`move` refuses someone else's meeting.**
- It needs `Calendars.ReadWrite`.

```toml
email = "you@example.com"        # defaults to login_hint
[invites]
days_ahead = 42
commute_blocks = ['^Travel']
fixed_blocks = ['^School run']
protected = ['^1:1 with my manager$']
# working_hours = "08:00-18:00"; travel_hours = "06:00-21:00"
# leave_note = "On annual leave"; date_format = "%d/%m"
# leave_pattern = '^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b'

[[movable_block]]
subject = '^Weekly report'
within = "day"                   # due that day

[[movable_block]]
subject = '^Prep for board'
before = '^Board meeting'        # due before the next board meeting

[[invite_rule]]
name = "Colleagues' leave sent as invites"
subject = 'holiday|annual leave'
all_day = true
show_as = ["free", "oof"]
action = "skip"                  # or "review": always raise
# also: organiser, via_group, optional, min_people (all must hold)
```

## Your 1:1s, when someone's on leave

With a `[one_to_ones]` section (it needs `[invites]`), each run also looks after the weekly 1:1s you hold with
your reports: when either of you is off, the occurrence is cancelled or moved. **The first rule that changes your
own meetings rather than answering someone else's**, so it starts with `act = false`, which only records what it
would do.

```
mail-triage one-to-ones          # what a run would do in the next days_ahead days; changes nothing
  Wed 18 Nov 15:30–16:20  Sam Jones: CANCEL (Sam Jones is on leave Wed 18 Nov (Sam off 3 of 5 days that week)) note: "Cancelling as you're on leave - enjoy the time off"
  Wed 02 Dec 08:45–09:35  Sam Jones: MOVE to Tue 01 Dec 08:15–09:05 (Sam Jones is on leave Wed 02 Dec (Sam off 1 of 5 days that week))
  Wed 09 Dec 15:30–16:20  Sam Jones: ASK (you have a half day Wed 09 Dec and which half isn't known). Suggest: cancel it, or move it to ...
mail-triage deleted              # each one cancelled or moved (or, with act = false, that would have been), by rule
```

| At the 1:1's time | It |
|---|---|
| Nobody's off (a half day counts only if it overlaps the meeting) | Does nothing |
| Whoever's off is off at least `cancel_days` (3) of that Monday-to-Friday week | Cancels the occurrence, with the note for whoever's off: `report_note`, `my_note`, or `bank_holiday_note` on a bank holiday |
| Whoever's off is off for less of the week | Moves it to the nearest free slot that week: same length, within `working_hours`, on a day neither of you is off, with nothing busy, out of office or tentative in either calendar. **The same time of day on the nearest day** (a Wednesday 11:00 goes to Tuesday or Thursday 11:00); only if that time is free on no day, the slot nearest in time. On a tie, the later |
| No such slot, or a half day whose half isn't known, or their free/busy can't be read | Queues it, once, with the reason and a suggestion: cancel, or the nearest free slot in the week either side |

- **Who's off.** A report: your own diary entries named `<Full Name> annual leave` or `<Full Name> on leave`,
  optionally with a half: `on leave PM`, `annual leave (AM)`, and on longer leave `(from PM)` (the first day is
  the afternoon only) or `(to AM)` (the last day is the morning only); `half` with no half is a half day of
  unknown half. You: your `leave_pattern` entries. People First enters leave as `Holiday (<job title>)`, 00:00
  to 23:59 and not all-day: every day it touches is a full day, and `Holiday (half day) (<job title>)` is a half
  day of unknown half. Bank holidays: your all-day entries in a `bank_holiday_categories` category (Outlook's
  UK holidays are in "Holiday"), and entries named "Bank holiday". They count as a day off for both of you.
- **Their free/busy** comes from Graph's getSchedule, one week at a time. Like yours, anything busy, out of
  office or tentative holds the time; the 1:1 itself doesn't.
- **Only the occurrence, never the series**, and only meetings you organise with that report in them, matched by
  their exact subject. Anything `protected` (your manager's 1:1, if you send it) is never touched.
- **`act = false`** records each "would be cancelled" or "would be moved" once, for `mail-triage deleted`, and
  changes nothing. **At most `max_actions` changes a run** (20). It's idempotent: a cancelled occurrence is
  gone, and a moved one no longer clashes with the leave.
- A queued one can't be deleted, filed or answered like an email or an invite: change it in Outlook (or with
  `mail-triage move`), then `keep` it.

```toml
[one_to_ones]
act = false                       # true: cancel and move; false: only record what it would do
# max_actions = 20; cancel_days = 3; midday = "13:00"; days_ahead = 42 (default: [invites] days_ahead)
# bank_holiday_categories = ["Holiday"]; subject_format = "Weekly 1-2-1 - {first}"
# leave_pattern = '^{name}\s+(annual leave|on leave)\b'
# report_note = "Cancelling as you're on leave - enjoy the time off"
# my_note = "Cancelling as I'm on leave"; bank_holiday_note = "Cancelling for the bank holiday"

[[one_to_one]]
name = "Sam Jones"                # as in your "Sam Jones annual leave" entries
email = "sam@example.com"         # must be in the meeting
# subject = "Weekly 1-2-1 - Sam"  # default: subject_format
```

## The queue file

JSON, so another program (an assistant, a script) can work from it. An invite's item also has
`"type": "invite"`, `"recommendation"`, `"series"`, `"occurrence"`, `"occurrences"`, `"decline"` (the occurrences to
decline), `"note"`, `"responseRequested"` and `"clashes"` (each with `"to"` and `"to_end"` if it can move), and its
`folder` is `"Calendar"`. A 1:1 that `[one_to_ones]` couldn't settle has `"type": "one-to-one"`, the occurrence's id,
the report's name in `"from"`, and the reason and suggestion in `"check"`:

```json
{"checked": "2026-11-10T09:00:00", "review": [{"n": 1, "folder": "Newsletters", "id": "<Graph id>",
  "key": "<internet message id>", "received": "2026-11-10T08:12:00Z", "from": "...", "subject": "...",
  "preview": "first 300 characters", "rule": "Vendor webinars", "check": "what a check found", "webLink": "..."}]}
```

## Tests

```
python -m unittest discover -s tests
```
