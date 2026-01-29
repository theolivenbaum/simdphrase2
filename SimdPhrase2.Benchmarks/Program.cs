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
            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "Validate");

            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
            Directory.CreateDirectory(tempPath);

            int n = 10000;
            Console.WriteLine($"Validating hit counts for N={n}...");
            var generator = new DataGenerator(42, 10000, 1.0);
            var docs = generator.GenerateDocuments(n);
            
            var idToDoc = docs.ToDictionary(v => (int)v.docId, v => v.content);

            var luceneResults = new List<int>();
            var simdResults = new List<int>();
            var naiveResults = new List<int>();

            using (var lucene = new LuceneService(Path.Combine(tempPath, $"lucene_val_{n}")))
            using (var simd = new SimdPhraseService(Path.Combine(tempPath, $"simd_val_{n}"), forceNaive: false))
            using (var naive = new SimdPhraseService(Path.Combine(tempPath, $"simd_naive_val_{n}"), forceNaive: true))
            {
                lucene.Index(docs);
                lucene.PrepareSearcher();

                simd.Index(docs);
                simd.PrepareSearcher();

                naive.Index(docs);
                naive.PrepareSearcher();

                int lTotal = 0;
                int sTotal = 0;
                int nTotal = 0;

                // Single Term
                for (int i = 0; i < 10; i++)
                {
                    luceneResults.Clear();
                    simdResults.Clear();
                    naiveResults.Clear();
                    var q = generator.GetRandomTerm();
                    lTotal += lucene.Search(q, luceneResults);
                    sTotal += simd.Search(q,simdResults);
                    nTotal += naive.Search(q, naiveResults);
                    PrintIfDifferent(idToDoc, luceneResults, simdResults, q);
                    PrintIfDifferent(idToDoc, luceneResults, naiveResults, q);
                }

                Console.WriteLine($"Single Term Hits: Lucene={lTotal}, SimdPhrase={sTotal} -> {(lTotal == sTotal ? "MATCH" : "MISMATCH")}");
                Console.WriteLine($"Single Term Hits: Lucene={lTotal}, Naive SimdPhrase={nTotal} -> {(lTotal == nTotal ? "MATCH" : "MISMATCH")}");

                // Phrase 2
                lTotal = 0; sTotal = 0; nTotal = 0;
                
                for (int i = 0; i < 10; i++)
                {
                    luceneResults.Clear();
                    simdResults.Clear();
                    naiveResults.Clear();
                    var q = generator.GetRandomPhrase(2);
                    lTotal += lucene.Search(q, luceneResults);
                    sTotal += simd.Search(q, simdResults);
                    nTotal += naive.Search(q, naiveResults);
                    PrintIfDifferent(idToDoc, luceneResults, simdResults, q);
                    PrintIfDifferent(idToDoc, luceneResults, naiveResults, q);
                }

                Console.WriteLine($"Phrase(2) Hits: Lucene={lTotal}, SimdPhrase={sTotal} -> {(lTotal == sTotal ? "MATCH" : "MISMATCH")}");
                Console.WriteLine($"Single Term Hits: Lucene={lTotal}, Naive SimdPhrase={nTotal} -> {(lTotal == nTotal ? "MATCH" : "MISMATCH")}");

                // Phrase 3
                lTotal = 0; sTotal = 0; nTotal = 0;

                for (int i = 0; i < 10; i++)
                {
                    luceneResults.Clear();
                    simdResults.Clear();
                    naiveResults.Clear();
                    var q = generator.GetRandomPhrase(3);
                    lTotal += lucene.Search(q, luceneResults);
                    sTotal += simd.Search(q,simdResults);
                    nTotal += naive.Search(q, naiveResults);
                    PrintIfDifferent(idToDoc, luceneResults, simdResults, q);
                    PrintIfDifferent(idToDoc, luceneResults, naiveResults, q);
                }
                Console.WriteLine($"Phrase(3) Hits: Lucene={lTotal}, SimdPhrase={sTotal} -> {(lTotal == sTotal ? "MATCH" : "MISMATCH")}");
                Console.WriteLine($"Single Term Hits: Lucene={lTotal}, Naive SimdPhrase={nTotal} -> {(lTotal == nTotal ? "MATCH" : "MISMATCH")}");
            }

            // Clean up
            if (Directory.Exists(Path.Combine(tempPath,$"lucene_val_{n}"))) Directory.Delete(Path.Combine(tempPath,$"lucene_val_{n}"), true);
            if (Directory.Exists(Path.Combine(tempPath,$"simd_val_{n}"))) Directory.Delete(Path.Combine(tempPath,$"simd_val_{n}"), true);
        }

        private static void PrintIfDifferent(Dictionary<int, string> idToDoc, List<int> luceneResults, List<int> simdResults, string query)
        {
            if (luceneResults.Count != simdResults.Count)
            {
                luceneResults.Sort();
                simdResults.Sort();
                Console.WriteLine("--------------------------------");
                Console.WriteLine("Search Term: " + query);
                Console.WriteLine("\nFound by Lucene, not by SIMD2: \n" + string.Join("\n", luceneResults.Except(simdResults).Select(v => $"\t{v} '{idToDoc[v]}'")));
                Console.WriteLine("\nFound by SIMD2, not by Lucene:  \n" + string.Join("\n", simdResults.Except(luceneResults).Select(v => $"\t{v} '{idToDoc[v]}'")));
                Console.WriteLine("--------------------------------\n\n");
            }
        }
    }
}
