using System;

namespace Cubeglass.Voxel.Tests
{
    /// <summary>
    /// A deterministic <see cref="IWorldGenerator"/> over a test-supplied script.
    /// </summary>
    internal sealed class ScriptedGenerator : IWorldGenerator
    {
        private readonly Func<ChunkCoord, long, Chunk> _script;

        internal ScriptedGenerator(Func<ChunkCoord, long, Chunk> script)
        {
            ArgumentNullException.ThrowIfNull(script);
            _script = script;
        }

        public Chunk Generate(ChunkCoord c, long seed)
        {
            return _script(c, seed);
        }
    }

    /// <summary>
    /// Test helper: builds worlds and chunks with scripted contents so tests do
    /// not depend on generation, meshing or persistence.
    /// </summary>
    internal static class TestWorld
    {
        internal static Chunk CreateChunk(ChunkCoord coord, params (Int3 Local, BlockId Block)[] blocks)
        {
            var chunk = new Chunk(coord);
            foreach ((Int3 local, BlockId block) in blocks)
            {
                chunk.Set(local, block);
            }

            return chunk;
        }

        internal static World CreateLoaded(ChunkCoord coord, params (Int3 Local, BlockId Block)[] blocks)
        {
            var world = new World();
            world.LoadChunk(CreateChunk(coord, blocks));
            return world;
        }

        internal static World CreateFilledWorld(ChunkCoord coord, Func<Int3, BlockId> fill)
        {
            ArgumentNullException.ThrowIfNull(fill);

            var chunk = new Chunk(coord);
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        var local = new Int3(x, y, z);
                        chunk.Set(local, fill(local));
                    }
                }
            }

            var world = new World();
            world.LoadChunk(chunk);
            return world;
        }

        internal static World CreateWithGenerator(Func<ChunkCoord, long, Chunk> script)
        {
            return new World(new ScriptedGenerator(script));
        }
    }
}
