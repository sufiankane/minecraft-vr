using System;
using System.Collections.Generic;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Mesh.Tests
{
    // I1: a corrupted save can carry any block id in 1..0xFFFE. Decoding a
    // delta, applying it to a world and meshing the result must not throw:
    // the voxel registry lookup is total and resolves an unknown id to the
    // documented placeholder definition, so the frame tick cannot die on a
    // save that names a block this build does not know.
    [TestFixture]
    public sealed class UnknownBlockIdTests
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly BlockId Poison = new BlockId(65000);
        private static readonly GreedyMesher Greedy = new GreedyMesher();
        private static readonly CulledMesher Culled = new CulledMesher();

        [Test]
        public void DecodedUnknownIdAppliesAndMeshesThroughBothMeshers()
        {
            World world = WorldWithDecodedPoison(out ChunkSnapshot snapshot);

            Assert.That(snapshot.Get(new Int3(8, 8, 8)), Is.EqualTo(Poison), "the raw id must survive apply");

            MeshData culled = Culled.Build(snapshot, NeighbourSnapshot.Empty, BlockRegistry.Default);
            Assert.That(culled.IndexCount, Is.EqualTo(36), "the placeholder cube emits six quads");
            culled.Release();

            MeshData greedy = Greedy.Build(snapshot, NeighbourSnapshot.Empty, BlockRegistry.Default);
            Assert.That(greedy.IndexCount, Is.EqualTo(36), "the placeholder cube emits six quads");
            greedy.Release();

            Assert.That(world.Get(new Int3(8, 8, 8)), Is.EqualTo(Poison));
        }

        [Test]
        public void EveryUnknownIdInTheCodecRangeMeshesWithoutThrowing()
        {
            uint state = 20261004u;
            for (int sample = 0; sample < 256; sample++)
            {
                state = (state * 1664525u) + 1013904223u;
                var id = new BlockId((ushort)(6 + (state % 65529u)));
                World world = WorldWithDecoded(id, new Int3(4, 4, 4), out ChunkSnapshot snapshot);
                Assert.That(snapshot.Get(new Int3(4, 4, 4)), Is.EqualTo(id));

                MeshData culled = Culled.Build(snapshot, NeighbourSnapshot.Empty, BlockRegistry.Default);
                Assert.That(culled.IndexCount, Is.EqualTo(36), $"id {id.Value}");
                culled.Release();

                MeshData greedy = Greedy.Build(snapshot, NeighbourSnapshot.Empty, BlockRegistry.Default);
                Assert.That(greedy.IndexCount, Is.EqualTo(36), $"id {id.Value}");
                greedy.Release();

                Assert.That(world.Get(new Int3(4, 4, 4)), Is.EqualTo(id));
            }
        }

        private static World WorldWithDecodedPoison(out ChunkSnapshot snapshot)
        {
            return WorldWithDecoded(Poison, new Int3(8, 8, 8), out snapshot);
        }

        private static World WorldWithDecoded(BlockId id, Int3 local, out ChunkSnapshot snapshot)
        {
            var delta = new ChunkDelta(
                Origin,
                new Dictionary<Int3, BlockId> { [local] = id });
            byte[] payload = ChunkDeltaCodec.Serialize(delta);
            Assert.That(ChunkDeltaCodec.TryDeserialize(payload, out ChunkDelta? decoded), Is.True);
            Assert.That(decoded, Is.Not.Null);

            var world = new World();
            world.LoadChunk(new Chunk(Origin));
            foreach (KeyValuePair<Int3, BlockId> edit in decoded!.Edits)
            {
                EditResult result = world.Apply(
                    new EditCommand(ChunkMath.ToWorld(Origin, edit.Key), BlockId.Air, edit.Value, 0L));
                Assert.That(result, Is.EqualTo(EditResult.Applied), $"setup apply at {edit.Key}");
            }

            snapshot = world.TryGetChunk(Origin)!.Snapshot();
            return world;
        }
    }
}
