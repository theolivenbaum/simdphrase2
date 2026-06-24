using System;
using System.Globalization;

namespace SimdPhrase2.ComparativeBench;

/// <summary>
/// Command-line configuration for the comparative benchmark. A trimmed version of the knobs that
/// matter in luceneutil's <c>nightlyBench.py</c> (corpus size, thread counts, search iterations),
/// kept implementation-agnostic so both engines run with identical parameters.
/// </summary>
internal sealed class BenchmarkOptions
{
    /// <summary>Number of documents in the synthetic corpus.</summary>
    public int DocCount { get; private set; } = 50_000;

    /// <summary>Indexing threads. Applies to the legacy Lucene.NET engine; SimdPhrase2 ingests
    /// single-threaded (it batches and spills internally), so it ignores this knob.</summary>
    public int IndexThreads { get; private set; } = Environment.ProcessorCount;

    /// <summary><c>IndexWriter</c> RAM buffer (MB). Applies to the legacy engine only.</summary>
    public double RamBufferMb { get; private set; } = 256.0;

    /// <summary>Number of queries executed per task category, per measured round.</summary>
    public int QueriesPerCategory { get; private set; } = 100;

    /// <summary>Warm-up rounds (results discarded) before timing.</summary>
    public int WarmupRounds { get; private set; } = 2;

    /// <summary>Measured rounds; the best (highest QPS) round is reported, matching luceneutil.</summary>
    public int MeasuredRounds { get; private set; } = 5;

    /// <summary>Top-N hits collected per query. Applies to the legacy engine; SimdPhrase2 always
    /// returns the full matching doc-id set, so it ignores this knob.</summary>
    public int TopN { get; private set; } = 10;

    /// <summary>Force-merge to this many segments before searching (-1 = leave as-is). For
    /// SimdPhrase2 any positive value triggers a single <c>ForceMerge()</c> pass.</summary>
    public int ForceMergeSegments { get; private set; } = 1;

    /// <summary>Threads for the concurrent-search phase (0 = CPU count).</summary>
    public int SearchThreads { get; private set; }

    /// <summary>Facets phase: documents to index (legacy engine only).</summary>
    public int FacetsDocs { get; private set; } = 50_000;

    /// <summary>Run only the SimdPhrase2 engine (skip the legacy comparison).</summary>
    public bool SimdOnly { get; private set; }

    /// <summary>Run only the legacy Lucene.NET engine.</summary>
    public bool LegacyOnly { get; private set; }

    public static BenchmarkOptions Parse(string[] args)
    {
        var o = new BenchmarkOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            switch (key)
            {
                case "--simd-only": o.SimdOnly = true; continue;
                case "--legacy-only": o.LegacyOnly = true; continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for option: {key}");
            }
            string val = args[++i];
            switch (key)
            {
                case "--docs": o.DocCount = ParseInt(val); break;
                case "--index-threads": o.IndexThreads = ParseInt(val); break;
                case "--ram-mb": o.RamBufferMb = double.Parse(val, CultureInfo.InvariantCulture); break;
                case "--queries": o.QueriesPerCategory = ParseInt(val); break;
                case "--warmup": o.WarmupRounds = ParseInt(val); break;
                case "--rounds": o.MeasuredRounds = ParseInt(val); break;
                case "--topn": o.TopN = ParseInt(val); break;
                case "--force-merge": o.ForceMergeSegments = ParseInt(val); break;
                case "--search-threads": o.SearchThreads = ParseInt(val); break;
                case "--facets-docs": o.FacetsDocs = ParseInt(val); break;
                default: throw new ArgumentException($"Unknown option: {key}");
            }
        }
        return o;
    }

    private static int ParseInt(string s) => int.Parse(s, CultureInfo.InvariantCulture);
}
