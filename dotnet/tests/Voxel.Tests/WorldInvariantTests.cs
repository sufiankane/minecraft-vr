using System;
using System.Collections.Generic;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.FSharp.Core;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    // Model-based properties for the single edit path: every accepted command
    // must be reproducible from the same start, and every rejected command must
    // leave the world untouched. The hash is a private test helper; the chunk
    // hash helper used by the generator tests is a separate, later addition.
    [TestFixture]
    public sealed class WorldInvariantTests
    {
        private const ulong Seed = 20261001UL;
        private const int MaxTests = 300;
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly BlockId Stone = new BlockId(1);
        private static readonly BlockId Dirt = new BlockId(2);

        [Test]
        public void AcceptedCommandsReplayToTheSameWorldAndMatchTheModel()
        {
            Property property = Prop.ForAll(
                CommandSequenceArbitrary(),
                ReplayMatchesModel);

            Check.One("model-based edit replay", DeterministicConfig(), property);
        }

        [Test]
        public void RejectedCommandsNeverChangeTheWorldHash()
        {
            Property property = Prop.ForAll(
                CommandSequenceArbitrary(),
                RejectionsAreNoOps);

            Check.One("rejected apply is a no-op", DeterministicConfig(), property);
        }

        private static bool ReplayMatchesModel(List<EditCommand> commands)
        {
            World world = NewWorld();
            Dictionary<Int3, BlockId> model = NewModel();
            var accepted = new List<EditCommand>(commands.Count);

            foreach (EditCommand command in commands)
            {
                bool loaded = world.IsLoaded(command.Cell);
                BlockId current = world.Get(command.Cell);
                EditResult result = world.Apply(in command);

                if (loaded && current == command.Expected)
                {
                    if (result != EditResult.Applied)
                    {
                        return false;
                    }

                    accepted.Add(command);
                    model[ChunkMath.ToLocal(command.Cell)] = command.New;
                }
                else if (result != EditResult.Rejected)
                {
                    return false;
                }
            }

            World replay = NewWorld();
            foreach (EditCommand command in accepted)
            {
                replay.Apply(in command);
            }

            ulong worldHash = HashWorld(world);
            return worldHash == HashWorld(replay) && worldHash == HashModel(model);
        }

        private static bool RejectionsAreNoOps(List<EditCommand> commands)
        {
            World world = NewWorld();

            foreach (EditCommand command in commands)
            {
                ulong before = HashWorld(world);
                EditResult result = world.Apply(in command);
                if (result == EditResult.Rejected && HashWorld(world) != before)
                {
                    return false;
                }
            }

            return true;
        }

        private static World NewWorld()
        {
            return TestWorld.CreateFilledWorld(Origin, Baseline);
        }

        private static Dictionary<Int3, BlockId> NewModel()
        {
            var model = new Dictionary<Int3, BlockId>(ChunkMath.ChunkSize * ChunkMath.ChunkSize * ChunkMath.ChunkSize);
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        var local = new Int3(x, y, z);
                        model[local] = Baseline(local);
                    }
                }
            }

            return model;
        }

        private static BlockId Baseline(Int3 local)
        {
            if (local.Y == 0)
            {
                return Stone;
            }

            return (local.X + local.Y + local.Z) % 5 == 0 ? Dirt : BlockId.Air;
        }

        private static ulong HashWorld(World world)
        {
            Chunk chunk = world.TryGetChunk(Origin) ?? throw new InvalidOperationException("The origin chunk must be loaded.");
            ulong hash = FnvOffset;
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        Mix(ref hash, chunk.Get(new Int3(x, y, z)));
                    }
                }
            }

            return hash;
        }

        private static ulong HashModel(Dictionary<Int3, BlockId> model)
        {
            ulong hash = FnvOffset;
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        Mix(ref hash, model[new Int3(x, y, z)]);
                    }
                }
            }

            return hash;
        }

        private static void Mix(ref ulong hash, BlockId block)
        {
            hash ^= block.Value;
            hash *= FnvPrime;
        }

        private static Arbitrary<List<EditCommand>> CommandSequenceArbitrary()
        {
            Gen<Int3> cells =
                from x in Gen.Choose(-8, 23)
                from y in Gen.Choose(-8, 23)
                from z in Gen.Choose(-8, 23)
                select new Int3(x, y, z);

            Gen<EditCommand> commands =
                from cell in cells
                from expected in Gen.Choose(0, 1)
                from next in Gen.Choose(0, 3)
                from tick in Gen.Choose(0, 1000000)
                select new EditCommand(cell, new BlockId((ushort)expected), new BlockId((ushort)next), tick);

            return Arb.From(Gen.ListOf(commands));
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
