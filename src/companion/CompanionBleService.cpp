#include "CompanionBleService.h"

#include <BluetoothDiagnostics.h>
#include <BluetoothHIDManager.h>
#include <Logging.h>
#include <NimBLEDevice.h>

#include <algorithm>
#include <cstring>
#include <utility>

#include "CompanionProtocol.h"

namespace {
CompanionBleService* g_service = nullptr;

class CompanionServerCallbacks : public NimBLEServerCallbacks {
  void onConnect(NimBLEServer* pServer, NimBLEConnInfo& connInfo) override {
    (void)pServer;
    (void)connInfo;
    if (g_service) {
      g_service->onHostConnected();
    }
  }

  void onDisconnect(NimBLEServer* pServer, NimBLEConnInfo& connInfo, int reason) override {
    (void)connInfo;
    (void)reason;
    if (g_service) {
      g_service->onHostDisconnected();
    }
    if (pServer && g_service && g_service->isRunning()) {
      NimBLEDevice::startAdvertising();
    }
  }
};

class HostStatusCallbacks : public NimBLECharacteristicCallbacks {
  void onWrite(NimBLECharacteristic* characteristic, NimBLEConnInfo& connInfo) override {
    (void)connInfo;
    if (g_service) {
      g_service->onHostStatusWritten(characteristic);
    }
  }
};

CompanionServerCallbacks serverCallbacks;
HostStatusCallbacks hostStatusCallbacks;
}  // namespace

CompanionBleService& CompanionBleService::getInstance() {
  static CompanionBleService instance;
  return instance;
}

bool CompanionBleService::begin() {
  if (running) {
    LOG_DBG("COMP", "Companion BLE already running");
    return true;
  }

  auto& btMgr = BluetoothHIDManager::getInstance();
  ownsBluetoothStack = !btMgr.isEnabled();
  LOG_INF("COMP", "Starting companion BLE service; owns stack=%d", ownsBluetoothStack);
  if (!btMgr.enable()) {
    LOG_ERR("COMP", "Bluetooth enable failed: %s", btMgr.lastError.c_str());
    BluetoothDiagnostics::recordf("companion_ble_enable_failed", "msg=%s", btMgr.lastError.c_str());
    return false;
  }

  g_service = this;
  if (!server) {
    server = NimBLEDevice::createServer();
    if (!server) {
      LOG_ERR("COMP", "Failed to create companion BLE server");
      BluetoothDiagnostics::record("companion_server_create_failed");
      if (ownsBluetoothStack) {
        btMgr.disable();
      }
      ownsBluetoothStack = false;
      return false;
    }

    server->setCallbacks(&serverCallbacks);
    auto* service = server->createService(CompanionProtocol::SERVICE_UUID);
    if (!service) {
      LOG_ERR("COMP", "Failed to create companion GATT service");
      BluetoothDiagnostics::record("companion_service_create_failed");
      if (ownsBluetoothStack) {
        btMgr.disable();
      }
      ownsBluetoothStack = false;
      server = nullptr;
      return false;
    }

    hostStatusCharacteristic = service->createCharacteristic(CompanionProtocol::HOST_STATUS_UUID,
                                                             NIMBLE_PROPERTY::WRITE | NIMBLE_PROPERTY::WRITE_NR);
    deviceCommandCharacteristic = service->createCharacteristic(CompanionProtocol::DEVICE_COMMAND_UUID,
                                                                NIMBLE_PROPERTY::READ | NIMBLE_PROPERTY::NOTIFY);
    deviceInfoCharacteristic = service->createCharacteristic(CompanionProtocol::DEVICE_INFO_UUID, NIMBLE_PROPERTY::READ);

    if (!hostStatusCharacteristic || !deviceCommandCharacteristic || !deviceInfoCharacteristic) {
      LOG_ERR("COMP", "Failed to create companion characteristics host=%p command=%p info=%p", hostStatusCharacteristic,
              deviceCommandCharacteristic, deviceInfoCharacteristic);
      BluetoothDiagnostics::record("companion_characteristic_create_failed");
      if (ownsBluetoothStack) {
        btMgr.disable();
      }
      ownsBluetoothStack = false;
      server = nullptr;
      return false;
    }

    hostStatusCharacteristic->setCallbacks(&hostStatusCallbacks);
    service->start();
  }

  publishDeviceInfo();

  auto* advertising = NimBLEDevice::getAdvertising();
  advertising->clearData();
  advertising->addServiceUUID(CompanionProtocol::SERVICE_UUID);
  advertising->start();
  LOG_INF("COMP", "Advertising companion service %s", CompanionProtocol::SERVICE_UUID);

  running = true;
  hostConnected = false;
  statusChanged = true;
  BluetoothDiagnostics::record("companion_ble_started");
  LOG_INF("COMP", "Companion BLE service started");
  return true;
}

