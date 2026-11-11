"""The settings file: which folders to triage, and the rules.

    login_hint = "you@example.com"          # optional
    tenant = "organizations"                # optional: a tenant id pins sign-in to one organisation
    msal_cache = "..."                      # optional: share another tool's sign-in
    queue_file = "..."                      # optional: the emails waiting for a decision (JSON)
    state_file = "..."                      # optional: every email ever queued, so none is raised twice
    deleted_file = "..."                    # optional: what the rules deleted, for `mail-triage deleted`
    log_file = "..."                        # optional
    notify = "new"                          # optional: "new" (default) also notifies new emails to review
                                            #   after a scheduled run; "errors" only notifies problems
    every_minutes = 15                      # optional: watch mode (autostart) runs this often...
    hours = "07:00-19:00"                   # ...between these times (and once at start, whatever the time)...
    weekdays = true                         # ...on Monday to Friday

    [[folder]]
    name = "Newsletters"                    # a mail folder's display name, anywhere in the tree
    since = "2026-09-17"                    # optional: never look at mail older than this

    [[rule]]                                # first match wins
    name = "Vendor newsletters"
    sender = '@news\\.example\\.com$'       # a regular expression on the sender's address, ignoring case
    match = 'newsletter|digest'             # optional: a regular expression on the subject and preview
    action = "delete"                       # delete (mark read, move to Deleted Items) | review | check
    check = "flow-failures"                 # for action = "check": which check
    why = "News, not action."               # optional, but say why: it's what makes a rule maintainable

    [[flow_check]]                          # for the flow-failures check: how to tell a flow has recovered
    flow = "Weekly report"                  # matched within the flow's name in the digest, ignoring case
    output = '~/OneDrive/Reports/report-*.html'      # what a successful run writes
    pending = '~/OneDrive/Reports/requests/*.txt'    # optional: what a run consumes

An email no rule matches is queued, never deleted. Paths may use ~ and %VARIABLES%; relative paths
are relative to the settings file. Regular expressions are Python's; write them in TOML's 'literal
strings' so backslashes are taken as they are.
"""

from __future__ import annotations

import os
import pathlib
import re
import tomllib
from dataclasses import dataclass, field

APPDATA = pathlib.Path(os.environ.get("APPDATA", pathlib.Path.home()))
LOCAL = pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home())) / "mail-triage"
DEFAULT_CONFIG = APPDATA / "mail-triage" / "config.toml"
ACTIONS = {"delete", "review", "check"}
ALIASES = {"resolve": "delete"}   # the first name for delete

TEMPLATE = """\
# mail-triage settings. See the README.
# login_hint = "you@example.com"

# The folders to triage. Point Outlook rules at these, and leave your inbox alone.
# [[folder]]
# name = "Newsletters"

# Rules, first match wins. Anything no rule matches is queued for you, never deleted.
# [[rule]]
# name = "Vendor newsletters"
# sender = '@news\\.example\\.com$'
# action = "delete"
# why = "News, not action."
"""


@dataclass
class Folder:
    name: str
    since: str | None = None


@dataclass
class Rule:
    name: str
    sender: str
    action: str
    match: str | None = None
    check: str | None = None
    why: str = ""

    def matches(self, sender: str, text: str) -> bool:
        return bool(re.search(self.sender, sender, re.I)) and (self.match is None or bool(re.search(self.match, text, re.I)))


@dataclass
class FlowCheck:
    flow: str
    output: str
    pending: str | None = None


@dataclass
class InviteRule:
    """Which invites need no response (action "skip"), or must always be raised ("review"). Every condition
    given must hold. Kept separate from mail rules: an invite is judged on time and who else is going, not
    on its sender (invites.py)."""
    name: str
    action: str = "skip"
    subject: str | None = None      # regular expression on the subject, ignoring case
    organiser: str | None = None    # regular expression on the organiser's name or address
    all_day: bool | None = None
    show_as: list | None = None     # e.g. ["free", "oof"]: how it would show in your calendar
    via_group: bool | None = None   # you're invited through a distribution list, not by name
    optional: bool | None = None    # you're listed as optional
    min_people: int | None = None
    why: str = ""


