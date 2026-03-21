using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using x86Emulator.ATADevice;
using x86Emulator.Configuration;

namespace x86Emulator.Devices
{
    public class ATA : IDevice
    {
        private readonly int[] portsUsed = {
                                               0x1f0, 0x1f1, 0x1f2, 0x1f3, 0x1f4, 0x1f5, 0x1f6, 0x1f7,
                                               0x170, 0x171, 0x172, 0x173, 0x174, 0x175, 0x176, 0x177,
                                               0x3f6, 0x376
                                           };
        private List<ATADrive> diskDrives = new List<ATADrive>();

        private byte[] deviceControl = new byte[2];
        private bool primarySelected;

        public HardDrive[] HardDrives
        {
            get
            {
                List<HardDrive> hdds = new List<HardDrive>();

                lock (diskDrives)
                {
                    foreach (ATADrive drive in diskDrives)
                    {
                        if (drive is HardDrive)
                            hdds.Add(drive as HardDrive);
                    }

                }
                return hdds.ToArray();
            }
        }

        public ATA()
        {
            diskDrives.Clear();
        }
        
        public void ClearHDDs()
        {
            lock (diskDrives)
            {
                for(int i=0; i<diskDrives.Count; i++)
                {
                    if (diskDrives[i] is HardDrive)
                    {
                        diskDrives.RemoveAt(i);
                    }
                }
            }
        }
        public void ClearCDROM()
        {
            lock (diskDrives)
            {
                for(int i=0; i<diskDrives.Count; i++)
                {
                    if (diskDrives[i] is CdRomDrive)
                    {
                        diskDrives.RemoveAt(i);
                    }
                }
            }
        }
        public void AddHDD(ATADrive newDrive)
        {
            diskDrives.Add(newDrive);

            primarySelected = true;
            Debug.WriteLine($"[ATA] AddHDD called — total drives: {diskDrives.Count}. Added type: {newDrive.GetType().Name}");
        }
        public void AddCDROM(ATADrive newDrive)
        {
            diskDrives.Add(newDrive);

            try
            {
                // Ensure the drive reports the ATAPI signature right away
                newDrive.Reset();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ATA] AddCDROM Reset exception: " + ex.Message);
            }

            primarySelected = true;
            Debug.WriteLine($"[ATA] AddCDROM attached as Primary Master (ATA0). Type: {newDrive.GetType().Name}");
        }

        public void Reset(int controller)
        {
            if (diskDrives.Count > 0)
            {
                if (controller == 0)
                {
                    diskDrives[0].Reset();
                    if (diskDrives.Count > 1)
                        diskDrives[1].Reset();
                }
                else
                {
                    // Drive index mapping: 0=primary master, 1=primary slave,
                    //                     2=secondary master, 3=secondary slave
                    if (diskDrives.Count > 2)
                        diskDrives[2].Reset();
                    if (diskDrives.Count > 3)
                        diskDrives[3].Reset();
                }
            }
            else
            {
                Helpers.Logger("Error: ATA 'Reset' called on empty list");
            }
        }

        public void RunCommand(int controller, byte command)
        {
            Debug.WriteLine($"[ATA] RunCommand(controller={controller}, command=0x{command:X2}) diskDrives.Count={diskDrives.Count} primarySelected={primarySelected}");
            ATADrive drive;
            if (diskDrives.Count > 0)
            {
                if (controller == 0)
                {
                    if (primarySelected)
                        drive = diskDrives[0];
                    else
                        drive = diskDrives[1];

                    drive.RunCommand(command);
                }
            }
            else
            {
                Helpers.Logger("Error: ATA 'RunCommand' called on empty list");
            }
        }

        #region IDevice Members

        public int[] PortsUsed
        {
            get { return portsUsed; }
        }

        public uint Read(ushort addr, int size)
        {
            switch (addr)
            {
                // ── Primary controller ────────────────────────────────────────────────

                case 0x1f0: // Data register (primary master or slave)
                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[0].SectorBuffer;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[1].SectorBuffer;
                    }
                    return 0;

                case 0x1f1: // Error register
                    if (primarySelected && diskDrives.Count > 0)
                        return (byte)diskDrives[0].Error;
                    else if (diskDrives.Count > 1)
                        return (byte)diskDrives[1].Error;
                    return 0;

                case 0x1f2: // Sector count
                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[0].SectorCount;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[1].SectorCount;
                    }
                    return 0;

