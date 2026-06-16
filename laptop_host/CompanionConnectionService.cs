using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace X3LaptopCompanion
{
    public sealed class CompanionConnectionService : IDisposable
    {
        private DeviceWatcher watcher;
        private BluetoothLEAdvertisementWatcher advertisementWatcher;
        private readonly Dictionary<ulong, DateTimeOffset> advertisementLogTimes = new Dictionary<ulong, DateTimeOffset>();
        private BluetoothLEDevice companionDevice;
        private GattDeviceService companionService;
        private GattCharacteristic hostStatusCharacteristic;
        private GattCharacteristic deviceCommandCharacteristic;
        private ushort hostStatusSequence;
        private int connecting;
        private bool disposed;

        public event EventHandler<CompanionConnectionStatus> StatusChanged;
        public event EventHandler<CompanionDeviceCommand> DeviceCommandReceived;

        public void Start()
        {
            if (watcher != null)
            {
                return;
            }

            var selector = GattDeviceService.GetDeviceSelectorFromUuid(CompanionProtocol.ServiceUuid);
            HostLog.Write("BLE watcher starting. Selector=" + selector);
            watcher = DeviceInformation.CreateWatcher(selector);
            watcher.Added += OnDeviceAdded;
            watcher.Removed += OnDeviceRemoved;
            watcher.EnumerationCompleted += OnWatcherEnumerationCompleted;
            watcher.Stopped += OnWatcherStopped;
            watcher.Start();
            HostLog.Write("BLE watcher started. Status=" + watcher.Status);

            advertisementWatcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            advertisementWatcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(CompanionProtocol.ServiceUuid);
            advertisementWatcher.Received += OnAdvertisementReceived;
            advertisementWatcher.Stopped += OnAdvertisementWatcherStopped;
            advertisementWatcher.Start();
            HostLog.Write("BLE advertisement watcher started. Status=" + advertisementWatcher.Status +
                " ServiceUuid=" + CompanionProtocol.ServiceUuid);
            PublishStatus(false, "Scanning for X3 companion service.");
        }

        public void Stop()
        {
            if (watcher != null)
            {
                watcher.Added -= OnDeviceAdded;
                watcher.Removed -= OnDeviceRemoved;
                watcher.EnumerationCompleted -= OnWatcherEnumerationCompleted;
                watcher.Stopped -= OnWatcherStopped;
                if (watcher.Status == DeviceWatcherStatus.Started ||
                    watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }

                watcher = null;
            }

            if (advertisementWatcher != null)
            {
                advertisementWatcher.Received -= OnAdvertisementReceived;
                advertisementWatcher.Stopped -= OnAdvertisementWatcherStopped;
                if (advertisementWatcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                {
                    advertisementWatcher.Stop();
                }

                advertisementWatcher = null;
            }

            if (deviceCommandCharacteristic != null)
            {
                deviceCommandCharacteristic.ValueChanged -= OnDeviceCommandValueChanged;
                deviceCommandCharacteristic = null;
            }

            hostStatusCharacteristic = null;
            companionService?.Dispose();
            companionService = null;
            companionDevice?.Dispose();
            companionDevice = null;
            HostLog.Write("BLE watcher/service stopped.");
            PublishStatus(false, "BLE scanning stopped.");
        }

        public void Dispose()
        {
            disposed = true;
            Stop();
        }

        public async Task SendHostStatusAsync(bool teamsDetected, CompanionTriState microphone, CompanionTriState camera, string message)
        {
            if (hostStatusCharacteristic == null)
            {
                HostLog.Write("Host status skipped; host status characteristic is not available.");
                return;
            }

            var writer = new DataWriter
            {
                ByteOrder = ByteOrder.LittleEndian
            };
            writer.WriteByte(CompanionProtocol.ProtocolVersion);
            writer.WriteByte((byte)CompanionMessageType.HostStatus);
            var sequence = hostStatusSequence++;
            writer.WriteUInt16(sequence);
            writer.WriteByte(teamsDetected ? (byte)1 : (byte)0);
            writer.WriteByte((byte)microphone);
            writer.WriteByte((byte)camera);
            if (!string.IsNullOrWhiteSpace(message))
            {
                writer.WriteString(message.Length > 48 ? message.Substring(0, 48) : message);
            }

            var status = await hostStatusCharacteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
            HostLog.Write("Host status write seq=" + sequence + " status=" + status + " teams=" + teamsDetected +
                " mic=" + microphone + " camera=" + camera + " message=" + message);
            if (status != GattCommunicationStatus.Success)
            {
                PublishStatus(false, "Host status write failed: " + status);
            }
        }

        private async void OnDeviceAdded(DeviceWatcher sender, DeviceInformation args)
        {
            HostLog.Write("BLE device added. Name=" + args.Name + " IsPaired=" + args.Pairing.IsPaired + " Id=" + args.Id);
            if (IsConnected())
            {
                HostLog.Write("BLE device add ignored; already connected.");
                return;
            }

            if (disposed || Interlocked.Exchange(ref connecting, 1) == 1)
            {
                HostLog.Write("BLE device add ignored. disposed=" + disposed + " connecting=" + connecting);
                return;
            }

            try
            {
                await ConnectAsync(args);
            }
            catch (Exception ex)
            {
                HostLog.Write("BLE connect failed with exception.", ex);
                PublishStatus(false, "BLE connect exception: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref connecting, 0);
            }
        }

        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            HostLog.Write("BLE device removed. Id=" + args.Id);
            PublishStatus(false, "X3 companion disconnected.");
        }

        private void OnWatcherEnumerationCompleted(DeviceWatcher sender, object args)
        {
            HostLog.Write("BLE watcher enumeration completed. Status=" + sender.Status);
            PublishStatus(false, "BLE scan enumeration completed; still watching for X3.");
        }

        private void OnWatcherStopped(DeviceWatcher sender, object args)
        {
            HostLog.Write("BLE watcher stopped. Status=" + sender.Status);
            if (!disposed)
            {
                PublishStatus(false, "BLE watcher stopped.");
            }
        }

        private async void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (disposed)
            {
                return;
            }

            LogAdvertisement(args);
            if (IsConnected())
            {
                return;
            }

            if (Interlocked.Exchange(ref connecting, 1) == 1)
            {
                return;
            }

            try
            {
                await ConnectFromAdvertisementAsync(args.BluetoothAddress);
            }
            catch (Exception ex)
            {
                HostLog.Write("BLE advertisement connect failed with exception.", ex);
                PublishStatus(false, "BLE advertisement connect exception: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref connecting, 0);
            }
        }

        private void OnAdvertisementWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            HostLog.Write("BLE advertisement watcher stopped. Status=" + sender.Status + " Error=" + args.Error);
            if (!disposed)
            {
                PublishStatus(false, "BLE advertisement watcher stopped: " + args.Error);
            }
        }

        private async Task ConnectAsync(DeviceInformation deviceInfo)
        {
            PublishStatus(false, "Found X3 companion service. Connecting...");
            HostLog.Write("Opening GATT service. Name=" + deviceInfo.Name + " Id=" + deviceInfo.Id);

            var service = await GattDeviceService.FromIdAsync(deviceInfo.Id);
            if (service == null)
            {
                HostLog.Write("GattDeviceService.FromIdAsync returned null.");
                PublishStatus(false, "Unable to open X3 companion service.");
                return;
            }

            await UseGattServiceAsync(service);
        }

        private async Task ConnectFromAdvertisementAsync(ulong bluetoothAddress)
        {
            PublishStatus(false, "Found X3 companion advertisement. Connecting...");
            HostLog.Write("Opening BLE device from advertisement. Address=" + FormatBluetoothAddress(bluetoothAddress));

            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
            if (device == null)
            {
                HostLog.Write("BluetoothLEDevice.FromBluetoothAddressAsync returned null for " +
                    FormatBluetoothAddress(bluetoothAddress));
                PublishStatus(false, "Unable to open X3 BLE device from advertisement.");
                return;
            }

            HostLog.Write("BLE device opened. Name=" + device.Name + " Address=" +
                FormatBluetoothAddress(device.BluetoothAddress) + " ConnectionStatus=" + device.ConnectionStatus);

            var services = await device.GetGattServicesForUuidAsync(CompanionProtocol.ServiceUuid, BluetoothCacheMode.Uncached);
            HostLog.Write("Advertisement GATT service lookup status=" + services.Status + " count=" + services.Services.Count);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                device.Dispose();
                PublishStatus(false, "X3 advertisement found, but GATT service could not be opened: " + services.Status);
                return;
            }

            companionDevice?.Dispose();
            companionDevice = device;
            await UseGattServiceAsync(services.Services[0]);
        }

        private async Task UseGattServiceAsync(GattDeviceService service)
        {
            if (deviceCommandCharacteristic != null)
            {
                deviceCommandCharacteristic.ValueChanged -= OnDeviceCommandValueChanged;
                deviceCommandCharacteristic = null;
            }

            companionService?.Dispose();
            companionService = service;
            HostLog.Write("GATT service opened. Uuid=" + service.Uuid);

            hostStatusCharacteristic = await GetCharacteristicAsync(service, CompanionProtocol.HostStatusUuid, "host status");
            deviceCommandCharacteristic = await GetCharacteristicAsync(service, CompanionProtocol.DeviceCommandUuid, "device command");

            if (hostStatusCharacteristic == null || deviceCommandCharacteristic == null)
            {
                HostLog.Write("Required characteristic missing. hostStatus=" + (hostStatusCharacteristic != null) +
                    " deviceCommand=" + (deviceCommandCharacteristic != null));
                PublishStatus(false, "X3 companion service is missing required characteristics.");
                return;
            }

            deviceCommandCharacteristic.ValueChanged += OnDeviceCommandValueChanged;
            var status = await deviceCommandCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            HostLog.Write("Device command notification subscribe status=" + status);
            if (status != GattCommunicationStatus.Success)
            {
                PublishStatus(false, "Could not subscribe to X3 command notifications.");
                return;
            }

            PublishStatus(true, "Connected to X3 companion.");
        }

        private static async Task<GattCharacteristic> GetCharacteristicAsync(GattDeviceService service, Guid uuid, string name)
        {
            var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
            HostLog.Write("Characteristic lookup " + name + " uuid=" + uuid + " status=" + result.Status +
                " count=" + result.Characteristics.Count);
            if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
            {
                return null;
            }

            return result.Characteristics[0];
        }

        private void LogAdvertisement(BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var now = DateTimeOffset.Now;
            if (advertisementLogTimes.TryGetValue(args.BluetoothAddress, out var previous) &&
                now - previous < TimeSpan.FromSeconds(5))
            {
                return;
            }

            advertisementLogTimes[args.BluetoothAddress] = now;
            HostLog.Write("BLE advertisement received. Address=" + FormatBluetoothAddress(args.BluetoothAddress) +
                " Name=" + args.Advertisement.LocalName + " RSSI=" + args.RawSignalStrengthInDBm +
                " Type=" + args.AdvertisementType + " ServiceUuids=" +
                string.Join(",", args.Advertisement.ServiceUuids));
        }

        private static string FormatBluetoothAddress(ulong address)
        {
            return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                (address >> 40) & 0xFF,
                (address >> 32) & 0xFF,
                (address >> 24) & 0xFF,
                (address >> 16) & 0xFF,
                (address >> 8) & 0xFF,
                address & 0xFF);
        }

        private void OnDeviceCommandValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            reader.ByteOrder = ByteOrder.LittleEndian;
            if (reader.UnconsumedBufferLength < 5)
            {
                HostLog.Write("Device command notification ignored; payload too short.");
                return;
            }

            var version = reader.ReadByte();
            var messageType = reader.ReadByte();
            var sequence = reader.ReadUInt16();
            var command = reader.ReadByte();

            if (version != CompanionProtocol.ProtocolVersion ||
                messageType != (byte)CompanionMessageType.DeviceCommand)
            {
                HostLog.Write("Device command notification ignored. version=" + version +
                    " messageType=" + messageType + " command=" + command);
                return;
            }

            HostLog.Write("Device command received. seq=" + sequence + " command=" + command);
            DeviceCommandReceived?.Invoke(this, (CompanionDeviceCommand)command);
        }

        private void PublishStatus(bool connected, string message)
        {
            HostLog.Write("Status changed. connected=" + connected + " message=" + message);
            StatusChanged?.Invoke(this, new CompanionConnectionStatus(connected, message));
        }

        private bool IsConnected()
        {
            return companionService != null && hostStatusCharacteristic != null && deviceCommandCharacteristic != null;
        }
    }
}
