using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace x86Emulator.ATADevice
{
    public class CdRomDrive : ATADrive
    {
        private Stream isoStream;
        private readonly byte[] packetBuffer = new byte[12];

        public CdRomDrive()
        {
            string isoPath = Resources.CdRomImagePath;

            if (string.IsNullOrEmpty(isoPath) || !File.Exists(isoPath))
            {
                Debug.WriteLine("[CDROM] No ISO file found to load.");
                return;
            }

            try
            {
                isoStream = File.OpenRead(isoPath);
                Debug.WriteLine($"[CDROM] ISO loaded: {isoPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CDROM] Failed to load ISO: {ex.Message}");
            }
        }

        public CdRomDrive(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                isoStream = File.OpenRead(path);
                Status = DeviceStatus.Ready;
                Debug.WriteLine($"[CDROM] ISO loaded manually: {path}");
            }
            else
            {
                Debug.WriteLine($"[CDROM] Invalid or missing ISO path: {path}");
            }
        }

        public override async Task LoadImage(StorageFile filename)
        {
            if (filename != null)
            {
                isoStream = await filename.OpenStreamForReadAsync();
                Status = DeviceStatus.Ready;
                Error = DeviceError.None;
                Debug.WriteLine($"[CDROM] ISO loaded (UWP): {filename.Path}");
                IdentifyPacketDevice();
            }
        }

        public override void Reset()
        {
            CylinderLow = 0x14;
            CylinderHigh = 0xEB;
            Status = DeviceStatus.Ready | DeviceStatus.SeekComplete;
            Error = DeviceError.None;
            Debug.WriteLine("[CDROM] Reset complete - ATAPI signature set (0xEB14)");
        }

        public override void RunCommand(byte command)
        {
            Debug.WriteLine($"[CDROM] RunCommand 0x{command:X2}");
            switch (command)
            {
                case 0xA1: // IDENTIFY PACKET DEVICE
                    IdentifyPacketDevice();
                    break;

                case 0xA0: // PACKET
                    ExecutePacketCommand(packetBuffer);
                    break;

                default:
                    Debug.WriteLine($"[CDROM] Unsupported ATA command 0x{command:X2}");
                    Status = DeviceStatus.Error;
                    Error = DeviceError.Aborted;
                    break;
            }
        }

        private void IdentifyPacketDevice()
        {
            Debug.WriteLine("[CDROM] IdentifyPacketDevice called");
            byte[] id = new byte[512];
            ushort[] data = new ushort[256];

            data[0] = 0x8500; // ATAPI removable
            WriteString(data, 10, 20, "CDROM0001");
            WriteString(data, 23, 8, "1.00");
            WriteString(data, 27, 40, "GPT-EMU ATAPI CD-ROM");
            data[49] = 0x0200; // LBA
            data[83] = 0x4000; // ATAPI supported

            Buffer.BlockCopy(data, 0, id, 0, 512);
            System.Diagnostics.Debug.WriteLine("[CDROM] Identify data first 8 words: " +
            string.Join(" ", data.Take(8).Select(w => w.ToString("X4"))));

            sectorBuffer = data;
            bufferIndex = 0;
            Status = DeviceStatus.DataRequest | DeviceStatus.Ready;
            Error = DeviceError.None;
            System.Diagnostics.Debug.WriteLine("[CDROM] IdentifyPacketDevice ready, status=" + Status);
        }

        private void WriteString(ushort[] dest, int startWord, int length, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text.PadRight(length, ' '));
            for (int i = 0; i < length / 2; i++)
                dest[startWord + i] = (ushort)((bytes[i * 2 + 1] << 8) | bytes[i * 2]);
        }

        private void ExecutePacketCommand(byte[] packet)
        {
            byte command = packet[0];
            Debug.WriteLine($"[CDROM] ExecutePacketCommand 0x{command:X2}");

            switch (command)
            {
                case 0x12:
                    SendInquiryResponse();
                    break;
                case 0x28:
                case 0xA8:
                    HandleReadPacket(packet);
                    break;
                default:
                    Debug.WriteLine($"[CDROM] Unsupported packet command 0x{command:X2}");
                    Status = DeviceStatus.Error;
                    Error = DeviceError.Aborted;
                    break;
            }
        }

        private void SendInquiryResponse()
        {
            byte[] response = new byte[36];
            response[0] = 0x05; // CD/DVD
            response[1] = 0x80; // removable
            response[2] = 0x00;
            response[3] = 0x21;
            Encoding.ASCII.GetBytes("GPT-EMU CDROM").CopyTo(response, 8);
            SendDataToHost(response);
            Debug.WriteLine("[CDROM] INQUIRY response sent");
        }

        private void HandleReadPacket(byte[] packet)
        {
            if (isoStream == null)
            {
                Debug.WriteLine("[CDROM] No ISO stream loaded - read failed");
                Status = DeviceStatus.Error;
                Error = DeviceError.Aborted;
                return;
            }

            byte opcode = packet[0];
            uint lba = 0;
            ushort count = 1;

            if (opcode == 0x28)
            {
                lba = (uint)((packet[2] << 24) | (packet[3] << 16) | (packet[4] << 8) | packet[5]);
                count = (ushort)((packet[7] << 8) | packet[8]);
            }
            else if (opcode == 0xA8)
            {
                lba = (uint)((packet[2] << 24) | (packet[3] << 16) | (packet[4] << 8) | packet[5]);
                count = (ushort)((packet[6] << 8) | packet[7]);
            }

            if (count == 0) count = 1;
            int sectorSize = 2048;
            byte[] buffer = new byte[sectorSize * count];

            try
            {
                System.Diagnostics.Debug.WriteLine($"[CDROM] READ request: LBA={lba}, count={count}");
                isoStream.Seek(lba * sectorSize, SeekOrigin.Begin);
                int bytesRead = isoStream.Read(buffer, 0, buffer.Length);

                Debug.WriteLine($"[CDROM] READ({(opcode == 0xA8 ? 12 : 10)}) LBA={lba}, Count={count}, Bytes={bytesRead}");

                if (bytesRead > 0)
                {
                    SendDataToHost(buffer);
                    Status = DeviceStatus.DataRequest | DeviceStatus.Ready;
                    Error = DeviceError.None;
                }
                else
                {
                    Debug.WriteLine("[CDROM] End of ISO reached");
                    Status = DeviceStatus.Error;
                    Error = DeviceError.Uncorrectable;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CDROM] Read error: {ex.Message}");
                Status = DeviceStatus.Error;
                Error = DeviceError.BadBlock;
            }
        }

        private void SendDataToHost(byte[] data)
        {
            int wordCount = data.Length / 2;
            if (wordCount < 1) wordCount = 1;
            sectorBuffer = new ushort[wordCount];
            Buffer.BlockCopy(data, 0, sectorBuffer, 0, Math.Min(data.Length, wordCount * 2));
            bufferIndex = 0;
            Status = DeviceStatus.DataRequest | DeviceStatus.Ready;
        }

        public override void FinishCommand()
        {
            Status = DeviceStatus.Ready;
            Debug.WriteLine("[CDROM] FinishCommand");
        }

        public override void FinishRead()
        {
            Status = DeviceStatus.Ready;
            Debug.WriteLine("[CDROM] FinishRead");
        }
    }
}
