#!/usr/bin/env python3
"""Print the next X3 BLE hardware validation prompt.

Optionally pass the user's answer with --answer to get the exact record/debug
command for the next step.
"""

from __future__ import annotations

import argparse
import os
import re
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

PROMPTS = {
    "Page input": (
        "Open an EPUB and test both Free3 page-turn buttons.\n"
        "Reply exactly: both directions worked, only forward worked, or only back worked."
    ),
    "Reader sleep/wake": (
        "With the Free3 connected and page turning working, sleep the reader, wake it, "
        "do not open Bluetooth settings, wait up to 60 seconds, then try page turns.\n"
        "Reply exactly: reader sleep/wake worked or reader sleep/wake failed."
    ),
    "Remote sleep/off/on": (
        "With the Free3 connected and page turning working, let the Free3 sleep or turn it off, "
        "wake/turn it on, do not open Bluetooth settings, wait up to 60 seconds, then try page turns. "
        "The first press may only wake the remote.\n"
        "Reply exactly: remote sleep/off/on worked or remote sleep/off/on failed."
    ),
    "Long idle recovery": (
        "Leave the X3 and Free3 unused long enough to cover the idlefix10 long-idle failure class. "
        "Confirm the X3 auto-sleeps by its configured timeout, wake it and/or restart the Free3, "
        "do not open Bluetooth settings, then try page turns.\n"
        "Reply exactly: long idle recovery worked or long idle recovery failed."
    ),
    "Repeated cycles": (
        "Repeat at least 3 reader sleep/wake cycles and 3 Free3 sleep/off/on cycles. "
        "Watch for crashes, reboot loops, frozen UI, or FreeRTOS asserts.\n"
        "Reply exactly: 3+3 cycles no crash or cycles failed."
    ),
    "Guard behavior": (
        "If no reconnect crash happened during validation, record that the guard audit passed and "
        "manual reconnect remained available.\n"
        "Reply exactly: guard behavior passed or guard behavior failed."
    ),
}

PASS_ANSWERS = {
    "Reader sleep/wake": {
        "reader sleep/wake worked",
        "reader sleep wake worked",
        "sleep/wake worked",
        "sleep wake worked",
    },
    "Remote sleep/off/on": {
        "remote sleep/off/on worked",
        "remote sleep off on worked",
        "remote sleep worked",
    },
    "Long idle recovery": {
        "long idle recovery worked",
        "long idle worked",
        "2h idle worked",
        "2 hour idle worked",
    },
    "Repeated cycles": {
        "3+3 cycles no crash",
        "3+3 cycles mo crash",
        "3 reader cycles and 3 remote cycles no crash",
        "3 reader cycles and 3 remote cycles mo crash",
        "yes both",
        "yes both no crash",
    },
    "Guard behavior": {
        "guard behavior passed",
        "guard passed",
    },
}

FAIL_ANSWERS = {
    "Reader sleep/wake": {
        "reader sleep/wake failed",
        "reader sleep wake failed",
        "sleep/wake failed",
        "sleep wake failed",
    },
    "Remote sleep/off/on": {
        "remote sleep/off/on failed",
        "remote sleep off on failed",
        "remote sleep failed",
    },
    "Long idle recovery": {
        "long idle recovery failed",
        "long idle failed",
        "2h idle failed",
        "2 hour idle failed",
    },
    "Repeated cycles": {
        "cycles failed",
        "repeated cycles failed",
    },
    "Guard behavior": {
        "guard behavior failed",
        "guard failed",
    },
}

PASS_RECORDS = {
    "Reader sleep/wake": (
        'python3 scripts/record_x3_ble_validation_result.py "Reader sleep/wake" '
        'passed "sleep wake reconnect restored page turns without settings menu"'
    ),
    "Remote sleep/off/on": (
        'python3 scripts/record_x3_ble_validation_result.py "Remote sleep/off/on" '
        'passed "remote sleep/off/on reconnected without settings menu"'
    ),
    "Long idle recovery": (
        'python3 scripts/record_x3_ble_validation_result.py "Long idle recovery" '
        'passed "long idle auto sleep and reconnect recovered without settings menu"'
    ),
    "Repeated cycles": (
        'python3 scripts/record_x3_ble_validation_result.py "Repeated cycles" '
        'passed "3 reader cycles and 3 remote cycles completed, no crash"'
    ),
    "Guard behavior": (
        'python3 scripts/record_x3_ble_validation_result.py "Guard behavior" '
        'passed "guard audit passed; manual reconnect remained available after reconnect validation"'
    ),
}

