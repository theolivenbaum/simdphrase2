using BenchmarkDotNet.Running;

namespace SimdPhrase2.Benchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var summary = BenchmarkRunner.Run<SearchBenchmark>();
        }
    }
}
