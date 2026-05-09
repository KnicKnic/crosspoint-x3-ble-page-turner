#!/usr/bin/env python3
"""Record an X3 BLE hardware validation result in the current checklist."""

from __future__ import annotations

import argparse
import os
from datetime import date
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
VALIDATION_DOC = Path(os.environ.get(
    "X3_BLE_VALIDATION_DOC",
    str(ROOT / "docs/x3-ble-idlefix15-hardware-validation.md"),
))
VALID_GATES = {
    "Flash",
    "Boot",
    "Manual reconnect",
    "Page input",
    "Reader sleep/wake",
    "Remote sleep/off/on",
    "Long idle recovery",
    "Repeated cycles",
    "Guard behavior",
}
PASS_EVIDENCE_HINTS = {
    "Flash": ("verify-flash", "app0", "app1"),
    "Boot": ("responsive",),
    "Manual reconnect": ("reconnect",),
    "Page input": ("forward", "back"),
    "Reader sleep/wake": ("sleep", "wake"),
    "Remote sleep/off/on": ("remote",),
    "Long idle recovery": ("long idle", "sleep", "reconnect"),
    "Repeated cycles": ("3", "no crash"),
    "Guard behavior": ("guard", "manual reconnect"),
}


def clean_cell(value: str) -> str:
    return value.replace("|", "/").strip()


def result_text(gate: str, status: str, evidence: str) -> str:
    if status == "pending":
        return "Pending"

    prefix = "Passed" if status == "passed" else "Failed"
    evidence = clean_cell(evidence)
    if status == "passed" and len(evidence) < 12:
        raise SystemExit("Passing results need short concrete evidence, not just 'passed'.")
    if status == "passed":
        normalized = evidence.lower()
        missing = [hint for hint in PASS_EVIDENCE_HINTS.get(gate, ()) if hint not in normalized]
        if missing:
            raise SystemExit(
                f"Passing result for {gate!r} must mention: {', '.join(missing)}"
            )
    if evidence:
        return f"{prefix} {date.today().isoformat()}: {evidence}"
    return f"{prefix} {date.today().isoformat()}"


def update_doc(gate: str, status: str, evidence: str, dry_run: bool) -> str:
    text = VALIDATION_DOC.read_text(encoding="utf-8")
    lines = text.splitlines()
    replacement_result = result_text(gate, status, evidence)
    updated = False
    output_lines: list[str] = []

    for line in lines:
        if not line.startswith("|"):
            output_lines.append(line)
            continue

        cells = [cell.strip() for cell in line.strip().split("|")[1:-1]]
        if len(cells) != 4 or cells[0] != gate:
            output_lines.append(line)
            continue

        output_lines.append(f"| {cells[0]} | {cells[1]} | {cells[2]} | {replacement_result} |")
        updated = True

    if not updated:
        raise SystemExit(f"Gate not found in validation table: {gate}")

    new_text = "\n".join(output_lines) + "\n"
    if not dry_run:
        VALIDATION_DOC.write_text(new_text, encoding="utf-8")

    return replacement_result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("gate", choices=sorted(VALID_GATES))
    parser.add_argument("status", choices=["passed", "failed", "pending"])
    parser.add_argument("evidence", nargs="*", help="Short concrete evidence to store in the result cell.")
    parser.add_argument("--dry-run", action="store_true", help="Print the proposed result without editing the doc.")
    args = parser.parse_args()

    evidence = " ".join(args.evidence)
    result = update_doc(args.gate, args.status, evidence, args.dry_run)
    action = "would record" if args.dry_run else "recorded"
    print(f"{action}: {args.gate} -> {result}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
