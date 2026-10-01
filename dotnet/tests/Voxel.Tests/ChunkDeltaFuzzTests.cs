using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    // Seed 20261001: mutate valid version-1 payloads (all 256 byte values at
    // every position, truncations at every length, random flips) and feed
    // random byte arrays, asserting that TryDeserialize either returns true
    // with a valid delta or false. Any exception escaping the codec fails the
    // test with the input that produced it.
    [TestFixture]
    public sealed class ChunkDeltaFuzzTests
    {
        private const ulong Seed = 20261001UL;
        private const int RandomArrayCases = 1000;
        private const int RandomFlipCases = 2048;

        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly BlockId Stone = new BlockId(1);
        private static readonly BlockId Dirt = new BlockId(2);
        private static readonly BlockId Grass = new BlockId(3);

        [Test]
        public void MutatingEveryByteToEveryValueNeverThrows()
        {
            long cases = 0;
            foreach (byte[] sample in ValidSamples())
            {
                for (int position = 0; position < sample.Length; position++)
                {
                    byte original = sample[position];
                    for (int value = 0; value < 256; value++)
                    {
                        sample[position] = (byte)value;
                        cases++;
                        CheckNoThrow(sample, $"byte {position} = {value} of a {sample.Length}-byte payload");
                    }

                    sample[position] = original;
                }
            }

            TestContext.Progress.WriteLine($"fuzz byte mutations: {cases} cases");
            Assert.That(cases, Is.GreaterThan(0));
        }

        [Test]
        public void TruncatingAtEveryLengthNeverThrows()
        {
            long cases = 0;
            foreach (byte[] sample in ValidSamples())
            {
                for (int length = 0; length < sample.Length; length++)
                {
                    cases++;
                    CheckNoThrow(
                        ClonePrefix(sample, length),
                        $"truncation to {length} of a {sample.Length}-byte payload");
                }
            }

            TestContext.Progress.WriteLine($"fuzz truncations: {cases} cases");
            Assert.That(cases, Is.GreaterThan(0));
        }

        [Test]
        public void RandomByteFlipsNeverThrow()
        {
            var random = new XorShift(Seed);
            byte[][] samples = ValidSamples();
            long cases = 0;
            for (int i = 0; i < RandomFlipCases; i++)
            {
                byte[] bytes = (byte[])samples[random.NextInt(samples.Length)].Clone();
                int flips = 1 + random.NextInt(8);
                for (int flip = 0; flip < flips; flip++)
                {
                    bytes[random.NextInt(bytes.Length)] = random.NextByte();
                }

                cases++;
                CheckNoThrow(bytes, $"random flip case {i} ({flips} flips on {bytes.Length} bytes)");
            }

            TestContext.Progress.WriteLine($"fuzz random flips: {cases} cases");
            Assert.That(cases, Is.EqualTo(RandomFlipCases));
        }

        [Test]
        public void RandomByteArraysNeverThrow()
        {
            var random = new XorShift(Seed);
            long pureCount = 0;
            long structuredCount = 0;

            for (int i = 0; i < RandomArrayCases; i++)
            {
                byte[] pure = RandomBytes(random, 0, 80);
                pureCount++;
                CheckNoThrow(pure, $"random array case {i} ({pure.Length} bytes)");

                byte[] structured = RandomBytes(random, 12, 80);
                structured[0] = (byte)'C';
                structured[1] = (byte)'G';
                structured[2] = (byte)'D';
                structured[3] = (byte)'L';
                WriteUInt16(structured, 4, 1);
                WriteUInt32(structured, 8, (uint)random.Next());
                structuredCount++;
                CheckNoThrow(structured, $"structured random array case {i} ({structured.Length} bytes)");
            }

            TestContext.Progress.WriteLine(
                $"fuzz random arrays: {pureCount} pure + {structuredCount} structured cases");
            Assert.That(pureCount, Is.EqualTo(RandomArrayCases));
            Assert.That(structuredCount, Is.EqualTo(RandomArrayCases));
        }

        private static byte[][] ValidSamples()
        {
            var mixed = new Dictionary<Int3, BlockId>
            {
                [new Int3(0, 0, 0)] = Stone,
                [new Int3(1, 0, 0)] = Stone,
                [new Int3(5, 3, 2)] = Dirt,
                [new Int3(15, 15, 15)] = Grass,
            };

            return new[]
            {
                ChunkDeltaCodec.Serialize(ChunkDelta.Empty(Origin)),
                ChunkDeltaCodec.Serialize(new ChunkDelta(Origin, mixed)),
                ChunkDeltaCodec.Serialize(new ChunkDelta(Origin, AllStone())),
            };
        }

        private static Dictionary<Int3, BlockId> AllStone()
        {
            var edits = new Dictionary<Int3, BlockId>(4096);
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

            return edits;
        }

        private static void CheckNoThrow(byte[] bytes, string context)
        {
            ChunkDelta? delta = null;
            bool ok;
            try
            {
                ok = ChunkDeltaCodec.TryDeserialize(bytes, out delta);
            }
            catch (Exception ex)
            {
                Assert.Fail($"{context}: {ex.GetType().Name} escaped TryDeserialize: {ex.Message}");
                return;
            }

            if (ok)
            {
                Assert.That(delta, Is.Not.Null, $"{context}: success must produce a delta");
                Validate(delta!, context);
            }
            else
            {
                Assert.That(delta, Is.Null, $"{context}: failure must clear the delta");
            }
        }

        private static void Validate(ChunkDelta delta, string context)
        {
            foreach (KeyValuePair<Int3, BlockId> edit in delta.Edits)
            {
                Int3 cell = edit.Key;
                bool inRange = cell.X >= 0 && cell.X < ChunkMath.ChunkSize
                    && cell.Y >= 0 && cell.Y < ChunkMath.ChunkSize
                    && cell.Z >= 0 && cell.Z < ChunkMath.ChunkSize;
                Assert.That(inRange, Is.True, $"{context}: decoded local {cell} is outside the chunk");
                Assert.That(edit.Value, Is.Not.EqualTo(BlockId.Air), $"{context}: decoded an air edit");
            }

            byte[] canonical = ChunkDeltaCodec.Serialize(delta);
            Assert.That(
                ChunkDeltaCodec.TryDeserialize(canonical, out ChunkDelta? reloaded),
                Is.True,
                $"{context}: a decoded delta must reserialize to decodable bytes");
            Assert.That(reloaded, Is.EqualTo(delta), $"{context}: a decoded delta must round trip");
        }

        private static byte[] RandomBytes(XorShift random, int minLength, int maxLength)
        {
            int length = minLength + random.NextInt(maxLength - minLength + 1);
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = random.NextByte();
            }

            return bytes;
        }

        private static byte[] ClonePrefix(byte[] bytes, int length)
        {
            var clone = new byte[length];
            Array.Copy(bytes, clone, length);
            return clone;
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

        // Deterministic xorshift64; System.Random is not pinned across
        // frameworks, and the fuzz corpus must be reproducible from the seed.
        private sealed class XorShift
        {
            private ulong _state;

            internal XorShift(ulong seed)
            {
                _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
            }

            internal ulong Next()
            {
                ulong x = _state;
                x ^= x << 13;
                x ^= x >> 7;
                x ^= x << 17;
                _state = x;
                return x;
            }

            internal int NextInt(int maxExclusive)
            {
                return (int)(Next() % (ulong)maxExclusive);
            }

            internal byte NextByte()
            {
                return (byte)(Next() >> 56);
            }
        }
    }
}
