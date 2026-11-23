# mail-triage

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
mail-triage forward 2    # apply the forward rule it matches: forward, mark read, file (1.9.0)
mail-triage deleted      # what the rules deleted since you last cleared the queue, by rule
mail-triage summary      # one line for a session briefing: what's queued and deleted (--json: the counts)
```

It works on Microsoft 365 mail through Microsoft Graph, and runs on Windows (x64).

## Install

Nothing else is needed.

1. Download `mail-triage-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/mail-triage/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\mail-triage`. The scheduled task (`autostart`) runs the program from there.
3. Run the commands below from that folder, or add it to your `PATH`.

It holds two programs: `mail-triage.exe`, for a console, and `mail-triage-background.exe`, the same program with no window, which the logon task runs.

**To upgrade while the watcher runs:** rename `mail-triage.exe` and `mail-triage-background.exe` to `.old` (Windows lets you rename a program that's running, but not overwrite it), unzip the new release into the same folder, then run `mail-triage autostart restart`. That stops the watcher, waits for it to exit and starts the new version, keeping the task as it was. Delete the `.old` files afterwards. If the old watcher hasn't exited after 30 seconds, nothing is started and it carries on as it was.

**Add a rule that uses a new check or action only after upgrading:** the running version rejects a check or action it doesn't know.

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

## Sharing settings with other tools: `base`

Several tools built on the shared library can keep their common settings in one file, named at the top of each
tool's settings with `base = "base-config.toml"` (relative to the settings file). Then:

- **the base's `[paths]` are variables for this tool's paths**: `ROOT = "%LOCALAPPDATA%/tools"` there lets this file
  say `log_file = "%ROOT%/mail-triage.log"`. They're looked up before the environment;
- **its other settings that this tool knows and doesn't set itself are used**: typically `login_hint`, `tenant` and
  `msal_cache`, and `calendar_months_ahead` (below). This file's own value always wins;
- **a table, such as `[invites]`, applies only if this file has that section too**, and its keys fill in the ones this
  file leaves out.

The loader is background-tools-lib's `PyCompat.BaseSettings`. A missing or unreadable base is a settings error.

**`calendar_months_ahead`** (1.15.0) is a top-level setting, usually kept in the base so the calendar tools look
equally far ahead: how far `[invites]` looks, in months, when `[invites]` gives neither `months_ahead` nor
`days_ahead`. The order is `[invites] months_ahead`, then `[invites] days_ahead`, then `calendar_months_ahead`, then
42 days.

## Settings

`%APPDATA%\mail-triage\config.toml` by default (`--config` or `MAIL_TRIAGE_CONFIG` for another).

```toml
base = "base-config.toml"   # optional: settings shared with other tools
login_hint = "you@example.com"
notify = "new"            # "new": the watcher notifies new emails to review; "errors": only problems

[schedule]                # when the watcher triages (the shared [schedule] settings, below)
every_minutes = 15        # this often...
hours = "07:00-19:00"     # ...between these times, and once at start whatever the time...
days = "Mon-Fri"          # ...on these days

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
action = "delete"                        # delete | review | check | file | handle | forward
# to = 'Inbox/Projects'                 # for file (and forward): the folder, as a path from the top
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

[[rule]]
name = "GitHub Actions failures"
sender = 'notifications@github\.com'
match = '^\[[^\]]+\] Run failed: '
action = "check"
check = "github-runs"                   # needs the GitHub CLI (gh), signed in

[[rule]]                                 # 1.9.0: pass it on, then put it away
name = "Trooli maintenance notices"
sender = 'NetworkMaintenanceChange@trooli\.com'
action = "forward"
forward_to = ["ert@example.com"]         # one or more addresses
comment = "Hi - Trooli's maintenance notice below, for your awareness. Thanks"
to = 'HSuppliers/Trooli/API/Change notifications'   # optional: file it afterwards
# mark_read = true                      # the default for forward (file leaves mail unread)

[[handler]]                              # a command a "handle" rule gives the email to
name = "team-chat-draft"
command = ['python', 'handlers/team-chat-draft.py', 'marketing']
# cwd = '.'                             # relative to this file; default: its folder
then = "delete"                          # once handled: delete | file (with to) | leave | review
# timeout = 120; max_attempts = 4
# attachments = true                    # save the email's file attachments for the handler (1.5.0)

