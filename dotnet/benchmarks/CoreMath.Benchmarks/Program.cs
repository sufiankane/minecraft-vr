using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace Cubeglass.CoreMath.Benchmarks
{
    public static class Program
    {
        public static void Main()
        {
            _ = BenchmarkRunner.Run<AbiVersionBenchmarks>();
        }
    }

    [SimpleJob(warmupCount: 1, iterationCount: 3)]
    [MemoryDiagnoser]
    public class AbiVersionBenchmarks
    {
        [Benchmark]
        [SuppressMessage(
            "Performance",
            "CA1822:Mark members as static",
            Justification = "BenchmarkDotNet requires instance benchmark methods.")]
        public int Value()
        {
            return AbiVersion.Value;
        }
    }
}
