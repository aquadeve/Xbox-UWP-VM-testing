// src/x86Emulator/ATADevice/ATADrive.cs
using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace x86Emulator.ATADevice
{
    [Flags]
    public enum DeviceStatus : byte
    {
        None = 0x00,
        Error = 0x01,
        Index = 0x02,
        CorrectedData = 0x04,
        DataRequest = 0x08,
        SeekComplete = 0x10,
        WriteFault = 0x20,
        Ready = 0x40,
        Busy = 0x80
    }

    [Flags]
    public enum DeviceError : byte
    {
        None = 0x00,
        Aborted = 0x04,
        MediaChange = 0x08,
        IDNotFound = 0x10,
        MediaChanged = 0x20,
        Uncorrectable = 0x40,
        BadBlock = 0x80
    }

    public abstract class ATADrive
    {
        public DeviceError Error { get; set; } = DeviceError.None;
        public byte SectorCount { get; set; }
        public byte SectorNumber { get; set; }
        public byte CylinderLow { get; set; }
        public byte CylinderHigh { get; set; }
        public byte DriveHead { get; set; }
        public DeviceStatus Status { get; set; } = DeviceStatus.None;

        protected ushort[] sectorBuffer;
        protected int bufferIndex;

        public ushort Cylinder
        {
            get { return (ushort)((CylinderHigh << 8) + CylinderLow); }
            set
            {
                CylinderLow = (byte)value;
                CylinderHigh = (byte)(value >> 8);
            }
        }

        public ushort SectorBuffer
        {
            get
            {
                ushort ret = sectorBuffer[bufferIndex++];

                if (Cylinder > 0 && (bufferIndex * 2) >= Cylinder)
                {
                    Status &= ~DeviceStatus.DataRequest;
                    FinishRead();
                    Cylinder = (ushort)((sectorBuffer.Length - bufferIndex) * 2);
                }

                if (bufferIndex >= sectorBuffer.Length)
                {
                    Status &= ~DeviceStatus.DataRequest;
                    FinishRead();
                }

                return ret;
            }
            set
            {
                sectorBuffer[bufferIndex++] = value;

                if (bufferIndex >= sectorBuffer.Length)
                {
                    Status &= ~DeviceStatus.DataRequest;
                    FinishCommand();
                }
            }
        }

        // Implementations must provide these
        public abstract Task LoadImage(StorageFile filename);
        public abstract void Reset();
        public abstract void RunCommand(byte command);
        public abstract void FinishCommand();
        public abstract void FinishRead();
    }
}
