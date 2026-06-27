#include "CompanionBleService.h"

#include <Arduino.h>
#include <BluetoothDiagnostics.h>
#include <BluetoothHIDManager.h>
#include <Logging.h>
#include <NimBLEDevice.h>
#include <NimBLEUtils.h>

#include <algorithm>
#include <cstring>
#include <utility>

#include "CompanionProtocol.h"

namespace {
constexpr unsigned long COMPANION_MAINTENANCE_INTERVAL_MS = 2000;
constexpr unsigned long COMPANION_HANDSHAKE_TIMEOUT_MS = 15000;
constexpr unsigned long COMPANION_ADVERTISING_RESTART_INTERVAL_MS = 5000;
constexpr unsigned long COMPANION_CONN_PARAM_REQUEST_MIN_INTERVAL_MS = 2500;
constexpr unsigned long COMPANION_COMMAND_RESPONSIVE_WINDOW_MS = 5000;

constexpr uint16_t BLE_CONN_INTERVAL_RESPONSIVE_MIN = 24;  // 30 ms
constexpr uint16_t BLE_CONN_INTERVAL_RESPONSIVE_MAX = 40;  // 50 ms
constexpr uint16_t BLE_CONN_LATENCY_RESPONSIVE = 0;
constexpr uint16_t BLE_CONN_TIMEOUT_RESPONSIVE = 400;      // 4 s

constexpr uint16_t BLE_CONN_INTERVAL_IDLE_MIN = 240;       // 300 ms
constexpr uint16_t BLE_CONN_INTERVAL_IDLE_MAX = 400;       // 500 ms
constexpr uint16_t BLE_CONN_LATENCY_IDLE = 2;
constexpr uint16_t BLE_CONN_TIMEOUT_IDLE = 1000;           // 10 s

CompanionBleService* g_service = nullptr;

class CompanionServerCallbacks : public NimBLEServerCallbacks {
  void onConnect(NimBLEServer* pServer, NimBLEConnInfo& connInfo) override {
    (void)pServer;
    LOG_INF("COMP", "Host connected address=%s handle=%u", connInfo.getAddress().toString().c_str(),
            static_cast<unsigned>(connInfo.getConnHandle()));
    BluetoothDiagnostics::recordf("companion_host_gap_connected", "addr=%s handle=%u",
                                  connInfo.getAddress().toString().c_str(),
                                  static_cast<unsigned>(connInfo.getConnHandle()));
    if (g_service) {
      g_service->onHostConnected(connInfo.getConnHandle());
    }
  }

  void onDisconnect(NimBLEServer* pServer, NimBLEConnInfo& connInfo, int reason) override {
    LOG_INF("COMP", "Host disconnected address=%s handle=%u reason=%d %s", connInfo.getAddress().toString().c_str(),
            static_cast<unsigned>(connInfo.getConnHandle()), reason, NimBLEUtils::returnCodeToString(reason));
    BluetoothDiagnostics::recordf("companion_host_gap_disconnected", "addr=%s handle=%u reason=%d",
                                  connInfo.getAddress().toString().c_str(),
                                  static_cast<unsigned>(connInfo.getConnHandle()), reason);
    if (g_service) {
      g_service->onHostDisconnected();
    }
    if (pServer && g_service && g_service->isRunning()) {
      g_service->restartAdvertising("disconnect");
    }
  }

  void onConnParamsUpdate(NimBLEConnInfo& connInfo) override {
    LOG_INF("COMP", "Host connection params interval=%.1f ms latency=%u timeout=%u ms",
            connInfo.getConnInterval() * 1.25f, static_cast<unsigned>(connInfo.getConnLatency()),
            static_cast<unsigned>(connInfo.getConnTimeout() * 10));
    BluetoothDiagnostics::recordf("companion_conn_params_updated", "itvl=%u latency=%u timeout=%u",
                                  static_cast<unsigned>(connInfo.getConnInterval()),
                                  static_cast<unsigned>(connInfo.getConnLatency()),
                                  static_cast<unsigned>(connInfo.getConnTimeout()));
    if (g_service) {
      g_service->onConnParamsUpdated(connInfo.getConnInterval(), connInfo.getConnLatency(),
                                     connInfo.getConnTimeout());
    }
  }
};

class HostStatusCallbacks : public NimBLECharacteristicCallbacks {
  void onWrite(NimBLECharacteristic* characteristic, NimBLEConnInfo& connInfo) override {
    const size_t len = characteristic ? characteristic->getValue().size() : 0;
    LOG_INF("COMP", "Host status write callback address=%s handle=%u len=%u",
            connInfo.getAddress().toString().c_str(), static_cast<unsigned>(connInfo.getConnHandle()),
            static_cast<unsigned>(len));
    BluetoothDiagnostics::recordf("companion_host_status_write_cb", "handle=%u len=%u",
                                  static_cast<unsigned>(connInfo.getConnHandle()), static_cast<unsigned>(len));
    if (g_service) {
      g_service->onHostStatusWritten(characteristic);
    }
  }
};

class DeviceCommandCallbacks : public NimBLECharacteristicCallbacks {
  void onSubscribe(NimBLECharacteristic* characteristic, NimBLEConnInfo& connInfo, uint16_t subValue) override {
    (void)characteristic;
    LOG_INF("COMP", "Device command subscription changed address=%s handle=%u sub=%u",
            connInfo.getAddress().toString().c_str(), static_cast<unsigned>(connInfo.getConnHandle()),
            static_cast<unsigned>(subValue));
    BluetoothDiagnostics::recordf("companion_command_subscribe", "handle=%u sub=%u",
                                  static_cast<unsigned>(connInfo.getConnHandle()),
                                  static_cast<unsigned>(subValue));
    if (g_service) {
      g_service->onDeviceCommandSubscribed(subValue != 0);
    }
  }
};

CompanionServerCallbacks serverCallbacks;
HostStatusCallbacks hostStatusCallbacks;
DeviceCommandCallbacks deviceCommandCallbacks;
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

