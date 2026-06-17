#pragma once

#include "../Activity.h"

#include <string>

class LaptopCompanionActivity final : public Activity {
  bool hostConnected = false;
  std::string statusMessage = "Waiting for host";
  std::string microphoneMessage = "Unknown";
  std::string cameraMessage = "Unknown";

 public:
  explicit LaptopCompanionActivity(GfxRenderer& renderer, MappedInputManager& mappedInput)
      : Activity("LaptopCompanion", renderer, mappedInput) {}

  void onEnter() override;
  void onExit() override;
  void loop() override;
  void render(RenderLock&&) override;
  bool preventAutoSleep() override;
  bool suppressAutoDeepSleep() override;
};
