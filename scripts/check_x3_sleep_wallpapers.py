#!/usr/bin/env python3
"""Inspect mounted X3 storage for custom sleep-screen wallpaper files."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


SLEEP_SCREEN_NAMES = {
    0: "Dark default",
    1: "Light default",
    2: "Custom",
    3: "Book cover",
    4: "Blank",
    5: "Cover, custom fallback",
}


def mounted_roots() -> list[Path]:
    volumes = Path("/Volumes")
    if not volumes.exists():
        return []
    roots: list[Path] = []
    for entry in volumes.iterdir():
        if entry.name == "Macintosh HD" or entry.is_symlink():
            continue
        if entry.is_dir():
            roots.append(entry)
    return roots


def is_bmp(path: Path) -> bool:
    try:
        with path.open("rb") as f:
            return f.read(2) == b"BM"
    except OSError:
        return False


def list_bmps(directory: Path) -> tuple[list[Path], list[Path]]:
    valid: list[Path] = []
    invalid: list[Path] = []
    if not directory.is_dir():
        return valid, invalid
    for item in sorted(directory.iterdir(), key=lambda p: p.name.lower()):
        if item.is_dir() or item.name.startswith("."):
            continue
        if item.suffix.lower() != ".bmp":
            continue
        if is_bmp(item):
            valid.append(item)
        else:
            invalid.append(item)
    return valid, invalid


def read_sleep_setting(root: Path) -> str:
    settings_path = root / ".crosspoint" / "settings.json"
    if not settings_path.exists():
        return "settings.json not found"
    try:
        settings = json.loads(settings_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return f"settings.json unreadable: {exc}"

    value = settings.get("sleepScreen")
    if isinstance(value, int):
        return f"{value} ({SLEEP_SCREEN_NAMES.get(value, 'unknown')})"
    return f"missing/invalid ({value!r})"


def inspect_root(root: Path) -> int:
    print(f"root: {root}")
    print(f"sleepScreen: {read_sleep_setting(root)}")

    total_valid = 0
    for rel in (".sleep", "sleep"):
        directory = root / rel
        valid, invalid = list_bmps(directory)
        total_valid += len(valid)
        status = "present" if directory.is_dir() else "missing"
        print(f"{rel}/: {status}, valid BMPs={len(valid)}, invalid BMPs={len(invalid)}")
        for path in valid[:10]:
            print(f"  ok: /{rel}/{path.name}")
        for path in invalid[:10]:
            print(f"  invalid: /{rel}/{path.name}")
        if len(valid) > 10:
            print(f"  ... {len(valid) - 10} more valid BMPs")
        if len(invalid) > 10:
            print(f"  ... {len(invalid) - 10} more invalid BMPs")

    root_sleep = root / "sleep.bmp"
    if root_sleep.exists():
        ok = is_bmp(root_sleep)
        total_valid += 1 if ok else 0
        print(f"sleep.bmp: {'valid BMP' if ok else 'present but invalid'}")
    else:
        print("sleep.bmp: missing")

    if total_valid == 0:
        print("result: no valid custom wallpaper BMPs found")
        return 1

    print(f"result: {total_valid} valid custom wallpaper BMP(s) found")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, help="Mounted reader storage root. Defaults to scanning /Volumes.")
    args = parser.parse_args()

    roots = [args.root] if args.root else mounted_roots()
    if not roots:
        print("no mounted reader storage found under /Volumes")
        return 2

    exit_code = 0
    for i, root in enumerate(roots):
        if i:
            print()
        exit_code = max(exit_code, inspect_root(root))
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
