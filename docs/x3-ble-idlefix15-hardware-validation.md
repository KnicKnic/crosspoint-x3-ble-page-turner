# X3 BLE Idlefix15 Hardware Validation

Packaged candidate:

- Firmware: `crosspoint-x3-ble-idlefix15.bin`
- Version marker: `1.2.0-x3-ble-idlefix15`
- SHA-256: `5b23aee2453df26f35fc837ea580eedc3b7c8fa7deeb0f01092e9b7ff7b2949f`
- Binary size: `0x5b62e0`
- Build command: `pio run -e gh_release`

Idlefix15 was created after idlefix14 restored the idlefix10 reconnect shape
but still crashed during manual `Reconnect Remote`. Idlefix10 passed boot,
manual reconnect, page input, reader sleep/wake, and remote sleep/off/on; it
failed only after about 2 hours unused, when the X3 stayed awake and auto
reconnect did not recover.

Idlefix15 restores the idlefix10 reconnect shape:

- manual reconnect uses the 45 second active manual scan again;
- settings starts manual bonded reconnect with the restored 12 second connect
  timeout;
- reconnect attempts again use the idlefix10 bond-reset connect attempt;
- HID report discovery uses broad characteristic enumeration instead of the
  idlefix13 minimal Free3 lookup;
- automatic page-turner-lost reconnect still uses 9 second active scans roughly
  every 10 seconds while the bounded lost-page-turner window is active.

Idlefix15 keeps the long-idle auto-sleep bookkeeping fix:

- the first 20 minutes of lost page-turner recovery still use dense active
  scans;
- page-turner recovery then continues with a sparse active-scan window for up to
  4 hours;
- reader wake and local user input refresh the page-turner fast recovery window;
- reconnect workers no longer count as recent BLE activity for the auto-sleep
  inactivity timer;
- auto-sleep waits only while a reconnect worker is actively running, then may
  sleep in the next reconnect gap;
- X3 no longer treats a USB-connected power-on as "USB power only, go back to
  sleep" because the X3 fuel-gauge charging signal can make a real button wake
  vanish back into sleep before the UI and USB CDC return.

Idlefix15 fixes the likely idlefix14 regression: reconnect workers no longer
reset the auto-sleep inactivity timer, but they now hold normal CPU speed while
the BLE scan/connect/subscribe path is running. The main loop also refuses to
enter the 10 MHz low-power mode while bonded reconnect work is active. This
keeps the idlefix10 reconnect baseline while avoiding the low-power BLE worker
condition that matched the manual reconnect freeze/reboot.

To reduce pressure during GATT discovery, idlefix15 records reconnect diagnostic
events in memory during the critical worker path and flushes them after the
worker exits. The `manual_reconnect_start` / `auto_reconnect_start` event is
still flushed before entering the critical section, so a crash still proves the
reconnect worker began.

Idlefix15 also keeps the serial `CMD:BLE_DIAG` command and preserves the
previous BLE diagnostic ring across reboot, so the next crash should leave the
last reconnect stage visible instead of being overwritten by the next boot.

## Pass Criteria

The candidate is acceptable only if all of these pass on the actual XTEink X3
plus Free3/Free3-ER page turner:

