#pragma once

#include "../Activity.h"

#include <string>

class LaptopCompanionActivity final : public Activity {
  bool hostConnected = false;
  unsigned long noHostConnectedSinceMs = 0;
  std::string statusMessage = "Waiting for host";
  std::string meetingMessage = "Unknown";
  std::string microphoneMessage = "Unknown";
  std::string cameraMessage = "Unknown";
  std::string handMessage = "Unknown";

  void updateNoHostTimer(bool connected);
  bool shouldHoldWakeForCompanion() const;

 public:
  explicit LaptopCompanionActivity(GfxRenderer& renderer, MappedInputManager& mappedInput)
      : Activity("LaptopCompanion", renderer, mappedInput) {}

  void onEnter() override;
  void onExit() override;
  void loop() override;
  void render(RenderLock&&) override;
  bool preventAutoSleep() override;
  bool suppressAutoDeepSleep() override;
  bool isCompanionActivity() const override { return true; }
};
