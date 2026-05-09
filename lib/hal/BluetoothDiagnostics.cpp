#include "BluetoothDiagnostics.h"

#include <Arduino.h>
#include <HalStorage.h>
#include <esp_system.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
#include <freertos/task.h>

#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

namespace {

constexpr char DIAG_DIR[] = "/.crosspoint";
constexpr char DIAG_FILE[] = "/.crosspoint/ble_diag.log";
constexpr char BOOT_FILE[] = "/.crosspoint/ble_boot_count.txt";
constexpr size_t MAX_ENTRIES = 32;
constexpr size_t MAX_LINE_LENGTH = 176;
constexpr size_t MAX_DETAIL_LENGTH = 96;

char entries[MAX_ENTRIES][MAX_LINE_LENGTH] = {};
size_t head = 0;
size_t count = 0;
uint32_t bootCount = 0;
bool dirty = false;
bool loadedPersisted = false;
uint8_t storageFlushSuppressDepth = 0;
SemaphoreHandle_t mutex = nullptr;

void ensureMutex() {
  if (!mutex) {
    mutex = xSemaphoreCreateMutex();
  }
}

bool takeMutex(TickType_t waitTicks = pdMS_TO_TICKS(10)) {
  ensureMutex();
  return mutex && xSemaphoreTake(mutex, waitTicks) == pdTRUE;
}

void giveMutex() {
  if (mutex) {
    xSemaphoreGive(mutex);
  }
}

const char* resetReasonName(esp_reset_reason_t reason) {
  switch (reason) {
    case ESP_RST_POWERON:
      return "poweron";
    case ESP_RST_EXT:
      return "external";
    case ESP_RST_SW:
      return "software";
    case ESP_RST_PANIC:
      return "panic";
    case ESP_RST_INT_WDT:
      return "int_wdt";
    case ESP_RST_TASK_WDT:
      return "task_wdt";
    case ESP_RST_WDT:
      return "wdt";
    case ESP_RST_DEEPSLEEP:
      return "deepsleep";
    case ESP_RST_BROWNOUT:
      return "brownout";
    case ESP_RST_SDIO:
      return "sdio";
    case ESP_RST_USB:
      return "usb";
    case ESP_RST_JTAG:
      return "jtag";
    case ESP_RST_EFUSE:
      return "efuse";
    case ESP_RST_PWR_GLITCH:
      return "power_glitch";
    case ESP_RST_CPU_LOCKUP:
      return "cpu_lockup";
    default:
      return "unknown";
  }
}

void appendLineLocked(const char* event, const char* detail) {
  const size_t index = (head + count) % MAX_ENTRIES;
  const UBaseType_t stackWords = uxTaskGetStackHighWaterMark(nullptr);
  snprintf(entries[index], MAX_LINE_LENGTH,
           "%lu boot=%lu event=%s detail=%s heap=%lu minHeap=%lu stackWords=%lu",
           static_cast<unsigned long>(millis()), static_cast<unsigned long>(bootCount), event,
           detail ? detail : "", static_cast<unsigned long>(ESP.getFreeHeap()),
           static_cast<unsigned long>(ESP.getMinFreeHeap()), static_cast<unsigned long>(stackWords));

  if (count < MAX_ENTRIES) {
    count++;
  } else {
    head = (head + 1) % MAX_ENTRIES;
  }
  dirty = true;
}

std::string snapshotLocked() {
  std::string output;
  output.reserve(count * MAX_LINE_LENGTH);
  for (size_t i = 0; i < count; i++) {
    const size_t index = (head + i) % MAX_ENTRIES;
    output += entries[index];
    output += "\n";
  }
  return output;
}

void loadPersistedLocked() {
  if (loadedPersisted) {
    return;
  }
  loadedPersisted = true;

  if (!Storage.ready() || !Storage.exists(DIAG_FILE)) {
    return;
  }

  const String content = Storage.readFile(DIAG_FILE);
  std::vector<std::string> lines;
  size_t start = 0;
  const char* text = content.c_str();
  const size_t length = content.length();
  while (start < length) {
    size_t end = start;
    while (end < length && text[end] != '\n' && text[end] != '\r') {
      end++;
    }
    if (end > start) {
      lines.emplace_back(text + start, end - start);
    }
    while (end < length && (text[end] == '\n' || text[end] == '\r')) {
      end++;
    }
    start = end;
  }

  const size_t first = lines.size() > MAX_ENTRIES ? lines.size() - MAX_ENTRIES : 0;
  head = 0;
  count = 0;
  for (size_t i = first; i < lines.size(); i++) {
    const size_t index = count % MAX_ENTRIES;
    strncpy(entries[index], lines[i].c_str(), MAX_LINE_LENGTH - 1);
    entries[index][MAX_LINE_LENGTH - 1] = '\0';
    count++;
  }
  if (count > MAX_ENTRIES) {
    count = MAX_ENTRIES;
  }
  dirty = false;
}

}  // namespace

