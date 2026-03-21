// VhdStream.cs – transparent Stream wrapper for VHD disk images.
//
// Supports:
//   • Fixed VHDs  – raw disk data followed by a 512-byte VHD footer.
//   • Dynamic VHDs – footer copy, dynamic header, BAT, sparse data blocks, footer.
//
// The stream presented to callers always covers only the logical disk payload,
// so sector arithmetic in HardDrive.cs stays unchanged.

using System;
using System.IO;
using System.Text;

namespace x86Emulator.Devices
{
    /// <summary>
    /// A <see cref="Stream"/> wrapper that presents the raw disk payload of a VHD image
    /// file, hiding the VHD container metadata (footer, dynamic header, BAT) from callers.
    /// </summary>
    internal sealed class VhdStream : Stream
    {
        // ── VHD constants ──────────────────────────────────────────────────────────
        private const int  FooterSize         = 512;
        private const int  DynHeaderSize      = 1024;
        private const uint BatEntryFree       = 0xFFFFFFFF;
        private const int  SectorSize         = 512;

        private static readonly byte[] CookieFixed   = Encoding.ASCII.GetBytes("conectix");
        private static readonly byte[] CookieDynamic = Encoding.ASCII.GetBytes("cxsparse");

        // VHD Disk Type constants (footer word at offset 60, big-endian)
        private const uint DiskTypeFixed       = 2;
        private const uint DiskTypeDynamic     = 3;
        private const uint DiskTypeDifferencing = 4;

        // ── State ─────────────────────────────────────────────────────────────────
        private readonly Stream _base;
        private readonly bool   _ownsBase;
        private readonly uint   _diskType;   // DiskTypeFixed / DiskTypeDynamic
        private readonly long   _diskSize;   // logical disk size in bytes

        // Dynamic-VHD fields (unused for fixed)
        private uint[]  _bat;            // Block Allocation Table (sector offsets into file)
        private int     _blockSectors;   // sectors per data block
        private int     _bitmapSectors;  // bitmap sectors preceding each data block

        private long _position;

        // ── Factory ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Opens <paramref name="baseStream"/> as a VHD and returns a
        /// <see cref="VhdStream"/> if a valid VHD footer is found, or
        /// <paramref name="baseStream"/> unchanged when the file is not a VHD.
        /// </summary>
        public static Stream OpenOrPassThrough(Stream baseStream, bool ownsBase = true)
        {
            if (baseStream == null) throw new ArgumentNullException(nameof(baseStream));
            if (baseStream.Length < FooterSize) return baseStream;

            byte[] footer = ReadFooter(baseStream);
            if (!StartsWithCookie(footer, 0, CookieFixed))
                return baseStream;   // not a VHD – use as-is

            uint diskType = ReadUInt32BE(footer, 60);
            if (diskType != DiskTypeFixed && diskType != DiskTypeDynamic)
                return baseStream;   // differencing or unknown – pass through unchanged

            return new VhdStream(baseStream, ownsBase, footer, diskType);
        }

        // ── Constructor ───────────────────────────────────────────────────────────

        private VhdStream(Stream baseStream, bool ownsBase, byte[] footer, uint diskType)
        {
            _base      = baseStream;
            _ownsBase  = ownsBase;
            _diskType  = diskType;
            _diskSize  = (long)ReadUInt64BE(footer, 48);  // "Current Size" field

            if (diskType == DiskTypeDynamic)
                LoadDynamicStructures(footer);
        }

        // ── Stream overrides ──────────────────────────────────────────────────────

