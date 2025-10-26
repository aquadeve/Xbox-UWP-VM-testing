using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace x86Emulator.ATADevice
{
    public class HardDrive : ATADrive
    {
        private FileStream diskStream;
        private string imagePath;
        private const int SectorSize = 512;

        // Basic geometry defaults (can be adjusted if BIOS expects specific values)
        public uint Cylinders { get; private set; } = 1024;
        public uint Heads { get; private set; } = 16;
        public uint Sectors { get; private set; } = 63;

        public HardDrive() { }

        public HardDrive(string path)
        {
            if (File.Exists(path))
            {
                imagePath = path;
                diskStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            }
        }

        public override async Task LoadImage(StorageFile file)
        {
            if (file != null)
            {
                imagePath = file.Path;
                Stream stream = await file.OpenStreamForReadAsync();
                diskStream = stream as FileStream;
            }
        }

        public override void Reset()
        {
            Status = DeviceStatus.Ready;
            Error = DeviceError.None;
            CylinderLow = 0;
            CylinderHigh = 0;
            Console.WriteLine("[HDD] Reset complete - drive ready");
        }

        public override void RunCommand(byte command)
        {
            switch (command)
            {
                case 0x20: // READ SECTOR(S)
                    ReadSectors();
                    break;

                case 0x30: // WRITE SECTOR(S)
                    WriteSectors();
                    break;

                case 0xEC: // IDENTIFY DEVICE
                    IdentifyDevice();
                    break;

                default:
                    Console.WriteLine("[HDD] Unsupported ATA command 0x{0:X2}", command);
                    Status = DeviceStatus.Error;
                    Error = DeviceError.Aborted;
                    break;
            }
        }

        private void IdentifyDevice()
        {
            Console.WriteLine("[HDD] IdentifyDevice called");
            sectorBuffer = new ushort[256];
            byte[] identifyData = new byte[512];

            identifyData[0] = 0x40; // Non-removable, hard disk
            identifyData[1] = 0x00;

            Encoding.ASCII.GetBytes("GPTEMU HDD").CopyTo(identifyData, 54);
            Encoding.ASCII.GetBytes("1.0").CopyTo(identifyData, 94);

            Buffer.BlockCopy(identifyData, 0, sectorBuffer, 0, 512);
            bufferIndex = 0;
            Status = DeviceStatus.DataRequest | DeviceStatus.Ready;
        }

        private void ReadSectors()
        {
            if (diskStream == null)
            {
                Console.WriteLine("[HDD] No image loaded - read failed");
                Status = DeviceStatus.Error;
                Error = DeviceError.Aborted;
                return;
            }

            uint lba = (uint)((CylinderHigh << 8) | CylinderLow);
            if (lba * SectorSize >= diskStream.Length)
            {
                Console.WriteLine("[HDD] LBA out of range");
                Status = DeviceStatus.Error;
                Error = DeviceError.IDNotFound;
                return;
            }

            byte[] buffer = new byte[SectorSize];
            diskStream.Seek(lba * SectorSize, SeekOrigin.Begin);
            diskStream.Read(buffer, 0, SectorSize);

            sectorBuffer = new ushort[SectorSize / 2];
            Buffer.BlockCopy(buffer, 0, sectorBuffer, 0, SectorSize);
            bufferIndex = 0;

            Status = DeviceStatus.DataRequest | DeviceStatus.Ready;
            Console.WriteLine("[HDD] ReadSector LBA={0}", lba);
        }

        private void WriteSectors()
        {
            if (diskStream == null)
            {
                Console.WriteLine("[HDD] No image loaded - write failed");
                Status = DeviceStatus.Error;
                Error = DeviceError.Aborted;
                return;
            }

            uint lba = (uint)((CylinderHigh << 8) | CylinderLow);
            if (lba * SectorSize >= diskStream.Length)
            {
                Console.WriteLine("[HDD] LBA out of range");
                Status = DeviceStatus.Error;
                Error = DeviceError.IDNotFound;
                return;
            }

            byte[] buffer = new byte[SectorSize];
            Buffer.BlockCopy(sectorBuffer, 0, buffer, 0, SectorSize);

            diskStream.Seek(lba * SectorSize, SeekOrigin.Begin);
            diskStream.Write(buffer, 0, SectorSize);
            diskStream.Flush();

            Status = DeviceStatus.Ready;
            Console.WriteLine("[HDD] WriteSector LBA={0}", lba);
        }

        public override void FinishCommand()
        {
            Status = DeviceStatus.Ready;
            Console.WriteLine("[HDD] Command finished");
        }

        public override void FinishRead()
        {
            Status = DeviceStatus.Ready;
            Console.WriteLine("[HDD] Read finished");
        }
    }
}