[[handler]]                              # or (1.8.0): save the email and queue a job, run later
name = "marketing-draft"
queue = ['claude-headless', 'job', 'marketing', '--input', '{saved}']   # {saved}: the saved email's path
save_to = '%LOCALAPPDATA%/my-tools/marketing'   # default: handler-saved/<name>, beside handled_file
then = "delete"

[[rule]]
name = "Weekly marketing update"
sender = 'marketing@example\.com'
match = 'Marketing update'
action = "handle"
handler = "team-chat-draft"
```

Write regular expressions in TOML's single-quoted literal strings, so backslashes stay as they
are. `mail-triage which <sender> "<subject>"` says which rule an email would meet, which is the
quickest way to check a new rule. Also: `msal_cache` (share another tool's sign-in), `tenant`,
`queue_file`, `state_file`, `handled_file`, `forwarded_file`, `log_file`.

**Keep each kind of table together**: all the `[[rule]]`s in one run, then the `[[handler]]`s, and so on. The TOML
reader silently drops the tables before a break when one kind is split by another, so since 1.3.1 a file whose
`[[...]]` count doesn't match what was read is refused, saying which.

### `[schedule]`: when the watcher triages (1.16.0)

The shared scheduling settings from background-tools-lib (its README, "[schedule]: the shared scheduling settings"), the
same keys in every tool. mail-triage is a watcher: it starts at logon and keeps running, so `every_minutes` is how often
it triages, and `hours` and `days` when; `daily_at` and `after_unlock_minutes` don't apply. **Defaults:** every 15 minutes,
07:00-19:00, Mon-Fri. The watcher re-reads its settings at each run, so a change takes effect from the next one; no
re-registering is needed. `autostart status` says if the registered task no longer matches the settings.

**Until 1.16.0** these were `every_minutes`, `hours` and `weekdays` at the top level (`weekdays = false` is `days =
"Mon-Sun"`). They're still read, and the watcher logs once at start that they're the old form; a file with both is refused.

### `[leave]`: what counts as your leave and your bank holidays (1.17.0)

The shared leave rules from background-tools-lib (its README, CalendarDays, "`[leave]`: the shared leave settings"),
the same keys calendar-out-of-office, calendar-work-location and meeting-preps read, so they all agree on a day:
`subjects`, `categories`, `min_hours`, `half_day`, `bank_holiday_subjects` and `bank_holiday_categories`. Kept once in a
base file's `[leave]`, with an empty `[leave]` here (a table applies only to a tool that has the section).

mail-triage adds one rule on top: **a half day counts only for what it overlaps.** A half day whose half is known (its
title says AM or PM, or "from PM" / "to AM" on a longer entry) is off for that half (before or after `[one_to_ones]`'
`midday`, 13:00 by default); a few hours marked as leave are off for those hours; one whose half can't be told (People
First's `Holiday (half day) (<job title>)`) is never acted on: invites on it are asked, accepted meetings left alone,
and 1:1s raised. `mail-triage days-off` shows each weekday's verdict and the entry behind it.

A file with neither `[leave]` nor the old settings keeps mail-triage's old defaults (`leave_pattern`'s
`^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b` and the "Holiday" category) under the new rules.

**Until 1.17.0** these were `leave_pattern` in `[invites]` (any length of entry matching it was a whole day off) and
`bank_holiday_categories` in `[invites]` and `[one_to_ones]`. They're still read, as `subjects` and
`bank_holiday_categories` (the two lists must agree), and the watcher logs once that they're the old form; a file with
both forms is refused. `[one_to_ones]` `leave_pattern` stays: it's how your reports' leave entries are named, read with
background-tools-lib's `ReportLeaveTitles`, the titles calendar-team-leave-sync writes.

## How it works, and why

- **Delete** marks the email read and moves it to Deleted Items, so it's recoverable.
- **Review** leaves it where it is and queues it.
- **Your inbox can take a few rules without being triaged.** A folder with `unmatched = "leave"` gets only the rules that name it in `folders`, and mail no rule names is left alone, never queued: tidy the noise (meeting acceptances) without every email becoming a question. A rule with `folders` acts only there; one without acts in every folder except those.
- **File** moves it to the folder in `to` (a path such as `Inbox/Projects`), left unread: for mail you keep but needn't decide on. `deleted` lists what was filed too.
- **Forward (1.9.0)** sends the email on to `forward_to` with `comment` above it (Outlook's Forward), then marks it read (`mark_read`, on by default) and files it to `to` if given. `deleted` lists each one ("forwarded to …"). It needs **Mail.Send** as well as Mail.ReadWrite.
  - **An email is never forwarded twice.** Its internet message ID is written to `forwarded_file` as "sending" before the forward goes, and as "sent" once Graph answers. If the answer is lost (a timeout or a dropped connection, after which the forward may well have gone), the email is queued saying so, to check Sent Items: it's never resent. If the forward went but marking or filing failed, the next run does only those.
  - **`mail-triage forward <n>`** applies the rule to emails already in the queue, by number or Graph id (`--rule NAME` to name one): for mail queued before its rule existed. It's recorded and taken off the queue the same way.
  - A dry run says what it would do ("would forward to … and file to …"); `--writes journal` records the forward and sends nothing, and doesn't count it as sent.
- **Check** runs a test on the email and deletes it only if there's nothing to do; otherwise it's queued with what the test found.
  - **And again on every run while it waits (1.9.0).** A check can be asked too soon: a failed build checked while its fixing run was still going sat in the queue saying "still failing" for two days. So each run checks every queued email its `check` rule raised (once a run, by the rule's name). If it passes now, it's deleted and recorded just as a first-time pass would be ("it has passed since: …") and taken off the queue; if not, its reason is brought up to date. A check that errors leaves the email as it was: an error never removes anything.
- **Each email is raised once.** Every email ever queued is recorded by its internet message ID (Graph's own id changes when an email moves), so one you've seen and kept in the folder doesn't come back. The first version re-queued everything still in the folders on every run, and the same six emails were put to their owner twice. **What's waiting stays in the queue until you `delete`, `keep` or `clear` it**, and an email deleted in Outlook drops out by itself.
- **The test for a rule is "does this need me to do something?"** A notification that only says something happened should be a delete rule. Hence the flow-failures check: a failure digest only matters if the flow is still failing.
- **flow-failures never guesses that a flow is fine.** Power Automate doesn't say whether a flow has worked since, so each flow needs a `[[flow_check]]` saying what a good run leaves behind. With `pending`, a flow has recovered if nothing pending is newer than its newest output. Without it, if its output is newer than the digest. A flow without a check is raised.
- **github-runs asks GitHub whether the workflow has passed since.** It reads the repository, workflow and branch from a "[owner/repo] Run failed: build - main (abc1234)" email, and asks the GitHub CLI (`gh run list`, as you) for that branch's latest finished run of that workflow. A success since deletes the email; a failure, or anything it can't tell (no `gh`, not signed in, no finished run), is raised with what it found.
- **sharepoint-access asks SharePoint whether someone else has already dealt with an access request** (1.12.0). A "Name wants to access 'Site'" email goes to every owner of the site, so another owner has often approved it by the time you see it. It reads the requested resource and who asked from the email, finds the request in that site's hidden Access Requests list by who asked and when (within 15 minutes of the email: the email's AccessRequestID isn't the list item's id), and deletes the email if it's approved, saying who approved it and when. Still waiting, any other status, or a request it can't find or read is raised with what it found. It reads the list with Files.ReadWrite.All (or Sites.Read.All), and only reads. A rule for it:

  ```toml
  [[rule]]
  name = "SharePoint access requests"
  sender = 'no-reply@sharepointonline\.com'
  match = "wants to access '"
  action = "check"
  check = "sharepoint-access"
  ```
- **Queued emails are checked again on every run** when a check rule applies to them: one raised by a check rule, and (1.12.0) one queued with no rule that a check rule added since now matches, so adding a check rule clears requests already waiting once they pass.
- **Handle gives the email to a command of yours, then does what the handler's `then` says.** mail-triage recognises the email; what's done with it (a draft written, a report reviewed) is a script you own, so the tool stays generic. The handler gets the email as JSON on stdin: `id`, `key` (internet message ID), `folder`, `from`, `subject`, `received`, `body` (as text), `webLink`, `hasAttachments` and `rule`.
  - **With `attachments = true` (1.5.0) it also gets `attachments`:** `[{name, path, size, contentType}]`, each file
    attachment already saved to a folder of its own (inline images and attached emails are left out). **The folder is
    removed once the handler exits, so the handler copies what it keeps.** If the attachments can't be fetched, the
    handler isn't run and the next run tries again, as for exit 75. Without it, a handler that needs attachments
    fetches them itself, by `id`, with its own Graph sign-in.
  - **Exit 0: handled.** `then` applies (delete by default), and the first line of output is the note `deleted` shows.
  - **Exit 75: try again later.** The email is left alone, and the next run hands it over again, up to `max_attempts`; then it's queued.
  - **Anything else, or a timeout: queued**, with the first line of output (or of errors) as the reason. **Neither is retried**: after a timeout the handler may have done part of its work, and a second go could do it twice.
    - **Any other exit above 0 is the handler's answer, not a fault (1.9.0)**: "this one isn't for me, raise it". It's queued as *kept by &lt;handler&gt; (exit 1): …*. **A timeout, a handler that won't start, a crash** (Windows reports an unhandled exception as a negative code) **or exit 75 past `max_attempts`** is queued as *&lt;handler&gt; failed …*. Before 1.9.0 every non-zero exit read "failed", which made a handler's deliberate exit 1 look like a fault.
  - **Each email is handed over once.** What each handler made of each email is recorded by its internet message ID (`handled_file`), before `then` is applied: if the delete fails, the next run retries only the delete.
  - **Capture what you need before exiting 0.** `then` may move the email, and moving changes its Graph id.
  - **Return quickly.** The watcher runs every few minutes; hand slow work to something else, and **start it without your output handles** (stdout and stderr to null), or mail-triage waits for that process too.
- **Or a handler can queue the work instead (`queue`, 1.8.0).** mail-triage saves the email as JSON (the same input a command gets) in `save_to`, with any attachments (`attachments = true`) beside it in a `-attachments` folder that's kept, then puts the `queue` command on the [claude-headless](https://github.com/gareth-jones-zen/claude-headless) job queue (`%LOCALAPPDATA%\claude-headless\queue.json`) with `{saved}` replaced by the file's path, to run from `cwd`. That's the handler done, so `then` applies at once. mail-triage needs to know nothing about the job: whatever runs the queue runs it, one job at a time and after any usage limit. The job deletes the saved file when it's finished with it. If saving or queuing fails, the email is left for the next run, as for exit 75.
  - **A handler only runs when the run may write**: never in a dry run, nor with `--writes journal` or `refuse`, because it writes outside mail-triage. `run --dry-run` and `which` say which handler would run.
  - **`mail-triage handle <n>`** hands one email over by hand: a queued one by number, or any email by Graph id (`--handler NAME` if no rule names one). For testing a handler, and for an email that was queued before its rule existed.
  - **Mail is from people you don't control.** A handler that gives it to an AI model should treat it as data, not instructions, and keep a person between the draft and anyone else.
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
mail-triage hold 2026-11-25T16:15 17:00 "Hold: ITS"     # a hold in your calendar, no attendees (--yes to create)
mail-triage sent                                       # with [sent]: what it would tidy in Sent Items (--yes to do it)
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
- **So are cancellation notices, and the cancelled meeting with them.** When someone cancels a meeting, Outlook leaves it on your calendar marked "Canceled:" until you remove it. In the `requests_folders`, each run takes a cancelled meeting that's still there off your calendar (as "Remove from calendar" does, which notifies nobody), then deletes the notice; if the meeting has already gone, just the notice. It never removes a meeting you organise, or one that isn't marked cancelled. `deleted` lists them under "Cancelled meetings". (Graph types a cancellation as an `eventMessage`, not an invitation, so before 1.2.0 they were never touched.)
- **`[[invite_rule]]` with `action = "skip"`** drops invites that need no response at all, such as a colleague's leave sent as an invite. Every condition given must hold.
- **Blocked-out time is assumed movable**, within `working_hours`, to the nearest free slot on the same day or the next week. Limit it with a `[[movable_block]]`: `within = "day"` keeps it on its day; `before = '<subject>'` keeps it before the next meeting of that name. Name blocks that never move in `fixed_blocks`. A meeting you organise counts as movable too (moving it sends attendees an update), so put any you never move, such as your own manager's 1:1 if you send it, in `protected`.
- **Travel (`commute_blocks`) moves only at either end of the day**, within `travel_hours`: morning travel earlier (come in earlier), evening travel later (leave later). Travel in the middle of the day, and any block shown as out of office, never moves.
- **Each invite is raised once:** a recurring series once as a whole, a one-off once per start time, so a moved meeting comes back. **So does one week of a series that the organiser changed on its own** (an exception), once the rest of the series is answered: it's raised and answered on its own. Answering the series doesn't answer such a week, so accepting a new series also accepts any changed weeks in it. An invite answered in Outlook drops off the queue by itself.
- **Bank holidays are days off too** (1.10.0): an invite on one is declined with `bank_holiday_note` ("Bank holiday"),
  or, inside a stretch of leave, the leave note. A bank holiday is any entry of yours, all day or a block of time,
  in a `bank_holiday_categories` category (`[invites]`' own, else `[one_to_ones]`', else "Holiday"), or titled
  "Bank holiday...". Before 1.10.0 invites saw only that title, so an all-day bank holiday entry alone was missed.
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
# bank_holiday_note = "Bank holiday"; bank_holiday_categories = ["Bank holiday"]   # default: [one_to_ones]', else ["Holiday"]

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

### Answering without asking, days off, and double bookings (1.11.0)

Each is off unless switched on in `[invites]`:

```toml
months_ahead = 3                 # how far ahead all of this looks (overrides days_ahead; leave both out to use calendar_months_ahead)
auto_accept = true               # accept an invite with no clash, and markers that hold no time
# auto_accept_outside = false    # ...from an outside organiser too
# auto_accept_optional = false   # ...where you're optional, or invited through a group, too
auto_move_blocks = true          # accept one that clashes only with your own blocks or end-of-day travel, and move them
decline_days_off = true          # decline meetings on your leave and bank holidays
# max_declines = 20              # never more days-off declines in one run
double_bookings = true           # queue meetings that overlap another meeting or a fixed block
```

- **`auto_accept`** answers a new invite the triage would recommend accepting with no clash, a whole series at
  once. All-day and free markers are accepted whoever sent them. **Asked as before:** an outside organiser, an
  invite where you're optional or invited through a group (unless allowed above), anything an `[[invite_rule]]`
  names, and any real clash.
- **`auto_move_blocks`** accepts an invite that clashes only with **your own blocks and travel at either end of the
  day**, and moves them to the nearest free slot within their limits; no one is told. A clash with a meeting you
  organise (moving it updates its attendees) is still asked.
- **`decline_days_off`:** your leave (`leave_pattern`) and bank holidays (`bank_holiday_categories`, or a "Bank
  holiday" title) are days off, never clashes. Each run declines meetings others organise that you've accepted or
  tentatively accepted on a day off, from now to `months_ahead`: **each occurrence once** (one you accept again is
  left alone; all-day and free entries are never declined, as they hold no time), with the leave note, or `bank_holiday_note` on a bank holiday. A half day (its title says half, AM or
  PM) counts only for meetings in its hours. A new invite wholly on your days off is declined; with `auto_accept`, a
  series free apart from them is accepted and those occurrences declined.
- **`double_bookings`** queues, once, each meeting that overlaps another meeting (accepted, tentative, or yours) or a
  fixed block (out of office, `fixed_blocks`, travel in the middle of the day). Nothing is declined: decide which
  goes, change it in Outlook, then `keep <n>`. Your own movable blocks don't count, nor do days off. Clashes that
  happen the same way every time can be settled by rules: see the next section.
- **What it did** is recorded with the rest (`deleted`, and the session summary): "Accepted: no clash", "Accepted:
  your own blocks moved", "Accepted: declined on your days off", "Declined: on your days off", "Declined: on leave",
  "Declined: bank holiday". Records of what was declined are kept in `days_off_file`.
- An invite queued before these were switched on stays queued; only new ones are answered.

**What you organise on your days off (1.14.0)**, also off unless switched on, over the same days off and
`months_ahead`, each occurrence once, at most `max_declines` a run:

```toml
cancel_days_off = true           # a meeting you organise with one other person: cancel that occurrence
remove_blocks_days_off = true    # your own blocks (no one else invited): delete that occurrence; no one is told
raise_group_days_off = true      # a meeting you organise with two or more others: queue it for you, once
```

- **Cancelled** with `[one_to_ones]`' `my_note` ("Cancelling as I'm on leave"), or `bank_holiday_note` on a bank
  holiday. Your manager's 1:1, if you send the invite, is cancelled too (it's only protected from being moved). Your
  reports' 1:1s are left to `[one_to_ones]`, which may move them instead.
- **A group meeting is yours to decide:** it may go ahead without you, or someone else chair it. Cancel it, hand it
  over or leave it, then `keep <n>`.
- Your leave and bank holiday entries themselves, all-day entries and anything shown as free are never touched.
- `[one_to_ones]` with no `days_ahead` of its own now looks as far as `months_ahead` (it had stopped at the invites'
  `days_ahead`).

### Double-booking rules (1.13.0)

Some double bookings come up the same way every time, and you always settle them the same way. A
`[[double_booking_rule]]` says how, so they're settled before they reach the queue. Two examples: a monthly in-person
leadership day always beats a catch-up you hold with one of your peers (the catch-up moves), and always beats another
team's regular meeting (you decline that occurrence).

```toml
[invites]
double_bookings = true           # the rules need this: they settle what would otherwise be queued

