using BenchmarkDotNet.Running;
using System.Reflection;
using System;
using System.Collections.Generic;
using System.IO;
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

            // Phrase / Term Validation
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

                Console.WriteLine("\n--- Validating Phrase/Term ---");

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
                Console.WriteLine($"Single Term Hits: Lucene={lTotal}, SimdPhrase={sTotal}");

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
                Console.WriteLine($"Phrase(2) Hits: Lucene={lTotal}, SimdPhrase={sTotal}");
            }

            // Boolean Validation
             Console.WriteLine("\n--- Validating Boolean ---");
             using (var lucene = new LuceneService(Path.Combine(tempPath, $"lucene_bool_val_{n}")))
             using (var simd = new SimdPhraseService(Path.Combine(tempPath, $"simd_bool_val_{n}"), forceNaive: false))
             {
                 lucene.Index(docs);
                 lucene.PrepareSearcher();
                 simd.Index(docs);
                 simd.PrepareSearcher();

                 int lTotal = 0;
                 int sTotal = 0;
                 for (int i = 0; i < 10; i++)
                 {
                     luceneResults.Clear();
                     simdResults.Clear();
                     var q = generator.GetRandomBooleanQuery();
                     lTotal += lucene.SearchBoolean(q, luceneResults);
                     sTotal += simd.SearchBoolean(q, simdResults);
                     PrintIfDifferent(idToDoc, luceneResults, simdResults, q);
                 }
                 Console.WriteLine($"Boolean Hits: Lucene={lTotal}, SimdPhrase={sTotal}");
             }

             // BM25 Validation (Sanity Check)
             Console.WriteLine("\n--- Validating BM25 (Sanity Check) ---");
             using (var lucene = new LuceneService(Path.Combine(tempPath, $"lucene_bm25_val_{n}"), useBm25: true))
             using (var simd = new SimdPhraseService(Path.Combine(tempPath, $"simd_bm25_val_{n}"), forceNaive: false))
             {
                 lucene.Index(docs);
                 lucene.PrepareSearcher();
                 simd.Index(docs);
                 simd.PrepareSearcher();

                 int lTotal = 0;
                 int sTotal = 0;
                 for (int i = 0; i < 10; i++)
                 {
                     luceneResults.Clear();
                     simdResults.Clear();
                     var q = generator.GetRandomTerm();

                     // We just check if we get results, exact scoring match is unlikely due to implementation differences
                     lTotal += lucene.SearchBM25(q, 10, luceneResults);
                     sTotal += simd.SearchBM25(q, 10, simdResults);

                     // Just verify overlap exists if hits > 0
                     if (luceneResults.Count > 0 && simdResults.Count > 0)
                     {
                         var intersection = luceneResults.Intersect(simdResults).Count();
                         // Console.WriteLine($"Query: {q} | Lucene: {luceneResults.Count}, Simd: {simdResults.Count}, Overlap: {intersection}");
                     }
                 }
                 Console.WriteLine($"BM25 Queries executed. Total Hits returned (sum of top K): Lucene={lTotal}, SimdPhrase={sTotal}");
             }

            // Clean up
            try { Directory.Delete(tempPath, true); } catch {}
        }

        private static void PrintIfDifferent(Dictionary<int, string> idToDoc, List<int> luceneResults, List<int> simdResults, string query)
        {
            // For boolean/phrase, exact match is expected.
            // Sorting is required for SequenceEqual
            luceneResults.Sort();
            simdResults.Sort();

            if (!luceneResults.SequenceEqual(simdResults))
            {
                Console.WriteLine("--------------------------------");
                Console.WriteLine("MISMATCH for Query: " + query);
                Console.WriteLine($"Lucene Count: {luceneResults.Count}, Simd Count: {simdResults.Count}");

                var lExceptS = luceneResults.Except(simdResults).ToList();
                var sExceptL = simdResults.Except(luceneResults).ToList();

                if (lExceptS.Any())
                    Console.WriteLine("\nFound by Lucene, not by SIMD2: \n" + string.Join("\n", lExceptS.Take(5).Select(v => $"\t{v} '{idToDoc[v]}'")));

                if (sExceptL.Any())
                    Console.WriteLine("\nFound by SIMD2, not by Lucene:  \n" + string.Join("\n", sExceptL.Take(5).Select(v => $"\t{v} '{idToDoc[v]}'")));

                Console.WriteLine("--------------------------------\n");
            }
        }
    }
}
