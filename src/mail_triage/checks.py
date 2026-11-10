"""Checks: an email that may need you, or may not.

A rule with action = "check" runs one of these on the email. Each returns (nothing to do?, why):
True deletes the email as a "delete" rule would, False queues it with the why attached, so whoever
reviews it sees what the check found.

flow-failures (10 Nov 2026): Power Automate's "flow(s) have failed" digest. A digest only matters if
the flow is still failing. If it has worked since, there's nothing to do. Power Automate doesn't say
whether it has, so each flow needs a [[flow_check]] in the settings saying what a good run leaves
behind. A flow without one is raised: the check never guesses that a flow is fine.
"""

from __future__ import annotations

import datetime as dt
import glob
import os
import re


def flow_failures(body_html: str) -> list:
    """[(flow name, failure count)] from the digest's table of flows."""
    flows = []
    for row in re.findall(r"(?is)<tr.*?</tr>", body_html):
        cells = [re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", cell)).strip() for cell in re.findall(r"(?is)<td.*?</td>", row)]
        if len(cells) == 2 and cells[0] and cells[1].isdigit():
            flows.append((cells[0], int(cells[1])))
    return flows


def _newest(pattern: str) -> float | None:
    times = [os.path.getmtime(path) for path in glob.glob(pattern)]
    return max(times) if times else None


def flow_recovered(check, received: dt.datetime) -> tuple:
    """Has the flow worked since? With `pending`: a good run consumes what's pending and writes the output,
    so it has worked if nothing pending is newer than the newest output. Without: if its output is newer
    than the digest itself."""
    latest = _newest(check.output)
    if latest is None:
        return False, "nothing it writes has ever been found"
    when = f"{dt.datetime.fromtimestamp(latest):%d %b %H:%M}"
    if check.pending:
        waiting = [path for path in glob.glob(check.pending) if os.path.getmtime(path) > latest]
        if waiting:
            return False, f"{len(waiting)} request(s) since its last success ({when}) haven't been picked up"
        return True, f"it has worked since: its output last came back {when}"
    if latest > received.timestamp():
        return True, f"it has worked since the digest: its output was last written {when}"
    return False, f"its output was last written {when}, before the digest"


def check_flow_failures(message: dict, body_html: str, config) -> tuple:
    flows = flow_failures(body_html)
    if not flows:
        return False, "couldn't read the digest's list of flows"
    received = dt.datetime.fromisoformat(message.get("receivedDateTime", "1970-01-01T00:00:00Z").replace("Z", "+00:00"))
    findings, fine = [], True
    for name, count in flows:
        check = next((c for c in config.flow_checks if c.flow.lower() in name.lower()), None)
        ok, why = flow_recovered(check, received) if check else (False, "no [[flow_check]] for this flow, so can't tell whether it has worked since")
        fine = fine and ok
        findings.append(f"{name} ({count} failures): {why}")
    return fine, "; ".join(findings)


# name -> (needs the body?, function(message, body_html, config) -> (fine, why))
CHECKS = {"flow-failures": (True, check_flow_failures)}
