#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN_PATH="${1:-${X3_BLE_FIRMWARE_BIN:-$HOME/Downloads/crosspoint-x3-ble-idlefix15.bin}}"
ESPTOOL="${ESPTOOL:-$HOME/.platformio/penv/bin/esptool}"
PYTHON="${PYTHON:-$HOME/.platformio/penv/bin/python}"
WAIT_SECONDS="${X3_FLASH_WAIT_SECONDS:-0}"
POLL_SECONDS="${X3_FLASH_POLL_SECONDS:-2}"

APP0_OFFSET="0x10000"
APP1_OFFSET="0x650000"
APP_PARTITION_SIZE=$((0x640000))

if ! [[ "$WAIT_SECONDS" =~ ^[0-9]+$ ]]; then
  echo "X3_FLASH_WAIT_SECONDS must be a non-negative integer" >&2
  exit 2
fi

if ! [[ "$POLL_SECONDS" =~ ^[0-9]+$ ]] || (( POLL_SECONDS < 1 )); then
  echo "X3_FLASH_POLL_SECONDS must be a positive integer" >&2
  exit 2
fi

if [[ ! -f "$BIN_PATH" ]]; then
  echo "Firmware binary not found: $BIN_PATH" >&2
  exit 2
fi

bin_size="$(stat -f%z "$BIN_PATH")"
if (( bin_size > APP_PARTITION_SIZE )); then
  printf 'Firmware too large: 0x%x > 0x%x\n' "$bin_size" "$APP_PARTITION_SIZE" >&2
  exit 3
fi

find_candidate_ports() {
  "$PYTHON" - <<'PY'
import serial.tools.list_ports

known_wrong = {
    (0x4C4A, 0x4155): "Jieli USB Composite Device, not the X3 ESP32-C3",
}

ports = list(serial.tools.list_ports.comports())
for port in ports:
    vid = port.vid
    pid = port.pid
    reason = known_wrong.get((vid, pid))
    if reason:
        print(f"SKIP\t{port.device}\t{vid:04x}:{pid:04x}\t{reason}")
        continue
    if vid is None or pid is None:
        if "usbmodem" in port.device:
            print(f"PORT\t{port.device}\tno-vidpid\t{port.description}")
        continue
    # Espressif USB serial/JTAG commonly appears as 303a:1001. Keep the
    # matcher permissive because prior successful X3 sessions used usbmodem
    # paths whose descriptors varied by boot state.
    if vid == 0x303A or "usbmodem" in port.device:
        print(f"PORT\t{port.device}\t{vid:04x}:{pid:04x}\t{port.description}")
PY
}

echo "Firmware: $BIN_PATH"
printf 'Size: 0x%x bytes\n' "$bin_size"
echo "SHA-256: $(shasum -a 256 "$BIN_PATH" | awk '{print $1}')"
echo

candidate_file="$(mktemp)"
trap 'rm -f "$candidate_file"' EXIT
port=""

deadline=$((SECONDS + WAIT_SECONDS))
attempt=0
while true; do
  attempt=$((attempt + 1))
  find_candidate_ports >"$candidate_file"
  while IFS= read -r line; do
    echo "$line"
  done <"$candidate_file"

  port=""
  while IFS= read -r line; do
    if [[ "$line" == PORT$'\t'* ]]; then
      port="$(cut -f2 <<<"$line")"
      break
    fi
  done <"$candidate_file"

  if [[ -n "$port" ]]; then
    break
  fi

  if (( WAIT_SECONDS == 0 || SECONDS >= deadline )); then
    break
  fi

  remaining=$((deadline - SECONDS))
  if (( remaining < POLL_SECONDS )); then
    sleep_for="$remaining"
  else
    sleep_for="$POLL_SECONDS"
  fi
  if (( sleep_for < 1 )); then
    sleep_for=1
  fi

  echo "No X3/ESP32-C3 flash port found yet; waiting ${sleep_for}s (attempt ${attempt})..."
  sleep "$sleep_for"
done

if [[ -z "$port" ]]; then
  cat >&2 <<EOF

No X3/ESP32-C3 flash port found.

Put the X3 into download mode, then run this script again. To wait while you do it:
  X3_FLASH_WAIT_SECONDS=120 scripts/flash_x3_ble_guardedauto.sh "$BIN_PATH"

Manual download-mode sequence:
  1. Hold BOOT/G0.
  2. Tap RST.
  3. Release BOOT/G0.

If macOS still only shows 4c4a:4155, that is the Jieli USB composite device,
not the X3 flash port. Move the X3 USB cable to a direct USB port or another hub.
EOF
  exit 4
fi

echo
echo "Using port: $port"
"$ESPTOOL" --chip esp32c3 -p "$port" chip-id

echo
echo "Writing both OTA app slots..."
"$ESPTOOL" --chip esp32c3 -p "$port" -b 921600 write-flash \
  "$APP0_OFFSET" "$BIN_PATH" \
  "$APP1_OFFSET" "$BIN_PATH"

echo
echo "Verifying both OTA app slots..."
"$ESPTOOL" --chip esp32c3 -p "$port" verify-flash "$APP0_OFFSET" "$BIN_PATH"
"$ESPTOOL" --chip esp32c3 -p "$port" verify-flash "$APP1_OFFSET" "$BIN_PATH"

echo
echo "Flashed and verified guarded auto reconnect firmware to app0 and app1."
