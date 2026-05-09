#!/usr/bin/env python3
"""Static audit for the Free3 remote-sleep auto-reconnect path.

This does not prove hardware behavior. It guards the source-level chain the
X3 needs when the page turner silently sleeps or powers down: detect the quiet
bonded page turner, disconnect stale state, arm the page-turner reconnect
window, and use frequent active scan reconnect attempts while that window is
open.
"""

from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MANAGER = ROOT / "lib/hal/BluetoothHIDManager.cpp"


def require(condition: bool, message: str, failures: list[str]) -> None:
    if condition:
        print(f"ok - {message}")
    else:
        print(f"FAIL - {message}")
        failures.append(message)


def section(text: str, start: str, end: str) -> str:
    start_idx = text.find(start)
    if start_idx < 0:
        return ""
    end_idx = text.find(end, start_idx + len(start))
    if end_idx < 0:
        return text[start_idx:]
    return text[start_idx:end_idx]


def appears_in_order(text: str, needles: list[str]) -> bool:
    cursor = 0
    for needle in needles:
        idx = text.find(needle, cursor)
        if idx < 0:
            return False
        cursor = idx + len(needle)
    return True


def main() -> int:
    source = MANAGER.read_text(encoding="utf-8")
    failures: list[str] = []

    constants = section(source, "constexpr uint32_t BLE_RECONNECT_SCAN_MS", "static BluetoothHIDManager::ScanCallbacks")
    page_window = section(source, "bool BluetoothHIDManager::pageTurnerReconnectModeActive", "void BluetoothHIDManager::armAutoReconnectOnNextWake")
    update_activity = section(source, "void BluetoothHIDManager::updateActivity()", "void BluetoothHIDManager::checkAutoReconnect")
    check_auto = section(source, "void BluetoothHIDManager::checkAutoReconnect", "void BluetoothHIDManager::saveState")
    start_reconnect = section(source, "bool BluetoothHIDManager::startBondedReconnect", "BluetoothReconnectStatus BluetoothHIDManager::getReconnectStatus")
    scan_reconnect = section(source, "bool BluetoothHIDManager::scanForBondedReconnectCandidate", "bool BluetoothHIDManager::findBondedReconnectCandidate")

    require("BLE_PAGE_TURNER_IDLE_RECONNECT_MS = 195000" in constants,
            "quiet Free3/page-turner links are considered stale after 195 seconds", failures)
    require("BLE_PAGE_TURNER_RECONNECT_FAST_WINDOW_MS = 20UL * 60UL * 1000UL" in constants,
            "page-turner fast lost window remains dense for 20 minutes", failures)
    require("BLE_PAGE_TURNER_RECONNECT_SLOW_INTERVAL_MS = 60000" in constants,
            "page-turner long-idle recovery has a sparse 60 second cadence", failures)
    require("BLE_PAGE_TURNER_RECONNECT_WINDOW_MS = 4UL * 60UL * 60UL * 1000UL" in constants,
            "page-turner long-idle window remains open for 4 hours", failures)
    require("BLE_AUTO_PAGE_TURNER_SCAN_MS = 9000" in constants,
            "lost-page-turner automatic scans last 9 seconds", failures)
    require("BLE_PAGE_TURNER_RECONNECT_INTERVAL_MS = 10000" in constants,
            "lost-page-turner automatic reconnect cadence is 10 seconds", failures)

    require(appears_in_order(page_window, [
        "const unsigned long now = millis();",
        "_pageTurnerReconnectUntil = now + BLE_PAGE_TURNER_RECONNECT_WINDOW_MS;",
        "_pageTurnerReconnectFastUntil = now + BLE_PAGE_TURNER_RECONNECT_FAST_WINDOW_MS;",
        "BluetoothDiagnostics::recordf(\"page_turner_reconnect_window\"",
    ]), "entering page-turner reconnect mode sets and logs fast plus long windows", failures)

    require(appears_in_order(update_activity, [
        "const bool pageTurnerLike = isFreePageTurnerDevice(device);",
        "const unsigned long timeoutMs = pageTurnerLike ? BLE_PAGE_TURNER_IDLE_RECONNECT_MS : INACTIVITY_TIMEOUT_MS;",
        "inactiveIsPageTurner = pageTurnerLike;",
        "inactiveMatchesBonded =",
        "BluetoothDiagnostics::recordf(\"idle_disconnect\"",
        "disconnectFromDevice(inactiveAddress) && inactiveMatchesBonded",
        "enterPageTurnerReconnectMode(\"page_turner_idle\", true);",
        "armAutoReconnect(inactiveIsPageTurner ? \"page_turner_idle\" : \"idle_timeout\");",
    ]), "quiet bonded page turner is disconnected, marked lost, and arms auto reconnect", failures)

    require(appears_in_order(check_auto, [
        "if (!it->client || !it->client->isConnected())",
        "const bool staleIsPageTurner = isFreePageTurnerDevice(*it);",
        "if (it->wasConnected)",
        "enterPageTurnerReconnectMode(\"stale_client\", true);",
        "armAutoReconnect(\"stale_client\");",
    ]), "already-disconnected stale page-turner client also arms auto reconnect", failures)

    require(appears_in_order(check_auto, [
        "if (hasConnectedDevices)",
        "return;",
        "const bool bondedPageTurner = bondedDeviceLooksLikePageTurner();",
        "enterPageTurnerReconnectMode(\"user_input\", false);",
        "const bool pageTurnerLostWindowActive = bondedPageTurner && pageTurnerReconnectModeActive(now);",
        "const bool pageTurnerFastWindowActive =",
        "const unsigned long pageTurnerInterval = pageTurnerFastWindowActive ? BLE_PAGE_TURNER_RECONNECT_INTERVAL_MS",
        "requiredInterval = pageTurnerInterval;",
        "startBondedReconnect(BLE_MANUAL_RECONNECT_TIMEOUT_MS, true)",
    ]), "auto reconnect waits until disconnected, then uses fast or sparse lost-page-turner cadence", failures)

    require(appears_in_order(start_reconnect, [
        "const bool pageTurnerAutoScan =",
        "automatic && bondedDeviceLooksLikePageTurner() && pageTurnerReconnectModeActive(now);",
        "_reconnectJobScanMs = automatic ? (pageTurnerAutoScan ? BLE_AUTO_PAGE_TURNER_SCAN_MS : BLE_RECONNECT_SCAN_MS)",
        "_reconnectJobAutomatic = automatic;",
        "xTaskCreate(&BluetoothHIDManager::bondedReconnectTaskEntry",
    ]), "automatic page-turner jobs use the 9 second scan and run on worker task", failures)

    require(appears_in_order(scan_reconnect, [
        "const bool activePageTurnerAutoScan =",
        "_reconnectJobAutomatic && bondedDeviceLooksLikePageTurner() && pageTurnerReconnectModeActive(millis());",
        "const bool activeReconnectScan = !_reconnectJobAutomatic || activePageTurnerAutoScan;",
        "BluetoothDiagnostics::recordf(\"manual_reconnect_scan_start\"",
        "activePageTurnerAutoScan",
        "pScan->setScanCallbacks(&scanCallbacks, activeReconnectScan);",
        "pScan->setActiveScan(activeReconnectScan);",
        "pScan->setDuplicateFilter(activeReconnectScan ? 0 : 1);",
    ]), "lost-page-turner automatic scan is active and allows duplicate callbacks", failures)

    if failures:
        print(f"\n{len(failures)} remote-sleep auto-reconnect audit check(s) failed.")
        return 1

    print("\nAll remote-sleep auto-reconnect source checks passed. Hardware validation is still required.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
