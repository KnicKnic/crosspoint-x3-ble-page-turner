#!/usr/bin/env python3
"""Diagnose whether macOS can see the X3 ESP32-C3 flash interface."""

from __future__ import annotations

import glob
import json
import os
import subprocess
import sys
from pathlib import Path


PIO_PYTHON = Path(os.environ.get("PIO_PYTHON", str(Path.home() / ".platformio/penv/bin/python")))


def serial_rows() -> list[dict[str, object]]:
    code = (
        "import json, serial.tools.list_ports\n"
        "print(json.dumps([{'device': p.device, 'vid': p.vid, 'pid': p.pid, "
        "'description': p.description, 'manufacturer': p.manufacturer, "
        "'product': p.product, 'serial_number': p.serial_number} "
        "for p in serial.tools.list_ports.comports()]))\n"
    )

    python = PIO_PYTHON if PIO_PYTHON.exists() else Path(sys.executable)
    result = subprocess.run([str(python), "-c", code], text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, check=False)
    if result.returncode != 0:
        print("serial scan failed:")
        print(result.stderr.strip())
        return []

    return json.loads(result.stdout)


def vidpid(row: dict[str, object]) -> str:
    vid = row.get("vid")
    pid = row.get("pid")
    if isinstance(vid, int) and isinstance(pid, int):
        return f"{vid:04x}:{pid:04x}"
    return "no-vidpid"


def classify(row: dict[str, object]) -> str:
    device = str(row.get("device", ""))
    vid = row.get("vid")
    pid = row.get("pid")

    if vid == 0x4C4A and pid == 0x4155:
        return "Jieli remote USB, not X3 flash"
    if vid == 0x303A:
        return "Espressif ESP32-C3 candidate"
    if "usbmodem" in device:
        return "usbmodem fallback candidate; esptool chip-id must confirm"
    return "not an X3 flash candidate"


def main() -> int:
    rows = serial_rows()
    fallback_nodes = sorted(set(glob.glob("/dev/cu.usbmodem*") + glob.glob("/dev/tty.usbmodem*")))
    candidates = []
    jieli = []

    print("Serial devices:")
    if not rows:
        print("  none")
    for row in rows:
        label = classify(row)
        if label.startswith("Espressif") or label.startswith("usbmodem fallback"):
            candidates.append(row)
        if label.startswith("Jieli"):
            jieli.append(row)
        print(f"  {row.get('device')} {vidpid(row)} {row.get('description')} -> {label}")

    print()
    print("usbmodem filesystem nodes:")
    if fallback_nodes:
        for node in fallback_nodes:
            print(f"  {node}")
    else:
        print("  none")

    print()
    if candidates:
        print("Conclusion: X3 flash candidate visible.")
        print("Next: scripts/flash_record_x3_ble_idlefix15.sh")
    elif jieli:
        print("Conclusion: only the Jieli/page-turner-style USB device is visible, not the X3 flash port.")
        print("Move the X3 USB cable, then enter download mode: hold BOOT/G0, tap RST, release BOOT/G0.")
    else:
        print("Conclusion: no X3 flash candidate is visible.")
        print("Connect the X3 directly or through a different hub, then enter download mode.")

    print("USB visibility diagnosis complete")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
