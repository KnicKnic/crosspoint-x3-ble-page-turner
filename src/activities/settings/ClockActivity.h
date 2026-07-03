#pragma once

#include <string>

#include "activities/Activity.h"

class ClockActivity final : public Activity {
  enum class State { Viewing, Syncing, Synced, Failed, NoClock };

  State state = State::Viewing;
  std::string status;
  bool shouldTearDownWifiOnExit = false;

  void launchWifiSelection();
  void onWifiSelectionComplete(bool connected);
  void runSync();
  std::string currentTimeText() const;
  std::string currentDateText() const;

 public:
  explicit ClockActivity(GfxRenderer& renderer, MappedInputManager& mappedInput)
      : Activity("Clock", renderer, mappedInput) {}

  void onEnter() override;
  void onExit() override;
  void loop() override;
  void render(RenderLock&&) override;
  bool skipLoopDelay() override { return state == State::Syncing; }
};
