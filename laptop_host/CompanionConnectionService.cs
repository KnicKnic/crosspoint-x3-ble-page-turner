using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace X3LaptopCompanion
{
    public sealed class CompanionConnectionService : IDisposable
    {
        private DeviceWatcher watcher;
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
            watcher = DeviceInformation.CreateWatcher(selector);
            watcher.Added += OnDeviceAdded;
            watcher.Removed += OnDeviceRemoved;
            watcher.Stopped += OnWatcherStopped;
            watcher.Start();
            PublishStatus(false, "Scanning for X3 companion service.");
        }

        public void Stop()
        {
            if (watcher != null)
            {
                watcher.Added -= OnDeviceAdded;
                watcher.Removed -= OnDeviceRemoved;
                watcher.Stopped -= OnWatcherStopped;
                if (watcher.Status == DeviceWatcherStatus.Started ||
                    watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }

                watcher = null;
            }

            if (deviceCommandCharacteristic != null)
            {
                deviceCommandCharacteristic.ValueChanged -= OnDeviceCommandValueChanged;
                deviceCommandCharacteristic = null;
            }

            hostStatusCharacteristic = null;
            companionService?.Dispose();
            companionService = null;
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
                return;
            }

            var writer = new DataWriter();
            writer.WriteByte(CompanionProtocol.ProtocolVersion);
            writer.WriteByte((byte)CompanionMessageType.HostStatus);
            writer.WriteUInt16(hostStatusSequence++);
            writer.WriteByte(teamsDetected ? (byte)1 : (byte)0);
            writer.WriteByte((byte)microphone);
            writer.WriteByte((byte)camera);
            if (!string.IsNullOrWhiteSpace(message))
            {
                writer.WriteString(message.Length > 48 ? message.Substring(0, 48) : message);
            }

            await hostStatusCharacteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
        }

        private async void OnDeviceAdded(DeviceWatcher sender, DeviceInformation args)
        {
            if (disposed || Interlocked.Exchange(ref connecting, 1) == 1)
            {
                return;
            }

            try
            {
                await ConnectAsync(args);
            }
            finally
            {
                Interlocked.Exchange(ref connecting, 0);
            }
        }

        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            PublishStatus(false, "X3 companion disconnected.");
        }

        private void OnWatcherStopped(DeviceWatcher sender, object args)
        {
            if (!disposed)
            {
                PublishStatus(false, "BLE watcher stopped.");
            }
        }

        private async Task ConnectAsync(DeviceInformation deviceInfo)
        {
            PublishStatus(false, "Found X3 companion service. Connecting...");

            var service = await GattDeviceService.FromIdAsync(deviceInfo.Id);
            if (service == null)
            {
                PublishStatus(false, "Unable to open X3 companion service.");
                return;
            }

            companionService?.Dispose();
            companionService = service;

            hostStatusCharacteristic = await GetCharacteristicAsync(service, CompanionProtocol.HostStatusUuid);
            deviceCommandCharacteristic = await GetCharacteristicAsync(service, CompanionProtocol.DeviceCommandUuid);

            if (hostStatusCharacteristic == null || deviceCommandCharacteristic == null)
            {
                PublishStatus(false, "X3 companion service is missing required characteristics.");
                return;
            }

            deviceCommandCharacteristic.ValueChanged += OnDeviceCommandValueChanged;
            var status = await deviceCommandCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (status != GattCommunicationStatus.Success)
            {
                PublishStatus(false, "Could not subscribe to X3 command notifications.");
                return;
            }

            PublishStatus(true, "Connected to X3 companion.");
        }

        private static async Task<GattCharacteristic> GetCharacteristicAsync(GattDeviceService service, Guid uuid)
        {
            var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
            {
                return null;
            }

            return result.Characteristics[0];
        }

        private void OnDeviceCommandValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            if (reader.UnconsumedBufferLength < 5)
            {
                return;
            }

            var version = reader.ReadByte();
            var messageType = reader.ReadByte();
            reader.ReadUInt16();
            var command = reader.ReadByte();

            if (version != CompanionProtocol.ProtocolVersion ||
                messageType != (byte)CompanionMessageType.DeviceCommand)
            {
                return;
            }

            DeviceCommandReceived?.Invoke(this, (CompanionDeviceCommand)command);
        }

        private void PublishStatus(bool connected, string message)
        {
            StatusChanged?.Invoke(this, new CompanionConnectionStatus(connected, message));
        }
    }
}
