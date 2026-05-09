#!/usr/bin/env python3
"""Static page-input path checks for the X3 BLE Free3 build.

These checks do not replace the real EPUB page-turn test. They prove the
source-level path that should carry a Free3 HID report into CrossPoint's
virtual page buttons.
"""

from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MANAGER = ROOT / "lib/hal/BluetoothHIDManager.cpp"
PROFILES_H = ROOT / "lib/hal/DeviceProfiles.h"
PROFILES_CPP = ROOT / "lib/hal/DeviceProfiles.cpp"
HAL_GPIO = ROOT / "lib/hal/HalGPIO.cpp"
MAPPED_INPUT = ROOT / "src/MappedInputManager.cpp"
READER_UTILS = ROOT / "src/activities/reader/ReaderUtils.h"
MAIN = ROOT / "src/main.cpp"


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
    manager = MANAGER.read_text(encoding="utf-8")
    profiles_h = PROFILES_H.read_text(encoding="utf-8")
    profiles_cpp = PROFILES_CPP.read_text(encoding="utf-8")
    hal_gpio = HAL_GPIO.read_text(encoding="utf-8")
    mapped_input = MAPPED_INPUT.read_text(encoding="utf-8")
    reader_utils = READER_UTILS.read_text(encoding="utf-8")
    main_cpp = MAIN.read_text(encoding="utf-8")
    failures: list[str] = []

    find_profile = section(profiles_cpp, "const DeviceProfile* findDeviceProfile", "bool isStandardConsumerPageCode")
    extract_key = section(manager, "static ExtractedHIDKey extractProfileOrGenericKeycode", "static ReportMapHints")
    process_input = section(manager, "void BluetoothHIDManager::processInputEvents()", "void BluetoothHIDManager::setInputCallback")
    notify = section(manager, "void BluetoothHIDManager::onHIDNotify", "void BluetoothHIDManager::processQueuedHIDReport")
    queued_report = section(manager, "void BluetoothHIDManager::processQueuedHIDReport", "uint16_t BluetoothHIDManager::parseHIDKeycode")
    map_button = section(manager, "uint8_t BluetoothHIDManager::mapKeycodeToButton", "void BluetoothHIDManager::updateActivity")
    virtual_button = section(hal_gpio, "void HalGPIO::setVirtualButtonState", "void HalGPIO::injectButtonPress")
    reader_detect = section(reader_utils, "inline PageTurnResult detectPageTurn", "inline void displayWithRefreshCycle")

    require('{"Free3-M", nullptr, 0x02, 0x01, false, 2, false}' in profiles_h,
            "Free3-M profile is present with non-strict page codes and report byte index", failures)
    require(appears_in_order(find_profile, [
        "strstr(deviceName, \"Free3\")",
        "findKnownProfileByName(\"Free3-M\")",
        "return profile;",
    ]), "Free3 names resolve to the Free3-M profile", failures)

    require(appears_in_order(extract_key, [
        "if (length > profile->reportByteIndex)",
        "isProfilePageCode(profile, code)",
        "const size_t firstPassStart = scanLen > 1 ? 1 : 0;",
        "if (isProfilePageCode(profile, code))",
        "return result;",
    ]), "Free3 profile key extraction checks the fixed index and scans short reports", failures)

    require(appears_in_order(notify, [
        "deviceAddr = client->getPeerAddress().toString();",
        "g_instance->queueInputReport(deviceAddr.c_str(), pData, length);",
    ]), "BLE notify callback copies HID reports into the input queue", failures)
    require(appears_in_order(process_input, [
        "xQueueReceive(_inputEventQueue, &event, 0)",
        "processQueuedHIDReport(event.address, event.data, event.length);",
    ]), "main loop input processing drains queued HID reports", failures)

    require(appears_in_order(map_button, [
        "if (keycode == profile->pageUpCode)",
        "return HalGPIO::BTN_UP;",
        "else if (keycode == profile->pageDownCode)",
        "return HalGPIO::BTN_DOWN;",
    ]), "profile page-up/page-down keycodes map to virtual up/down buttons", failures)
    require(appears_in_order(queued_report, [
        "uint8_t mappedButton = isPressed ? g_instance->mapKeycodeToButton(keycode, device) : 0xFF;",
        "g_instance->_buttonInjector(mappedButton, true);",
        "device->activeInjectedButton = mappedButton;",
    ]) and "releaseInjectedButton();" in queued_report,
            "processed HID reports inject and release virtual buttons", failures)

    require(appears_in_order(main_cpp, [
        "btMgr.setButtonInjector([](uint8_t buttonIndex, bool pressed) { gpio.setVirtualButtonState(buttonIndex, pressed); });",
        "btMgr.updateActivity();",
        "btMgr.processInputEvents();",
    ]), "CrossPoint main loop registers the injector and processes BLE input events", failures)
    require(appears_in_order(virtual_button, [
        "desiredVirtualButtonState |= mask;",
        "desiredVirtualButtonState &= ~mask;",
    ]), "virtual button state can press and release GPIO buttons", failures)

    require(appears_in_order(mapped_input, [
        "{HalGPIO::BTN_UP, HalGPIO::BTN_DOWN}",
        "case Button::PageBack:",
        "return (gpio.*fn)(side.pageBack);",
        "case Button::PageForward:",
        "return (gpio.*fn)(side.pageForward);",
    ]), "reader page-back/page-forward are mapped onto up/down side buttons", failures)
    require(appears_in_order(reader_detect, [
        "input.wasPressed(MappedInputManager::Button::PageBack)",
        "input.wasPressed(MappedInputManager::Button::PageForward)",
    ]), "reader page-turn detection consumes virtual page-back/page-forward presses", failures)

    if failures:
        print(f"\n{len(failures)} page-input path check(s) failed.")
        return 1

    print("\nAll page-input path checks passed. Hardware page-turn validation is still required.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
