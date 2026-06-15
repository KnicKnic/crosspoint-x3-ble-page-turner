#pragma once

#include <cstdint>

namespace CompanionProtocol {

constexpr const char* SERVICE_UUID = "7d2d5f00-778d-4df6-a6d5-7c4e7a000001";
constexpr const char* HOST_STATUS_UUID = "7d2d5f00-778d-4df6-a6d5-7c4e7a000002";
constexpr const char* DEVICE_COMMAND_UUID = "7d2d5f00-778d-4df6-a6d5-7c4e7a000003";
constexpr const char* DEVICE_INFO_UUID = "7d2d5f00-778d-4df6-a6d5-7c4e7a000004";
constexpr const char* NOTES_TRANSFER_UUID = "7d2d5f00-778d-4df6-a6d5-7c4e7a000005";

constexpr uint8_t PROTOCOL_VERSION = 1;

enum class MessageType : uint8_t {
  HostStatus = 1,
  DeviceCommand = 2,
  Ack = 3,
  Error = 4,
};

enum class DeviceCommand : uint8_t {
  ToggleMute = 1,
};

enum class TriState : uint8_t {
  Unknown = 0,
  Off = 1,
  On = 2,
};

}  // namespace CompanionProtocol