        public override bool CanRead  => true;
        public override bool CanSeek  => true;
        public override bool CanWrite => _base.CanWrite;
        public override long Length   => _diskSize;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos = origin switch
            {
                SeekOrigin.Begin   => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End     => _diskSize + offset,
                _                  => throw new ArgumentException("Invalid SeekOrigin", nameof(origin))
            };
            if (newPos < 0) throw new IOException("Seek before beginning of stream.");
            _position = newPos;
            return _position;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)  throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)      throw new ArgumentOutOfRangeException(nameof(offset));
            if (count  < 0)      throw new ArgumentOutOfRangeException(nameof(count));
            if (_position >= _diskSize) return 0;

            int toRead = (int)Math.Min(count, _diskSize - _position);
            int read   = 0;

            while (read < toRead)
            {
                int n = _diskType == DiskTypeDynamic
                    ? ReadDynamic(buffer, offset + read, toRead - read)
                    : ReadFixed(buffer,   offset + read, toRead - read);
                if (n == 0) break;
                read      += n;
                _position += n;
            }
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null)  throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)      throw new ArgumentOutOfRangeException(nameof(offset));
            if (count  < 0)      throw new ArgumentOutOfRangeException(nameof(count));
            if (!CanWrite) throw new NotSupportedException("Stream is read-only.");
            if (_position + count > _diskSize)
                throw new IOException("Write would exceed disk boundary.");

            int written = 0;
            while (written < count)
            {
                int n = _diskType == DiskTypeDynamic
                    ? WriteDynamic(buffer, offset + written, count - written)
                    : WriteFixed(buffer,   offset + written, count - written);
                if (n == 0) break;
                written   += n;
                _position += n;
            }
        }

        public override void Flush() => _base.Flush();

        public override void SetLength(long value) =>
            throw new NotSupportedException("VhdStream does not support SetLength.");

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsBase)
                _base.Dispose();
            base.Dispose(disposing);
        }

        // ── Fixed VHD I/O ─────────────────────────────────────────────────────────
        // For a fixed VHD the logical bytes sit at the same offsets in the file;
        // the footer is simply appended and is never part of the logical disk.

        private int ReadFixed(byte[] buf, int off, int count)
        {
            long clamp = Math.Min(count, _diskSize - _position);
            _base.Seek(_position, SeekOrigin.Begin);
            return _base.Read(buf, off, (int)clamp);
        }

        private int WriteFixed(byte[] buf, int off, int count)
        {
            _base.Seek(_position, SeekOrigin.Begin);
            _base.Write(buf, off, count);
            return count;
        }

        // ── Dynamic VHD I/O ───────────────────────────────────────────────────────

        private int ReadDynamic(byte[] buf, int off, int count)
        {
            long sectorIndex      = _position / SectorSize;
            int  offsetInSector   = (int)(_position % SectorSize);
            int  blockIndex       = (int)(sectorIndex / _blockSectors);
            int  sectorInBlock    = (int)(sectorIndex % _blockSectors);

            int  canRead  = Math.Min(count, SectorSize - offsetInSector);

            uint batEntry = (blockIndex < _bat.Length) ? _bat[blockIndex] : BatEntryFree;

            if (batEntry == BatEntryFree)
            {
                // Unallocated block – logical zeros
                Array.Clear(buf, off, canRead);
            }
            else
            {
                long fileOffset = (long)batEntry * SectorSize          // start of bitmap
                                + (long)_bitmapSectors * SectorSize    // skip bitmap
                                + (long)sectorInBlock  * SectorSize    // sector within block
                                + offsetInSector;
                _base.Seek(fileOffset, SeekOrigin.Begin);
                int got = _base.Read(buf, off, canRead);
                if (got < canRead)
                    Array.Clear(buf, off + got, canRead - got);
            }
            return canRead;
        }

        private int WriteDynamic(byte[] buf, int off, int count)
        {
            long sectorIndex    = _position / SectorSize;
            int  offsetInSector = (int)(_position % SectorSize);
            int  blockIndex     = (int)(sectorIndex / _blockSectors);
            int  sectorInBlock  = (int)(sectorIndex % _blockSectors);

            int  canWrite = Math.Min(count, SectorSize - offsetInSector);

            uint batEntry = (blockIndex < _bat.Length) ? _bat[blockIndex] : BatEntryFree;
            if (batEntry == BatEntryFree)
                throw new IOException($"Write to unallocated dynamic VHD block {blockIndex} is not supported.");

            long fileOffset = (long)batEntry * SectorSize
                            + (long)_bitmapSectors * SectorSize
                            + (long)sectorInBlock  * SectorSize
                            + offsetInSector;
            _base.Seek(fileOffset, SeekOrigin.Begin);
            _base.Write(buf, off, canWrite);
            return canWrite;
        }

        // ── Dynamic-VHD structure loading ─────────────────────────────────────────

        private void LoadDynamicStructures(byte[] footer)
        {
            // The dynamic header is located at the Data Offset from the footer.
            long dynHeaderOffset = (long)ReadUInt64BE(footer, 16);

            byte[] dynHeader = new byte[DynHeaderSize];
            _base.Seek(dynHeaderOffset, SeekOrigin.Begin);
            ReadExact(_base, dynHeader, 0, DynHeaderSize);

            if (!StartsWithCookie(dynHeader, 0, CookieDynamic))
                throw new InvalidDataException("Dynamic VHD header cookie mismatch.");

            long batOffset    = (long)ReadUInt64BE(dynHeader, 16);
            uint maxEntries   = ReadUInt32BE(dynHeader, 28);
            uint blockSize    = ReadUInt32BE(dynHeader, 32);   // bytes per data block

            _blockSectors  = (int)(blockSize / SectorSize);

            // Bitmap: ceil(blockSectors / 8) bytes, rounded up to a 512-byte boundary.
            int bitmapBytes   = (_blockSectors + 7) / 8;
            _bitmapSectors = (bitmapBytes + SectorSize - 1) / SectorSize;

            // Read BAT
            _bat = new uint[maxEntries];
            byte[] batRaw = new byte[maxEntries * 4];
            _base.Seek(batOffset, SeekOrigin.Begin);
            ReadExact(_base, batRaw, 0, batRaw.Length);
            for (int i = 0; i < (int)maxEntries; i++)
                _bat[i] = ReadUInt32BE(batRaw, i * 4);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static byte[] ReadFooter(Stream s)
        {
            byte[] footer = new byte[FooterSize];
            // VHD footer can appear at either the very end (standard) or the very
            // beginning of the file (dynamic VHD has a copy at offset 0).
            // We read from the end first (authoritative copy for all types).
            s.Seek(-FooterSize, SeekOrigin.End);
            ReadExact(s, footer, 0, FooterSize);
            return footer;
        }

        private static bool StartsWithCookie(byte[] data, int offset, byte[] cookie)
        {
            if (data.Length - offset < cookie.Length) return false;
            for (int i = 0; i < cookie.Length; i++)
                if (data[offset + i] != cookie[i]) return false;
            return true;
        }

        private static void ReadExact(Stream s, byte[] buf, int off, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, off + read, count - read);
                if (n == 0) throw new EndOfStreamException("Unexpected end of VHD file.");
                read += n;
            }
        }

        // Big-endian multi-byte readers (VHD uses big-endian throughout)
        private static uint ReadUInt32BE(byte[] buf, int offset) =>
            ((uint)buf[offset]     << 24) |
            ((uint)buf[offset + 1] << 16) |
            ((uint)buf[offset + 2] <<  8) |
             (uint)buf[offset + 3];

        private static ulong ReadUInt64BE(byte[] buf, int offset) =>
            ((ulong)buf[offset]     << 56) |
            ((ulong)buf[offset + 1] << 48) |
            ((ulong)buf[offset + 2] << 40) |
            ((ulong)buf[offset + 3] << 32) |
            ((ulong)buf[offset + 4] << 24) |
            ((ulong)buf[offset + 5] << 16) |
            ((ulong)buf[offset + 6] <<  8) |
             (ulong)buf[offset + 7];
    }
}