@dataclass
class Invites:
    days_ahead: int = 42
    fixed_blocks: list = field(default_factory=list)   # own blocks that can't move (regular expressions on subject)
    protected: list = field(default_factory=list)      # meetings never moved or declined for anything
    leave_pattern: str = r"^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b"
    rules: list = field(default_factory=list)


@dataclass
class Config:
    path: pathlib.Path
    folders: list[Folder] = field(default_factory=list)
    rules: list[Rule] = field(default_factory=list)
    flow_checks: list[FlowCheck] = field(default_factory=list)
    login_hint: str | None = None
    tenant: str = "organizations"
    msal_cache: pathlib.Path | None = None
    queue_file: pathlib.Path = LOCAL / "queue.json"
    state_file: pathlib.Path = LOCAL / "raised.json"
    deleted_file: pathlib.Path = LOCAL / "deleted.json"
    log_file: pathlib.Path = LOCAL / "mail-triage.log"
    notify: str = "new"
    every_minutes: int = 15
    hours: tuple = ("07:00", "19:00")
    weekdays: bool = True
    email: str | None = None             # your address (default: login_hint), for telling you from others in invites
    invites: Invites | None = None       # [invites]: triage meeting invites too (invites.py)

    def rule_for(self, sender: str, text: str) -> Rule | None:
        return next((rule for rule in self.rules if rule.matches(sender, text)), None)


def _path(value: str, base: pathlib.Path) -> pathlib.Path:
    path = pathlib.Path(os.path.expandvars(os.path.expanduser(value)))
    return path if path.is_absolute() else (base / path).resolve()


def _pattern(value: str, base: pathlib.Path) -> str:
    """A glob, expanded like a path but not resolved (resolve would touch the * parts)."""
    path = pathlib.Path(os.path.expandvars(os.path.expanduser(value)))
    return str(path if path.is_absolute() else base / path)


