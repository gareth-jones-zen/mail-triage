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
    mail-triage watch               run now, then every every_minutes in working hours; what autostart runs
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