[groups]                         # named lists of addresses, for rules to name
first_team = ['rich@example.com', 'kurt@example.com', 'sarah@example.com']

[double_bookings]
act = false                      # true: move and decline; false: only record what it would do
# max_actions = 20               # never more changes in one run

[[double_booking_rule]]
name = "In-person SLT beats a first-team catch-up"
keep = '^In Person Monthly Catchup- SysDev & PMO SLT'   # the meeting that stays
other = 'catch-?up'                                      # the meeting that goes (optional)
other_with = 'first_team'                                # ...has one person in it besides you, from this group (optional)
action = "move"

[[double_booking_rule]]
name = "In-person SLT beats ERT"
keep = '^In Person Monthly Catchup- SysDev & PMO SLT'
other = '^Regular ERT/Sys Dev Catchup'
action = "decline"
note = "Sorry, there's an in-person SysDev & PMO SLT that day"
```

```
mail-triage double-bookings      # each upcoming double booking, and what the rules would do; changes nothing
  Thu 19 Nov 11:00  In Person Monthly Catchup- SysDev & PMO SLT / GJ and RE catchup: MOVE "GJ and RE catchup" to Thu 19 Nov 09:30–10:00 [In-person SLT beats a first-team catch-up]
  Thu 19 Nov 13:00  In Person Monthly Catchup- SysDev & PMO SLT / Regular ERT/Sys Dev Catchup: DECLINE "Regular ERT/Sys Dev Catchup" (note: "Sorry, there's an in-person SysDev & PMO SLT that day") [In-person SLT beats ERT]
  Thu 19 Nov 12:00  In Person Monthly Catchup- SysDev & PMO SLT / Board prep: QUEUE (no rule)