def load(path: pathlib.Path | str | None = None) -> Config:
    path = pathlib.Path(path or os.environ.get("MAIL_TRIAGE_CONFIG") or DEFAULT_CONFIG).resolve()
    if not path.exists():
        raise FileNotFoundError(f"No settings file at {path}. Run `mail-triage config` to create one.")
    raw = tomllib.loads(path.read_text(encoding="utf-8"))
    base = path.parent
    hours = str(raw.get("hours", "07:00-19:00"))
    if not re.fullmatch(r"([01]\d|2[0-3]):[0-5]\d-([01]\d|2[0-4]):[0-5]\d", hours):
        raise ValueError(f"hours in {path} should look like \"07:00-19:00\".")
    every = int(raw.get("every_minutes", 15))
    if every < 1:
        raise ValueError(f"every_minutes in {path} should be at least 1.")
    notify = raw.get("notify", "new")
    if notify not in ("new", "errors"):
        raise ValueError(f"notify in {path} should be \"new\" or \"errors\".")
    config = Config(
        path=path, login_hint=raw.get("login_hint"), tenant=raw.get("tenant", "organizations"),
        msal_cache=_path(raw["msal_cache"], base) if raw.get("msal_cache") else None,
        queue_file=_path(raw["queue_file"], base) if raw.get("queue_file") else LOCAL / "queue.json",
        state_file=_path(raw["state_file"], base) if raw.get("state_file") else LOCAL / "raised.json",
        deleted_file=_path(raw["deleted_file"], base) if raw.get("deleted_file") else LOCAL / "deleted.json",
        log_file=_path(raw["log_file"], base) if raw.get("log_file") else LOCAL / "mail-triage.log",
        notify=notify, every_minutes=every, hours=tuple(hours.split("-")), weekdays=bool(raw.get("weekdays", True)))

    for item in raw.get("folder", []):
        if not item.get("name"):
            raise ValueError(f"A [[folder]] in {path} has no name.")
        since = item.get("since")
        if since is not None and not re.fullmatch(r"\d{4}-\d{2}-\d{2}", str(since)):
            raise ValueError(f"since for folder {item['name']} in {path} should be a date such as \"2026-09-17\".")
        config.folders.append(Folder(item["name"], str(since) if since else None))

    from .checks import CHECKS   # here, not at the top: checks imports nothing from config at load time
    for i, item in enumerate(raw.get("rule", []), 1):
        name = item.get("name", f"rule {i}")
        if not item.get("sender"):
            raise ValueError(f"Rule {name!r} in {path} has no sender.")
        action = ALIASES.get(item.get("action", ""), item.get("action", ""))
        if action not in ACTIONS:
            raise ValueError(f"Rule {name!r} in {path} has action {item.get('action')!r}: use delete, review or check.")
        if action == "check" and item.get("check") not in CHECKS:
            raise ValueError(f"Rule {name!r} in {path} needs check = one of {', '.join(sorted(CHECKS))}.")
        for key in ("sender", "match"):
            if item.get(key):
                try:
                    re.compile(item[key])
                except re.error as error:
                    raise ValueError(f"Rule {name!r} in {path}: {key} isn't a valid regular expression ({error}).")
        config.rules.append(Rule(name=name, sender=item["sender"], action=action, match=item.get("match"),
                                 check=item.get("check"), why=item.get("why", "")))

    for item in raw.get("flow_check", []):
        if not item.get("flow") or not item.get("output"):
            raise ValueError(f"A [[flow_check]] in {path} needs flow and output.")
        config.flow_checks.append(FlowCheck(item["flow"], _pattern(item["output"], base),
                                            _pattern(item["pending"], base) if item.get("pending") else None))
    config.email = (raw.get("email") or raw.get("login_hint") or "").lower() or None
    if "invites" in raw:
        section = raw["invites"]
        if not config.email:
            raise ValueError(f"[invites] in {path} needs email (or login_hint) set, to tell you from other attendees.")
        rules = []
        for i, item in enumerate(raw.get("invite_rule", []), 1):
            name = item.get("name", f"invite rule {i}")
            if item.get("action", "skip") not in ("skip", "review"):
                raise ValueError(f"Invite rule {name!r} in {path}: action is skip or review (nothing acts on its own yet).")
            for key in ("subject", "organiser"):
                if item.get(key):
                    try:
                        re.compile(item[key])
                    except re.error as error:
                        raise ValueError(f"Invite rule {name!r} in {path}: {key} isn't a valid regular expression ({error}).")
            rules.append(InviteRule(name=name, action=item.get("action", "skip"), subject=item.get("subject"),
                                    organiser=item.get("organiser"), all_day=item.get("all_day"),
                                    show_as=item.get("show_as"), via_group=item.get("via_group"),
                                    optional=item.get("optional"), min_people=item.get("min_people"), why=item.get("why", "")))
        config.invites = Invites(days_ahead=int(section.get("days_ahead", 42)),
                                 fixed_blocks=list(section.get("fixed_blocks", [])),
                                 protected=list(section.get("protected", [])),
                                 leave_pattern=section.get("leave_pattern", Invites.leave_pattern), rules=rules)
    if not config.folders and not config.invites:
        raise ValueError(f"{path} names no [[folder]] to triage, and no [invites].")
    return config


def create_default() -> pathlib.Path:
    if not DEFAULT_CONFIG.exists():
        DEFAULT_CONFIG.parent.mkdir(parents=True, exist_ok=True)
        DEFAULT_CONFIG.write_text(TEMPLATE, encoding="utf-8")
    return DEFAULT_CONFIG
