#!/usr/bin/env python3
"""Show the next pending X3 BLE hardware validation gate."""

from __future__ import annotations

import re
import os
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
RECORD_EXAMPLES = {
    "Flash": 'scripts/flash_record_x3_ble_idlefix15.sh',
    "Boot": 'scripts/record_x3_ble_validation_result.py "Boot" passed "CrossPoint UI responsive for 2 minutes"',
    "Manual reconnect": 'scripts/record_x3_ble_validation_result.py "Manual reconnect" passed "Reconnect Remote succeeded without freeze/reboot"',
    "Page input": 'scripts/record_x3_ble_validation_result.py "Page input" passed "forward and back buttons turned pages reliably"',
    "Reader sleep/wake": 'scripts/record_x3_ble_validation_result.py "Reader sleep/wake" passed "sleep wake reconnect restored page turns"',
    "Remote sleep/off/on": 'scripts/record_x3_ble_validation_result.py "Remote sleep/off/on" passed "remote sleep/off/on reconnected without settings menu"',
    "Long idle recovery": 'scripts/record_x3_ble_validation_result.py "Long idle recovery" passed "long idle auto sleep and reconnect recovered without settings menu"',
    "Repeated cycles": 'scripts/record_x3_ble_validation_result.py "Repeated cycles" passed "3 reader cycles and 3 remote cycles, no crash"',
    "Guard behavior": 'scripts/record_x3_ble_validation_result.py "Guard behavior" passed "guard audit passed; manual reconnect remained available after reconnect validation"',
}
GATE_NOTES = {
    "Manual reconnect": (
        "Do not use Scan. If the Free3 already shows connected after boot, "
        "that can pass if the UI did not freeze or reboot."
    ),
    "Page input": (
        "Test inside a book and confirm one exact result: both directions worked, "
        "only forward worked, or only back worked."
    ),
    "Reader sleep/wake": "Do not open Bluetooth settings after wake; wait up to 60 seconds and try page turning.",
    "Remote sleep/off/on": "Do not open Bluetooth settings; the first press may only wake the remote.",
    "Long idle recovery": "Confirm the X3 auto-sleeps by its configured timeout, then wake/restart Free3 and test without Bluetooth settings.",
    "Repeated cycles": "Use at least 3 reader sleep/wake cycles and 3 remote sleep/off/on cycles.",
    "Guard behavior": "If no reconnect crash happened, record the local guard audit plus manual reconnect availability.",
}


def parse_rows() -> dict[str, dict[str, str]]:
    rows: dict[str, dict[str, str]] = {}
    pattern = re.compile(r"^\|\s*([^|]+?)\s*\|([^|]+?)\|([^|]+?)\|([^|]+?)\|$")
    for line in VALIDATION_DOC.read_text(encoding="utf-8").splitlines():
        match = pattern.match(line)
        if not match:
            continue
        gate = match.group(1).strip()
        if gate not in GATE_ORDER:
            continue
        rows[gate] = {
            "test": match.group(2).strip(),
            "pass": match.group(3).strip(),
            "result": match.group(4).strip(),
        }
    return rows


def passed(result: str) -> bool:
    return result.lower().startswith("passed") or result.lower().startswith("pass ")


def main() -> int:
    rows = parse_rows()
    for gate in GATE_ORDER:
        row = rows.get(gate)
        if row is None:
            print(f"Next gate unavailable: missing validation row {gate}")
            return 1
        if not passed(row["result"]):
            print(f"Next pending gate: {gate}")
            print(f"Test: {row['test']}")
            print(f"Pass condition: {row['pass']}")
            print(f"Current result: {row['result']}")
            note = GATE_NOTES.get(gate)
            if note:
                print(f"Note: {note}")
            print(f"Record command shape: {RECORD_EXAMPLES[gate]}")
            return 0

    print("Next pending gate: none")
    print("All validation gates appear passed. Run scripts/audit_x3_ble_goal_completion.py")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