```

- **Which rule.** For each double booking, the first rule where one meeting's subject matches `keep` and the other's
  matches `other` (if given) and, with `other_with`, has exactly one person in it besides you (attendees and
  organiser, rooms aside) who is in that `[groups]` list. Patterns ignore case. No rule: it's queued as before. A rule
  that fits both meetings (so which stays isn't clear) doesn't act.
- **`move`** only a meeting you organise (not one in `protected`, nor a block): that occurrence, never the series, the
  same length, to the nearest time when you and everyone in it are free (busy, out of office and tentative hold time;
  their free/busy comes from getSchedule, as for 1:1s), within `working_hours`, on a weekday of the same week that
  isn't one of your days off, and within `months_ahead` (or `days_ahead`). **The nearest time that day first; then
  the same time on the nearest other day; then the nearest time that week.** On a tie, the later. Attendees get the
  update, as from Outlook.
- **`decline`** only a meeting someone else organises (not one in `protected`): that occurrence, never the series,
  with `note`, sending a response unless the organiser asked for none.
- **When a rule can't act** (someone else's meeting to move, your own to decline, no free slot that week, an
  attendee's calendar that can't be read, or an occurrence a rule has already moved or declined once) the double
  booking is queued as before, with the reason in its `check`.
- **Each occurrence once.** What the rules did is kept in `double_bookings_file` (default
  `%LOCALAPPDATA%\mail-triage\double-bookings.json`), so an occurrence is never moved or declined twice; past ones are
  forgotten. **At most `max_actions` changes a run**; the rest wait for the next run.
- **`act = false`** changes nothing: each "would move" or "would decline" is recorded once (for `deleted` and the
  session summary, under "Double booking (preview): would move" / "would decline"), and the double booking is still
  queued, saying what the rule would do. With `act = true` they're recorded as "Double booking: moved" and "Double
  booking: declined", with the rule's name. `run --dry-run` changes and records nothing; `--writes journal` records
  the calendar writes instead of sending them, but (as for days-off declines) still counts them as done.
- **Keep all the `[[double_booking_rule]]` tables together** (as for every kind of `[[...]]` table): one split by
  another table is refused, rather than silently losing the rules before the break. Settings are checked strictly: an
  unknown key, an unknown group, an `action` other than move or decline, or a `note` on a move is an error.

## Sent Items: tidied by kind, never real mail

With a `[sent]` section (1.19.0), mail-triage tidies **Sent Items** too. It works by Graph's message type, never by
subject, so only these are ever touched:

| Kind | What it is (Graph's `meetingMessageType`) |
|---|---|
| `invites` | meeting requests and updates you sent (`meetingRequest`) |
| `cancellations` | cancellations you sent (`meetingCancelled`) |
| `responses` | your accepts, tentatives and declines (`meetingAccepted`, `meetingTenativelyAccepted`, `meetingDeclined`), and (1.20.0) Outlook's **Follow** notifications ("Following: …", sent when you follow a meeting you can't attend). Graph types those as responses but with no meetingMessageType it names, so they're told by their message class, `IPM.Schedule.Meeting.Resp.Tent.Follow`, never by subject; the preview shows them as "follows" |
| forwards | the Sent Items copy of a forward a `forward` rule made, when that rule has `file_sent` |

```toml
[sent]
invites = "immediately"        # "off" (the default), "immediately", "after_meeting" or "after_sending"
cancellations = "immediately"
responses = "after_sending"
responses_days = 7             # with after_meeting or after_sending: that many days after the meeting ends, or after you sent it
# sent_file = "%EA_LOCAL%/mail-triage-sent.json"   (top level: how far it has read, and what waits for its time)

