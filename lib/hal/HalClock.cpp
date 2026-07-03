#include "HalClock.h"

#include <Logging.h>
#include <WiFi.h>
#include <esp_sntp.h>
#include <time.h>

#include <cassert>

HalClock halClock;

namespace {
uint8_t bcdToDec(uint8_t bcd) {
  return ((bcd >> 4) * 10) + (bcd & 0x0F);
}

uint8_t decToBcd(uint8_t dec) {
  return ((dec / 10) << 4) | (dec % 10);
}
}  // namespace

void HalClock::begin() {
  if (!gpio.deviceIsX3()) {
    _available = false;
    return;
  }

  Wire.beginTransmission(I2C_ADDR_DS3231);
  Wire.write(DS3231_SEC_REG);
  if (Wire.endTransmission(false) != 0) {
    LOG_INF("CLK", "DS3231 RTC not found");
    _available = false;
    return;
  }

  Wire.requestFrom(I2C_ADDR_DS3231, static_cast<uint8_t>(1));
  if (Wire.available() < 1) {
    _available = false;
    return;
  }
  Wire.read();

  _available = true;
  LOG_INF("CLK", "DS3231 RTC found");

  uint8_t h = 0;
  uint8_t m = 0;
  getTime(h, m);
}

bool HalClock::getTime(uint8_t& hour, uint8_t& minute) const {
  uint8_t second = 0;
  return getTime(hour, minute, second);
}

bool HalClock::getTime(uint8_t& hour, uint8_t& minute, uint8_t& second) const {
  if (!_available) {
    return false;
  }

  const unsigned long now = millis();
  if (_lastPollMs != 0 && (now - _lastPollMs) < CLOCK_POLL_MS) {
    hour = _cachedHour;
    minute = _cachedMinute;
    second = _cachedSecond;
    return true;
  }

  if (!readDateTimeFromRTC()) {
    if (!_hasCachedTime) {
      return false;
    }
    _lastPollMs = now;
  }

  hour = _cachedHour;
  minute = _cachedMinute;
  second = _cachedSecond;
  return true;
}

bool HalClock::getDate(uint16_t& year, uint8_t& month, uint8_t& day) const {
  if (!_available) {
    return false;
  }

  const unsigned long now = millis();
  if (_lastPollMs == 0 || (now - _lastPollMs) >= CLOCK_POLL_MS) {
    if (!readDateTimeFromRTC() && !_hasCachedTime) {
      return false;
    }
  }

  year = _cachedYear;
  month = _cachedMonth;
  day = _cachedDay;
  return true;
}

bool HalClock::readDateTimeFromRTC() const {
  const unsigned long now = millis();

  Wire.beginTransmission(I2C_ADDR_DS3231);
  Wire.write(DS3231_SEC_REG);
  if (Wire.endTransmission(false) != 0) {
    return false;
  }

  Wire.requestFrom(I2C_ADDR_DS3231, static_cast<uint8_t>(7));
  if (Wire.available() < 7) {
    while (Wire.available()) {
      Wire.read();
    }
    return false;
  }

  const uint8_t rawSec = Wire.read();
  const uint8_t rawMin = Wire.read();
  const uint8_t rawHour = Wire.read();
  Wire.read();
  const uint8_t rawDay = Wire.read();
  const uint8_t rawMonth = Wire.read();
  const uint8_t rawYear = Wire.read();

  _cachedSecond = bcdToDec(rawSec & 0x7F);
  _cachedMinute = bcdToDec(rawMin & 0x7F);
  if (rawHour & 0x40) {
    uint8_t h12 = bcdToDec(rawHour & 0x1F);
    const bool pm = rawHour & 0x20;
    if (h12 == 12) {
      h12 = 0;
    }
    _cachedHour = pm ? h12 + 12 : h12;
  } else {
    _cachedHour = bcdToDec(rawHour & 0x3F);
  }

  _cachedDay = bcdToDec(rawDay & 0x3F);
  _cachedMonth = bcdToDec(rawMonth & 0x1F);
  _cachedYear = 2000 + bcdToDec(rawYear);
  if (rawMonth & 0x80) {
    _cachedYear += 100;
  }

  _lastPollMs = now;
  _hasCachedTime = true;
  return true;
}

