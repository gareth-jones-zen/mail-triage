"""mail-triage: standing rules for your mail. Delete the routine; queue the rest for you, each email once.

    mail-triage config              create the settings file (if missing) and say where it is
    mail-triage sign-in             sign in to Microsoft (once; opens a browser)
    mail-triage run                 apply the rules: delete what they allow, queue the rest
    mail-triage run --dry-run       say what it would do, change nothing (needs only Mail.Read)
    mail-triage list                the queue, numbered (--json for the file as it is)
    mail-triage delete 2 5          delete queued emails, by number (or by Graph id)
    mail-triage keep 1 3            take them off the queue and leave them in the folder
    mail-triage clear               empty the queue (each email stays in its folder, never raised again)
    mail-triage deleted             what the rules deleted since the last clear, by rule (--all: the last 60 days)
    mail-triage which <sender> [subject]   which rule an email would meet

    With [invites] in the settings, unanswered meeting invites are queued too, each with a recommendation:
    mail-triage invite accept 3     answer a queued invite: accept, tentative or decline (a series as a whole)
        --note "..."                    a note to the organiser; --silent answers without telling them
    mail-triage move <event id> 2026-11-11T16:00 [16:50]   move one of your own meetings or blocks
    mail-triage leave [--decline]   meetings in your leave you haven't declined (the next 60 days), and decline them

    mail-triage watch              run now, then every every_minutes in working hours; what autostart runs
    mail-triage autostart on        the watcher at every logon, and now (off, status)
"""

from __future__ import annotations

import argparse
import json
import sys

from m365_graph import Client, SignInNeeded
from win_background import exclusive, key, logger, notify

from . import __version__, autostart, config as cfg, triage


def client(config: cfg.Config, interactive: bool | None = None) -> Client:
    kwargs = {"cache_path": config.msal_cache} if config.msal_cache else {}
    return Client(tenant=config.tenant, login_hint=config.login_hint, scopes=triage.SCOPES, interactive=interactive,
                  sign_in_hint="At a console, run: mail-triage sign-in", **kwargs)


