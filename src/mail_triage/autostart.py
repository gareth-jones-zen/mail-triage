"""The watcher at logon, through Windows Task Scheduler (win-background).

It runs `pythonw -m mail_triage --config <settings> watch` directly, so there's no window, and stopping
the task stops the watcher itself. Restarted if it crashes; never stopped for time. The watcher runs
once at start and then every every_minutes within working hours (watch.py).
"""

from __future__ import annotations

import pathlib

from win_background import Task, python_command

TASK = "mail-triage watch"
_task = Task(TASK, "Applies standing rules to mail folders as mail arrives (mail-triage watch).",
             results={"3": "another watcher was already running"})
OLD_TASK = "mail-triage run"   # 0.1.0's daily task


def command(config_path: pathlib.Path) -> tuple[str, list]:
    return python_command("mail_triage", "--config", str(config_path.resolve()), "watch")


def enable(config_path: pathlib.Path) -> str:
    Task(OLD_TASK).disable()
    return _task.enable(*command(config_path), working_dir=config_path.resolve().parent)


def disable() -> bool:
    return _task.disable() | Task(OLD_TASK).disable()


def status() -> dict | None:
    return _task.status()
