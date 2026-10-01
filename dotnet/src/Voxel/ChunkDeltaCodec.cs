using System;
using System.Collections.Generic;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// The byte-level versioned codec for <see cref="ChunkDelta"/> (ADR-0006).
    /// </summary>
    /// <remarks>
    /// Version 1 is little-endian: magic <c>"CGDL"</c>, <c>uint16 version = 1</c>,
    /// <c>uint16 reserved = 0</c>, <c>uint32 entryCount</c>, then
    /// <c>entryCount</c> runs of <c>uint16 blockId</c> and
    /// <c>uint32 runLength</c>. Runs cover exactly the 4096 cells in Z-major
    /// local order (<c>i = x + 16y + 256z</c>). <see cref="Serialize"/> is
    /// canonical: it expands the edit map over an air baseline, sorts edits
    /// into that order and merges adjacent equal blocks, so a codec-produced
    /// payload is byte-stable across a decode/encode round trip. The payload
    /// carries no coordinate, because <see cref="IWorldStore"/> keys deltas by
    /// <see cref="ChunkCoord"/>; a decoded delta therefore carries the origin
    /// coordinate. Cells edited to <see cref="BlockId.Air"/> are
    /// indistinguishable from untouched cells in version 1.
    /// </remarks>
    public static class ChunkDeltaCodec
    {
        private const int HeaderSize = 12;
        private const int RunSize = 6;
        private const int CellCount = ChunkMath.ChunkSize * ChunkMath.ChunkSize * ChunkMath.ChunkSize;
        private const int LayerSize = ChunkMath.ChunkSize * ChunkMath.ChunkSize;
        private const int MaxRuns = CellCount;

        /// <summary>
        /// Serializes <paramref name="delta"/> to the canonical version-1
        /// payload (ADR-0006).
        /// </summary>
        /// <remarks>
        /// Every local cell of <paramref name="delta"/> must lie in
        /// <c>[0, ChunkMath.ChunkSize)</c> per axis.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="delta"/> is null.</exception>
        public static byte[] Serialize(ChunkDelta delta)
        {
            if (delta is null)
            {
                throw new ArgumentNullException(nameof(delta));
            }

            var cells = new BlockId[CellCount];
            foreach (KeyValuePair<Int3, BlockId> edit in delta.Edits)
            {
                cells[Index(edit.Key)] = edit.Value;
            }

            int runs = CountRuns(cells);
            var bytes = new byte[HeaderSize + (runs * RunSize)];
            bytes[0] = (byte)'C';
            bytes[1] = (byte)'G';
            bytes[2] = (byte)'D';
            bytes[3] = (byte)'L';
            WriteUInt16(bytes, 4, 1);
            WriteUInt16(bytes, 6, 0);
            WriteUInt32(bytes, 8, (uint)runs);

            int offset = HeaderSize;
            int start = 0;
            for (int i = 1; i <= CellCount; i++)
            {
                if (i == CellCount || !cells[i].Equals(cells[i - 1]))
                {
                    WriteUInt16(bytes, offset, cells[start].Value);
                    WriteUInt32(bytes, offset + 2, (uint)(i - start));
                    offset += RunSize;
                    start = i;
                }
            }

            return bytes;
        }

        /// <summary>
        /// Reads a version-1 payload. Returns false and a null delta for any
        /// input that is not a complete, exactly-covering version-1 payload;
        /// this method never throws for any input.
        /// </summary>
        /// <remarks>
        /// Rejected: a short or bad header; a version other than 1; a non-zero
        /// reserved field; an entry count above 4096 or inconsistent with the
        /// remaining bytes; a zero run length; run coverage summing to less or
        /// more than 4096; and trailing bytes.
        /// </remarks>
        public static bool TryDeserialize(ReadOnlySpan<byte> bytes, out ChunkDelta? delta)
        {
            delta = null;

            if (bytes.Length < HeaderSize
                || bytes[0] != (byte)'C'
                || bytes[1] != (byte)'G'
                || bytes[2] != (byte)'D'
                || bytes[3] != (byte)'L')
            {
                return false;
            }

            if (ReadUInt16(bytes, 4) != 1 || ReadUInt16(bytes, 6) != 0)
            {
                return false;
            }

            uint entryCount = ReadUInt32(bytes, 8);
            if (entryCount > MaxRuns || bytes.Length - HeaderSize != (int)entryCount * RunSize)
            {
                return false;
            }

            var edits = new Dictionary<Int3, BlockId>();
            int cell = 0;
            int offset = HeaderSize;
            for (uint run = 0; run < entryCount; run++)
            {
                ushort block = ReadUInt16(bytes, offset);
                uint runLength = ReadUInt32(bytes, offset + 2);
                offset += RunSize;

                if (runLength == 0 || runLength > (uint)(CellCount - cell))
                {
                    return false;
                }

                if (block != 0)
                {
                    var blockId = new BlockId(block);
                    int end = cell + (int)runLength;
                    for (int index = cell; index < end; index++)
                    {
                        edits[new Int3(
                            index % ChunkMath.ChunkSize,
                            (index / ChunkMath.ChunkSize) % ChunkMath.ChunkSize,
                            index / LayerSize)] = blockId;
                    }
                }

                cell += (int)runLength;
            }

            if (cell != CellCount)
            {
                return false;
            }

            delta = new ChunkDelta(default, edits);
            return true;
        }

        private static int Index(Int3 local)
        {
            return local.X + (ChunkMath.ChunkSize * local.Y) + (LayerSize * local.Z);
        }

        private static int CountRuns(BlockId[] cells)
        {
            int runs = 1;
            for (int i = 1; i < CellCount; i++)
            {
                if (!cells[i].Equals(cells[i - 1]))
                {
                    runs++;
                }
            }

            return runs;
        }

        private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
        {
            return (uint)(bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24));
        }

        private static void WriteUInt16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }
    }
}
