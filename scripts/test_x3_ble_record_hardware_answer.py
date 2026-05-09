#!/usr/bin/env python3
"""Self-test the hardware answer recorder against a temporary validation doc."""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
VALIDATION_DOC = ROOT / "docs/x3-ble-idlefix15-hardware-validation.md"
RECORDER = ROOT / "scripts/x3_ble_record_hardware_answer.py"
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


def require(condition: bool, message: str, failures: list[str]) -> None:
    if condition:
        print(f"ok - {message}")
    else:
        print(f"FAIL - {message}")
        failures.append(message)


def run_with_doc(doc_path: Path, *args: str) -> subprocess.CompletedProcess[str]:
    env = os.environ.copy()
    env["X3_BLE_VALIDATION_DOC"] = str(doc_path)
    return subprocess.run(
        [sys.executable, str(RECORDER), *args],
        cwd=ROOT,
        env=env,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=False,
    )


def validation_result(doc_path: Path, gate: str) -> str:
    pattern = re.compile(r"^\|\s*([^|]+?)\s*\|[^|]*\|[^|]*\|\s*([^|]+?)\s*\|$")
    for line in doc_path.read_text(encoding="utf-8").splitlines():
        match = pattern.match(line)
        if match and match.group(1).strip() == gate:
            return match.group(2).strip()
    return ""


def set_validation_result(doc_path: Path, gate: str, result: str) -> None:
    lines: list[str] = []
    updated = False
    for line in doc_path.read_text(encoding="utf-8").splitlines():
        if not line.startswith("|"):
            lines.append(line)
            continue

        cells = [cell.strip() for cell in line.strip().split("|")[1:-1]]
        if len(cells) != 4 or cells[0] != gate:
            lines.append(line)
            continue

        lines.append(f"| {cells[0]} | {cells[1]} | {cells[2]} | {result} |")
        updated = True

    if not updated:
        raise RuntimeError(f"validation row not found: {gate}")
    doc_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def mark_before_gate_passed(doc_path: Path, gate: str) -> None:
    target = GATE_ORDER.index(gate)
    for prior in GATE_ORDER[:target]:
        set_validation_result(doc_path, prior, f"Passed 2026-05-09: test fixture passed {prior.lower()}")


