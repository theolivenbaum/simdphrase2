using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimdPhrase2.ComparativeBench;

BenchmarkOptions options;
try
{
    options = BenchmarkOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage: SimdPhrase2.ComparativeBench [options]");
    Console.Error.WriteLine("  --docs N            synthetic corpus size (default 50000)");
    Console.Error.WriteLine("  --queries N         queries per task category (default 100)");
    Console.Error.WriteLine("  --warmup N          warm-up rounds (default 2)");
    Console.Error.WriteLine("  --rounds N          measured rounds, best wins (default 5)");
    Console.Error.WriteLine("  --topn N            hits collected per query (legacy only; default 10)");
    Console.Error.WriteLine("  --index-threads N   indexing threads (legacy only; default = CPU count)");
    Console.Error.WriteLine("  --search-threads N  concurrent-search threads (default = CPU count)");
    Console.Error.WriteLine("  --ram-mb N          IndexWriter RAM buffer MB (legacy only; default 256)");
    Console.Error.WriteLine("  --force-merge N     force-merge before search (default 1)");
    Console.Error.WriteLine("  --facets-docs N     documents for the facets phase (legacy only; default 50000)");
    Console.Error.WriteLine("  --simd-only         run only the SimdPhrase2 engine");
    Console.Error.WriteLine("  --legacy-only       run only the legacy Lucene.NET engine");
    return 1;
}

var source = new SyntheticDocSource(options.DocCount);
Console.WriteLine($"Corpus: {source.Description}");
Console.WriteLine($"Config: queries/cat={options.QueriesPerCategory}, warmup={options.WarmupRounds}, rounds={options.MeasuredRounds}, topN={options.TopN}, index-threads={options.IndexThreads}");
Console.WriteLine();

// Materialize the corpus once and share it byte-for-byte with both engines.
List<DocItem> docs = source.Documents().ToList();

var engineResults = new List<EngineResults>();

if (!options.LegacyOnly)
{
    using var engine = new SimdPhraseEngine();
    engineResults.Add(BenchHarness.Run(engine, docs, options));
    Console.WriteLine();
}

if (!options.SimdOnly)
{
    using var engine = new LegacyEngine();
    engineResults.Add(BenchHarness.Run(engine, docs, options));
    Console.WriteLine();
}

Report.Print(engineResults, options);
return 0;

namespace SimdPhrase2.ComparativeBench
{
    /// <summary>Renders the side-by-side comparison tables to the console.</summary>
    internal static class Report
    {
        public static void Print(IReadOnlyList<EngineResults> results, BenchmarkOptions options)
        {
            if (results.Count == 0)
            {
                return;
            }

            Console.WriteLine("================================================================");
            Console.WriteLine(" RESULTS");
            Console.WriteLine("================================================================");
            Console.WriteLine();

            PrintIndexing(results);
            Console.WriteLine();
            PrintSearch(results);
            Console.WriteLine();
            PrintConcurrent(results);
            Console.WriteLine();
            PrintFacets(results);
        }

        private static EngineResults? Simd(IReadOnlyList<EngineResults> r) =>
            r.FirstOrDefault(e => e.Engine.Contains("simd", StringComparison.OrdinalIgnoreCase));

        private static EngineResults? Legacy(IReadOnlyList<EngineResults> r) =>
            r.FirstOrDefault(e => e.Engine.Contains("legacy", StringComparison.OrdinalIgnoreCase));

        private static void PrintIndexing(IReadOnlyList<EngineResults> results)
        {
            Console.WriteLine("Indexing");
            Console.WriteLine("--------");
            foreach (EngineResults e in results)
            {
                if (e.Indexing is { } s)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"  {e.Engine,-26} {s.DocsPerSecond,12:N0} docs/s   {s.MegabytesPerSecond,8:N2} MB/s   force-merge {s.ForceMergeSeconds,6:N2}s"));
                }
            }
        }

        private static void PrintSearch(IReadOnlyList<EngineResults> results)
        {
            EngineResults? simdE = Simd(results);
            EngineResults? legacyE = Legacy(results);

            Console.WriteLine("Search throughput (best-of-N QPS, higher is better)");
            Console.WriteLine("---------------------------------------------------");
            if (simdE is not null && legacyE is not null)
            {
                Console.WriteLine($"  {"Category",-20} {"simd QPS",14} {"legacy QPS",14}   {"simd/legacy",12}");
                foreach (string cat in Categories.All)
                {
                    bool hasSimd = simdE.SearchQps.TryGetValue(cat, out double sq);
                    bool hasLegacy = legacyE.SearchQps.TryGetValue(cat, out double lq);
                    if (!hasSimd && !hasLegacy)
                    {
                        continue;
                    }
                    string simdCol = hasSimd ? FormattableString.Invariant($"{sq,14:N0}") : $"{"(disabled)",14}";
                    string ratio = (hasSimd && hasLegacy && lq > 0)
                        ? FormattableString.Invariant($"{sq / lq,11:N2}x")
                        : "-";
                    Console.WriteLine(FormattableString.Invariant($"  {cat,-20} {simdCol} {lq,14:N0}   {ratio,12}"));
                }
            }
            else
            {
                EngineResults only = simdE ?? legacyE!;
                Console.WriteLine($"  {"Category",-20} {"QPS",14}");
                foreach (string cat in Categories.All)
                {
                    if (only.SearchQps.TryGetValue(cat, out double q))
                    {
                        Console.WriteLine(FormattableString.Invariant($"  {cat,-20} {q,14:N0}"));
                    }
                }
            }
        }

        private static void PrintConcurrent(IReadOnlyList<EngineResults> results)
        {
            Console.WriteLine("Concurrent search");
            Console.WriteLine("-----------------");
            foreach (EngineResults e in results)
            {
                if (e.Concurrent is { } c)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"  {e.Engine,-26} {c.AggregateQps,12:N0} qps on {c.Threads,3} threads   speedup {c.Speedup,6:N2}x   efficiency {c.Efficiency,5:P0}"));
                }
            }
        }

        private static void PrintFacets(IReadOnlyList<EngineResults> results)
        {
            Console.WriteLine("Facets (taxonomy facet counting over match-all)");
            Console.WriteLine("-----------------------------------------------");
            foreach (EngineResults e in results)
            {
                if (e.Facets is { } f)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"  {e.Engine,-26} {f.CountsPerSecond,12:N0} counts/s   latency {f.CountLatencyMs,7:N3} ms   dims {f.DimValues}"));
                }
                else
                {
                    Console.WriteLine($"  {e.Engine,-26} not supported by this engine");
                }
            }
        }
    }
}
