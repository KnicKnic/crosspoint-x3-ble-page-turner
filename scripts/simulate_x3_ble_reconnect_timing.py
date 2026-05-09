#!/usr/bin/env python3
"""Model the current idlefix15 page-turner stale-link timing.

This does not prove hardware behavior. It captures the timing argument for why
the firmware waits just after the likely Free3 sleep point before dropping a
stale BLE link and entering the page-turner reconnect window. Idlefix15 keeps
the idlefix7 stale-link cutoff, keeps the first 20 minutes dense, then leaves a
sparse long-idle active-scan catch window open for later wake/restart attempts.
"""

from __future__ import annotations


REMOTE_SLEEP_MS = 180_000
IDLEFIX5_CUTOFF_MS = 165_000
IDLEFIX7_CUTOFF_MS = 195_000
IDLEFIX15_CUTOFF_MS = 195_000
RECONNECT_INTERVAL_MS = 10_000
SLOW_RECONNECT_INTERVAL_MS = 60_000
SCAN_MS = 9_000
FAST_WINDOW_MS = 20 * 60 * 1000
LOST_WINDOW_MS = 4 * 60 * 60 * 1000


def fmt(ms: int) -> str:
    return f"{ms / 1000:.1f}s"


def next_scan_hit(wake_ms: int, cutoff_ms: int) -> int:
    if wake_ms < cutoff_ms:
        return cutoff_ms
    elapsed = wake_ms - cutoff_ms
    interval = RECONNECT_INTERVAL_MS if elapsed < FAST_WINDOW_MS else SLOW_RECONNECT_INTERVAL_MS
    phase_start = cutoff_ms if elapsed < FAST_WINDOW_MS else cutoff_ms + FAST_WINDOW_MS
    slot = (wake_ms - phase_start) // interval
    scan_start = phase_start + slot * interval
    if wake_ms <= scan_start + SCAN_MS:
        return wake_ms
    return scan_start + interval


def main() -> int:
    print("Remote sleep assumption:", fmt(REMOTE_SLEEP_MS))
    print("idlefix5 stale cutoff:", fmt(IDLEFIX5_CUTOFF_MS))
    print("idlefix7 stale cutoff:", fmt(IDLEFIX7_CUTOFF_MS))
    print("idlefix15 stale cutoff:", fmt(IDLEFIX15_CUTOFF_MS))
    print("fast lost-page-turner scan:", fmt(SCAN_MS), "every", fmt(RECONNECT_INTERVAL_MS))
    print("slow long-idle scan:", fmt(SCAN_MS), "every", fmt(SLOW_RECONNECT_INTERVAL_MS))
    print()

    if IDLEFIX5_CUTOFF_MS < REMOTE_SLEEP_MS:
        print("idlefix5 risk: stale cleanup can fire before the Free3 actually sleeps.")
    if IDLEFIX7_CUTOFF_MS > REMOTE_SLEEP_MS:
        print("idlefix7 intent: stale cleanup fires shortly after the likely Free3 sleep point.")
    print("idlefix15 intent: once stale cleanup fires, the reader scans for 9s out of each 10s first,")
    print("idlefix15 intent: then keeps a sparse 9s/60s active-scan catch window for long idle.")
    print()

    wake_times = [180_000, 195_000, 204_500, 210_000, 217_000, 224_500, 225_000, 240_000, 300_000]
    print("Wake time after last HID report -> expected catch time with idlefix15")
    for wake_ms in wake_times:
        hit_ms = next_scan_hit(wake_ms, IDLEFIX15_CUTOFF_MS)
        wait_ms = max(0, hit_ms - wake_ms)
        print(f"  wake at {fmt(wake_ms):>6}: catch at {fmt(hit_ms):>6}, wait {fmt(wait_ms)}")

    long_wake_times = [25 * 60 * 1000, 60 * 60 * 1000, 2 * 60 * 60 * 1000]
    print()
    print("Long-idle wake/restart -> expected sparse catch time with idlefix15")
    for wake_ms in long_wake_times:
        hit_ms = next_scan_hit(wake_ms, IDLEFIX15_CUTOFF_MS)
        wait_ms = max(0, hit_ms - wake_ms)
        print(f"  wake at {fmt(wake_ms):>6}: catch at {fmt(hit_ms):>6}, wait {fmt(wait_ms)}")

    print()
    print("The model only covers scheduler timing; real proof still requires X3/Free3 flashing and testing.")
    assert IDLEFIX7_CUTOFF_MS > REMOTE_SLEEP_MS
    assert IDLEFIX15_CUTOFF_MS == IDLEFIX7_CUTOFF_MS
    assert FAST_WINDOW_MS >= 20 * 60 * 1000
    assert LOST_WINDOW_MS >= 4 * 60 * 60 * 1000
    assert RECONNECT_INTERVAL_MS - SCAN_MS == 1000
    post_cutoff_waits = [
        next_scan_hit(wake, IDLEFIX15_CUTOFF_MS) - wake
        for wake in wake_times
        if wake >= IDLEFIX15_CUTOFF_MS
    ]
    assert max(post_cutoff_waits) <= RECONNECT_INTERVAL_MS
    assert max(post_cutoff_waits) <= 1000
    assert next_scan_hit(REMOTE_SLEEP_MS, IDLEFIX15_CUTOFF_MS) - REMOTE_SLEEP_MS == 15_000
    long_waits = [next_scan_hit(wake, IDLEFIX15_CUTOFF_MS) - wake for wake in long_wake_times]
    assert max(long_waits) <= SLOW_RECONNECT_INTERVAL_MS
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
