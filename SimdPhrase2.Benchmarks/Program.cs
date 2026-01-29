using BenchmarkDotNet.Running;
using System.Reflection;
using System;
using System.Linq;

namespace SimdPhrase2.Benchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            if (args.Contains("validate"))
            {
                ValidateCounts();
                return;
            }
            BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
        }

        private static void ValidateCounts()
        {
            int n = 10000;
            Console.WriteLine($"Validating hit counts for N={n}...");
            var generator = new DataGenerator(42, 10000, 1.0);
            var docs = generator.GenerateDocuments(n);

            using var lucene = new LuceneService($"lucene_val_{n}");
            lucene.Index(docs);
            lucene.PrepareSearcher();

            using var simd = new SimdPhraseService($"simd_val_{n}");
            simd.Index(docs);
            simd.PrepareSearcher();

            int lTotal = 0;
            int sTotal = 0;

            // Single Term
            for(int i=0; i<10; i++)
            {
                var q = generator.GetRandomTerm();
                lTotal += lucene.Search(q);
                sTotal += simd.Search(q);
            }
            Console.WriteLine($"Single Term Hits: Lucene={lTotal}, SimdPhrase={sTotal} -> {(lTotal == sTotal ? "MATCH" : "MISMATCH")}");

            // Phrase 2
            lTotal = 0; sTotal = 0;
            for(int i=0; i<10; i++)
            {
                var q = generator.GetRandomPhrase(2);
                lTotal += lucene.Search(q);
                sTotal += simd.Search(q);
            }
            Console.WriteLine($"Phrase(2) Hits: Lucene={lTotal}, SimdPhrase={sTotal} -> {(lTotal == sTotal ? "MATCH" : "MISMATCH")}");

             // Phrase 3
            lTotal = 0; sTotal = 0;
            for(int i=0; i<10; i++)
            {
                var q = generator.GetRandomPhrase(3);
                lTotal += lucene.Search(q);
                sTotal += simd.Search(q);
            }
            Console.WriteLine($"Phrase(3) Hits: Lucene={lTotal}, SimdPhrase={sTotal} -> {(lTotal == sTotal ? "MATCH" : "MISMATCH")}");

            // Clean up
            if (System.IO.Directory.Exists($"lucene_val_{n}")) System.IO.Directory.Delete($"lucene_val_{n}", true);
            if (System.IO.Directory.Exists($"simd_val_{n}")) System.IO.Directory.Delete($"simd_val_{n}", true);
        }
    }
}
