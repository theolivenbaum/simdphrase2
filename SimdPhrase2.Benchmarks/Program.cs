using BenchmarkDotNet.Running;
using System.Reflection;

namespace SimdPhrase2.Benchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
        }
    }
}