  resetSessionState();

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
    deviceCommandCharacteristic->setCallbacks(&deviceCommandCallbacks);
    if (!server->start()) {
      LOG_ERR("COMP", "Failed to start companion GATT server");
      BluetoothDiagnostics::record("companion_gatt_start_failed");
      if (ownsBluetoothStack) {
        btMgr.disable();
      }
      ownsBluetoothStack = false;
      server = nullptr;
      hostStatusCharacteristic = nullptr;
      deviceCommandCharacteristic = nullptr;
      deviceInfoCharacteristic = nullptr;
      return false;
    }
    BluetoothDiagnostics::record("companion_gatt_started");
  }

  publishDeviceInfo();

  auto* advertising = NimBLEDevice::getAdvertising();
  advertising->clearData();
  advertising->enableScanResponse(true);
  advertising->addServiceUUID(CompanionProtocol::SERVICE_UUID);
  advertising->setPreferredParams(BLE_CONN_INTERVAL_IDLE_MIN, BLE_CONN_INTERVAL_IDLE_MAX);
  advertising->setName("X3 Companion");
  if (!advertising->start()) {
    LOG_ERR("COMP", "Failed to start companion advertising");
    BluetoothDiagnostics::record("companion_advertising_start_failed");
    if (ownsBluetoothStack) {
      BluetoothHIDManager::getInstance().disable();
      server = nullptr;
      hostStatusCharacteristic = nullptr;
      deviceCommandCharacteristic = nullptr;
      deviceInfoCharacteristic = nullptr;
    }
    ownsBluetoothStack = false;
    return false;
  }
  LOG_INF("COMP", "Advertising companion service %s", CompanionProtocol::SERVICE_UUID);

  running = true;
  resetSessionState();
  statusChanged = true;
  lastMaintenanceAtMs = millis();
  lastAdvertisingRestartAtMs = 0;
  BluetoothDiagnostics::record("companion_ble_started");
  LOG_INF("COMP", "Companion BLE service started");
  return true;
}

