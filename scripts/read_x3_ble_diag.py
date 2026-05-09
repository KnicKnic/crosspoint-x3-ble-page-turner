#!/usr/bin/env python3
"""Read and summarize X3 CrossPoint BLE diagnostics.

Usage examples:
  scripts/read_x3_ble_diag.py
  scripts/read_x3_ble_diag.py /Volumes/X3
  scripts/read_x3_ble_diag.py /Volumes/X3/.crosspoint/ble_diag.log
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path


DEFAULT_GLOB = "/Volumes/*/.crosspoint/ble_diag.log"
LINE_RE = re.compile(
    r"^(?P<ms>\d+)\s+boot=(?P<boot>\d+)\s+event=(?P<event>\S+)"
    r"\s+detail=(?P<detail>.*?)\s+heap=(?P<heap>\d+)"
    r"\s+minHeap=(?P<min_heap>\d+)\s+stackWords=(?P<stack_words>\d+)\s*$"
)
BAD_RESETS = ("panic", "int_wdt", "task_wdt", "wdt", "cpu_lockup", "brownout")
KEY_EVENTS = {
    "boot",
    "auto_reconnect_guard_disable",
    "auto_reconnect_attempt",
    "auto_reconnect_start",
    "auto_reconnect_done",
    "auto_reconnect_queue_failed",
    "manual_reconnect_start",
    "manual_reconnect_done",
    "manual_reconnect_scan_start",
    "manual_reconnect_scan_match",
    "manual_reconnect_scan_no_match",
    "manual_reconnect_seen",
    "manual_reconnect_fallback_single_hid",
    "manual_reconnect_fallback_single_connectable",
    "manual_reconnect_fallback_skip",
    "manual_reconnect_no_candidate",
    "connect_failed",
    "idle_disconnect",
    "page_turner_reconnect_window",
    "client_disconnect_processed",
}


@dataclass
class Entry:
    ms: int
    boot: int
    event: str
    detail: str
    heap: int
    min_heap: int
    stack_words: int
    raw: str


def resolve_diag_path(arg: str | None) -> Path | None:
    if arg:
        path = Path(arg).expanduser()
        if path.is_dir():
            return path / ".crosspoint" / "ble_diag.log"
        return path

    matches = sorted(Path("/Volumes").glob("*/.crosspoint/ble_diag.log"))
    return matches[0] if matches else None


def parse_entries(content: str) -> tuple[list[Entry], list[str]]:
    entries: list[Entry] = []
    malformed: list[str] = []
    for line in content.splitlines():
        line = line.strip()
        if not line:
            continue
        match = LINE_RE.match(line)
        if not match:
            malformed.append(line)
            continue
        entries.append(
            Entry(
                ms=int(match.group("ms")),
                boot=int(match.group("boot")),
                event=match.group("event"),
                detail=match.group("detail"),
                heap=int(match.group("heap")),
                min_heap=int(match.group("min_heap")),
                stack_words=int(match.group("stack_words")),
                raw=line,
            )
        )
    return entries, malformed


def shorten(text: str, limit: int) -> str:
    if len(text) <= limit:
        return text
    return text[: limit - 3] + "..."


def print_entries(entries: list[Entry], limit: int) -> None:
    shown = entries[-limit:] if limit > 0 else entries
    print(f"\nLast {len(shown)} entr{'y' if len(shown) == 1 else 'ies'}:")
    print(f"{'ms':>10} {'boot':>4} {'event':<34} detail")
    print("-" * 92)
    for entry in shown:
        marker = "!" if entry.event in KEY_EVENTS else " "
        print(f"{entry.ms:>10} {entry.boot:>4} {marker}{entry.event:<33} {shorten(entry.detail, 42)}")


def print_summary(entries: list[Entry], malformed: list[str]) -> None:
    if not entries:
        print("No parseable BLE diagnostics entries found.")
        return

    boots = sorted({entry.boot for entry in entries})
    print(f"Entries: {len(entries)}")
    print(f"Boots: {boots[0]}..{boots[-1]} ({len(boots)} boot{'s' if len(boots) != 1 else ''})")
    print(f"Min heap seen: {min(entry.min_heap for entry in entries)} bytes")
    print(f"Min stack high-water mark seen: {min(entry.stack_words for entry in entries)} words")

    warnings: list[str] = []
    for entry in entries:
        if entry.event == "boot" and any(f"reset={name}" in entry.detail for name in BAD_RESETS):
            warnings.append(f"boot {entry.boot}: suspicious reset ({entry.detail})")
        if entry.event == "auto_reconnect_guard_disable":
            warnings.append(f"boot {entry.boot}: auto reconnect guard disabled this boot ({entry.detail})")
        if entry.event == "auto_reconnect_done" and "success=0" in entry.detail:
            warnings.append(f"boot {entry.boot}: automatic reconnect failed ({entry.detail})")
        if entry.event == "manual_reconnect_done" and "success=0" in entry.detail:
            warnings.append(f"boot {entry.boot}: manual reconnect failed ({entry.detail})")
        if entry.event == "manual_reconnect_scan_no_match":
            warnings.append(f"boot {entry.boot}: reconnect scan saw no matching remote ({entry.detail})")
        if entry.event == "manual_reconnect_scan_start" and "pageTurner=1" in entry.detail:
            warnings.append(f"boot {entry.boot}: active page-turner reconnect scan started ({entry.detail})")
        if entry.event == "manual_reconnect_fallback_single_connectable":
            warnings.append(f"boot {entry.boot}: reconnect tried the single connectable fallback ({entry.detail})")
        if entry.event == "connect_failed":
            warnings.append(f"boot {entry.boot}: BLE connect/GATT setup failed ({entry.detail})")

    if malformed:
        warnings.append(f"{len(malformed)} malformed log line(s) ignored")

    if warnings:
        print("\nNotable findings:")
        for warning in warnings:
            print(f"- {warning}")
    else:
        print("\nNo crash-reset or reconnect-failure markers found in the parsed entries.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?", help="Mounted X3 root or path to ble_diag.log")
    parser.add_argument("--limit", type=int, default=32, help="Number of recent entries to print")
    parser.add_argument("--require", action="store_true", help="Exit non-zero if no diagnostics file is found")
    args = parser.parse_args()

    path = resolve_diag_path(args.path)
    if not path or not path.exists():
        print("No BLE diagnostics file found.")
        print(f"Searched: {args.path or DEFAULT_GLOB}")
        return 2 if args.require else 0

    content = path.read_text(encoding="utf-8", errors="replace")
    entries, malformed = parse_entries(content)
    print(f"Diagnostics: {path}")
    print_summary(entries, malformed)
    print_entries(entries, args.limit)
    return 0


if __name__ == "__main__":
    sys.exit(main())
