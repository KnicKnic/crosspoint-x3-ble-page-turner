param(
    [string]$Firmware = ".pio\build\gh_release\firmware.bin",
    [string]$Port = "",
    [int]$Baud = 921600,
    [switch]$Build,
    [switch]$SkipVerify
)

$ErrorActionPreference = "Stop"

Set-StrictMode -Version Latest
$env:PYTHONUTF8 = "1"
$env:PYTHONIOENCODING = "utf-8"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

$app0Offset = "0x10000"
$app1Offset = "0x650000"
$appPartitionSize = 0x640000

function Find-Esptool {
    $candidates = @(
        (Join-Path $env:USERPROFILE ".platformio\penv\Scripts\esptool.exe"),
        (Join-Path $env:USERPROFILE ".platformio\packages\tool-esptoolpy\esptool.py")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    $fromPath = Get-Command esptool.exe -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    throw "esptool not found. Build once with PlatformIO or install PlatformIO first."
}

function Find-X3Port {
    $ports = Get-CimInstance Win32_SerialPort |
        Where-Object {
            $_.PNPDeviceID -match "VID_303A" -or
            $_.Name -match "ESP32|USB Serial|USB JTAG" -or
            $_.Description -match "ESP32|USB Serial|USB JTAG"
        } |
        Sort-Object DeviceID

    if (-not $ports) {
        throw "No likely ESP32-C3/X3 serial port found. Put the X3 in download mode and retry."
    }

    if ($ports.Count -gt 1) {
        Write-Host "Multiple candidate ports found:"
        $ports | ForEach-Object { Write-Host "  $($_.DeviceID)  $($_.Name)" }
        throw "Pass -Port COMx to choose the X3."
    }

    return $ports[0].DeviceID
}

if ($Build) {
    Write-Host "Building gh_release firmware..."
    pio run -e gh_release -j 4
}

$firmwarePath = Resolve-Path $Firmware
$firmwareInfo = Get-Item $firmwarePath
if ($firmwareInfo.Length -gt $appPartitionSize) {
    throw ("Firmware is too large: 0x{0:x} > 0x{1:x}" -f $firmwareInfo.Length, $appPartitionSize)
}

if ([string]::IsNullOrWhiteSpace($Port)) {
    $Port = Find-X3Port
}

$esptool = Find-Esptool
$sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $firmwarePath).Hash.ToLowerInvariant()

Write-Host "Firmware: $firmwarePath"
Write-Host ("Size:     0x{0:x} bytes" -f $firmwareInfo.Length)
Write-Host "SHA-256:  $sha256"
Write-Host "Port:     $Port"
Write-Host "esptool:  $esptool"
Write-Host ""

& $esptool --chip esp32c3 -p $Port chip-id

Write-Host ""
Write-Host "Writing both OTA app slots..."
& $esptool --chip esp32c3 -p $Port -b $Baud write-flash `
    $app0Offset $firmwarePath `
    $app1Offset $firmwarePath

if (-not $SkipVerify) {
    Write-Host ""
    Write-Host "Verifying both OTA app slots..."
    & $esptool --chip esp32c3 -p $Port verify-flash $app0Offset $firmwarePath
    & $esptool --chip esp32c3 -p $Port verify-flash $app1Offset $firmwarePath
}

Write-Host ""
Write-Host "Flashed X3 firmware to app0 and app1 successfully."
