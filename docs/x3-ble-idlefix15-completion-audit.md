# X3 BLE Idlefix15 Completion Audit

Date: 2026-05-09

Objective: create and validate a robust CrossPoint-derived firmware build for
the XTEink X3 with Bluetooth page-turner support and safe automatic reconnect.

Idlefix15 is the final validated candidate. It keeps the known-good short
reconnect behavior, keeps the long-idle auto-sleep bookkeeping fix, and prevents
BLE reconnect workers from running while the X3 is in 10 MHz low-power mode.

## Current Candidate

- Firmware name: `crosspoint-x3-ble-idlefix15.bin`
- Version marker: `1.2.0-x3-ble-idlefix15`
- SHA-256: `5b23aee2453df26f35fc837ea580eedc3b7c8fa7deeb0f01092e9b7ff7b2949f`
- Size: `0x5b62e0`
- App partition limit: `0x640000`

## Requirement Checklist

| Requirement | Evidence | Status |
| --- | --- | --- |
| CrossPoint-derived firmware artifact for X3 | idlefix15 binary exists, matches expected SHA-256 and size, and contains the idlefix15 version marker | Passed |
| Bluetooth support uses the updated safe stack | `platformio.ini` pins `h2zero/NimBLE-Arduino @ 2.5.0` | Passed |
| Firmware fits the X3 OTA app partition | idlefix15 size `0x5b62e0` is below `0x640000` | Passed |
| Manual reconnect returns to the known-good baseline | Restored 45 second active scan, 12 second connect timeout, bond-reset connect attempt, and broad HID report enumeration | Passed |
| BLE reconnect runs at normal CPU speed | Reconnect worker owns a `HalPowerManager::Lock`; main loop keeps power saving disabled while reconnect work is active | Passed |
| Reconnect diagnostics avoid critical-path storage writes | Reconnect start is flushed before scan/connect; non-forced diagnostic flushes are suppressed until the worker exits | Passed |
| Long-idle diagnostics survive reboot | BLE diagnostics reload the previous ring before appending the new boot entry | Passed |
| Flash was performed safely | app0 and app1 were written and verified with `verify-flash` | Passed |
| Device boots after flash | CrossPoint UI stayed responsive after flashing | Passed |
| Manual reconnect path is available | `Reconnect Remote` reconnected the Free3 without freeze or reboot | Passed |
| Page-turn input works both ways | Forward and back page turns were confirmed in an EPUB | Passed |
| Reader sleep/wake auto reconnect works | Page turns returned after reader sleep/wake without opening Bluetooth settings | Passed |
| Remote sleep/off/on auto reconnect works | Free3 sleep/off/on reconnected without opening Bluetooth settings | Passed |
| Long-idle recovery works | Auto-sleep plus reconnect recovery worked after the prior long-idle failure class | Passed |
| Repeated cycles are stable | 3 reader sleep/wake cycles and 3 remote sleep/off/on cycles completed with no crash | Passed |
| Guard behavior remains safe after validation | Source guard audit passed; no guarded reconnect crash was reported; manual reconnect remained available | Passed |
| Remaining limits are documented | README and `docs/x3-ble-page-turner.md` document hardware scope and limitations | Passed |

## Local Checks

These passed locally on 2026-05-09:

- `pio run -e gh_release`
- `python3 scripts/verify_x3_ble_idlefix15.py`
- `python3 scripts/preflight_x3_ble_idlefix15.py --local-only`
- `python3 scripts/inspect_x3_ble_firmware_image.py`
- `python3 scripts/simulate_x3_ble_reconnect_timing.py`
- `python3 scripts/audit_x3_ble_reconnect_invariants.py`
- `python3 scripts/audit_x3_ble_remote_sleep_autoreconnect.py`
- `python3 scripts/audit_x3_ble_guard_behavior.py`
- `python3 scripts/audit_x3_ble_page_input_path.py`
- `python3 scripts/test_x3_ble_record_hardware_answer.py`
- `git diff --check`

The strict completion audit now passes:

```sh
python3 scripts/audit_x3_ble_goal_completion.py
```

Result: `Completion audit result: COMPLETE`.
