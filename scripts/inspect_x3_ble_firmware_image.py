#!/usr/bin/env python3
"""Inspect an X3 BLE firmware image with esptool."""

from __future__ import annotations

import subprocess
import os
import shutil
from pathlib import Path


LABEL = os.environ.get("X3_BLE_IMAGE_LABEL", "idlefix15")
BIN = Path(os.environ.get(
    "X3_BLE_FIRMWARE_BIN",
    str(Path.home() / "Downloads/crosspoint-x3-ble-idlefix15.bin"),
))
ESPTOOL = Path(os.environ.get(
    "ESPTOOL",
    shutil.which("esptool")
    or str(Path.home() / ".platformio/penv/bin/esptool"),
))


REQUIRED_OUTPUT = [
    "ESP32C3 Image Header",
    "Flash size: 16MB",
    "Chip ID: 5 (ESP32-C3)",
    "Checksum:",
    "(valid)",
    "Validation hash:",
]


def main() -> int:
    if not BIN.exists():
        print(f"missing firmware image: {BIN}")
        return 1

    result = subprocess.run(
        [str(ESPTOOL), "--chip", "esp32c3", "image-info", str(BIN)],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=False,
    )
    print(result.stdout, end="")
    if result.returncode != 0:
        return result.returncode

    missing = [needle for needle in REQUIRED_OUTPUT if needle not in result.stdout]
    if missing:
        print("missing expected image-info marker(s): " + ", ".join(missing))
        return 1

    print(f"{LABEL} image-info check passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
