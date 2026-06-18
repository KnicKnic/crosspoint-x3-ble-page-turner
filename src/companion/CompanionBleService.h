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
  void onHostDisconnected();
  void onHostStatusWritten(NimBLECharacteristic* characteristic);
  void onDeviceCommandSubscribed(bool subscribed);

 private:
  CompanionBleService() = default;

  void disconnectConnectedHosts();
  bool hasConnectedHosts() const;
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
  unsigned long hostConnectedAtMs = 0;
  unsigned long lastMaintenanceAtMs = 0;
  unsigned long lastAdvertisingRestartAtMs = 0;
  uint16_t commandSequence = 0;
  HostStatus hostStatus;
  std::function<void()> statusChangedCallback;
};