FAIL_RECORDS = {
    "Reader sleep/wake": (
        'python3 scripts/record_x3_ble_validation_result.py "Reader sleep/wake" '
        'failed "sleep wake did not restore page turns without settings menu"'
    ),
    "Remote sleep/off/on": (
        'python3 scripts/record_x3_ble_validation_result.py "Remote sleep/off/on" '
        'failed "remote sleep/off/on did not reconnect without settings menu"'
    ),
    "Long idle recovery": (
        'python3 scripts/record_x3_ble_validation_result.py "Long idle recovery" '
        'failed "long idle auto sleep or reconnect recovery failed; inspect diagnostics"'
    ),
    "Repeated cycles": (
        'python3 scripts/record_x3_ble_validation_result.py "Repeated cycles" '
        'failed "cycles failed; capture crash/freeze/reconnect details"'
    ),
    "Guard behavior": (
        'python3 scripts/record_x3_ble_validation_result.py "Guard behavior" '
        'failed "guard behavior failed; manual reconnect or crash guard needs investigation"'
    ),
}


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


def page_input_answer(answer: str) -> int:
    normalized = " ".join(answer.strip().lower().split())
    if normalized == "both directions worked":
        print("Page input answer: both directions worked")
        print("Record command:")
        print('  python3 scripts/record_x3_ble_validation_result.py "Page input" passed "forward and back buttons turned pages reliably"')
        print("Next hardware gate after recording: Reader sleep/wake.")
        return 0

    if normalized in {"only forward worked", "only back worked"}:
        print(f"Page input answer: {normalized}")
        print("Do not record Page input as passed.")
        print("Suggested failure record command:")
        print(f'  python3 scripts/record_x3_ble_validation_result.py "Page input" failed "{normalized}; capture HID diagnostics before changing mapping"')
        print("If the X3 storage or SD is mounted, run:")
        print("  python3 scripts/read_x3_ble_diag.py --limit 120")
        return 1

    print("Unrecognized page-input answer.")
    print("Use exactly: both directions worked, only forward worked, or only back worked.")
    return 2


def generic_gate_answer(gate: str, answer: str) -> int:
    normalized = " ".join(answer.strip().lower().split())
    if gate == "Repeated cycles" and normalized in {
        "3 cycles no crash",
        "3 cycle no crash",
        "3 cycles mo crash",
        "cycles no crash",
    }:
        print("Ambiguous repeated-cycle answer.")
        print("This gate needs both sets: 3 reader sleep/wake cycles and 3 Free3 sleep/off/on cycles.")
        print("Reply exactly: 3+3 cycles no crash or cycles failed.")
        return 2

    if normalized in PASS_ANSWERS.get(gate, set()):
        print(f"{gate} answer: passed")
        print("Record command:")
        print(f"  {PASS_RECORDS[gate]}")
        if gate == "Guard behavior":
            print("Next step after recording: run scripts/audit_x3_ble_goal_completion.py")
        else:
            print("Next step after recording: run scripts/x3_ble_hardware_prompt.py")
        return 0

    if normalized in FAIL_ANSWERS.get(gate, set()):
        print(f"{gate} answer: failed")
        print("Do not record this gate as passed.")
        print("Suggested failure record command:")
        print(f"  {FAIL_RECORDS[gate]}")
        print("If the X3 storage or SD is mounted, run:")
        print("  python3 scripts/read_x3_ble_diag.py --limit 120")
        return 1

    print(f"Unrecognized answer for {gate}.")
    prompt = PROMPTS.get(gate)
    if prompt:
        print(prompt)
    return 2


def answer_for_gate(gate: str, answer: str) -> int:
    if gate == "Page input":
        return page_input_answer(answer)
    if gate in PASS_RECORDS:
        return generic_gate_answer(gate, answer)
    print(f"No answer parser for gate: {gate}")
    return 2


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--answer", help="Optional exact user answer for the current hardware gate.")
    parser.add_argument("--gate", choices=GATE_ORDER, help="Preview/interpret a specific gate instead of the next pending one.")
    args = parser.parse_args()

    gate = args.gate or next_gate()
    if gate is None:
        print("Next hardware gate: none")
        print("All hardware gates appear passed. Run scripts/audit_x3_ble_goal_completion.py")
        return 0

    print(f"Next hardware gate: {gate}")
    prompt = PROMPTS.get(gate)
    if prompt:
        print(prompt)

    if args.answer:
        return answer_for_gate(gate, args.answer)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
