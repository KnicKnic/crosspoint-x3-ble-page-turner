#pragma once

#include <Arduino.h>
#include <Wire.h>

#include "HalGPIO.h"

class HalClock;
extern HalClock halClock;

class HalClock {
  bool _available = false;
  mutable uint8_t _cachedHour = 0;
  mutable uint8_t _cachedMinute = 0;
  mutable uint8_t _cachedSecond = 0;
  mutable uint16_t _cachedYear = 2000;
  mutable uint8_t _cachedMonth = 1;
  mutable uint8_t _cachedDay = 1;
  mutable bool _hasCachedTime = false;
  mutable unsigned long _lastPollMs = 0;

  static constexpr unsigned long CLOCK_POLL_MS = 10000;

 public:
  // Call after gpio.begin() and powerManager.begin() (I2C already initialised for X3).
  void begin();

  bool isAvailable() const { return _available; }

  // Get current hour (0-23) and minute (0-59). Returns false if RTC is not available.
  bool getTime(uint8_t& hour, uint8_t& minute) const;
  bool getTime(uint8_t& hour, uint8_t& minute, uint8_t& second) const;

  // Get current date. Returns false if RTC is not available.
  bool getDate(uint16_t& year, uint8_t& month, uint8_t& day) const;

  // 24h mode produces "HH:MM" or "HH:MM:SS"; 12h mode produces "H:MM AM" or "H:MM:SS AM".
  // utcOffsetQuarterHoursBiased: biased quarter-hour offset (48 = UTC+0, 0 = UTC-12, 104 = UTC+14).
  bool formatTime(char* buf, size_t bufSize, uint8_t utcOffsetQuarterHoursBiased = 48, bool use12Hour = false,
                  bool includeSeconds = false) const;

  // Produces "YYYY-MM-DD" (needs >=11 bytes).
  bool formatDate(char* buf, size_t bufSize) const;

  // Sync the DS3231 RTC from an NTP server. Requires WiFi to be connected.
  bool syncFromNTP();

 private:
  bool readDateTimeFromRTC() const;
  bool writeDateTimeToRTC(uint16_t year, uint8_t month, uint8_t day, uint8_t weekday, uint8_t hour, uint8_t minute,
                          uint8_t second);
};
