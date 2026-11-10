"""The settings file: which folders to triage, and the rules.

    login_hint = "you@example.com"          # optional
    tenant = "organizations"                # optional: a tenant id pins sign-in to one organisation
    msal_cache = "..."                      # optional: share another tool's sign-in
    queue_file = "..."                      # optional: the emails waiting for a decision (JSON)
    state_file = "..."                      # optional: every email ever queued, so none is raised twice
    log_file = "..."                        # optional
    notify = "new"                          # optional: "new" (default) also notifies new emails to review
                                            #   after a scheduled run; "errors" only notifies problems
    run_at = "08:30"                        # optional: when autostart's daily run is (it also runs at logon)

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
    log_file: pathlib.Path = LOCAL / "mail-triage.log"
    notify: str = "new"
    run_at: str = "08:30"

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
    run_at = str(raw.get("run_at", "08:30"))
    if not re.fullmatch(r"([01]\d|2[0-3]):[0-5]\d", run_at):
        raise ValueError(f"run_at in {path} should be a time such as \"08:30\".")
    notify = raw.get("notify", "new")
    if notify not in ("new", "errors"):
        raise ValueError(f"notify in {path} should be \"new\" or \"errors\".")
    config = Config(
        path=path, login_hint=raw.get("login_hint"), tenant=raw.get("tenant", "organizations"),
        msal_cache=_path(raw["msal_cache"], base) if raw.get("msal_cache") else None,
        queue_file=_path(raw["queue_file"], base) if raw.get("queue_file") else LOCAL / "queue.json",
        state_file=_path(raw["state_file"], base) if raw.get("state_file") else LOCAL / "raised.json",
        log_file=_path(raw["log_file"], base) if raw.get("log_file") else LOCAL / "mail-triage.log",
        notify=notify, run_at=run_at)

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
    if not config.folders:
        raise ValueError(f"{path} names no [[folder]] to triage.")
    return config


def create_default() -> pathlib.Path:
    if not DEFAULT_CONFIG.exists():
        DEFAULT_CONFIG.parent.mkdir(parents=True, exist_ok=True)
        DEFAULT_CONFIG.write_text(TEMPLATE, encoding="utf-8")
    return DEFAULT_CONFIG
