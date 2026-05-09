#!/usr/bin/env python3
"""Verify the local X3 BLE idlefix15 artifact and documentation invariants.

This checks only local evidence. Real boot/reconnect/crash-free behavior still
needs the physical X3 plus Free3 hardware validation table.
"""

from __future__ import annotations

import hashlib
import os
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BIN = Path(os.environ.get(
    "X3_BLE_FIRMWARE_BIN",
    str(Path.home() / "Downloads/crosspoint-x3-ble-idlefix15.bin"),
))
BUILD_BIN = ROOT / ".pio/build/gh_release/firmware.bin"
EXPECTED_SHA256 = "5b23aee2453df26f35fc837ea580eedc3b7c8fa7deeb0f01092e9b7ff7b2949f"
EXPECTED_SIZE = 0x5b62e0
APP_PARTITION_SIZE = 0x640000


def read(path: Path, binary: bool = False):
    mode = "rb" if binary else "r"
    with path.open(mode, encoding=None if binary else "utf-8") as handle:
        return handle.read()


def require(condition: bool, message: str, failures: list[str]) -> None:
    if condition:
        print(f"ok - {message}")
    else:
        print(f"FAIL - {message}")
        failures.append(message)


def contains(path: str, needle: str) -> bool:
    return needle in read(ROOT / path)


def command_ok(command: list[str], required_output: str | None = None,
               env: dict[str, str] | None = None) -> bool:
    try:
        run_env = os.environ.copy()
        if env:
            run_env.update(env)
        result = subprocess.run(command, cwd=ROOT, text=True, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, check=False, env=run_env)
    except OSError:
        return False

    if result.returncode != 0:
        return False
    if required_output is not None and required_output not in result.stdout:
        return False
    return True


