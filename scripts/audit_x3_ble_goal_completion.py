#!/usr/bin/env python3
"""Completion audit for the X3 BLE page-turner goal.

This script is intentionally stricter than the local artifact verifier. It must
fail until the current idlefix15 binary has been flashed and the X3 plus Free3
hardware validation table contains real passing results.
"""

from __future__ import annotations

import hashlib
import os
import re
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BIN = Path(os.environ.get(
    "X3_BLE_FIRMWARE_BIN",
    str(Path.home() / "Downloads/crosspoint-x3-ble-idlefix15.bin"),
))
VALIDATION_DOC = ROOT / "docs/x3-ble-idlefix15-hardware-validation.md"
EXPECTED_SHA256 = "5b23aee2453df26f35fc837ea580eedc3b7c8fa7deeb0f01092e9b7ff7b2949f"
EXPECTED_SIZE = 0x5b62e0
REQUIRED_GATES = [
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
GATE_EVIDENCE_HINTS = {
    "Flash": ["verify-flash", "app0", "app1"],
    "Boot": ["responsive"],
    "Manual reconnect": ["reconnect"],
    "Page input": ["forward", "back"],
    "Reader sleep/wake": ["sleep", "wake"],
    "Remote sleep/off/on": ["remote"],
    "Long idle recovery": ["long idle", "sleep", "reconnect"],
    "Repeated cycles": ["3 reader", "3 remote", "no crash"],
    "Guard behavior": ["guard", "manual reconnect"],
}


def run(command: list[str]) -> tuple[int, str]:
    result = subprocess.run(command, cwd=ROOT, text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, check=False)
    return result.returncode, result.stdout


def ok(message: str) -> None:
    print(f"ok - {message}")


def fail(message: str, failures: list[str]) -> None:
    print(f"MISSING - {message}")
    failures.append(message)


def validation_results() -> dict[str, str]:
    if not VALIDATION_DOC.exists():
        return {}

    results: dict[str, str] = {}
    pattern = re.compile(r"^\|\s*([^|]+?)\s*\|[^|]*\|[^|]*\|\s*([^|]+?)\s*\|$")
    for line in VALIDATION_DOC.read_text(encoding="utf-8").splitlines():
        match = pattern.match(line)
        if not match:
            continue
        gate = match.group(1).strip()
        result = match.group(2).strip()
        if gate in REQUIRED_GATES:
            results[gate] = result
    return results


def result_passed(result: str) -> bool:
    normalized = result.strip().lower()
    return normalized.startswith("pass") or normalized.startswith("passed")


def result_has_evidence(gate: str, result: str) -> tuple[bool, str]:
    if ":" not in result:
        return False, "missing evidence text after ':'"

    evidence = result.split(":", 1)[1].strip().lower()
    if len(evidence) < 12:
        return False, "evidence text is too short"

    missing_hints = [hint for hint in GATE_EVIDENCE_HINTS.get(gate, []) if hint not in evidence]
    if missing_hints:
        return False, "evidence missing hint(s): " + ", ".join(missing_hints)

    return True, ""


def main() -> int:
    failures: list[str] = []

    print("Objective:")
    print("  Create and validate a robust CrossPoint/CrossOver firmware build for")
    print("  the X3 with Bluetooth page-turner support and safe auto reconnect.")
    print()

    if BIN.exists():
        data = BIN.read_bytes()
        sha = hashlib.sha256(data).hexdigest()
        if ((EXPECTED_SHA256 == "pending" or sha == EXPECTED_SHA256) and
                (EXPECTED_SIZE == 0 or len(data) == EXPECTED_SIZE) and
                b"1.2.0-x3-ble-idlefix15" in data):
            ok("idlefix15 artifact exists with expected SHA, size, and version marker")
        else:
            fail("idlefix15 artifact SHA, size, or version marker does not match", failures)
    else:
        fail(f"idlefix15 artifact missing at {BIN}", failures)

    for label, command in [
        ("local idlefix15 verifier passes", [sys.executable, "scripts/verify_x3_ble_idlefix15.py"]),
        ("idlefix15 firmware image-info inspection passes", [
            sys.executable, "-c",
            "import os, subprocess, sys; "
            "env=os.environ.copy(); "
            "env['X3_BLE_IMAGE_LABEL']='idlefix15'; "
            f"env['X3_BLE_FIRMWARE_BIN']={str(BIN)!r}; "
            "raise SystemExit(subprocess.run([sys.executable, 'scripts/inspect_x3_ble_firmware_image.py'], env=env).returncode)"
        ]),
        ("remote-sleep timing model passes", [sys.executable, "scripts/simulate_x3_ble_reconnect_timing.py"]),
        ("reconnect invariant audit passes", [sys.executable, "scripts/audit_x3_ble_reconnect_invariants.py"]),
        ("remote-sleep auto-reconnect source audit passes",
         [sys.executable, "scripts/audit_x3_ble_remote_sleep_autoreconnect.py"]),
        ("guard behavior audit passes", [sys.executable, "scripts/audit_x3_ble_guard_behavior.py"]),
        ("page-input path audit passes", [sys.executable, "scripts/audit_x3_ble_page_input_path.py"]),
        ("hardware prompt helper identifies next gate",
         [sys.executable, "scripts/x3_ble_hardware_prompt.py"]),
        ("hardware answer recorder dry-run accepts page-input pass answer",
         [sys.executable, "scripts/x3_ble_record_hardware_answer.py",
          "--dry-run", "--gate", "Page input", "both directions worked"]),
    ]:
        code, output = run(command)
        if code == 0:
            ok(label)
        else:
            fail(label, failures)
            print(output)

    results = validation_results()
    for gate in REQUIRED_GATES:
        result = results.get(gate)
        if result is None:
            fail(f"hardware validation gate not found: {gate}", failures)
        elif result_passed(result):
            has_evidence, reason = result_has_evidence(gate, result)
            if not has_evidence:
                fail(f"hardware validation gate lacks concrete evidence: {gate} ({reason}; {result})", failures)
                continue
            ok(f"hardware validation gate passed: {gate}")
        else:
            fail(f"hardware validation gate pending or not passing: {gate} ({result})", failures)

    if failures:
        print()
        print("Completion audit result: NOT COMPLETE")
        print("The active goal must stay open until every missing hardware gate passes.")
        return 1

    print()
    print("Completion audit result: COMPLETE")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
