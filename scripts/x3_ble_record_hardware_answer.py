#!/usr/bin/env python3
"""Record an exact X3 BLE hardware-test answer.

This is the command to run after a physical X3/Free3 test result comes back
from the user. It only accepts the narrow answers used by
`x3_ble_hardware_prompt.py`, records the current hardware gate, and prints the
next prompt.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
VALIDATION_DOC = Path(os.environ.get(
    "X3_BLE_VALIDATION_DOC",
    str(ROOT / "docs/x3-ble-idlefix15-hardware-validation.md"),
))

GATE_ORDER = [
    "Flash",
    "Boot",
    "Manual reconnect",
    "Page input",
    "Reader sleep/wake",
    "Remote sleep/off/on",
    "Long idle recovery",
    "Repeated cycles",
    "Guard behavior",
]

ANSWERS: dict[str, dict[str, tuple[str, str]]] = {
    "Page input": {
        "both directions worked": (
            "passed",
            "forward and back buttons turned pages reliably",
        ),
        "only forward worked": (
            "failed",
            "only forward worked; capture HID diagnostics before changing mapping",
        ),
        "only back worked": (
            "failed",
            "only back worked; capture HID diagnostics before changing mapping",
        ),
    },
    "Reader sleep/wake": {
        "reader sleep/wake worked": (
            "passed",
            "sleep wake reconnect restored page turns without settings menu",
        ),
        "reader sleep wake worked": (
            "passed",
            "sleep wake reconnect restored page turns without settings menu",
        ),
        "sleep/wake worked": (
            "passed",
            "sleep wake reconnect restored page turns without settings menu",
        ),
        "sleep wake worked": (
            "passed",
            "sleep wake reconnect restored page turns without settings menu",
        ),
        "reader sleep/wake failed": (
            "failed",
            "sleep wake did not restore page turns without settings menu",
        ),
        "reader sleep wake failed": (
            "failed",
            "sleep wake did not restore page turns without settings menu",
        ),
        "sleep/wake failed": (
            "failed",
            "sleep wake did not restore page turns without settings menu",
        ),
        "sleep wake failed": (
            "failed",
            "sleep wake did not restore page turns without settings menu",
        ),
    },
    "Remote sleep/off/on": {
        "remote sleep/off/on worked": (
            "passed",
            "remote sleep/off/on reconnected without settings menu",
        ),
        "remote sleep off on worked": (
            "passed",
            "remote sleep/off/on reconnected without settings menu",
        ),
        "remote sleep worked": (
            "passed",
            "remote sleep/off/on reconnected without settings menu",
        ),
        "remote sleep/off/on failed": (
            "failed",
            "remote sleep/off/on did not reconnect without settings menu",
        ),
        "remote sleep off on failed": (
            "failed",
            "remote sleep/off/on did not reconnect without settings menu",
        ),
        "remote sleep failed": (
            "failed",
            "remote sleep/off/on did not reconnect without settings menu",
        ),
    },
    "Long idle recovery": {
        "long idle recovery worked": (
            "passed",
            "long idle auto sleep and reconnect recovered without settings menu",
        ),
        "long idle worked": (
            "passed",
            "long idle auto sleep and reconnect recovered without settings menu",
        ),
        "2h idle worked": (
            "passed",
            "long idle auto sleep and reconnect recovered without settings menu",
        ),
        "2 hour idle worked": (
            "passed",
            "long idle auto sleep and reconnect recovered without settings menu",
        ),
        "long idle recovery failed": (
            "failed",
            "long idle auto sleep or reconnect recovery failed; inspect diagnostics",
        ),
        "long idle failed": (
            "failed",
            "long idle auto sleep or reconnect recovery failed; inspect diagnostics",
        ),
        "2h idle failed": (
            "failed",
            "long idle auto sleep or reconnect recovery failed; inspect diagnostics",
        ),
        "2 hour idle failed": (
            "failed",
            "long idle auto sleep or reconnect recovery failed; inspect diagnostics",
        ),
    },
    "Repeated cycles": {
        "3+3 cycles no crash": (
            "passed",
            "3 reader cycles and 3 remote cycles completed, no crash",
        ),
        "3+3 cycles mo crash": (
            "passed",
            "3 reader cycles and 3 remote cycles completed, no crash",
        ),
        "3 reader cycles and 3 remote cycles no crash": (
            "passed",
            "3 reader cycles and 3 remote cycles completed, no crash",
        ),
        "3 reader cycles and 3 remote cycles mo crash": (
            "passed",
            "3 reader cycles and 3 remote cycles completed, no crash",
        ),
        "yes both": (
            "passed",
            "user confirmed both required sets: 3 reader cycles and 3 remote cycles completed, no crash",
        ),
        "yes both no crash": (
            "passed",
            "user confirmed both required sets: 3 reader cycles and 3 remote cycles completed, no crash",
        ),
        "cycles failed": (
            "failed",
            "cycles failed; capture crash/freeze/reconnect details",
        ),
        "repeated cycles failed": (
            "failed",
            "cycles failed; capture crash/freeze/reconnect details",
        ),
    },
    "Guard behavior": {
        "guard behavior passed": (
            "passed",
            "guard audit passed; manual reconnect remained available after reconnect validation",
        ),
        "guard passed": (
            "passed",
            "guard audit passed; manual reconnect remained available after reconnect validation",
        ),
        "guard behavior failed": (
            "failed",
            "guard behavior failed; manual reconnect or crash guard needs investigation",
        ),
        "guard failed": (
            "failed",
            "guard behavior failed; manual reconnect or crash guard needs investigation",
        ),
    },
}


def normalize(value: str) -> str:
    return " ".join(value.strip().lower().split())


def parse_rows() -> dict[str, str]:
    rows: dict[str, str] = {}
    pattern = re.compile(r"^\|\s*([^|]+?)\s*\|[^|]*\|[^|]*\|\s*([^|]+?)\s*\|$")
    for line in VALIDATION_DOC.read_text(encoding="utf-8").splitlines():
        match = pattern.match(line)
        if not match:
            continue
        gate = match.group(1).strip()
        result = match.group(2).strip()
        if gate in GATE_ORDER:
            rows[gate] = result
    return rows


def result_passed(result: str) -> bool:
    normalized = result.lower()
    return normalized.startswith("passed") or normalized.startswith("pass ")


def next_gate() -> str | None:
    rows = parse_rows()
    for gate in GATE_ORDER:
        result = rows.get(gate, "")
        if not result_passed(result):
            return gate
    return None


def record_result(gate: str, status: str, evidence: str, dry_run: bool) -> int:
    command = [
        sys.executable,
        "scripts/record_x3_ble_validation_result.py",
    ]
    if dry_run:
        command.append("--dry-run")
    command.extend([gate, status, evidence])
    result = subprocess.run(command, cwd=ROOT, text=True, check=False)
    return result.returncode


def print_next_prompt(dry_run: bool) -> None:
    if dry_run:
        print("dry-run: validation document unchanged")
        return
    subprocess.run([sys.executable, "scripts/x3_ble_hardware_prompt.py"], cwd=ROOT, check=False)


def repeated_cycles_answer_is_ambiguous(answer: str) -> bool:
    return answer in {
        "3 cycles no crash",
        "3 cycle no crash",
        "3 cycles mo crash",
        "cycles no crash",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("answer", help="Exact answer from the current hardware gate.")
    parser.add_argument("--gate", choices=GATE_ORDER, help="Record a specific gate instead of the next pending one.")
    parser.add_argument("--dry-run", action="store_true", help="Validate and print without editing the validation doc.")
    args = parser.parse_args()

    gate = args.gate or next_gate()
    if gate is None:
        print("All hardware gates appear passed. Run scripts/audit_x3_ble_goal_completion.py")
        return 0

    normalized_answer = normalize(args.answer)
    if gate == "Repeated cycles" and repeated_cycles_answer_is_ambiguous(normalized_answer):
        print("Ambiguous repeated-cycle answer.")
        print("This gate needs both sets: 3 reader sleep/wake cycles and 3 Free3 sleep/off/on cycles.")
        print("Reply exactly: 3+3 cycles no crash or cycles failed.")
        return 2

    mapping = ANSWERS.get(gate, {})
    action = mapping.get(normalized_answer)
    if action is None:
        print(f"Unrecognized answer for {gate}: {args.answer}")
        subprocess.run([sys.executable, "scripts/x3_ble_hardware_prompt.py", "--gate", gate],
                       cwd=ROOT, check=False)
        return 2

    status, evidence = action
    code = record_result(gate, status, evidence, args.dry_run)
    if code != 0:
        return code

    if status == "failed":
        prefix = "Would record" if args.dry_run else "Recorded"
        print(f"{prefix} a failed hardware gate. If storage or SD is mounted, inspect diagnostics:")
        print("  python3 scripts/read_x3_ble_diag.py --limit 120")

    print_next_prompt(args.dry_run)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
