"""Watch mode: triage at start, then every every_minutes within working hours.

- **The first run is at start, whatever the time**, so logging on in the evening still clears the
  day's mail; after that, only within `hours` (and Monday to Friday, with `weekdays`).
- **One watcher per settings file.** A second exits at once (code 3).
- **The settings file is re-read each run**, so a new rule needs no restart.
- **It never opens a browser.** A lapsed sign-in is logged and notified once, and it keeps trying.
- **A failure is notified once, until a run succeeds again**, not every 15 minutes.
- **It logs `alive` once an hour**, so a silent log means a stuck watcher, not a quiet inbox.
- **With notify = "new"**, new emails to review are notified (one notification per run that found any).
"""

from __future__ import annotations

import datetime as dt
import time

from m365_graph import SignInNeeded
from win_background import exclusive, key, logger, notify, single_instance

from . import config as cfg, triage

ALREADY_RUNNING = 3


def due(config: cfg.Config, now: dt.datetime) -> bool:
    if config.weekdays and now.weekday() >= 5:
        return False
    start, end = config.hours
    return start <= now.strftime("%H:%M") < end


def watch(config: cfg.Config, client_for) -> int:
    """client_for(config) -> an m365_graph Client that never opens a browser."""
    log = logger(config.log_file)
    lock = single_instance(f"mail-triage-watch-{key(config.path)}", cfg.LOCAL)   # held until the process ends
    if lock is None:
        log("another watcher is already running for these settings; not starting a second")
        return ALREADY_RUNNING
    log(f"watching {len(config.folders)} folder(s) every {config.every_minutes} min, "
        f"{'-'.join(config.hours)}{' Mon-Fri' if config.weekdays else ''}, and once now")
    next_run, beat, failing, first = 0.0, time.time(), None, True
    while True:
        if time.time() >= next_run:
            next_run = time.time() + config.every_minutes * 60
            if first or due(config, dt.datetime.now()):
                first = False
                try:
                    config = cfg.load(config.path)
                    with exclusive(f"mail-triage-run-{key(config.path)}", 600, cfg.LOCAL, what="triage run"):
                        report = triage.run(config, triage.Mailbox(client_for(config)), False, log)
                    if report["new"] and config.notify == "new":
                        notify(f"{report['new']} email{'s' if report['new'] > 1 else ''} to review", ["mail-triage list"])
                    failing = None
                except SignInNeeded as error:
                    log(f"sign-in needed: {error}")
                    if failing != "sign-in":
                        notify("mail-triage needs a sign-in", [str(error)[:300]])
                    failing = "sign-in"
                except Exception as error:
                    log(f"run failed: {type(error).__name__}: {error}")
                    if failing != type(error).__name__:
                        notify("mail-triage couldn't run", [f"{type(error).__name__}: {error}"[:300], f"Log: {config.log_file}"])
                    failing = type(error).__name__
        if time.time() - beat >= 3600:
            beat = time.time()
            log("alive")
        time.sleep(30)
