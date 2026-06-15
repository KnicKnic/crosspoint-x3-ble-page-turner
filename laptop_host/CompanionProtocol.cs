using System;

namespace X3LaptopCompanion
{
    public static class CompanionProtocol
    {
        public static readonly Guid ServiceUuid = Guid.Parse("7d2d5f00-778d-4df6-a6d5-7c4e7a000001");
        public static readonly Guid HostStatusUuid = Guid.Parse("7d2d5f00-778d-4df6-a6d5-7c4e7a000002");
        public static readonly Guid DeviceCommandUuid = Guid.Parse("7d2d5f00-778d-4df6-a6d5-7c4e7a000003");
        public static readonly Guid DeviceInfoUuid = Guid.Parse("7d2d5f00-778d-4df6-a6d5-7c4e7a000004");
        public static readonly Guid NotesTransferUuid = Guid.Parse("7d2d5f00-778d-4df6-a6d5-7c4e7a000005");

        public const byte ProtocolVersion = 1;
    }

    public enum CompanionMessageType : byte
    {
        HostStatus = 1,
        DeviceCommand = 2,
        Ack = 3,
        Error = 4
    }

    public enum CompanionDeviceCommand : byte
    {
        ToggleMute = 1
    }

    public enum CompanionTriState : byte
    {
        Unknown = 0,
        Off = 1,
        On = 2
    }
}
