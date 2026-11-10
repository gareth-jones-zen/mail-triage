"""A daily run through Windows Task Scheduler (win-background): at run_at, and at logon.

It runs `pythonw -m mail_triage --config <settings> run --scheduled` directly, so there's no window,
it never opens a browser, and it notifies a problem (and, with notify = "new", new emails to review).
"""

from __future__ import annotations

import pathlib

from win_background import Task, python_command

TASK = "mail-triage run"
_task = Task(TASK, "Applies standing rules to mail folders (mail-triage run).")


def command(config_path: pathlib.Path) -> tuple[str, list]:
    return python_command("mail_triage", "--config", str(config_path.resolve()), "run", "--scheduled")


def enable(config_path: pathlib.Path, run_at: str) -> str:
    return _task.enable(*command(config_path), working_dir=config_path.resolve().parent,
                        daily_at=run_at, keep_running=False)


def disable() -> bool:
    return _task.disable()


def status() -> dict | None:
    return _task.status()