def main() -> int:
    failures: list[str] = []

    with tempfile.TemporaryDirectory() as tmpdir:
        temp_doc = Path(tmpdir) / "validation.md"
        shutil.copyfile(VALIDATION_DOC, temp_doc)
        real_before = VALIDATION_DOC.read_text(encoding="utf-8")
        mark_before_gate_passed(temp_doc, "Page input")
        set_validation_result(temp_doc, "Page input", "Pending")
        set_validation_result(temp_doc, "Reader sleep/wake", "Pending")

        before = temp_doc.read_text(encoding="utf-8")
        pass_result = run_with_doc(temp_doc, "both directions worked")
        after_pass = temp_doc.read_text(encoding="utf-8")

        print(pass_result.stdout, end="")
        require(pass_result.returncode == 0, "page-input pass answer records successfully", failures)
        require("Page input -> Passed" in pass_result.stdout, "pass output records Page input as passed", failures)
        require("Next hardware gate: Reader sleep/wake" in pass_result.stdout,
                "pass output advances to reader sleep/wake gate", failures)
        require("forward and back buttons turned pages reliably" in after_pass,
                "temporary validation doc stores page-input pass evidence", failures)
        require(before != after_pass, "temporary validation doc changed after real temp record", failures)
        require(VALIDATION_DOC.read_text(encoding="utf-8") == real_before,
                "real validation doc remains unchanged", failures)

    with tempfile.TemporaryDirectory() as tmpdir:
        temp_doc = Path(tmpdir) / "validation.md"
        shutil.copyfile(VALIDATION_DOC, temp_doc)
        mark_before_gate_passed(temp_doc, "Page input")
        set_validation_result(temp_doc, "Page input", "Pending")
        set_validation_result(temp_doc, "Reader sleep/wake", "Pending")

        fail_result = run_with_doc(temp_doc, "only forward worked")
        after_fail = temp_doc.read_text(encoding="utf-8")

        print(fail_result.stdout, end="")
        require(fail_result.returncode == 0, "page-input failure answer records successfully", failures)
        require("Page input -> Failed" in fail_result.stdout, "failure output records Page input as failed", failures)
        require("inspect diagnostics" in fail_result.stdout, "failure output points to diagnostics", failures)
        require("only forward worked; capture HID diagnostics" in after_fail,
                "temporary validation doc stores page-input failure evidence", failures)

    with tempfile.TemporaryDirectory() as tmpdir:
        temp_doc = Path(tmpdir) / "validation.md"
        shutil.copyfile(VALIDATION_DOC, temp_doc)
        real_before = VALIDATION_DOC.read_text(encoding="utf-8")
        mark_before_gate_passed(temp_doc, "Long idle recovery")
        set_validation_result(temp_doc, "Long idle recovery", "Pending")
        set_validation_result(temp_doc, "Repeated cycles", "Pending")

        long_idle_result = run_with_doc(temp_doc, "long idle recovery worked")
        after_long_idle = temp_doc.read_text(encoding="utf-8")

        print(long_idle_result.stdout, end="")
        require(long_idle_result.returncode == 0, "long-idle pass answer records successfully", failures)
        require("Long idle recovery -> Passed" in long_idle_result.stdout,
                "long-idle output records Long idle recovery as passed", failures)
        require("Next hardware gate: Repeated cycles" in long_idle_result.stdout,
                "long-idle output advances to repeated cycles gate", failures)
        require("long idle auto sleep and reconnect recovered without settings menu" in after_long_idle,
                "temporary validation doc stores long-idle pass evidence", failures)
        require(VALIDATION_DOC.read_text(encoding="utf-8") == real_before,
                "real validation doc remains unchanged after long-idle temp record", failures)

    with tempfile.TemporaryDirectory() as tmpdir:
        temp_doc = Path(tmpdir) / "validation.md"
        shutil.copyfile(VALIDATION_DOC, temp_doc)
        real_before = VALIDATION_DOC.read_text(encoding="utf-8")
        mark_before_gate_passed(temp_doc, "Repeated cycles")
        set_validation_result(temp_doc, "Repeated cycles", "Pending")
        set_validation_result(temp_doc, "Guard behavior", "Pending")

        cycles_result = run_with_doc(temp_doc, "3+3 cycles no crash")
        after_cycles = temp_doc.read_text(encoding="utf-8")

        print(cycles_result.stdout, end="")
        require(cycles_result.returncode == 0, "repeated-cycle pass answer records successfully", failures)
        require("Repeated cycles -> Passed" in cycles_result.stdout,
                "repeated-cycle output records Repeated cycles as passed", failures)
        require("Next hardware gate: Guard behavior" in cycles_result.stdout,
                "repeated-cycle output advances to guard behavior gate", failures)
        require("3 reader cycles and 3 remote cycles completed, no crash" in after_cycles,
                "temporary validation doc stores repeated-cycle pass evidence", failures)

        guard_result = run_with_doc(temp_doc, "guard behavior passed")
        after_guard = temp_doc.read_text(encoding="utf-8")

        print(guard_result.stdout, end="")
        require(guard_result.returncode == 0, "guard behavior pass answer records successfully", failures)
        require("Guard behavior -> Passed" in guard_result.stdout,
                "guard behavior output records Guard behavior as passed", failures)
        require("All hardware gates appear passed" in guard_result.stdout,
                "guard behavior output points to final completion audit", failures)
        require("manual reconnect remained available after reconnect validation" in after_guard,
                "temporary validation doc stores guard behavior pass evidence", failures)
        require(VALIDATION_DOC.read_text(encoding="utf-8") == real_before,
                "real validation doc remains unchanged after final-gate temp records", failures)

    with tempfile.TemporaryDirectory() as tmpdir:
        temp_doc = Path(tmpdir) / "validation.md"
        shutil.copyfile(VALIDATION_DOC, temp_doc)
        mark_before_gate_passed(temp_doc, "Repeated cycles")
        set_validation_result(temp_doc, "Repeated cycles", "Pending")
        set_validation_result(temp_doc, "Guard behavior", "Pending")

        typo_result = run_with_doc(temp_doc, "3+3 cycles mo crash")
        after_typo = temp_doc.read_text(encoding="utf-8")

        print(typo_result.stdout, end="")
        require(typo_result.returncode == 0,
                "unambiguous repeated-cycle typo answer records successfully", failures)
        require("Repeated cycles -> Passed" in typo_result.stdout,
                "unambiguous repeated-cycle typo output records pass", failures)
        require("Next hardware gate: Guard behavior" in typo_result.stdout,
                "unambiguous repeated-cycle typo advances to guard behavior gate", failures)
        require("3 reader cycles and 3 remote cycles completed, no crash" in after_typo,
                "temporary validation doc stores repeated-cycle typo pass evidence", failures)

    with tempfile.TemporaryDirectory() as tmpdir:
        temp_doc = Path(tmpdir) / "validation.md"
        shutil.copyfile(VALIDATION_DOC, temp_doc)
        mark_before_gate_passed(temp_doc, "Repeated cycles")
        set_validation_result(temp_doc, "Repeated cycles", "Pending")
        set_validation_result(temp_doc, "Guard behavior", "Pending")

        confirmation_result = run_with_doc(temp_doc, "yes both")
        after_confirmation = temp_doc.read_text(encoding="utf-8")

        print(confirmation_result.stdout, end="")
        require(confirmation_result.returncode == 0,
                "clarifying yes-both repeated-cycle answer records successfully", failures)
        require("Repeated cycles -> Passed" in confirmation_result.stdout,
                "clarifying yes-both repeated-cycle output records pass", failures)
        require("Next hardware gate: Guard behavior" in confirmation_result.stdout,
                "clarifying yes-both repeated-cycle answer advances to guard behavior gate", failures)
        require("user confirmed both required sets: 3 reader cycles and 3 remote cycles completed, no crash"
                in after_confirmation,
                "temporary validation doc stores clarifying yes-both pass evidence", failures)

    with tempfile.TemporaryDirectory() as tmpdir:
        temp_doc = Path(tmpdir) / "validation.md"
        shutil.copyfile(VALIDATION_DOC, temp_doc)
        mark_before_gate_passed(temp_doc, "Repeated cycles")
        set_validation_result(temp_doc, "Repeated cycles", "Pending")
        set_validation_result(temp_doc, "Guard behavior", "Pending")
        before = temp_doc.read_text(encoding="utf-8")

        ambiguous_result = run_with_doc(temp_doc, "3 cycles mo crash")
        after_ambiguous = temp_doc.read_text(encoding="utf-8")

        print(ambiguous_result.stdout, end="")
        require(ambiguous_result.returncode == 2,
                "ambiguous repeated-cycle answer is rejected", failures)
        require("Ambiguous repeated-cycle answer" in ambiguous_result.stdout,
                "ambiguous repeated-cycle output explains ambiguity", failures)
        require("3 reader sleep/wake cycles and 3 Free3 sleep/off/on cycles" in ambiguous_result.stdout,
                "ambiguous repeated-cycle output requests both required sets", failures)
        require(before == after_ambiguous,
                "temporary validation doc stays unchanged after ambiguous repeated-cycle answer", failures)

    if failures:
        print(f"\n{len(failures)} hardware answer recorder self-test check(s) failed.")
        return 1

    print("\nHardware answer recorder self-test passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
