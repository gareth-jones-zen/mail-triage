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

[[rule]]                  # first match wins, top to bottom
name = "Vendor newsletters"
sender = '@news\.example\.com$'          # regular expression on the sender's address, ignoring case
match = 'newsletter|digest'              # optional: on the subject and preview
action = "delete"                        # delete | review | check
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
mail-triage invite decline 5 --note "Clashes, sorry"
mail-triage move <event id> 2026-11-11T16:00        # move one of your own meetings or blocks, keeping its length
mail-triage leave                                   # meetings in your leave you haven't declined
mail-triage leave --decline --note "On leave"
```

| What it finds | Recommends |
|---|---|
| Falls in your leave (your own entries matching `leave_pattern`: Holiday, Annual leave, Bank holiday) | Decline |
| Clashes with something fixed: a meeting you've accepted, anything `protected`, a block in `fixed_blocks` | Decline (or propose another time) |
| Clashes only with things you can move: your own blocks, meetings you organise | Accept, and move them |
| A series where only some occurrences clash, or fall in leave | Accept, and decline those |
| All-day, or shown as free (a deadline marker, a notice) | Accept: it holds no time, so nothing clashes |
| No clash | Accept, noting a recurring series, being optional or invited through a group, over 50 people, or an outside organiser |

- **Nothing answers on its own.** The recommendation is advice; `invite accept|tentative|decline <n>` answers. Rules that act come later, one class of invite at a time, once the advice has proved itself.
- **`[[invite_rule]]` with `action = "skip"`** drops invites that need no response at all, such as a colleague's leave sent as an invite. Every condition given must hold.
- **Blocked-out time is assumed movable** unless it's there for something with a due date: name those in `fixed_blocks`. A meeting you organise counts as movable too (moving it sends attendees an update), so put any you never move, such as your own manager's 1:1 if you send it, in `protected`.
- **Each invite is raised once:** a recurring series once as a whole, a one-off once per start time, so a moved meeting comes back. An invite answered in Outlook drops off the queue by itself.
- **`delete` refuses an invite**; answer it instead. **`move` refuses someone else's meeting.**
- It needs `Calendars.ReadWrite`.

```toml
email = "you@example.com"        # defaults to login_hint
[invites]
days_ahead = 42
fixed_blocks = ['^Travel', '^Weekly report prep']
protected = ['^1:1 with my manager$']
# leave_pattern = '^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b'

[[invite_rule]]
name = "Colleagues' leave sent as invites"
subject = 'holiday|annual leave'
all_day = true
show_as = ["free", "oof"]
action = "skip"                  # or "review": always raise
# also: organiser, via_group, optional, min_people (all must hold)
```

## The queue file

JSON, so another program (an assistant, a script) can work from it. An invite's item also has
`"type": "invite"`, `"recommendation"`, `"clashes"` and `"occurrence"`, and its `folder` is `"Calendar"`:

```json
{"checked": "2026-11-10T09:00:00", "review": [{"n": 1, "folder": "Newsletters", "id": "<Graph id>",
  "key": "<internet message id>", "received": "2026-11-10T08:12:00Z", "from": "...", "subject": "...",
  "preview": "first 300 characters", "rule": "Vendor webinars", "check": "what a check found", "webLink": "..."}]}
```

## Tests

```
python -m unittest discover -s tests
```
