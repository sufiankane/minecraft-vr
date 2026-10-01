using System;
using System.Collections.Generic;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.FSharp.Core;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    // Pins the version-1 delta format from ADR-0006: a hand-built byte fixture,
    // canonical single-run encodings, a byte-stable round trip and every
    // rejection rule. The payload carries no coordinate (the store keys by
    // ChunkCoord), so decoded deltas carry the origin coordinate.
    [TestFixture]
    public sealed class ChunkDeltaCodecTests
    {
        private const ulong Seed = 20261001UL;
        private const int MaxTests = 300;
        private const int HeaderSize = 12;
        private const int RunSize = 6;
        private const int CellCount = 4096;

        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly BlockId Stone = new BlockId(1);
        private static readonly BlockId Dirt = new BlockId(2);
        private static readonly BlockId Grass = new BlockId(3);

        [Test]
        public void EmptyDeltaSerializesToASingleAirRun()
        {
            byte[] bytes = ChunkDeltaCodec.Serialize(ChunkDelta.Empty(Origin));

            Assert.That(bytes.Length, Is.EqualTo(HeaderSize + RunSize));
            Assert.That(ReadUInt32(bytes, 8), Is.EqualTo(1u));
            Assert.That(ReadUInt16(bytes, 12), Is.EqualTo((ushort)0));
            Assert.That(ReadUInt32(bytes, 14), Is.EqualTo(4096u));
        }

        [Test]
        public void FullStoneChunkSerializesToASingleRun()
        {
            var edits = new Dictionary<Int3, BlockId>(CellCount);
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        edits[new Int3(x, y, z)] = Stone;
                    }
                }
            }

            byte[] bytes = ChunkDeltaCodec.Serialize(new ChunkDelta(Origin, edits));

            Assert.That(bytes.Length, Is.EqualTo(HeaderSize + RunSize));
            Assert.That(ReadUInt32(bytes, 8), Is.EqualTo(1u));
            Assert.That(ReadUInt16(bytes, 12), Is.EqualTo(Stone.Value));
            Assert.That(ReadUInt32(bytes, 14), Is.EqualTo(4096u));
        }

        [Test]
        public void FixedVersionOneFixtureDecodesToTheExpectedDelta()
        {
            // Hand-built v1 payload: air, stone at local (1, 0, 0), air to 4096.
            byte[] bytes =
            {
                0x43, 0x47, 0x44, 0x4C,             // magic "CGDL"
                0x01, 0x00,                         // version 1
                0x00, 0x00,                         // reserved 0
                0x03, 0x00, 0x00, 0x00,             // entryCount 3
                0x00, 0x00, 0x01, 0x00, 0x00, 0x00, // (Air, 1)
                0x01, 0x00, 0x01, 0x00, 0x00, 0x00, // (Stone, 1)
                0x00, 0x00, 0xFE, 0x0F, 0x00, 0x00, // (Air, 4094)
            };

            bool ok = ChunkDeltaCodec.TryDeserialize(bytes, out ChunkDelta? delta);

            Assert.That(ok, Is.True);
            Assert.That(delta, Is.Not.Null);
            var expected = new ChunkDelta(
                Origin,
                new Dictionary<Int3, BlockId> { [new Int3(1, 0, 0)] = Stone });
            Assert.That(delta, Is.EqualTo(expected));
            Assert.That(delta!.Coord, Is.EqualTo(Origin));
            Assert.That(ChunkDeltaCodec.Serialize(delta), Is.EqualTo(bytes));
        }

        [Test]
        public void EmptyFixtureFromTheAdrDecodesToAnEmptyDelta()
        {
            // ADR-0006: an all-air delta is the single run (Air, 4096).
            byte[] bytes = Encode(1, 0, 1, ((ushort)0, 4096u));

            bool ok = ChunkDeltaCodec.TryDeserialize(bytes, out ChunkDelta? delta);

            Assert.That(ok, Is.True);
            Assert.That(delta, Is.EqualTo(ChunkDelta.Empty(Origin)));
        }

        [Test]
        public void SerializeOrdersRunsInZMajorLocalOrder()
        {
            var edits = new Dictionary<Int3, BlockId>
            {
                [new Int3(1, 0, 0)] = Dirt,
                [new Int3(0, 0, 1)] = Stone,
            };

            byte[] bytes = ChunkDeltaCodec.Serialize(new ChunkDelta(Origin, edits));
            List<(ushort Block, uint Length)> runs = ReadRuns(bytes);

            // i = x + 16y + 256z: (1,0,0) is i=1 and (0,0,1) is i=256.
            var expected = new List<(ushort Block, uint Length)>
            {
                ((ushort)0, 1u),
                (Dirt.Value, 1u),
                ((ushort)0, 254u),
                (Stone.Value, 1u),
                ((ushort)0, 3839u),
            };
            Assert.That(runs, Is.EqualTo(expected));
        }

        [Test]
        public void NonCanonicalAdjacentRunsDecodeAndReserializeCanonically()
        {
            byte[] nonCanonical = Encode(1, 0, 3, (Stone.Value, 1u), (Stone.Value, 1u), (0, 4094u));

            bool ok = ChunkDeltaCodec.TryDeserialize(nonCanonical, out ChunkDelta? delta);

            Assert.That(ok, Is.True);
            var expected = new ChunkDelta(
                Origin,
                new Dictionary<Int3, BlockId>
                {
                    [new Int3(0, 0, 0)] = Stone,
                    [new Int3(1, 0, 0)] = Stone,
                });
            Assert.That(delta, Is.EqualTo(expected));
            Assert.That(
                ChunkDeltaCodec.Serialize(delta!),
                Is.EqualTo(Encode(1, 0, 2, (Stone.Value, 2u), (0, 4094u))));
        }

        [Test]
        public void AirEditsAreIndistinguishableFromUntouchedCells()
        {
            // The v1 payload expands edits over an air baseline, so an edit
            // that sets a cell back to Air produces no bytes of its own.
            var delta = new ChunkDelta(
                Origin,
                new Dictionary<Int3, BlockId> { [new Int3(1, 2, 3)] = BlockId.Air });

            byte[] bytes = ChunkDeltaCodec.Serialize(delta);

            Assert.That(ChunkDeltaCodec.TryDeserialize(bytes, out ChunkDelta? loaded), Is.True);
            Assert.That(loaded, Is.EqualTo(ChunkDelta.Empty(Origin)));
        }

        [Test]
        public void ThePayloadDoesNotCarryTheCoordinate()
        {
            var edits = new Dictionary<Int3, BlockId> { [new Int3(2, 4, 6)] = Stone };
            byte[] fromFirst = ChunkDeltaCodec.Serialize(new ChunkDelta(new ChunkCoord(1, 2, 3), edits));
            byte[] fromSecond = ChunkDeltaCodec.Serialize(new ChunkDelta(new ChunkCoord(-4, 5, -6), edits));

            Assert.That(fromFirst, Is.EqualTo(fromSecond));
            Assert.That(ChunkDeltaCodec.TryDeserialize(fromFirst, out ChunkDelta? loaded), Is.True);
            Assert.That(loaded!.Coord, Is.EqualTo(Origin));
            Assert.That(loaded.Edits, Is.EquivalentTo(edits));
        }

        [Test]
        public void SerializeDeserializeRoundTripPreservesEveryEditAndTheBytes()
        {
            Property property = Prop.ForAll(DeltaArbitrary(), RoundTrips);

            Check.One("delta round trip", DeterministicConfig(), property);
        }

        [Test]
        public void SerializeRejectsANullDelta()
        {
            Assert.Throws<ArgumentNullException>(() => ChunkDeltaCodec.Serialize(null!));
        }

        [Test]
        public void RejectsBadMagic()
        {
            byte[] valid = ChunkDeltaCodec.Serialize(ChunkDelta.Empty(Origin));
            for (int i = 0; i < 4; i++)
            {
                byte[] bytes = (byte[])valid.Clone();
                bytes[i] = (byte)'X';

                AssertRejected(bytes, $"magic byte {i}");
            }
        }

        [Test]
        public void RejectsUnknownOrNewerVersions()
        {
            AssertRejected(Encode(0, 0, 1, (0, 4096u)), "version 0");
            AssertRejected(Encode(2, 0, 1, (0, 4096u)), "version 2");
            AssertRejected(Encode(ushort.MaxValue, 0, 1, (0, 4096u)), "version 65535");
        }

        [Test]
        public void RejectsNonZeroReserved()
        {
            AssertRejected(Encode(1, 1, 1, (0, 4096u)), "reserved 1");
            AssertRejected(Encode(1, ushort.MaxValue, 1, (0, 4096u)), "reserved 65535");
        }

        [Test]
        public void RejectsEntryCountAbove4096()
        {
            var bytes = new byte[HeaderSize + (4097 * RunSize)];
            bytes[0] = (byte)'C';
            bytes[1] = (byte)'G';
            bytes[2] = (byte)'D';
            bytes[3] = (byte)'L';
            WriteUInt16(bytes, 4, 1);
            WriteUInt32(bytes, 8, 4097);

            AssertRejected(bytes, "entryCount 4097");
        }

        [Test]
        public void RejectsATruncatedHeader()
        {
            byte[] valid = ChunkDeltaCodec.Serialize(ChunkDelta.Empty(Origin));
            for (int length = 0; length < HeaderSize; length++)
            {
                AssertRejected(ClonePrefix(valid, length), $"header length {length}");
            }
        }

        [Test]
        public void RejectsATruncatedPayload()
        {
            byte[] valid = Encode(
                1,
                0,
                3,
                (0, 1u),
                (Stone.Value, 1u),
                (0, 4094u));
            for (int length = HeaderSize; length < valid.Length; length++)
            {
                AssertRejected(ClonePrefix(valid, length), $"payload length {length}");
            }
        }

        [Test]
        public void RejectsTrailingBytes()
        {
            byte[] valid = ChunkDeltaCodec.Serialize(ChunkDelta.Empty(Origin));
            var withTrailer = new byte[valid.Length + 1];
            valid.CopyTo(withTrailer, 0);

            AssertRejected(withTrailer, "one trailing byte");
        }

        [Test]
        public void RejectsACountThatDoesNotMatchTheRemainingBytes()
        {
            byte[] twoRunPayload = Encode(1, 0, 3, (0, 1u), (Stone.Value, 1u), (0, 4094u));

            AssertRejected(Encode(1, 0, 1), "header count 1, zero runs");
            AssertRejected(ClonePrefix(twoRunPayload, HeaderSize + (2 * RunSize)), "header count 3, two runs");
        }

        [Test]
        public void RejectsZeroLengthRuns()
        {
            AssertRejected(Encode(1, 0, 2, (0, 0u), (0, 4096u)), "leading zero run");
            AssertRejected(Encode(1, 0, 2, (0, 4096u), (Stone.Value, 0u)), "trailing zero run");
        }

        [Test]
        public void RejectsCoverageUnderrun()
        {
            AssertRejected(Encode(1, 0, 1, (0, 4095u)), "single short run");
            AssertRejected(
                Encode(1, 0, 2, (0, 1u), (Stone.Value, 4094u)),
                "runs summing to 4095");
        }

        [Test]
        public void RejectsCoverageOverrun()
        {
            AssertRejected(Encode(1, 0, 1, (0, 4097u)), "single long run");
            AssertRejected(
                Encode(1, 0, 2, (0, 4096u), (Stone.Value, 1u)),
                "runs summing to 4097");
        }

        [Test]
        public void RejectsAnEmptyRunList()
        {
            AssertRejected(Encode(1, 0, 0), "entryCount 0");
        }

        [Test]
        public void FailureLeavesTheOutDeltaNull()
        {
            bool ok = ChunkDeltaCodec.TryDeserialize(new byte[] { 1, 2, 3 }, out ChunkDelta? delta);

            Assert.That(ok, Is.False);
            Assert.That(delta, Is.Null);
        }

        private static void AssertRejected(byte[] bytes, string description)
        {
            bool ok = ChunkDeltaCodec.TryDeserialize(bytes, out ChunkDelta? delta);

            Assert.That(ok, Is.False, $"accepted {description}");
            Assert.That(delta, Is.Null, $"accepted {description} with a delta");
        }

        private static bool RoundTrips(ChunkDelta delta)
        {
            byte[] bytes = ChunkDeltaCodec.Serialize(delta);
            if (!ChunkDeltaCodec.TryDeserialize(bytes, out ChunkDelta? loaded) || loaded is null)
            {
                return false;
            }

            return loaded.Equals(delta) && ChunkDeltaCodec.Serialize(loaded).AsSpan().SequenceEqual(bytes);
        }

        private static Arbitrary<ChunkDelta> DeltaArbitrary()
        {
            Gen<(int X, int Y, int Z, ushort Block)> entries =
                from x in Gen.Choose(0, ChunkMath.ChunkSize - 1)
                from y in Gen.Choose(0, ChunkMath.ChunkSize - 1)
                from z in Gen.Choose(0, ChunkMath.ChunkSize - 1)
                from block in Gen.Choose(1, 8)
                select (x, y, z, (ushort)block);

            return Arb.From(
                from list in Gen.ListOf(entries)
                select BuildDelta(list));
        }

        private static ChunkDelta BuildDelta(IEnumerable<(int X, int Y, int Z, ushort Block)> entries)
        {
            var edits = new Dictionary<Int3, BlockId>();
            foreach ((int x, int y, int z, ushort block) in entries)
            {
                edits[new Int3(x, y, z)] = new BlockId(block);
            }

            return new ChunkDelta(Origin, edits);
        }

        private static byte[] Encode(ushort version, ushort reserved, uint entryCount, params (ushort Block, uint Length)[] runs)
        {
            var bytes = new byte[HeaderSize + (runs.Length * RunSize)];
            bytes[0] = (byte)'C';
            bytes[1] = (byte)'G';
            bytes[2] = (byte)'D';
            bytes[3] = (byte)'L';
            WriteUInt16(bytes, 4, version);
            WriteUInt16(bytes, 6, reserved);
            WriteUInt32(bytes, 8, entryCount);
            int offset = HeaderSize;
            foreach ((ushort block, uint length) in runs)
            {
                WriteUInt16(bytes, offset, block);
                WriteUInt32(bytes, offset + 2, length);
                offset += RunSize;
            }

            return bytes;
        }

        private static List<(ushort Block, uint Length)> ReadRuns(byte[] bytes)
        {
            var runs = new List<(ushort Block, uint Length)>();
            int count = (int)ReadUInt32(bytes, 8);
            int offset = HeaderSize;
            for (int i = 0; i < count; i++)
            {
                runs.Add((ReadUInt16(bytes, offset), ReadUInt32(bytes, offset + 2)));
                offset += RunSize;
            }

            return runs;
        }

        private static byte[] ClonePrefix(byte[] bytes, int length)
        {
            var clone = new byte[length];
            Array.Copy(bytes, clone, length);
            return clone;
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
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

        private static Config DeterministicConfig()
        {
            Replay replay = new Replay(new Rnd(Seed), FSharpOption<int>.None);
            return Config.QuickThrowOnFailure
                .WithMaxTest(MaxTests)
                .WithReplay(FSharpOption<Replay>.Some(replay));
        }
    }
}
