using System;
using System.Diagnostics;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;

namespace Cubeglass.Mesh.Benchmarks
{
    /// <summary>
    /// Mesh benchmark entry point: BenchmarkDotNet by default, or the budget
    /// harness with <c>dotnet run -- p95</c>.
    /// </summary>
    /// <remarks>
    /// The <c>p95</c> mode is the ADR-0007 budget harness: after 100 warm-up
    /// builds it runs 2,000 build/release cycles per chunk shape and prints
    /// p50/p95/p99 in milliseconds. It always exits 0 — the numbers are
    /// recorded and compared against the 2.0 ms budget in
    /// <c>docs/perf/s3.md</c>, never asserted by a test.
    /// </remarks>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "p95")
            {
                P95Measurement.Run();
                return 0;
            }

            _ = BenchmarkRunner.Run<MeshBenchmarks>();
            return 0;
        }
    }

    [SimpleJob(warmupCount: 1, iterationCount: 3)]
    [MemoryDiagnoser]
    public class MeshBenchmarks
    {
        private readonly GreedyMesher _mesher = new GreedyMesher();

        private ChunkSnapshot _solid = null!;
        private ChunkSnapshot _terrain = null!;
        private ChunkSnapshot _checkerboard = null!;

        [GlobalSetup]
        public void Setup()
        {
            _solid = Fixtures.SolidChunk();
            _terrain = Fixtures.TerrainChunk();
            _checkerboard = Fixtures.CheckerboardChunk();
        }

        /// <summary>A fully solid 16^3 chunk: six merged quads.</summary>
        [Benchmark]
        public int GreedySolidChunk()
        {
            return Build(_solid);
        }

        /// <summary>The terrain seed 42 origin chunk.</summary>
        [Benchmark]
        public int GreedyTerrainChunk()
        {
            return Build(_terrain);
        }

        /// <summary>
        /// The R22 worst case: a 2x2x2 stone checkerboard whose per-cell AO
        /// variation keeps roughly 5,568 quads separate.
        /// </summary>
        [Benchmark]
        public int GreedyCheckerboardChunk()
        {
            return Build(_checkerboard);
        }

        private int Build(ChunkSnapshot chunk)
        {
            MeshData mesh = _mesher.Build(chunk, NeighbourSnapshot.Empty, Fixtures.Blocks);
            int quads = mesh.VertexCount / 4;
            mesh.Release();
            return quads;
        }
    }

    /// <summary>The ADR-0007 p95 budget harness (see <see cref="Program"/>).</summary>
    internal static class P95Measurement
    {
        private const int WarmupBuilds = 100;
        private const int MeasuredBuilds = 2_000;

        internal static void Run()
        {
            var mesher = new GreedyMesher();
            (string Name, ChunkSnapshot Chunk)[] cases =
            {
                ("GreedySolidChunk", Fixtures.SolidChunk()),
                ("GreedyTerrainChunk", Fixtures.TerrainChunk()),
                ("GreedyCheckerboardChunk", Fixtures.CheckerboardChunk()),
            };

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "Mesh p95 run: {0} warm-up builds then {1} build+release cycles per shape; times in ms.",
                WarmupBuilds,
                MeasuredBuilds));

            var samples = new double[MeasuredBuilds];
            foreach ((string name, ChunkSnapshot chunk) in cases)
            {
                Measure(mesher, name, chunk, samples);
            }
        }

        private static void Measure(GreedyMesher mesher, string name, ChunkSnapshot chunk, double[] samples)
        {
            long warmupSink = 0L;
            for (int i = 0; i < WarmupBuilds; i++)
            {
                MeshData mesh = mesher.Build(chunk, NeighbourSnapshot.Empty, Fixtures.Blocks);
                warmupSink += mesh.VertexCount;
                mesh.Release();
            }

            int quads = 0;
            long measuredSink = 0L;
            for (int i = 0; i < samples.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                MeshData mesh = mesher.Build(chunk, NeighbourSnapshot.Empty, Fixtures.Blocks);
                quads = mesh.VertexCount / 4;
                measuredSink += mesh.IndexCount;
                mesh.Release();
                long end = Stopwatch.GetTimestamp();
                samples[i] = (end - start) * 1000.0 / Stopwatch.Frequency;
            }

            if (warmupSink == 0L || measuredSink == 0L)
            {
                throw new InvalidOperationException("the benchmark fixture meshed nothing");
            }

            Array.Sort(samples);
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: quads={1} p50={2:F3} ms p95={3:F3} ms p99={4:F3} ms",
                name,
                quads,
                Percentile(samples, 0.50),
                Percentile(samples, 0.95),
                Percentile(samples, 0.99)));
        }

        /// <summary>Nearest-rank percentile of an ascending sample array.</summary>
        private static double Percentile(double[] sorted, double percentile)
        {
            int rank = (int)Math.Ceiling(percentile * sorted.Length) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        }
    }

    /// <summary>Deterministic benchmark chunks and the block registry they use.</summary>
    internal static class Fixtures
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly BlockId Stone = new BlockId(1);

        internal static readonly IBlockRegistry Blocks = BlockRegistry.Parse(BlocksJson);

        private const string BlocksJson = @"[
  { ""Id"": 0, ""Name"": ""Air"",   ""Solid"": false, ""Opaque"": false, ""Hardness"": 0.0, ""AtlasIndexTop"": 0, ""AtlasIndexFront"": 0, ""AtlasIndexSide"": 0 },
  { ""Id"": 1, ""Name"": ""Stone"", ""Solid"": true,  ""Opaque"": true,  ""Hardness"": 1.5, ""AtlasIndexTop"": 1, ""AtlasIndexFront"": 1, ""AtlasIndexSide"": 1 },
  { ""Id"": 2, ""Name"": ""Dirt"",  ""Solid"": true,  ""Opaque"": true,  ""Hardness"": 0.5, ""AtlasIndexTop"": 2, ""AtlasIndexFront"": 2, ""AtlasIndexSide"": 2 },
  { ""Id"": 3, ""Name"": ""Grass"", ""Solid"": true,  ""Opaque"": true,  ""Hardness"": 0.6, ""AtlasIndexTop"": 3, ""AtlasIndexFront"": 4, ""AtlasIndexSide"": 4 }
]";

        internal static ChunkSnapshot SolidChunk()
        {
            var world = new World();
            var chunk = new Chunk(Origin);
            world.LoadChunk(chunk);
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        Apply(world, Origin, new Int3(x, y, z), Stone);
                    }
                }
            }

            return chunk.Snapshot();
        }

        internal static ChunkSnapshot TerrainChunk()
        {
            return new TerrainGenerator().Generate(Origin, 42L).Snapshot();
        }

        internal static ChunkSnapshot CheckerboardChunk()
        {
            var world = new World();
            var chunk = new Chunk(Origin);
            world.LoadChunk(chunk);
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        if (((((x >> 1) + (y >> 1)) + (z >> 1)) & 1) == 0)
                        {
                            Apply(world, Origin, new Int3(x, y, z), Stone);
                        }
                    }
                }
            }

            return chunk.Snapshot();
        }

        private static void Apply(World world, ChunkCoord coord, Int3 local, BlockId block)
        {
            var command = new EditCommand(ChunkMath.ToWorld(coord, local), BlockId.Air, block, 0L);
            if (world.Apply(in command) != EditResult.Applied)
            {
                throw new InvalidOperationException("benchmark fixture failed to place a block");
            }
        }
    }
}