[[rule]]                       # a forward rule: its Sent Items copies are moved (never deleted) beside the originals
action = "forward"
to = 'Suppliers/Notices'
file_sent = 'Suppliers/Notices'
```

- **A kind left out is off**, so a new `[sent]` does nothing until you choose. Forwards are filed whenever there's a
  `[sent]` and the rule has `file_sent`.
- **Never touched:** anything else (real correspondence, other meeting messages), Drafts and the Outbox. **A message
  with attachments is never deleted**, whatever its kind: it's skipped and listed. Forwards are only ever moved.
- **Deletes go to Deleted Items** (recoverable). Each delete and move is listed by `mail-triage deleted`, under
  "Sent Items: <kind>".
- **`mail-triage sent` previews:** per kind, how many, the oldest and newest, what would be deleted, moved, left waiting
  or skipped (and why). **`mail-triage sent --yes` does it.** The first `--yes` reads the whole of Sent Items; from
  then on each run (and the watcher) reads only what was sent since, plus what was waiting for its time. Until that
  first `--yes`, a run leaves Sent Items alone.
- `after_meeting` uses the meeting's end time as the message gives it (for a series, its first occurrence).
- A Sent Items problem is logged and never stops the triage run.


## Holds: a time kept for a meeting you'll send yourself

`mail-triage hold START END SUBJECT [--body TEXT | --body-file FILE] [--location TEXT] [--online] [--yes]` puts an event
in your own calendar, shown as busy, **with no attendees, ever**: there's no option to add any, and the write refuses an
event that has one, because an attendee is sent an invitation the moment the event exists. It's for holding a time
you've chosen (a slot-finding skill proposes it): you open the hold, add the people and send it yourself.

- START is `YYYY-MM-DDTHH:MM`, END `HH:MM` (or a full time), in local time; a past start, or an end before the start, is
  refused.
- **Without `--yes` it only shows what it would create.** With `--yes` it creates it, reads it back, prints its id and
  link, logs it, and lists it in `mail-triage deleted` under "Holds", next to what the rules moved and declined.
- `--online` makes it a Teams meeting; Microsoft adds the join details, which can't be removed afterwards.
- **A create that may have reached Microsoft is never retried**: if the call fails after it was sent, it says so and
  asks you to check the calendar, so a hold is never made twice.

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
  day of unknown half. Bank holidays: your entries in a `bank_holiday_categories` category, all day or a block of time
  (Outlook's UK holidays are in "Holiday"; until 1.10.0 only all-day ones counted), and entries named "Bank
  holiday". They count as a day off for both of you.
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

## Building

It's .NET 11, published as Native AOT. It needs [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)
checked out beside this repository (or set the `BackgroundToolsLib` property to a checkout elsewhere), then:

```
dotnet test -c Release
dotnet publish src/MailTriage.Console -c Release -r win-x64 -o out
dotnet publish src/MailTriage.Background -c Release -r win-x64 -o out
```

A tag `v*` builds and publishes a release.
