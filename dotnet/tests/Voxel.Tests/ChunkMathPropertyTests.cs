using FsCheck;
using FsCheck.Fluent;
using Microsoft.FSharp.Core;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    // FsCheck 3.4: a fixed Rnd seed plus a bounded Int3 generator makes each run
    // reproducible; failures report the replay seed so they can be repeated.
    [TestFixture]
    public sealed class ChunkMathPropertyTests
    {
        private const ulong Seed = 20261001UL;
        private const int MaxTests = 1000;
        private const int Bound = 1000;

        [Test]
        public void ToWorldOfToChunkAndToLocalReturnsTheOriginalCell()
        {
            Property property = Prop.ForAll(
                CellArbitrary(),
                (Int3 cell) => ChunkMath.ToWorld(ChunkMath.ToChunk(cell), ChunkMath.ToLocal(cell)) == cell);

            Check.One("world-to-chunk-to-world round trip", DeterministicConfig(), property);
        }

        [Test]
        public void ToLocalComponentsAlwaysLieInTheChunkBounds()
        {
            Property property = Prop.ForAll(
                CellArbitrary(),
                (Int3 cell) =>
                {
                    Int3 local = ChunkMath.ToLocal(cell);
                    return local.X >= 0 && local.X < ChunkMath.ChunkSize
                        && local.Y >= 0 && local.Y < ChunkMath.ChunkSize
                        && local.Z >= 0 && local.Z < ChunkMath.ChunkSize;
                });

            Check.One("local range invariant", DeterministicConfig(), property);
        }

        private static Arbitrary<Int3> CellArbitrary()
        {
            return Arb.From(
                from x in Gen.Choose(-Bound, Bound)
                from y in Gen.Choose(-Bound, Bound)
                from z in Gen.Choose(-Bound, Bound)
                select new Int3(x, y, z));
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
