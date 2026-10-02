using System;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;

namespace Cubeglass.Mesh.Tests
{
    /// <summary>
    /// Mesh-test fixtures: a small block registry (stone opaque, glass
    /// non-opaque, grass with distinct atlas tiles) and helpers that build
    /// snapshots and neighbours through the public world API, because the
    /// internal chunk setters are not visible to this assembly.
    /// </summary>
    internal static class TestChunks
    {
        internal static readonly BlockId Stone = new BlockId(1);
        internal static readonly BlockId Glass = new BlockId(2);
        internal static readonly BlockId Grass = new BlockId(3);

        internal static readonly IBlockRegistry Registry = BlockRegistry.Parse(BlocksJson);

        private const string BlocksJson = @"[
  { ""Id"": 0, ""Name"": ""Air"",   ""Solid"": false, ""Opaque"": false, ""Hardness"": 0.0, ""AtlasIndexTop"": 0, ""AtlasIndexFront"": 0, ""AtlasIndexSide"": 0 },
  { ""Id"": 1, ""Name"": ""Stone"", ""Solid"": true,  ""Opaque"": true,  ""Hardness"": 1.5, ""AtlasIndexTop"": 1, ""AtlasIndexFront"": 1, ""AtlasIndexSide"": 1 },
  { ""Id"": 2, ""Name"": ""Glass"", ""Solid"": true,  ""Opaque"": false, ""Hardness"": 0.3, ""AtlasIndexTop"": 2, ""AtlasIndexFront"": 2, ""AtlasIndexSide"": 2 },
  { ""Id"": 3, ""Name"": ""Grass"", ""Solid"": true,  ""Opaque"": true,  ""Hardness"": 0.6, ""AtlasIndexTop"": 3, ""AtlasIndexFront"": 4, ""AtlasIndexSide"": 4 }
]";

        /// <summary>Builds a chunk containing exactly the given blocks.</summary>
        internal static Chunk CreateChunk(ChunkCoord coord, params (Int3 Local, BlockId Block)[] blocks)
        {
            var world = new World();
            var chunk = new Chunk(coord);
            world.LoadChunk(chunk);
            foreach ((Int3 local, BlockId block) in blocks)
            {
                Apply(world, coord, local, block);
            }

            return chunk;
        }

        /// <summary>Builds an immutable snapshot containing exactly the given blocks.</summary>
        internal static ChunkSnapshot Snapshot(ChunkCoord coord, params (Int3 Local, BlockId Block)[] blocks)
        {
            return CreateChunk(coord, blocks).Snapshot();
        }

        /// <summary>Builds an immutable snapshot with every cell set to <paramref name="block"/>.</summary>
        internal static ChunkSnapshot FilledSnapshot(ChunkCoord coord, BlockId block)
        {
            var world = new World();
            var chunk = new Chunk(coord);
            world.LoadChunk(chunk);
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        Apply(world, coord, new Int3(x, y, z), block);
                    }
                }
            }

            return chunk.Snapshot();
        }

        /// <summary>
        /// Builds a neighbour snapshot around <paramref name="center"/> from the
        /// given neighbour cells; neighbours may receive several cells.
        /// </summary>
        internal static NeighbourSnapshot Neighbours(
            ChunkCoord center,
            params (ChunkCoord Coord, Int3 Local, BlockId Block)[] neighbourBlocks)
        {
            var world = new World();
            world.LoadChunk(new Chunk(center));
            foreach ((ChunkCoord coord, Int3 local, BlockId block) in neighbourBlocks)
            {
                if (world.TryGetChunk(coord) is null)
                {
                    world.LoadChunk(new Chunk(coord));
                }

                Apply(world, coord, local, block);
            }

            return world.CreateNeighbourSnapshot(center);
        }

        private static void Apply(World world, ChunkCoord coord, Int3 local, BlockId block)
        {
            var command = new EditCommand(ChunkMath.ToWorld(coord, local), BlockId.Air, block, 0L);
            EditResult result = world.Apply(in command);
            if (result != EditResult.Applied)
            {
                throw new InvalidOperationException(
                    $"Test setup failed to place block {block.Value} at local {local} in chunk {coord}.");
            }
        }
    }
}
