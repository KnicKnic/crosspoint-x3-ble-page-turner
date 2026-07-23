# X3 Laptop Companion Host

.NET 10 WPF tray app for the X3 BLE Laptop Companion.

## Requirements

- Windows 10 19041 or newer.
- .NET 10 SDK.
- Bluetooth LE adapter.

## Current Status

- Scans for the X3 companion GATT service.
- Subscribes to device-command notifications.
- Sends best-effort Teams presence, microphone, and camera status to the X3.
- Handles `ToggleMute` by posting `Ctrl+Shift+M` directly to the Teams window.
- The GUI can also post Teams shortcuts for speaker (`Ctrl+Shift+U`),
  raise/lower hand (`Ctrl+Shift+K`), and video (`Ctrl+Shift+O`).
- The GUI can dump a selected window's UI Automation tree to the host log.
  The window target accepts `hwnd:0x...`, `0x...`, `pid:1234`, or a title
  fragment; use `List` to discover visible top-level windows. Dumps focus on
  the selected process's RawView subtree and log upward paths for buttons named
  `Unmute`.
- Includes a test mode that keeps BLE active and sends simulated Teams,
  microphone, camera, and status-message values to the X3.
- Includes a Teams dry-run mode that keeps BLE active and acknowledges X3 mute
  commands without focusing or controlling Teams.
- Writes a diagnostic log to `%LOCALAPPDATA%\X3LaptopCompanion\host.log`.
  Use the `Open Log` button or tray menu item to jump to it.

Microphone status is detected from WASAPI endpoint/session mute state, with
active Teams capture sessions used as a best-effort live signal.
Camera status is detected from the Windows webcam consent-store timestamps.

## Build

```powershell
dotnet build .\X3LaptopCompanion.csproj
```
