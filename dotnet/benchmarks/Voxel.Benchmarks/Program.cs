using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Cubeglass.CoreMath;

namespace Cubeglass.Voxel.Benchmarks
{
    public static class Program
    {
        public static void Main()
        {
            _ = BenchmarkRunner.Run<VoxelBenchmarks>();
        }
    }

    [SimpleJob(warmupCount: 1, iterationCount: 3)]
    [MemoryDiagnoser]
    [JsonExporter]
    public class VoxelBenchmarks
    {
        private const int Seed = 42;
        private const int RayCount = 100_000;
        private const float MaxDistance = 32f;

        private readonly TerrainGenerator _generator = new TerrainGenerator();

        private World _world = null!;
        private Ray[] _rays = null!;
        private DdaRaycaster _raycaster = null!;

        [GlobalSetup]
        public void Setup()
        {
            _world = new World();
            _world.LoadChunk(_generator.Generate(new ChunkCoord(0, 0, 0), Seed));
            _raycaster = new DdaRaycaster();
            _rays = new Ray[RayCount];
            for (int i = 0; i < RayCount; i++)
            {
                float x = (i % ChunkMath.ChunkSize) + 0.5f;
                float z = ((i / ChunkMath.ChunkSize) % ChunkMath.ChunkSize) + 0.5f;
                _rays[i] = new Ray(new Vec3(x, 20.0, z), new Vec3(0.0, -1.0, 0.0));
            }
        }

        /// <summary>One full <see cref="TerrainGenerator"/> chunk at the origin.</summary>
        [Benchmark]
        [SuppressMessage(
            "Performance",
            "CA1822:Mark members as static",
            Justification = "BenchmarkDotNet requires instance benchmark methods.")]
        public Chunk ChunkGeneration()
        {
            return _generator.Generate(new ChunkCoord(0, 0, 0), Seed);
        }

        /// <summary>
        /// 100k downward rays across a loaded generated chunk. Reported per ray:
        /// <see cref="OperationsPerInvoke"/> divides the measured time by the ray count.
        /// </summary>
        [Benchmark(OperationsPerInvoke = RayCount)]
        [SuppressMessage(
            "Performance",
            "CA1822:Mark members as static",
            Justification = "BenchmarkDotNet requires instance benchmark methods.")]
        [SuppressMessage(
            "Performance",
            "CA1859:Use concrete types when possible for improved performance",
            Justification = "The benchmark must measure the frozen IRaycaster/IWorld interfaces consumers call.")]
        public int Raycast100k()
        {
            IRaycaster raycaster = _raycaster;
            IWorld world = _world;
            Ray[] rays = _rays;
            int hits = 0;
            for (int i = 0; i < rays.Length; i++)
            {
                RayHit? hit = raycaster.Cast(world, rays[i], MaxDistance);
                if (hit.HasValue)
                {
                    hits++;
                }
            }

            return hits;
        }
    }
}