def main() -> int:
    failures: list[str] = []

    require(BIN.exists(), f"firmware artifact exists: {BIN}", failures)
    require(BUILD_BIN.exists(), f"PlatformIO build output exists: {BUILD_BIN}", failures)
    if BIN.exists():
        data = read(BIN, binary=True)
        actual_sha = hashlib.sha256(data).hexdigest()
        require(actual_sha == EXPECTED_SHA256, f"firmware SHA-256 is {EXPECTED_SHA256}", failures)
        require(len(data) == EXPECTED_SIZE, f"firmware size is 0x{EXPECTED_SIZE:x}", failures)
        require(len(data) < APP_PARTITION_SIZE, "firmware fits the 0x640000 app partition", failures)
        require(b"1.2.0-x3-ble-idlefix15" in data, "firmware contains idlefix15 version marker", failures)
        require(b"1.2.0-x3-ble-idlefix10" not in data, "firmware does not contain idlefix10 marker", failures)
        if BUILD_BIN.exists():
            require(data == read(BUILD_BIN, binary=True),
                    "packaged Downloads firmware matches PlatformIO build output byte-for-byte", failures)

    require(contains("platformio.ini", "version = 1.2.0-x3-ble-idlefix15"),
            "platformio version is idlefix15", failures)
    require(contains("platformio.ini", "h2zero/NimBLE-Arduino @ 2.5.0"),
            "platformio pins NimBLE-Arduino 2.5.0", failures)
    require(contains("platformio.ini", "-DENABLE_BT_DEBUG_MONITOR"),
            "BT debug monitor is enabled for this candidate", failures)

    manager = "lib/hal/BluetoothHIDManager.cpp"
    require(contains(manager, "BLE_PAGE_TURNER_IDLE_RECONNECT_MS = 195000"),
            "page-turner fake-connected idle cutoff is 195 seconds", failures)
    require(contains(manager, "BLE_MANUAL_RECONNECT_SCAN_MS = 45000"),
            "manual bonded reconnect scan is restored to the idlefix10 45 second window", failures)
    require(contains(manager, "BLE_MANUAL_RECONNECT_TIMEOUT_MS = 12000"),
            "manual bonded reconnect connect timeout is restored to 12 seconds", failures)
    require(contains(manager, "BLE_AUTO_PAGE_TURNER_SCAN_MS = 9000"),
            "lost page-turner automatic scan window is 9 seconds", failures)
    require(contains(manager, "BLE_PAGE_TURNER_RECONNECT_INTERVAL_MS = 10000"),
            "lost page-turner retry cadence is 10 seconds", failures)
    require(contains(manager, "BLE_PAGE_TURNER_RECONNECT_FAST_WINDOW_MS = 20UL * 60UL * 1000UL"),
            "lost page-turner fast window remains 20 minutes", failures)
    require(contains(manager, "BLE_PAGE_TURNER_RECONNECT_SLOW_INTERVAL_MS = 60000"),
            "long-idle page-turner retry cadence is 60 seconds", failures)
    require(contains(manager, "BLE_PAGE_TURNER_RECONNECT_WINDOW_MS = 4UL * 60UL * 60UL * 1000UL"),
            "lost page-turner recovery window lasts 4 hours", failures)
    require(contains(manager, "_pageTurnerReconnectFastUntil = now + BLE_PAGE_TURNER_RECONNECT_FAST_WINDOW_MS"),
            "entering page-turner mode refreshes the fast reconnect window", failures)
    require(contains(manager, "enterPageTurnerReconnectMode(\"wake_request\", false)"),
            "reader wake refreshes page-turner reconnect mode", failures)
    require(contains(manager, "enterPageTurnerReconnectMode(\"user_input\", false)"),
            "local user input refreshes page-turner reconnect mode while disconnected", failures)
    require(contains(manager, "_reconnectJobAutomatic && bondedDeviceLooksLikePageTurner() && pageTurnerReconnectModeActive(millis())"),
            "automatic page-turner lost-window reconnect can use active scans", failures)
    require(contains(manager, "const bool activeReconnectScan = !_reconnectJobAutomatic || activePageTurnerAutoScan;"),
            "manual reconnect and page-turner lost-window auto reconnect use active scan", failures)
    require(contains(manager, "pScan->setActiveScan(activeReconnectScan)"),
            "reconnect scan active/passive mode is controlled per reconnect type", failures)
    require(contains(manager, "isUnknownOrEmptyName"),
            "Unknown names are handled explicitly", failures)
    require(contains(manager, "addressLooksRandom(_bondedDeviceAddress)"),
            "random-address bonded remotes can use single-HID fallback", failures)
    require(contains(manager, "BLE_MANUAL_SCAN_RESPONSE_TIMEOUT_MS = 1200"),
            "manual reconnect uses a bounded scan-response timeout", failures)
    require(contains(manager, "setScanCallbacks(&scanCallbacks, activeReconnectScan)"),
            "manual active reconnect accepts duplicate scan callbacks", failures)
    require(contains(manager, "setDuplicateFilter(activeReconnectScan ? 0 : 1)"),
            "manual active reconnect disables controller duplicate filtering", failures)
    require(contains(manager, "hidCandidateCount == 1"),
            "single-HID fallback remains bounded to exactly one candidate", failures)
    require(contains(manager, "manual_reconnect_fallback_single_connectable"),
            "manual reconnect can try exactly one connectable fallback candidate", failures)
    require(contains(manager, "manual_reconnect_seen"),
            "failed reconnect scans record seen devices for diagnostics", failures)
    require(contains(manager, "NimBLEDevice::deleteBond(bleAddress);"),
            "manual reconnect restores the idlefix10 bond-reset connect attempt", failures)
    require(not contains(manager, "updateConnParams("),
            "manual reconnect avoids post-connect connection-parameter updates", failures)
    require(not contains(manager, "setDataLen("),
            "manual reconnect avoids post-connect data-length negotiation", failures)
    require(not contains(manager, "getRssi()"),
            "manual reconnect avoids post-connect RSSI reads", failures)
    require(not contains(manager, "HID_PROTOCOL_MODE_UUID"),
            "manual reconnect avoids HID protocol-mode writes", failures)
    require(not contains(manager, "HID_REPORT_MAP_UUID"),
            "manual reconnect avoids HID report-map reads", failures)
    require(contains(manager, "connect_hid_service_start"),
            "connect diagnostics mark HID service discovery start", failures)
    require(contains(manager, "connect_hid_service_ok"),
            "connect diagnostics mark HID service discovery success", failures)
    require(contains(manager, "connect_report_discovery_start"),
            "connect diagnostics mark report discovery start", failures)
    require(contains(manager, "connect_report_discovery_done"),
            "connect diagnostics mark report discovery completion", failures)
    require(contains(manager, "auto pCharacteristics = pService->getCharacteristics(true)"),
            "HID report discovery restores the idlefix10 broad characteristic enumeration", failures)
    require(contains(manager, "connect_subscribe_start"),
            "connect diagnostics mark subscription start", failures)
    require(contains(manager, "connect_subscribe_done"),
            "connect diagnostics mark subscription completion", failures)
    require(contains(manager, "pageTurner=%d"),
            "reconnect diagnostics record active page-turner auto scans", failures)
    require(contains(manager, "setAutoReconnectGuard(true)"),
            "automatic reconnect guard is written while worker runs", failures)
    require(contains(manager, "auto_reconnect_wake_request_blocked_by_guard"),
            "wake reconnect remains blocked by crash guard", failures)
    require(contains(manager, "HalPowerManager::Lock reconnectPowerLock;"),
            "reconnect worker holds normal CPU speed for BLE scan/connect/subscribe", failures)
    require(contains(manager, 'xTaskCreate(&BluetoothHIDManager::bondedReconnectTaskEntry, "bt_reconn", 12288'),
            "reconnect worker stack is increased for BLE/GATT work", failures)
    require(contains(manager, "BluetoothDiagnostics::setStorageFlushSuppressed(true);"),
            "reconnect worker suppresses non-forced diagnostic storage writes during BLE critical path", failures)
    require(contains(manager, "BluetoothDiagnostics::setStorageFlushSuppressed(false);"),
            "reconnect worker restores diagnostic storage flushing before completion", failures)
    require(not contains(manager, "if (isBondedReconnectInProgress()) {\n    return true;\n  }\n\n  // Check if any connected device has had activity"),
            "background reconnect workers do not reset the auto-sleep activity timer", failures)
    require(contains("lib/hal/BluetoothDiagnostics.h", "setStorageFlushSuppressed"),
            "diagnostics expose scoped storage-flush suppression", failures)
    require(contains("src/main.cpp", "sleep_deferred_reconnect"),
            "deep sleep is deferred while a BLE reconnect worker is active", failures)
    require(contains("src/main.cpp", "bleActiveWork = bleActiveWork || btMgr.isBondedReconnectInProgress();"),
            "auto-sleep defers only while BLE reconnect work is active", failures)
    require(contains("src/main.cpp", "if (bleActiveWork) {\n    powerManager.setPowerSaving(false);\n  }"),
            "main loop keeps normal CPU speed while reconnect work is active", failures)
    require(contains("src/main.cpp", "if (activityManager.skipLoopDelay() || bleActiveWork)"),
            "end-of-loop power saving cannot drop CPU speed during reconnect work", failures)
    require(contains("src/main.cpp", "x3_after_usb_power_boot_allowed"),
            "X3 USB-power wake classification boots instead of re-entering sleep", failures)
    require(contains("src/main.cpp", "if (gpio.deviceIsX3())"),
            "X3 wake classification fix is scoped to X3 hardware", failures)

    require(contains("src/activities/settings/BluetoothSettingsActivity.cpp",
                     "Scan disabled: unstable on X3"),
            "manual scan UI remains disabled on X3 candidate", failures)
    require(contains("src/activities/settings/BluetoothSettingsActivity.cpp",
                     "startBondedReconnect(12000)"),
            "settings manual reconnect UI uses the restored 12 second reconnect timeout", failures)
    require(contains("src/main.cpp", 'cmd == "BLE_DIAG"'),
            "serial command CMD:BLE_DIAG is available", failures)
    require(contains("src/main.cpp", "BluetoothDiagnostics::persistedSnapshot()"),
            "serial BLE_DIAG command prints the persisted diagnostics snapshot", failures)
    require(contains("src/main.cpp", "BLE_DIAG_START"),
            "serial BLE_DIAG command frames diagnostic output", failures)

    doc = "docs/x3-ble-page-turner.md"
    validation_doc = "docs/x3-ble-idlefix15-hardware-validation.md"
    completion_doc = "docs/x3-ble-idlefix15-completion-audit.md"
    require(contains(doc, "This fork adds Bluetooth HID page-turner support"),
            "docs explain the X3 BLE page-turner fork", failures)
    require(contains(doc, "4 hours"), "docs record the long page-turner recovery window", failures)
    require(contains(doc, "reconnect workers do not count as recent BLE activity"),
            "docs explain the auto-sleep fix", failures)
    require(contains(doc, "known-good reconnect shape"),
            "docs explain the idlefix15 return to the known-good reconnect baseline", failures)
    require(contains(doc, "45 second active manual scan"),
            "docs explain the restored manual scan window", failures)
    require(contains(doc, "long-idle") and contains(doc, "auto-sleep bookkeeping fix"),
            "docs explain the narrow long-idle fix", failures)
    require((ROOT / validation_doc).exists(), "idlefix15 hardware validation checklist exists", failures)
    require(contains(validation_doc, "Long idle recovery"),
            "idlefix15 validation doc includes a long-idle recovery gate", failures)
    require(contains(validation_doc, "4 hours"),
            "idlefix15 validation doc records the long recovery window", failures)
    require(contains(validation_doc, "auto-sleep"),
            "idlefix15 validation doc records the sleep interaction fix", failures)
    require(contains(validation_doc, "scripts/read_x3_ble_diag.py"),
            "idlefix15 validation doc references diagnostics reader", failures)
    require((ROOT / completion_doc).exists(), "idlefix15 completion audit note exists", failures)
    require(contains(completion_doc, "Long-idle recovery works"),
            "completion audit includes long-idle recovery requirement", failures)
    require(contains(completion_doc, "Requirement Checklist"),
            "completion audit maps requirements to evidence", failures)

    require((ROOT / "scripts/read_x3_ble_diag.py").exists(),
            "BLE diagnostics reader exists", failures)
    require(command_ok([sys.executable, "scripts/test_x3_ble_diag_reader.py"],
                       "Diagnostic reader self-test passed"),
            "BLE diagnostics reader self-test passes", failures)
    require(command_ok([sys.executable, "scripts/inspect_x3_ble_firmware_image.py"],
                       "idlefix15 image-info check passed",
                       {"X3_BLE_IMAGE_LABEL": "idlefix15", "X3_BLE_FIRMWARE_BIN": str(BIN)}),
            "idlefix15 firmware image-info inspection passes", failures)
    require(command_ok([sys.executable, "scripts/simulate_x3_ble_reconnect_timing.py"],
                       "idlefix15 intent"),
            "BLE reconnect timing simulation passes", failures)
    require(command_ok([sys.executable, "scripts/audit_x3_ble_reconnect_invariants.py"],
                       "All reconnect invariant checks passed"),
            "BLE reconnect invariant audit passes", failures)
    require(command_ok([sys.executable, "scripts/audit_x3_ble_remote_sleep_autoreconnect.py"],
                       "All remote-sleep auto-reconnect source checks passed"),
            "BLE remote-sleep auto-reconnect audit passes", failures)
    require(command_ok([sys.executable, "scripts/audit_x3_ble_guard_behavior.py"],
                       "All guard behavior checks passed"),
            "BLE guard behavior audit passes", failures)
    require(command_ok([sys.executable, "scripts/audit_x3_ble_page_input_path.py"],
                       "All page-input path checks passed"),
            "BLE page-input path audit passes", failures)
    require((ROOT / "scripts/audit_x3_ble_goal_completion.py").exists(),
            "strict goal completion audit exists", failures)
    require(contains("scripts/audit_x3_ble_goal_completion.py",
                     '"Repeated cycles": ["3 reader", "3 remote", "no crash"]'),
            "strict goal completion audit requires concrete repeated-cycle evidence", failures)
    require(contains("scripts/audit_x3_ble_goal_completion.py",
                     '"Long idle recovery": ["long idle", "sleep", "reconnect"]'),
            "strict goal completion audit requires concrete long-idle evidence", failures)
    require((ROOT / "scripts/record_x3_ble_validation_result.py").exists(),
            "hardware validation result recorder exists", failures)
    require(command_ok([sys.executable, "scripts/record_x3_ble_validation_result.py",
                        "--dry-run", "Flash", "passed", "app0", "app1", "write", "and",
                        "verify-flash", "succeeded"],
                       env={"X3_BLE_VALIDATION_DOC": str(ROOT / validation_doc)}),
            "hardware validation result recorder dry-run works for idlefix15", failures)
    require((ROOT / "scripts/flash_x3_ble_idlefix15.sh").exists(),
            "idlefix15 flash wrapper exists", failures)
    require((ROOT / "scripts/flash_record_x3_ble_idlefix15.sh").exists(),
            "idlefix15 flash-and-record wrapper exists", failures)
    require(contains("scripts/flash_record_x3_ble_idlefix15.sh",
                     "x3-ble-idlefix15-hardware-validation.md"),
            "idlefix15 flash-and-record wrapper records into idlefix15 validation doc", failures)
    require(command_ok([sys.executable, "scripts/next_x3_ble_validation_gate.py"],
                       "Next pending gate:",
                       {"X3_BLE_VALIDATION_DOC": str(ROOT / validation_doc)}),
            "next hardware validation gate helper runs for idlefix15", failures)
    require(command_ok([sys.executable, "scripts/x3_ble_hardware_prompt.py"],
                       "Next hardware gate:",
                       {"X3_BLE_VALIDATION_DOC": str(ROOT / validation_doc)}),
            "hardware validation prompt helper runs for idlefix15", failures)
    require(command_ok([sys.executable, "scripts/x3_ble_hardware_prompt.py",
                        "--gate", "Page input", "--answer", "both directions worked"],
                       'record_x3_ble_validation_result.py "Page input" passed',
                       {"X3_BLE_VALIDATION_DOC": str(ROOT / validation_doc)}),
            "hardware prompt helper maps page-input pass answer to record command", failures)
    require(command_ok([sys.executable, "scripts/x3_ble_hardware_prompt.py",
                        "--gate", "Reader sleep/wake", "--answer", "reader sleep/wake worked"],
                       'record_x3_ble_validation_result.py "Reader sleep/wake" passed',
                       {"X3_BLE_VALIDATION_DOC": str(ROOT / validation_doc)}),
            "hardware prompt helper maps reader sleep/wake pass answer", failures)
    require(command_ok([sys.executable, "scripts/x3_ble_hardware_prompt.py",
                        "--gate", "Guard behavior", "--answer", "guard behavior passed"],
                       'record_x3_ble_validation_result.py "Guard behavior" passed',
                       {"X3_BLE_VALIDATION_DOC": str(ROOT / validation_doc)}),
            "hardware prompt helper maps guard behavior pass answer", failures)
    require(command_ok([sys.executable, "scripts/x3_ble_hardware_prompt.py",
                        "--gate", "Long idle recovery", "--answer", "long idle recovery worked"],
                       'record_x3_ble_validation_result.py "Long idle recovery" passed',
                       {"X3_BLE_VALIDATION_DOC": str(ROOT / validation_doc)}),
            "hardware prompt helper maps long-idle pass answer", failures)
    require(command_ok([sys.executable, "scripts/x3_ble_record_hardware_answer.py",
                        "--dry-run", "--gate", "Page input", "both directions worked"],
                       "would record: Page input -> Passed",
                       {"X3_BLE_VALIDATION_DOC": str(ROOT / validation_doc)}),
            "hardware answer recorder dry-runs page-input pass answer", failures)
    require(command_ok([sys.executable, "scripts/x3_ble_record_hardware_answer.py",
                        "--dry-run", "--gate", "Remote sleep/off/on", "remote sleep/off/on failed"],
                       "would record: Remote sleep/off/on -> Failed",
                       {"X3_BLE_VALIDATION_DOC": str(ROOT / validation_doc)}),
            "hardware answer recorder dry-runs remote sleep failure answer", failures)
    require(command_ok([sys.executable, "scripts/x3_ble_record_hardware_answer.py",
                        "--dry-run", "--gate", "Long idle recovery", "long idle recovery worked"],
                       "would record: Long idle recovery -> Passed",
                       {"X3_BLE_VALIDATION_DOC": str(ROOT / validation_doc)}),
            "hardware answer recorder dry-runs long-idle pass answer", failures)
    require(command_ok([sys.executable, "scripts/test_x3_ble_record_hardware_answer.py"],
                       "Hardware answer recorder self-test passed"),
            "hardware answer recorder self-test passes", failures)

    if failures:
        print(f"\n{len(failures)} verification check(s) failed.")
        return 1

    print("\nAll local idlefix15 checks passed. Hardware validation evidence is recorded separately.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