void CompanionBleService::end() {
  if (!running) {
    resetSessionState();
    statusChanged = true;
    return;
  }

  NimBLEDevice::stopAdvertising();
  running = false;
  disconnectConnectedHosts();
  resetSessionState();
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

void CompanionBleService::disconnectConnectedHosts() {
  if (!server) {
    return;
  }

  const auto peers = server->getPeerDevices();
  if (peers.empty()) {
    return;
  }

  LOG_INF("COMP", "Disconnecting %u companion host link(s)", static_cast<unsigned>(peers.size()));
  BluetoothDiagnostics::recordf("companion_disconnect_hosts", "count=%u", static_cast<unsigned>(peers.size()));
  for (const auto connHandle : peers) {
    const bool ok = server->disconnect(connHandle);
    LOG_INF("COMP", "Disconnect host handle=%u result=%d", static_cast<unsigned>(connHandle), ok);
  }

  delay(150);
}

bool CompanionBleService::hasConnectedHosts() const {
  if (!server) {
    return false;
  }

  return !server->getPeerDevices().empty();
}

void CompanionBleService::resetSessionState() {
  hostConnected = false;
  hostConnHandle = 0xFFFF;
  hostConnectedAtMs = 0;
  hostStatusReceived = false;
  deviceCommandSubscribed = false;
  connectionProfile = ConnectionPowerProfile::Unknown;
  lastConnParamRequestAtMs = 0;
  responsiveUntilMs = 0;
  commandSequence = 0;
  hostStatus = HostStatus{};
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

bool CompanionBleService::isConnectionHandshakeActive() const {
  return hostConnected && (!deviceCommandSubscribed || !hostStatusReceived);
}

void CompanionBleService::update() {
  if (!running || !server) {
    return;
  }

  const unsigned long now = millis();
  if (now - lastMaintenanceAtMs < COMPANION_MAINTENANCE_INTERVAL_MS) {
    return;
  }
  lastMaintenanceAtMs = now;

  const bool hasPeers = hasConnectedHosts();
  if (hostConnected && !hasPeers) {
    LOG_INF("COMP", "Recovering missed host disconnect; no active peer remains");
    BluetoothDiagnostics::record("companion_missed_disconnect_recovered");
    onHostDisconnected();
    restartAdvertising("missed_disconnect");
    return;
  }

  if (hostConnected && responsiveUntilMs != 0 && static_cast<long>(now - responsiveUntilMs) >= 0) {
    responsiveUntilMs = 0;
    requestIdleConnectionParamsIfReady("responsive_window_elapsed");
  }

  if (!hostConnected && !hasPeers) {
    auto* advertising = NimBLEDevice::getAdvertising();
    if (!advertising || !advertising->isAdvertising()) {
      restartAdvertising("idle_maintenance");
    }
    return;
  }

  if (isConnectionHandshakeActive() && hostConnectedAtMs != 0 &&
      now - hostConnectedAtMs > COMPANION_HANDSHAKE_TIMEOUT_MS) {
    LOG_INF("COMP", "Disconnecting stale companion host handshake; subscribed=%d statusReceived=%d ageMs=%lu",
            deviceCommandSubscribed, hostStatusReceived, now - hostConnectedAtMs);
    BluetoothDiagnostics::recordf("companion_stale_handshake_disconnect", "sub=%d status=%d ageMs=%lu",
                                  deviceCommandSubscribed, hostStatusReceived, now - hostConnectedAtMs);
    disconnectConnectedHosts();
    resetSessionState();
    statusChanged = true;
    notifyStatusChanged();
    restartAdvertising("stale_handshake");
  }
}

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
  onHostConnected(0xFFFF);
}

void CompanionBleService::onHostConnected(uint16_t connHandle) {
  resetSessionState();
  hostConnected = true;
  hostConnHandle = connHandle;
  hostConnectedAtMs = millis();
  statusChanged = true;
  BluetoothDiagnostics::record("companion_host_connected");
  LOG_INF("COMP", "Host connected to companion BLE service");
  requestConnectionParams(ConnectionPowerProfile::Responsive, "connect");
  notifyStatusChanged();
}

void CompanionBleService::onHostDisconnected() {
  resetSessionState();
  statusChanged = true;
  BluetoothDiagnostics::record("companion_host_disconnected");
  LOG_INF("COMP", "Host disconnected from companion BLE service");
  notifyStatusChanged();
}

void CompanionBleService::onConnParamsUpdated(uint16_t interval, uint16_t latency, uint16_t timeout) {
  if (interval >= BLE_CONN_INTERVAL_IDLE_MIN && interval <= BLE_CONN_INTERVAL_IDLE_MAX &&
      latency >= BLE_CONN_LATENCY_IDLE && timeout >= BLE_CONN_TIMEOUT_IDLE) {
    connectionProfile = ConnectionPowerProfile::Idle;
  } else if (interval <= BLE_CONN_INTERVAL_RESPONSIVE_MAX && latency == BLE_CONN_LATENCY_RESPONSIVE) {
    connectionProfile = ConnectionPowerProfile::Responsive;
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
  hostStatusReceived = true;
  statusChanged = true;
  publishAck(next.sequence, static_cast<uint8_t>(CompanionProtocol::MessageType::HostStatus));
  LOG_INF("COMP", "Host status seq=%u teams=%d mic=%u camera=%u msg=%s", static_cast<unsigned>(next.sequence),
          next.teamsDetected, static_cast<unsigned>(next.microphone), static_cast<unsigned>(next.camera),
          next.message.c_str());
  requestIdleConnectionParamsIfReady("host_status");
  notifyStatusChanged();
}

void CompanionBleService::onDeviceCommandSubscribed(bool subscribed) {
  deviceCommandSubscribed = subscribed;
  statusChanged = true;
  if (subscribed) {
    LOG_INF("COMP", "Companion host command notifications subscribed");
  }
  requestIdleConnectionParamsIfReady("subscribe");
  notifyStatusChanged();
}

void CompanionBleService::requestConnectionParams(ConnectionPowerProfile profile, const char* reason) {
  if (!server || !hostConnected || hostConnHandle == 0xFFFF) {
    return;
  }

  const unsigned long now = millis();
  if (connectionProfile == profile) {
    return;
  }
  if (lastConnParamRequestAtMs != 0 &&
      now - lastConnParamRequestAtMs < COMPANION_CONN_PARAM_REQUEST_MIN_INTERVAL_MS) {
    return;
  }

  uint16_t minInterval = BLE_CONN_INTERVAL_IDLE_MIN;
  uint16_t maxInterval = BLE_CONN_INTERVAL_IDLE_MAX;
  uint16_t latency = BLE_CONN_LATENCY_IDLE;
  uint16_t timeout = BLE_CONN_TIMEOUT_IDLE;
  const char* profileName = "idle";
  if (profile == ConnectionPowerProfile::Responsive) {
    minInterval = BLE_CONN_INTERVAL_RESPONSIVE_MIN;
    maxInterval = BLE_CONN_INTERVAL_RESPONSIVE_MAX;
    latency = BLE_CONN_LATENCY_RESPONSIVE;
    timeout = BLE_CONN_TIMEOUT_RESPONSIVE;
    profileName = "responsive";
  }

  lastConnParamRequestAtMs = now;
  connectionProfile = profile;
  server->updateConnParams(hostConnHandle, minInterval, maxInterval, latency, timeout);
  LOG_INF("COMP", "Requested %s connection params reason=%s min=%u max=%u latency=%u timeout=%u", profileName,
          reason ? reason : "", static_cast<unsigned>(minInterval), static_cast<unsigned>(maxInterval),
          static_cast<unsigned>(latency), static_cast<unsigned>(timeout));
  BluetoothDiagnostics::recordf("companion_conn_params_requested", "profile=%s reason=%s", profileName,
                                reason ? reason : "");
}

void CompanionBleService::requestIdleConnectionParamsIfReady(const char* reason) {
  if (!hostConnected || !hostStatusReceived || !deviceCommandSubscribed) {
    return;
  }
  if (responsiveUntilMs != 0) {
    return;
  }
  requestConnectionParams(ConnectionPowerProfile::Idle, reason);
}

bool CompanionBleService::restartAdvertising(const char* reason) {
  if (!running) {
    return false;
  }

  if (hasConnectedHosts()) {
    LOG_DBG("COMP", "Advertising restart skipped; host peer still connected reason=%s", reason ? reason : "");
    return false;
  }

  const unsigned long now = millis();
  auto* advertising = NimBLEDevice::getAdvertising();
  if (advertising && advertising->isAdvertising()) {
    return true;
  }

  if (lastAdvertisingRestartAtMs != 0 &&
      now - lastAdvertisingRestartAtMs < COMPANION_ADVERTISING_RESTART_INTERVAL_MS) {
    return false;
  }

  lastAdvertisingRestartAtMs = now;
  const bool ok = NimBLEDevice::startAdvertising();
  LOG_INF("COMP", "Advertising restart reason=%s ok=%d", reason ? reason : "", ok);
  BluetoothDiagnostics::recordf("companion_advertising_restart", "reason=%s ok=%d", reason ? reason : "", ok);
  return ok;
}

void CompanionBleService::notifyStatusChanged() {
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

void CompanionBleService::publishAck(uint16_t sequence, uint8_t ackedMessageType) {
  if (!deviceCommandCharacteristic || !deviceCommandSubscribed) {
    LOG_INF("COMP", "Ack not sent; commandChar=%p subscribed=%d seq=%u type=%u", deviceCommandCharacteristic,
            deviceCommandSubscribed, static_cast<unsigned>(sequence), static_cast<unsigned>(ackedMessageType));
    return;
  }

  uint8_t payload[] = {
      CompanionProtocol::PROTOCOL_VERSION,
      static_cast<uint8_t>(CompanionProtocol::MessageType::Ack),
      static_cast<uint8_t>(sequence & 0xFF),
      static_cast<uint8_t>((sequence >> 8) & 0xFF),
      ackedMessageType,
  };
  deviceCommandCharacteristic->setValue(payload, sizeof(payload));
  deviceCommandCharacteristic->notify();
  LOG_INF("COMP", "Ack notified seq=%u type=%u", static_cast<unsigned>(sequence),
          static_cast<unsigned>(ackedMessageType));
}

void CompanionBleService::publishDeviceCommand(uint8_t command) {
  if (!deviceCommandCharacteristic) {
    return;
  }

  responsiveUntilMs = millis() + COMPANION_COMMAND_RESPONSIVE_WINDOW_MS;
  requestConnectionParams(ConnectionPowerProfile::Responsive, "device_command");

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