namespace BluetoothDiagnostics {

void recordBoot() {
  if (Storage.ready()) {
    Storage.mkdir(DIAG_DIR);
    const String previous = Storage.readFile(BOOT_FILE);
    bootCount = strtoul(previous.c_str(), nullptr, 10) + 1;

    char buffer[16];
    snprintf(buffer, sizeof(buffer), "%lu", static_cast<unsigned long>(bootCount));
    Storage.writeFile(BOOT_FILE, String(buffer));

    if (takeMutex(pdMS_TO_TICKS(50))) {
      loadPersistedLocked();
      giveMutex();
    }
  } else {
    bootCount++;
  }

  const auto reason = esp_reset_reason();
  recordf("boot", "count=%lu reset=%s(%d)", static_cast<unsigned long>(bootCount), resetReasonName(reason),
          static_cast<int>(reason));
}

void record(const char* event, const char* detail) {
  if (!event || !*event) {
    event = "unknown";
  }

  if (!takeMutex()) {
    return;
  }
  appendLineLocked(event, detail ? detail : "");
  giveMutex();
}

void recordf(const char* event, const char* format, ...) {
  char detail[MAX_DETAIL_LENGTH];
  detail[0] = '\0';

  if (format && *format) {
    va_list args;
    va_start(args, format);
    vsnprintf(detail, sizeof(detail), format, args);
    va_end(args);
  }

  record(event, detail);
}

void setStorageFlushSuppressed(bool suppressed) {
  if (!takeMutex(pdMS_TO_TICKS(50))) {
    return;
  }

  if (suppressed) {
    if (storageFlushSuppressDepth < UINT8_MAX) {
      storageFlushSuppressDepth++;
    }
  } else if (storageFlushSuppressDepth > 0) {
    storageFlushSuppressDepth--;
  }
  giveMutex();
}

void flushToStorage(bool force) {
  if (!force && !dirty) {
    return;
  }
  if (!Storage.ready()) {
    return;
  }
  if (!force) {
    if (!takeMutex()) {
      return;
    }
    const bool suppressed = storageFlushSuppressDepth > 0;
    giveMutex();
    if (suppressed) {
      return;
    }
  }

  const std::string content = snapshot();
  Storage.mkdir(DIAG_DIR);
  if (Storage.writeFile(DIAG_FILE, String(content.c_str()))) {
    if (takeMutex()) {
      dirty = false;
      giveMutex();
    }
  }
}

std::string snapshot() {
  if (!takeMutex()) {
    return "";
  }
  std::string output = snapshotLocked();
  giveMutex();
  return output;
}

std::string persistedSnapshot() {
  if (!Storage.ready() || !Storage.exists(DIAG_FILE)) {
    return snapshot();
  }

  const String content = Storage.readFile(DIAG_FILE);
  return std::string(content.c_str());
}

}  // namespace BluetoothDiagnostics
