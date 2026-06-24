using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SimdPhrase2.ComparativeBench;

/// <summary>
/// One search implementation under test. Each engine knows how to build an index from the shared
/// corpus and execute a single round of each benchmark; the timing (warm-up + best-of-N) lives in
/// <see cref="BenchHarness"/> so both engines are measured identically.
/// </summary>
internal interface IBenchEngine : IDisposable
{
    /// <summary>Display name, e.g. "SimdPhrase2" or "Lucene.NET 4.8 (legacy)".</summary>
    string Name { get; }

    /// <summary>
    /// Whether this engine can run the taxonomy-facets phase. SimdPhrase2 is a pure text phrase
    /// engine with no faceting, so it returns <c>false</c> and the harness skips the phase entirely
    /// rather than reporting a misleading zero.
    /// </summary>
    bool SupportsFacets { get; }

    /// <summary>Builds the search index and returns indexing throughput.</summary>
    IndexStats BuildSearchIndex(IReadOnlyList<DocItem> docs, BenchmarkOptions options);

    /// <summary>The task categories that actually produced queries for this index.</summary>
    IReadOnlyList<string> Categories { get; }

    /// <summary>Number of queries in a category (0 if the category is empty / unsupported).</summary>
    int CategoryQueryCount(string category);

    /// <summary>Runs every query in one category once; returns the summed hit count.</summary>
    long RunCategoryRound(string category, int topN);

    /// <summary>Total number of queries across all categories (used by the concurrent phase).</summary>
    int TaskCount { get; }

    /// <summary>Runs the full task set once (single slice); returns the summed hit count.</summary>
    long RunAllTasksRound(int topN);

    /// <summary>Builds the taxonomy-faceted index used by the facets phase. Only called when
    /// <see cref="SupportsFacets"/> is true.</summary>
    void BuildFacetIndex(IReadOnlyList<DocItem> docs, BenchmarkOptions options);

    /// <summary>Counts facets over a match-all query once; returns the number of dim values. Only
    /// called when <see cref="SupportsFacets"/> is true.</summary>
    int CountFacetsRound();
}

/// <summary>
/// Implementation-agnostic timing harness. Runs warm-up rounds, then the best (highest QPS) of N
/// measured rounds — the same convention luceneutil uses to filter out GC / JIT noise.
/// </summary>
internal static class BenchHarness
{
    public static EngineResults Run(IBenchEngine engine, IReadOnlyList<DocItem> docs, BenchmarkOptions options)
    {
        var results = new EngineResults { Engine = engine.Name };

        Console.WriteLine($"[{engine.Name}] building search index ({docs.Count} docs)...");
        results.Indexing = engine.BuildSearchIndex(docs, options);

        Console.WriteLine($"[{engine.Name}] search phase...");
        foreach (string category in engine.Categories)
        {
            int count = engine.CategoryQueryCount(category);
            if (count == 0)
            {
                continue;
            }
            double qps = BestQps(options, count, () => engine.RunCategoryRound(category, options.TopN));
            results.SearchQps[category] = qps;
        }

        Console.WriteLine($"[{engine.Name}] concurrent phase...");
        results.Concurrent = RunConcurrent(engine, options);

        if (engine.SupportsFacets)
        {
            Console.WriteLine($"[{engine.Name}] facets phase...");
            engine.BuildFacetIndex(docs, options);
            int dims = 0;
            double facetCps = BestQps(options, 1, () => { dims = engine.CountFacetsRound(); return dims; });
            // BestQps divides by the work unit count (1), so facetCps is counts/sec for one facet count op.
            results.Facets = new FacetsResult(facetCps, dims);
        }
        else
        {
            Console.WriteLine($"[{engine.Name}] facets phase... not supported by this engine, skipped");
        }

        return results;
    }

    /// <summary>Best-of-N QPS for an operation that performs <paramref name="workUnits"/> queries per round.</summary>
    private static double BestQps(BenchmarkOptions options, int workUnits, Func<long> round)
    {
        for (int w = 0; w < options.WarmupRounds; w++)
        {
            round();
        }

        double best = 0;
        for (int r = 0; r < options.MeasuredRounds; r++)
        {
            var sw = Stopwatch.StartNew();
            round();
            sw.Stop();
            double qps = workUnits / sw.Elapsed.TotalSeconds;
            if (qps > best)
            {
                best = qps;
            }
        }
        return best;
    }

    private static ConcurrentResult RunConcurrent(IBenchEngine engine, BenchmarkOptions options)
    {
        int threads = options.SearchThreads > 0 ? options.SearchThreads : Environment.ProcessorCount;
        int tasks = engine.TaskCount;

        for (int w = 0; w < options.WarmupRounds; w++)
        {
            engine.RunAllTasksRound(options.TopN);
        }

        double singleThreadQps = 0;
        for (int r = 0; r < options.MeasuredRounds; r++)
        {
            var sw = Stopwatch.StartNew();
            engine.RunAllTasksRound(options.TopN);
            sw.Stop();
            double qps = tasks / sw.Elapsed.TotalSeconds;
            if (qps > singleThreadQps)
            {
                singleThreadQps = qps;
            }
        }

        double bestQps = 0;
        for (int r = 0; r < options.MeasuredRounds; r++)
        {
            var sw = Stopwatch.StartNew();
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ =>
            {
                engine.RunAllTasksRound(options.TopN);
            });
            sw.Stop();
            double qps = (long)tasks * threads / sw.Elapsed.TotalSeconds;
            if (qps > bestQps)
            {
                bestQps = qps;
            }
        }

        return new ConcurrentResult(threads, bestQps, singleThreadQps);
    }
}
