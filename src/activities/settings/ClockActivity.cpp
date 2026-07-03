#include "ClockActivity.h"

#include <GfxRenderer.h>
#include <HalClock.h>
#include <Logging.h>
#include <WiFi.h>

#include <cstdio>

#include "MappedInputManager.h"
#include "activities/ActivityManager.h"
#include "activities/network/WifiSelectionActivity.h"
#include "components/UITheme.h"
#include "fontIds.h"

void ClockActivity::onEnter() {
  Activity::onEnter();
  state = halClock.isAvailable() ? State::Viewing : State::NoClock;
  status = halClock.isAvailable() ? "RTC time (UTC)" : "RTC not available";
  shouldTearDownWifiOnExit = false;
  requestUpdate();
}

void ClockActivity::onExit() {
  Activity::onExit();

  if (shouldTearDownWifiOnExit && WiFi.getMode() != WIFI_MODE_NULL) {
    WiFi.disconnect(false);
    delay(100);
    WiFi.mode(WIFI_OFF);
    delay(100);
  }
}

std::string ClockActivity::currentTimeText() const {
  char timeBuf[12];
  if (!halClock.formatTime(timeBuf, sizeof(timeBuf), 48, false, true)) {
    return "--:--:--";
  }
  return timeBuf;
}

std::string ClockActivity::currentDateText() const {
  char dateBuf[11];
  if (!halClock.formatDate(dateBuf, sizeof(dateBuf))) {
    return "---- -- --";
  }
  return dateBuf;
}

void ClockActivity::launchWifiSelection() {
  LOG_INF("CLK", "Launching WiFi selection for manual clock sync");
  shouldTearDownWifiOnExit = true;
  startActivityForResult(std::make_unique<WifiSelectionActivity>(renderer, mappedInput),
                         [this](const ActivityResult& result) { onWifiSelectionComplete(!result.isCancelled); });
}

void ClockActivity::onWifiSelectionComplete(bool connected) {
  if (!connected) {
    state = State::Viewing;
    status = "WiFi connection cancelled";
    requestUpdate();
    return;
  }

  state = State::Syncing;
  status = "Syncing time...";
  requestUpdate();
}

void ClockActivity::runSync() {
  requestUpdateAndWait();

  if (!halClock.isAvailable()) {
    state = State::NoClock;
    status = "RTC not available";
    requestUpdate();
    return;
  }

  if (WiFi.status() != WL_CONNECTED) {
    state = State::Failed;
    status = "WiFi not connected";
    requestUpdate();
    return;
  }

  if (!halClock.syncFromNTP()) {
    state = State::Failed;
    status = "Clock sync failed";
    requestUpdate();
    return;
  }

  state = State::Synced;
  status = "Clock synced";
  requestUpdate();
}

void ClockActivity::loop() {
  if (state == State::Syncing) {
    runSync();
    return;
  }

  if (mappedInput.wasPressed(MappedInputManager::Button::Back)) {
    activityManager.goHome();
    return;
  }

  if (mappedInput.wasPressed(MappedInputManager::Button::Confirm)) {
    if (!halClock.isAvailable()) {
      state = State::NoClock;
      status = "RTC not available";
      requestUpdate();
      return;
    }

    if (WiFi.status() == WL_CONNECTED) {
      state = State::Syncing;
      status = "Syncing time...";
      requestUpdate();
    } else {
      launchWifiSelection();
    }
  }
}

void ClockActivity::render(RenderLock&&) {
  renderer.clearScreen();

  const auto& metrics = UITheme::getInstance().getMetrics();
  const auto pageWidth = renderer.getScreenWidth();
  const auto pageHeight = renderer.getScreenHeight();

  GUI.drawHeader(renderer, Rect{0, metrics.topPadding, pageWidth, metrics.headerHeight}, "Clock");

  const int centreY = pageHeight / 2 - 45;
  renderer.drawCenteredText(UI_12_FONT_ID, centreY, currentTimeText().c_str(), true, EpdFontFamily::BOLD);
  renderer.drawCenteredText(UI_10_FONT_ID, centreY + renderer.getLineHeight(UI_12_FONT_ID) + 16,
                            currentDateText().c_str());
  renderer.drawCenteredText(UI_10_FONT_ID, centreY + renderer.getLineHeight(UI_12_FONT_ID) + 42, status.c_str());

  if (state == State::Syncing) {
    renderer.drawCenteredText(UI_10_FONT_ID, centreY + renderer.getLineHeight(UI_12_FONT_ID) + 68,
                              "Connecting to NTP");
  }

  const char* syncLabel = state == State::Syncing ? "" : "Sync";
  const auto labels = mappedInput.mapLabels("Back", syncLabel, "", "");
  GUI.drawButtonHints(renderer, labels.btn1, labels.btn2, labels.btn3, labels.btn4);

  renderer.displayBuffer();
}
