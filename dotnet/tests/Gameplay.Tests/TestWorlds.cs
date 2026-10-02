using System;
using Cubeglass.Voxel;

namespace Cubeglass.Gameplay.Tests
{
    /// <summary>
    /// Small deterministic worlds for player-movement tests.
    /// </summary>
    /// <remarks>
    /// Cells are written through the public <see cref="IWorld.Apply"/> path
    /// because <c>Chunk.Set</c> is internal to Cubeglass.Voxel and only its own
    /// test assembly can see it. A chunk at the origin is loaded once, then
    /// every write starts from air so <c>Expected</c> matches.
    /// </remarks>
    internal static class TestWorlds
    {
        internal static readonly BlockId Stone = new BlockId(1);

        /// <summary>No loaded chunk: every cell reads as air.</summary>
        internal static World CreateEmpty()
        {
            return new World();
        }

        /// <summary>A full 16x16 floor at y = 0, so its top face is y = 1.</summary>
        internal static World CreateFloor()
        {
            var world = new World();
            world.LoadChunk(new Chunk(new ChunkCoord(0, 0, 0)));
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int x = 0; x < ChunkMath.ChunkSize; x++)
                {
                    Set(world, x, 0, z, Stone);
                }
            }

            return world;
        }

        /// <summary>
        /// A floor plus a one-cell-thick wall at <paramref name="wallX"/> in
        /// the y = 1 layer (the layer the player body occupies while grounded).
        /// </summary>
        internal static World CreateFloorWithWallX(int wallX)
        {
            World world = CreateFloor();
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                Set(world, wallX, 1, z, Stone);
            }

            return world;
        }

        private static void Set(IWorld world, int x, int y, int z, BlockId block)
        {
            if (world.Apply(new EditCommand(new Int3(x, y, z), BlockId.Air, block, 0)) != EditResult.Applied)
            {
                throw new InvalidOperationException("Test world setup was rejected.");
            }
        }
    }
}
