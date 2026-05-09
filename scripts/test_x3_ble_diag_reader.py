#!/usr/bin/env python3
"""Self-test the X3 BLE diagnostics summarizer on reconnect markers."""

from __future__ import annotations

import subprocess
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
READER = ROOT / "scripts/read_x3_ble_diag.py"


SAMPLE_LOG = """\
1000 boot=4 event=boot detail=reset=poweron heap=120000 minHeap=118000 stackWords=900
2200 boot=4 event=manual_reconnect_scan_start detail=durationMs=9000 active=1 pageTurner=1 name=Free3-R heap=119000 minHeap=117500 stackWords=860
3100 boot=4 event=manual_reconnect_seen detail=addr=aa:bb:cc:dd:ee:ff name=Free3-R type=1 hid=1 conn=1 rssi=-48 heap=118500 minHeap=117000 stackWords=850
3300 boot=4 event=manual_reconnect_fallback_single_connectable detail=addr=aa:bb:cc:dd:ee:ff name=Unknown hid=0 rssi=-48 heap=118000 minHeap=116800 stackWords=845
5600 boot=4 event=connect_failed detail=addr=aa:bb:cc:dd:ee:ff reason=no_hid_service heap=117000 minHeap=116500 stackWords=830
5700 boot=4 event=manual_reconnect_done detail=success=0 msg=HID service not found heap=116900 minHeap=116400 stackWords=828
"""


def require(condition: bool, message: str, failures: list[str]) -> None:
    if condition:
        print(f"ok - {message}")
    else:
        print(f"FAIL - {message}")
        failures.append(message)


def main() -> int:
    failures: list[str] = []
    with tempfile.TemporaryDirectory() as tmpdir:
        log_path = Path(tmpdir) / "ble_diag.log"
        log_path.write_text(SAMPLE_LOG, encoding="utf-8")
        result = subprocess.run(
            [sys.executable, str(READER), str(log_path), "--limit", "8"],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            check=False,
        )

    output = result.stdout
    print(output, end="")
    require(result.returncode == 0, "diagnostic reader exits successfully", failures)
    require("manual_reconnect_seen" in output, "seen-device marker is printed", failures)
    require("single connectable fallback" in output,
            "single-connectable fallback warning is highlighted", failures)
    require("active page-turner reconnect scan" in output,
            "active page-turner reconnect scan warning is highlighted", failures)
    require("BLE connect/GATT setup failed" in output,
            "connect/GATT failure warning is highlighted", failures)
    require("manual reconnect failed" in output,
            "manual reconnect failure warning is highlighted", failures)

    if failures:
        print(f"\n{len(failures)} diagnostic reader self-test check(s) failed.")
        return 1

    print("\nDiagnostic reader self-test passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
