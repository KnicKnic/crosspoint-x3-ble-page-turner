#pragma once

#include <cstdint>
#include <functional>
#include <string>

class NimBLECharacteristic;
class NimBLEServer;

class CompanionBleService {
 public:
  struct HostStatus {
    bool teamsDetected = false;
    uint8_t microphone = 0;
    uint8_t camera = 0;
    uint16_t sequence = 0;
    std::string message;
  };

  static CompanionBleService& getInstance();

  bool begin();
  void end();
  bool isRunning() const { return running; }
  bool isHostConnected() const { return hostConnected; }
  bool isConnectionHandshakeActive() const;
  void update();
  bool restartAdvertising(const char* reason);
  std::string getStatusText() const;
  HostStatus getHostStatus() const;
  bool consumeStatusChanged();
  bool sendToggleMute();
  void setStatusChangedCallback(std::function<void()> callback);

  void onHostConnected();
  void onHostConnected(uint16_t connHandle);
  void onHostDisconnected();
  void onConnParamsUpdated(uint16_t interval, uint16_t latency, uint16_t timeout);
  void onHostStatusWritten(NimBLECharacteristic* characteristic);
  void onDeviceCommandSubscribed(bool subscribed);

 private:
  CompanionBleService() = default;

  enum class ConnectionPowerProfile : uint8_t {
    Unknown,
    Responsive,
    Idle,
  };

  void disconnectConnectedHosts();
  bool hasConnectedHosts() const;
  void resetSessionState();
  void requestConnectionParams(ConnectionPowerProfile profile, const char* reason);
  void requestIdleConnectionParamsIfReady(const char* reason);
  void notifyStatusChanged();
  void publishDeviceInfo();
  void publishAck(uint16_t sequence, uint8_t ackedMessageType);
  void publishDeviceCommand(uint8_t command);

  NimBLEServer* server = nullptr;
  NimBLECharacteristic* hostStatusCharacteristic = nullptr;
  NimBLECharacteristic* deviceCommandCharacteristic = nullptr;
  NimBLECharacteristic* deviceInfoCharacteristic = nullptr;
  bool running = false;
  bool hostConnected = false;
  bool hostStatusReceived = false;
  bool deviceCommandSubscribed = false;
  bool ownsBluetoothStack = false;
  bool statusChanged = false;
  ConnectionPowerProfile connectionProfile = ConnectionPowerProfile::Unknown;
  uint16_t hostConnHandle = 0xFFFF;
  unsigned long hostConnectedAtMs = 0;
  unsigned long lastMaintenanceAtMs = 0;
  unsigned long lastAdvertisingRestartAtMs = 0;
  unsigned long lastConnParamRequestAtMs = 0;
  unsigned long responsiveUntilMs = 0;
  uint16_t commandSequence = 0;
  HostStatus hostStatus;
  std::function<void()> statusChangedCallback;
};