def main(argv: list | None = None) -> int:
    # Subjects carry emoji, which a Windows console's code page can't print.
    for stream in (sys.stdout, sys.stderr):
        if stream and hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    parser = argparse.ArgumentParser(prog="mail-triage", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--config", help="settings file (default %%APPDATA%%\\mail-triage\\config.toml)")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("config", help="create the settings file if missing, and say where it is")
    sub.add_parser("sign-in", help="sign in to Microsoft (opens a browser)")
    p = sub.add_parser("run", help="apply the rules")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--scheduled", action="store_true", help="as autostart runs it: no browser, and notify")
    p = sub.add_parser("list", help="the queue")
    p.add_argument("--json", action="store_true")
    for name, text in (("delete", "delete queued emails"), ("resolve", "the old name for delete"),
                       ("keep", "take them off the queue, leave them in the folder")):
        sub.add_parser(name, help=text).add_argument("refs", nargs="+", help="numbers from `list`, or Graph ids")
    sub.add_parser("clear", help="empty the queue")
    sub.add_parser("watch", help="run now, then every every_minutes in working hours")
    p = sub.add_parser("deleted", help="what the rules deleted since the queue was last cleared")
    p.add_argument("--all", action="store_true", help="everything kept (60 days)")
    sub.add_parser("raised", help="the old name for clear")
    p = sub.add_parser("which", help="which rule an email would meet")
    p.add_argument("sender")
    p.add_argument("subject", nargs="?", default="")
    p = sub.add_parser("invite", help="answer queued invites")
    p.add_argument("action", choices=["accept", "tentative", "decline"])
    p.add_argument("refs", nargs="+", help="numbers from `list`")
    p.add_argument("--note", default="", help="a note to the organiser")
    p.add_argument("--silent", action="store_true", help="answer without sending a response")
    p = sub.add_parser("move", help="move one of your own meetings or blocks")
    p.add_argument("event")
    p.add_argument("start", help="yyyy-mm-ddTHH:MM")
    p.add_argument("end", nargs="?", help="HH:MM (default: keep its length)")
    p = sub.add_parser("leave", help="meetings in your leave you haven't declined")
    p.add_argument("--days", type=int, default=60)
    p.add_argument("--decline", action="store_true", help="decline them (each occurrence)")
    p.add_argument("--note", default="", help="a note to each organiser")
    p = sub.add_parser("autostart", help="run daily and at logon (Task Scheduler)")
    p.add_argument("action", choices=["on", "off", "status"])
    args = parser.parse_args(argv)

    if args.command == "config":
        print(args.config or cfg.create_default())
        return 0
    try:
        config = cfg.load(args.config)
    except (FileNotFoundError, ValueError) as error:
        print(error, file=sys.stderr)
        return 2
    log = logger(config.log_file, echo=not getattr(args, "scheduled", False), stamp_echo=False)

    if args.command == "autostart":
        return _autostart(args.action, config)
    if args.command == "watch":
        from .watch import watch
        return watch(config, lambda c: client(c, interactive=False))
    if args.command == "which":
        rule = config.rule_for(args.sender, args.subject)
        print(f"{rule.name}: {rule.action}" + (f" ({rule.check})" if rule.check else "") + (f"\n  {rule.why}" if rule.why else "")
              if rule else "No rule: it would be queued for review.")
        return 0
    if args.command == "list":
        items = triage.queue(config)
        if args.json:
            print(json.dumps(items, indent=2, ensure_ascii=False))
        for item in [] if args.json else items:
            print(f"{item.get('n', '?'):>3}. {item['received'][:10]}  {item['from']}\n     {item['subject']}\n"
                  f"     [{item['rule']}]" + (f" {item['check']}" if item.get("check") else ""))
        if not items and not args.json:
            print("Nothing waiting.")
        gone = triage.deleted(config)
        if gone and not args.json:
            print(f"\n{len(gone)} deleted by the rules since the queue was last cleared: `mail-triage deleted`.")
        return 0
    if args.command == "deleted":
        return _deleted(triage.deleted(config, args.all))
    if args.command in ("clear", "raised", "keep"):
        # Locked like a run, so a run in progress can't write back a queue that's just been cleared (10 Nov 2026).
        with exclusive(f"mail-triage-run-{key(config.path)}", 600, cfg.LOCAL, what="triage run"):
            if args.command == "keep":
                triage.keep(config, args.refs, log)
            else:
                triage.clear(config, log)
        return 0

    try:
        graph = client(config, interactive=False if getattr(args, "scheduled", False) else None)
        if args.command == "sign-in":
            graph.token(triage.SCOPES, interactive=True)
            print("Signed in.")
            return 0
        if args.command in ("invite", "move", "leave"):
            if not config.invites:
                print(f"{config.path} has no [invites] section.", file=sys.stderr)
                return 2
            return _calendar(args, config, graph, log)
        with exclusive(f"mail-triage-run-{key(config.path)}", 600, cfg.LOCAL, what="triage run"):
            if args.command in ("delete", "resolve"):
                triage.delete(config, triage.Mailbox(graph), args.refs, log)
                return 0
            report = triage.run(config, triage.Mailbox(graph, args.dry_run), args.dry_run, log)
        if args.scheduled and report["new"] and config.notify == "new":
            notify(f"{report['new']} email{'s' if report['new'] > 1 else ''} to review", ["mail-triage list"])
        return 0
    except SignInNeeded as error:
        log(f"sign-in needed: {error}")
        if getattr(args, "scheduled", False):
            notify("mail-triage needs a sign-in", [str(error)[:300]])
        return 1
    except LookupError as error:
        log(str(error))
        return 2
    except Exception as error:
        log(f"failed: {type(error).__name__}: {error}")
        if getattr(args, "scheduled", False):
            notify("mail-triage failed", [f"{type(error).__name__}: {error}"[:300], f"Log: {config.log_file}"])
        return 1


def _calendar(args, config: cfg.Config, graph, log) -> int:
    import datetime as dt
    from . import invites
    calendar = invites.Calendar(graph)
    if args.command == "invite":
        with exclusive(f"mail-triage-run-{key(config.path)}", 600, cfg.LOCAL, what="triage run"):
            triage.answer(config, calendar, args.refs, args.action, args.note, not args.silent, log)
        return 0
    if args.command == "move":
        event = calendar.event(args.event)
        if not event.get("isOrganizer"):
            print("That's someone else's meeting: it can't be moved, only declined with a proposed time.", file=sys.stderr)
            return 2
        old_start, old_end = invites._times(event)
        start = dt.datetime.fromisoformat(args.start)
        end = dt.datetime.combine(start.date(), dt.time.fromisoformat(args.end)) if args.end else start + (old_end - old_start)
        calendar.move(args.event, start, end)
        log(f"moved  {event.get('subject')}: {old_start:%a %d %b %H:%M} -> {start:%a %d %b %H:%M}–{end:%H:%M}"
            + ("" if len(invites._people(event)) <= 1 else " (attendees get an update)"))
        return 0
    found = invites.leave_sweep(config, calendar, dt.date.today(), args.days)
    if not found:
        print(f"Nothing in your leave over the next {args.days} days that you haven't declined.")
        return 0
    for e in found:
        start, end = invites._times(e)
        print(f"  {start:%a %d %b %H:%M}–{end:%H:%M}  {e.get('subject')}  ({invites._organiser(e)[0]}; "
              f"{(e.get('responseStatus') or {}).get('response')})")
        if args.decline:
            calendar.respond(e["id"], "decline", args.note)
    print(f"{len(found)} meeting(s) in your leave" + (": declined." if args.decline else ". `--decline` declines them."))
    if args.decline:
        log(f"leave sweep: declined {len(found)} meeting(s)")
    return 0


def _deleted(items: list) -> int:
    if not items:
        print("Nothing deleted by the rules since the queue was last cleared.")
        return 0
    by_rule = {}
    for item in items:
        by_rule.setdefault(item["rule"] or "(no rule)", []).append(item)
    print(f"{len(items)} deleted, {items[0]['when'][:16].replace('T', ' ')} to {items[-1]['when'][:16].replace('T', ' ')}:")
    for rule, group in sorted(by_rule.items(), key=lambda kv: -len(kv[1])):
        print(f"\n  {rule} ({len(group)})")
        for item in group:
            print(f"    {item['received'][:10]}  {item['from']}: {item['subject'][:90]}")
    return 0


def _autostart(action: str, config: cfg.Config) -> int:
    if action == "on":
        command = autostart.enable(config.path)
        print(f"Autostart is on: '{autostart.TASK}' runs at every logon, and has started now. It triages at once, then "
              f"every {config.every_minutes} minutes, {'-'.join(config.hours)}{' Mon-Fri' if config.weekdays else ''}.\n"
              f"  {command}")
    elif action == "off":
        print("Autostart is off." if autostart.disable() else "Autostart was already off.")
    else:
        info = autostart.status()
        if not info:
            print("Autostart is off. `mail-triage autostart on` turns it on.")
        else:
            print(f"Autostart is on ('{autostart.TASK}').")
            for name in ("state", "last_run", "last_result", "next_run", "command"):
                print(f"  {name.replace('_', ' '):<12} {info.get(name, '')}")
    return 0