void CompanionBleService::end() {
  if (!running) {
    return;
  }

  NimBLEDevice::stopAdvertising();
  running = false;
  hostConnected = false;
  statusChanged = true;
  BluetoothDiagnostics::record("companion_ble_stopped");
  LOG_INF("COMP", "Companion BLE service stopped; owns stack=%d", ownsBluetoothStack);

  if (ownsBluetoothStack) {
    BluetoothHIDManager::getInstance().disable();
    server = nullptr;
    hostStatusCharacteristic = nullptr;
    deviceCommandCharacteristic = nullptr;
    deviceInfoCharacteristic = nullptr;
  }
  ownsBluetoothStack = false;
}

std::string CompanionBleService::getStatusText() const {
  if (!running) {
    return "BLE failed";
  }
  if (hostConnected) {
    return hostStatus.teamsDetected ? "Teams running" : "Host connected";
  }
  return "Waiting for host";
}

CompanionBleService::HostStatus CompanionBleService::getHostStatus() const { return hostStatus; }

bool CompanionBleService::consumeStatusChanged() {
  const bool changed = statusChanged;
  statusChanged = false;
  return changed;
}

bool CompanionBleService::sendToggleMute() {
  if (!running || !hostConnected || !deviceCommandCharacteristic) {
    LOG_INF("COMP", "Toggle mute not sent; running=%d connected=%d commandChar=%p", running, hostConnected,
            deviceCommandCharacteristic);
    return false;
  }

  publishDeviceCommand(static_cast<uint8_t>(CompanionProtocol::DeviceCommand::ToggleMute));
  return true;
}

void CompanionBleService::setStatusChangedCallback(std::function<void()> callback) {
  statusChangedCallback = std::move(callback);
}

void CompanionBleService::onHostConnected() {
  hostConnected = true;
  statusChanged = true;
  BluetoothDiagnostics::record("companion_host_connected");
  LOG_INF("COMP", "Host connected to companion BLE service");
  if (statusChangedCallback) {
    statusChangedCallback();
  }
}

void CompanionBleService::onHostDisconnected() {
  hostConnected = false;
  statusChanged = true;
  BluetoothDiagnostics::record("companion_host_disconnected");
  LOG_INF("COMP", "Host disconnected from companion BLE service");
  if (statusChangedCallback) {
    statusChangedCallback();
  }
}

void CompanionBleService::onHostStatusWritten(NimBLECharacteristic* characteristic) {
  if (!characteristic) {
    return;
  }

  const std::string value = characteristic->getValue();
  if (value.size() < 6 || static_cast<uint8_t>(value[0]) != CompanionProtocol::PROTOCOL_VERSION ||
      static_cast<uint8_t>(value[1]) != static_cast<uint8_t>(CompanionProtocol::MessageType::HostStatus)) {
    BluetoothDiagnostics::record("companion_host_status_invalid");
    LOG_INF("COMP", "Invalid host status payload len=%u", static_cast<unsigned>(value.size()));
    return;
  }

  HostStatus next;
  next.sequence = static_cast<uint16_t>(static_cast<uint8_t>(value[2]) |
                                        (static_cast<uint16_t>(static_cast<uint8_t>(value[3])) << 8));
  next.teamsDetected = value[4] != 0;
  next.microphone = static_cast<uint8_t>(value[5]);
  next.camera = value.size() > 6 ? static_cast<uint8_t>(value[6]) : 0;
  if (value.size() > 7) {
    next.message = value.substr(7, std::min<size_t>(value.size() - 7, 48));
  }

  hostStatus = next;
  statusChanged = true;
  LOG_INF("COMP", "Host status seq=%u teams=%d mic=%u camera=%u msg=%s", static_cast<unsigned>(next.sequence),
          next.teamsDetected, static_cast<unsigned>(next.microphone), static_cast<unsigned>(next.camera),
          next.message.c_str());
  if (statusChangedCallback) {
    statusChangedCallback();
  }
}

void CompanionBleService::publishDeviceInfo() {
  if (!deviceInfoCharacteristic) {
    return;
  }

  uint8_t payload[] = {
      CompanionProtocol::PROTOCOL_VERSION,
      0x01,
  };
  deviceInfoCharacteristic->setValue(payload, sizeof(payload));
}

void CompanionBleService::publishDeviceCommand(uint8_t command) {
  if (!deviceCommandCharacteristic) {
    return;
  }

  commandSequence++;
  uint8_t payload[] = {
      CompanionProtocol::PROTOCOL_VERSION,
      static_cast<uint8_t>(CompanionProtocol::MessageType::DeviceCommand),
      static_cast<uint8_t>(commandSequence & 0xFF),
      static_cast<uint8_t>((commandSequence >> 8) & 0xFF),
      command,
  };
  deviceCommandCharacteristic->setValue(payload, sizeof(payload));
  deviceCommandCharacteristic->notify();
  LOG_INF("COMP", "Device command notified seq=%u command=%u", static_cast<unsigned>(commandSequence),
          static_cast<unsigned>(command));
}