                case 0x1f3: // Sector number / LBA low
                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[0].SectorNumber;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[1].SectorNumber;
                    }
                    return 0;

                case 0x1f4: // Cylinder low / LBA mid
                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[0].CylinderLow;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[1].CylinderLow;
                    }
                    return 0;

                case 0x1f5: // Cylinder high / LBA high
                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[0].CylinderHigh;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[1].CylinderHigh;
                    }
                    return 0;

                case 0x1f6: // Drive/head register
                    if (primarySelected && diskDrives.Count > 0)
                        return diskDrives[0].DriveHead;
                    else if (diskDrives.Count > 1)
                        return diskDrives[1].DriveHead;
                    return 0;

                case 0x1f7: // Status register (primary)
                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return (byte)diskDrives[0].Status;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return (byte)diskDrives[1].Status;
                    }
                    return 0;

                case 0x3f6: // Alternate status / device control (primary)
                    return deviceControl[0];

                // ── Secondary controller ──────────────────────────────────────────────

                case 0x170: // Data register (secondary)
                    if (diskDrives.Count > 2)
                    {
                        if (diskDrives[2] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[2].SectorBuffer;
                    }
                    return 0;

                case 0x171: // Error register (secondary)
                    if (diskDrives.Count > 2)
                        return (byte)diskDrives[2].Error;
                    return 0;

                case 0x172: // Sector count (secondary)
                    if (diskDrives.Count > 2)
                    {
                        if (diskDrives[2] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[2].SectorCount;
                    }
                    return 0;

                case 0x173: // Sector number (secondary)
                    if (diskDrives.Count > 2)
                    {
                        if (diskDrives[2] is HardDrive)
                            SystemConfig.IO_HDDCall();
                        else
                            SystemConfig.IO_CDCall();
                        return diskDrives[2].SectorNumber;
                    }
                    return 0;

                case 0x174: // Cylinder low (secondary)
                    if (diskDrives.Count > 2)
                        return diskDrives[2].CylinderLow;
                    return 0;

                case 0x175: // Cylinder high (secondary)
                    if (diskDrives.Count > 2)
                        return diskDrives[2].CylinderHigh;
                    return 0;

                case 0x176: // Drive/head (secondary)
                    if (diskDrives.Count > 2)
                        return diskDrives[2].DriveHead;
                    return 0;

                case 0x177: // Status register (secondary) – 0xFF = no device
                    if (diskDrives.Count > 2)
                        return (byte)diskDrives[2].Status;
                    return 0xFF;

                case 0x376: // Alternate status (secondary) – 0xFF = no device
                    if (diskDrives.Count > 2)
                        return deviceControl[1];
                    return 0xFF;

                default:
                    break;
            }
            return 0;
        }

        public void Write(ushort addr, uint value, int size)
        {
            switch (addr)
            {
                case 0x1f0:
                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[0].SectorBuffer = (ushort)value;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[1].SectorBuffer = (ushort)value;
                    }
                    break;
                case 0x1f1:     // Precomp, do nothing
                    break;
                case 0x172:
                    if (primarySelected && diskDrives.Count > 2)
                    {
                        if (diskDrives[2] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[2].SectorCount = (byte)value;
                    }
                    else if (diskDrives.Count > 3)
                    {
                        if (diskDrives[3] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[3].SectorCount = (byte)value;
                    }
                    break;
                case 0x1f2:
                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[0].SectorCount = (byte)value;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[1].SectorCount = (byte)value;
                    }
                    break;
                case 0x173:
                    if (primarySelected && diskDrives.Count > 2)
                    {
                        if (diskDrives[2] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[2].SectorNumber = (byte)value;
                    }
                    else if (diskDrives.Count > 3)
                    {
                        if (diskDrives[2] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[2].SectorNumber = (byte)value;
                    }
                    break;
                case 0x1f3:
                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[0].SectorNumber = (byte)value;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[1].SectorNumber = (byte)value;
                    }
                    break;
                case 0x1f4:
                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[0].CylinderLow = (byte)value;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[1].CylinderLow = (byte)value;
                    }
                    break;
                case 0x1f5:
                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[0].CylinderHigh = (byte)value;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[1].CylinderHigh = (byte)value;
                    }
                    break;
                case 0x176:
                    if ((value & 0x10) == 0x10)
                        primarySelected = false;
                    else
                        primarySelected = true;

                    if (primarySelected && diskDrives.Count > 2)
                    {
                        if (diskDrives[2] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[2].DriveHead = (byte)value;
                    }
                    else if (diskDrives.Count > 3)
                    {
                        if (diskDrives[3] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[3].DriveHead = (byte)value;
                    }
                    break;
                case 0x1f6:
                    if ((value & 0x10) == 0x10)
                        primarySelected = false;
                    else
                        primarySelected = true;

                    if (primarySelected && diskDrives.Count > 0)
                    {
                        if (diskDrives[0] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[0].DriveHead = (byte)value;
                    }
                    else if (diskDrives.Count > 1)
                    {
                        if (diskDrives[1] is HardDrive)
                        {
                            SystemConfig.IO_HDDCall();
                        }
                        else
                        {
                            SystemConfig.IO_CDCall();
                        }
                        diskDrives[1].DriveHead = (byte)value;
                    }
                    break;
                case 0x1f7:
                    Debug.WriteLine($"[ATA] Port write 0x1f7 value=0x{value:X2}");
                    RunCommand(0, (byte)value);
                    break;
                case 0x3f6:
                    if ((value & 0x4) == 0x4)
                    {
                        if ((deviceControl[0] & 0x4) != 0x4)
                            Reset(0);
                    }
                    else if ((deviceControl[0] & 0x4) == 0x4)
                    {
                        if (diskDrives.Count > 0)
                        {
                            diskDrives[0].Status &= ~DeviceStatus.Busy;
                            diskDrives[0].Status |= DeviceStatus.Ready;
                        }
                        if (diskDrives.Count > 1)
                        {
                            diskDrives[1].Status &= ~DeviceStatus.Busy;
                            diskDrives[1].Status |= DeviceStatus.Ready;
                        }
                    }

                    deviceControl[0] = (byte)value;
                    break;
                case 0x376:
                    if ((value & 0x4) == 0x4)
                    {
                        if ((deviceControl[1] & 0x4) != 0x4)
                            Reset(1);
                    }
                    else if ((deviceControl[1] & 0x4) == 0x4)
                    {
                        if (diskDrives.Count > 2)
                        {
                            diskDrives[2].Status &= ~DeviceStatus.Busy;
                            diskDrives[2].Status |= DeviceStatus.Ready;
                        }
                        if (diskDrives.Count > 3)
                        {
                            diskDrives[3].Status &= ~DeviceStatus.Busy;
                            diskDrives[3].Status |= DeviceStatus.Ready;
                        }
                    }

                    deviceControl[0] = (byte)value;
                    break;
                default:
                    System.Diagnostics.Debugger.Break();
                    break;
            }
        }

        #endregion
    }
}