| Gate | Test | Pass condition | Result |
| --- | --- | --- | --- |
| Flash | Run `scripts/flash_record_x3_ble_idlefix15.sh` | Both app slots write and explicit `verify-flash` checks pass | Passed 2026-05-09: app0 app1 write and verify-flash succeeded |
| Boot | Start the X3 after flashing idlefix15 | CrossPoint UI appears and stays responsive for at least 2 minutes | Passed 2026-05-09: CrossPoint UI responsive; manual reconnect and current auto reconnect testing possible |
| Manual reconnect | Open Bluetooth settings, use `Reconnect Remote` if needed | Reconnect succeeds without freeze/reboot | Passed 2026-05-09: Reconnect Remote succeeded without freeze/reboot on idlefix15 |
| Page input | Open an EPUB and press both page-turn buttons | Forward and back page turns work reliably | Passed 2026-05-09: forward and back buttons turned pages reliably |
| Reader sleep/wake | Sleep the reader with Bluetooth enabled, wake it, wait up to 60 seconds | The X3 wakes responsively and page turner works again without opening settings | Passed 2026-05-09: sleep wake reconnect restored page turns without settings menu |
| Remote sleep/off/on | Connect, turn a page, leave the remote untouched until it sleeps or turn it off, then wake/turn it on | Page turner works again without opening settings; first press may only wake the remote | Passed 2026-05-09: remote sleep/off/on reconnected without settings menu |
| Long idle recovery | Leave the X3 and Free3 unused long enough to exceed the prior idlefix10 failure window or a practical overnight/2-hour window, then test wake and Free3 restart without opening Bluetooth settings | The X3 auto-sleeps by its configured timeout, and page-turner reconnect recovers after wake or remote restart | Passed 2026-05-09: long idle auto sleep and reconnect recovered without settings menu |
| Repeated cycles | Repeat reader sleep/wake and remote sleep/off/on at least 3 times each | No crash, reboot loop, frozen UI, or FreeRTOS assert | Passed 2026-05-09: 3 reader cycles and 3 remote cycles completed, no crash |
| Guard behavior | Run `scripts/audit_x3_ble_guard_behavior.py`; after reconnect validation, confirm no guard-triggering reconnect crash occurred, or if one did, reboot once and use manual reconnect | Guard source audit passes, automatic reconnect stays conservative after guarded/crash-like boots, and manual reconnect remains available | Passed 2026-05-09: guard audit passed; manual reconnect remained available during validation and no guarded reconnect crash was reported |

Manual reconnect is not the final success condition. It is only the safe entry
gate that proves the bonded remote can be found and connected. The auto
reconnect and long-idle gates must pass for this candidate to count.

Do not use `Scan for devices` during daily-driver validation. It is
intentionally disabled in the UI because earlier X3 tests made scan a crash
path. The intended flow is one manual `Reconnect Remote`, then automatic bonded
reconnect.

## Live Observations

- 2026-05-09: Short-path manual reconnect was stabler than idlefix14 and
  succeeded without a freeze/reboot.
- 2026-05-09: Short-path automatic reconnect worked, then the longer idle
  recovery gate also passed.
- 2026-05-09: Sleep wallpaper did not appear during sleep. macOS did not show
  a mounted reader storage volume at the time of inspection, so `/.sleep`,
  `/sleep`, and `/sleep.bmp` could not be verified from the host. Firmware code
  only reads those wallpaper paths and should fall back to the default sleep
  screen if custom wallpapers are missing. Added
  `scripts/check_x3_sleep_wallpapers.py` to inspect the mounted storage once it
  is available.
- 2026-05-09: User clarified the sleep screen is completely white while
  sleeping. In the current code, that exact behavior matches the `Blank` /
  `None` sleep-screen setting. A missing custom wallpaper should fall back to
  the default CrossPoint sleep screen, not pure white.
- 2026-05-09: User confirmed the sleep-screen setting was the cause; after
  changing it back, the wallpaper appears during sleep again.

## Immediate On-Device Test Order

Run these in order and stop at the first failure:

1. Boot: confirm CrossPoint appears and the UI responds for at least 2 minutes.
2. Manual reconnect: open Bluetooth settings and use `Reconnect Remote` if the
   Free3 is not already connected. Do not use `Scan for devices`.
3. Page input: open an EPUB and test both page-forward and page-back. Record
   the exact result as one of: both directions worked, only forward worked, or
   only back worked.
4. Reader sleep/wake: sleep the reader with Bluetooth enabled, wake it, wait up
   to 60 seconds, then test page turning without opening Bluetooth settings.
5. Remote sleep/off/on: after one successful page turn, leave the Free3 idle
   until it sleeps or turn it off, wake/turn it back on, wait up to 60 seconds,
   then test page turning without opening Bluetooth settings.
6. Long idle recovery: leave the setup unused long enough to reproduce the
   idlefix10 class of failure. Confirm the X3 auto-sleeps by its configured
   timeout, then wake it and/or restart the Free3 and test page turns without
   opening Bluetooth settings.
7. Repeated cycles: repeat reader sleep/wake and remote sleep/off/on at least 3
   times each, watching for freezes, reboot loops, or FreeRTOS asserts.
8. Guard behavior: after the repeated cycles, run
   `scripts/audit_x3_ble_guard_behavior.py` locally. If there was no reconnect
   crash, record that the guard audit passed and manual reconnect stayed
   available during validation.

If a failure happens and the X3 storage or SD card is mounted, summarize:

```sh
scripts/read_x3_ble_diag.py
```