bool HalClock::formatTime(char* buf, size_t bufSize, uint8_t utcOffsetQuarterHoursBiased, bool use12Hour,
                          bool includeSeconds) const {
  if (bufSize < (use12Hour ? (includeSeconds ? 12u : 9u) : (includeSeconds ? 9u : 6u))) {
    return false;
  }

  uint8_t h = 0;
  uint8_t m = 0;
  uint8_t s = 0;
  if (!getTime(h, m, s)) {
    return false;
  }

  if (utcOffsetQuarterHoursBiased > 104) {
    utcOffsetQuarterHoursBiased = 104;
  }

  const int offsetQuarterHours = static_cast<int>(utcOffsetQuarterHoursBiased) - 48;
  int totalMinutes = static_cast<int>(h) * 60 + static_cast<int>(m) + offsetQuarterHours * 15;
  totalMinutes = ((totalMinutes % 1440) + 1440) % 1440;

  const int hour24 = totalMinutes / 60;
  const int min = totalMinutes % 60;
  if (use12Hour) {
    const bool pm = hour24 >= 12;
    int hour12 = hour24 % 12;
    if (hour12 == 0) {
      hour12 = 12;
    }
    if (includeSeconds) {
      snprintf(buf, bufSize, "%d:%02d:%02u %s", hour12, min, static_cast<unsigned>(s), pm ? "PM" : "AM");
    } else {
      snprintf(buf, bufSize, "%d:%02d %s", hour12, min, pm ? "PM" : "AM");
    }
  } else {
    if (includeSeconds) {
      snprintf(buf, bufSize, "%02d:%02d:%02u", hour24, min, static_cast<unsigned>(s));
    } else {
      snprintf(buf, bufSize, "%02d:%02d", hour24, min);
    }
  }
  return true;
}

bool HalClock::formatDate(char* buf, size_t bufSize) const {
  if (bufSize < 11u) {
    return false;
  }

  uint16_t year = 0;
  uint8_t month = 0;
  uint8_t day = 0;
  if (!getDate(year, month, day)) {
    return false;
  }

  snprintf(buf, bufSize, "%04u-%02u-%02u", year, month, day);
  return true;
}

bool HalClock::writeDateTimeToRTC(uint16_t year, uint8_t month, uint8_t day, uint8_t weekday, uint8_t hour,
                                  uint8_t minute, uint8_t second) {
  assert(year >= 2000 && year <= 2199);
  assert(month >= 1 && month <= 12);
  assert(day >= 1 && day <= 31);
  assert(weekday >= 1 && weekday <= 7);
  assert(hour < 24);
  assert(minute < 60);
  assert(second < 60);

  const bool century = year >= 2100;
  const uint8_t dsYear = static_cast<uint8_t>(year % 100);

  Wire.beginTransmission(I2C_ADDR_DS3231);
  Wire.write(DS3231_SEC_REG);
  Wire.write(decToBcd(second));
  Wire.write(decToBcd(minute));
  Wire.write(decToBcd(hour));
  Wire.write(decToBcd(weekday));
  Wire.write(decToBcd(day));
  Wire.write(decToBcd(month) | (century ? 0x80 : 0x00));
  Wire.write(decToBcd(dsYear));
  if (Wire.endTransmission() != 0) {
    LOG_ERR("CLK", "Failed to write date/time to DS3231");
    return false;
  }

  _lastPollMs = 0;
  _cachedHour = hour;
  _cachedMinute = minute;
  _cachedSecond = second;
  _cachedYear = year;
  _cachedMonth = month;
  _cachedDay = day;
  _hasCachedTime = true;
  return true;
}

bool HalClock::syncFromNTP() {
  if (!_available) {
    return false;
  }

  if (WiFi.status() != WL_CONNECTED) {
    LOG_ERR("CLK", "WiFi not connected, cannot sync NTP");
    return false;
  }

  LOG_INF("CLK", "Starting NTP sync");
  configTzTime("UTC0", "pool.ntp.org", "time.nist.gov");

  constexpr int maxAttempts = 50;
  for (int i = 0; i < maxAttempts; ++i) {
    if (sntp_get_sync_status() == SNTP_SYNC_STATUS_COMPLETED) {
      const time_t now = time(nullptr);
      struct tm timeinfo;
      gmtime_r(&now, &timeinfo);

      const uint16_t year = static_cast<uint16_t>(timeinfo.tm_year + 1900);
      const uint8_t month = static_cast<uint8_t>(timeinfo.tm_mon + 1);
      const uint8_t day = static_cast<uint8_t>(timeinfo.tm_mday);
      const uint8_t weekday = static_cast<uint8_t>(timeinfo.tm_wday == 0 ? 7 : timeinfo.tm_wday);
      if (writeDateTimeToRTC(year, month, day, weekday, timeinfo.tm_hour, timeinfo.tm_min, timeinfo.tm_sec)) {
        LOG_INF("CLK", "RTC set to %04u-%02u-%02u %02d:%02d:%02d UTC", year, month, day, timeinfo.tm_hour,
                timeinfo.tm_min, timeinfo.tm_sec);
        return true;
      }
      return false;
    }
    delay(100);
  }

  LOG_ERR("CLK", "NTP sync timed out");
  return false;
}
