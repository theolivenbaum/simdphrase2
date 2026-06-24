using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SimdPhrase2;
using SimdPhrase2.Queries;

namespace SimdPhrase2.ComparativeBench;

/// <summary>
/// Benchmark engine backed by the SimdPhrase2 library. Everything in this file binds to the
/// <c>SimdPhrase2.*</c> namespace root, never <c>Lucene.Net.*</c>. SimdPhrase2 is a text phrase
/// engine: it indexes one free-text field and answers exact-phrase, single-term and boolean
/// (AND/OR/NOT) queries over it. Query categories that depend on capabilities it does not provide
/// (sloppy phrase, span-near, wildcard/prefix/fuzzy term expansion, numeric range, primary-key
/// point lookup, sort-by-doc-value and taxonomy facets) are intentionally <b>disabled</b> here —
/// the building blocks are kept in <see cref="BuildTasks"/> as commented-out calls so the parity
/// with <c>LegacyEngine</c> stays visible, and the corresponding categories simply produce zero
/// queries (the harness then skips them).
/// </summary>
internal sealed class SimdPhraseEngine : IBenchEngine
{
    /// <summary>
    /// A single benchmark query expressed against the SimdPhrase2 public API. <see cref="Run"/>
    /// takes the <see cref="Searcher"/> to use (so the concurrent phase can hand each thread its own
    /// instance) and returns the matching hit count.
    /// </summary>
    private sealed class BenchTask
    {
        public required string Category { get; init; }
        public required Func<Searcher, long> Run { get; init; }
    }

    private readonly string _indexDir;
    private Searcher _searcher = null!;
    private readonly Dictionary<string, List<BenchTask>> _byCategory = new();
    private readonly List<BenchTask> _allTasks = new();

    // Per-thread Searcher for the concurrent phase. SimdPhrase2's SegmentReader loads posting lists
    // through a single stateful Seek+Read stream per Searcher, so one Searcher is not safe to share
    // across threads; each thread instead opens its own read-only view of the same on-disk index.
    private ThreadLocal<Searcher> _threadSearchers = null!;

    public string Name => "SimdPhrase2";

    // SimdPhrase2 has no taxonomy faceting; the facets phase is disabled (see BuildFacetIndex).
    public bool SupportsFacets => false;

    public SimdPhraseEngine()
    {
        _indexDir = Path.Combine(
            Path.GetTempPath(), "SimdPhrase2.ComparativeBench", "idx-" + Guid.NewGuid().ToString("N"));
    }

    public IndexStats BuildSearchIndex(IReadOnlyList<DocItem> docs, BenchmarkOptions options)
    {
        if (Directory.Exists(_indexDir))
        {
            Directory.Delete(_indexDir, true);
        }
        Directory.CreateDirectory(_indexDir);

        long contentBytes = 0;
        foreach (DocItem item in docs)
        {
            contentBytes += item.Body.Length + item.Title.Length;
        }

        var sw = Stopwatch.StartNew();
        double mergeSeconds = 0;
        using (var indexer = new Indexer(_indexDir, CommonTokensConfig.None))
        {
            // SimdPhrase2 ingests on a single thread (it batches and spills internally), so
            // options.IndexThreads does not apply. Only the body field is indexed: SimdPhrase2 has
            // no separate stored numeric / id / facet fields, and none of the categories that would
            // use them are enabled for this engine. Index(...) commits internally.
            indexer.Index(docs.Select(d => (d.Body, (uint)d.Id)));
            sw.Stop();

            if (options.ForceMergeSegments > 0)
            {
                var mergeSw = Stopwatch.StartNew();
                indexer.ForceMerge();
                mergeSw.Stop();
                mergeSeconds = mergeSw.Elapsed.TotalSeconds;
            }
        }

        _searcher = new Searcher(_indexDir);
        _threadSearchers = new ThreadLocal<Searcher>(() => new Searcher(_indexDir), trackAllValues: true);

        BuildTasks(docs, options);

        return new IndexStats(docs.Count, sw.Elapsed.TotalSeconds, mergeSeconds, contentBytes);
    }

    public IReadOnlyList<string> Categories => SimdPhrase2.ComparativeBench.Categories.All;

    public int CategoryQueryCount(string category) =>
        _byCategory.TryGetValue(category, out List<BenchTask>? list) ? list.Count : 0;

    public int TaskCount => _allTasks.Count;

    public long RunCategoryRound(string category, int topN) =>
        _byCategory.TryGetValue(category, out List<BenchTask>? list) ? Execute(list) : 0;

    public long RunAllTasksRound(int topN) => Execute(_allTasks);

    // topN has no analogue: SimdPhrase2's Search / SearchBoolean always return the full matching
    // doc-id set, so the "hit count" is the size of that set (and the count-only categories are
    // identical to their search counterparts).
    private long Execute(List<BenchTask> tasks)
    {
        Searcher s = _threadSearchers.Value!;
        long hits = 0;
        foreach (BenchTask task in tasks)
        {
            hits += task.Run(s);
        }
        return hits;
    }

    // ---- task building (mirrors luceneutil categories; disabled ones kept as comments) ----

    private void BuildTasks(IReadOnlyList<DocItem> docs, BenchmarkOptions options)
    {
        int n = docs.Count;
        (List<string> high, List<string> med, List<string> low) = BucketTerms(docs);
        var rng = new Random(20240601);
        int per = options.QueriesPerCategory;

        // --- Supported: single-term queries (a phrase of one token). ---
        AddTerms("Term", med, per, rng);
        AddTerms("HighTerm", high, per, rng);
        AddTerms("MedTerm", med, per, rng);
        AddTerms("LowTerm", low, per, rng);

        // --- Supported: boolean AND / OR over the SimdPhrase2 Query AST. ---
        AddBoolean("AndHighHigh", high, high, isAnd: true, per, rng);
        AddBoolean("AndHighMed", high, med, isAnd: true, per, rng);
        AddBoolean("OrHighHigh", high, high, isAnd: false, per, rng);
        AddBoolean("OrHighMed", high, med, isAnd: false, per, rng);
        AddBoolean("OrHighRare", high, low, isAnd: false, per, rng);
        AddBoolean3("And3Terms", med, isAnd: true, per, rng);
        AddBoolean3("Or3Terms", med, isAnd: false, per, rng);

        // --- Supported: exact phrase (slop 0) — SimdPhrase2's core capability. ---
        List<(string A, string B)> bigrams = SampleBigrams(docs, high.Concat(med).ToHashSet(), 120);
        AddPhrases("Phrase", bigrams, per, rng);

        // --- DISABLED (no capability yet): SimdPhrase2 enforces phrase adjacency (slop 0), so there
        //     is no sloppy-phrase or span-near query. ---
        // AddSloppyPhrase("SloppyPhrase", bigrams, 2, per, rng);
        // AddSpanNear("SpanNear", bigrams, 4, per, rng);

        // --- DISABLED (no capability yet): no wildcard / prefix / fuzzy term expansion. ---
        // AddWildcard("Wildcard", high.Concat(med).ToList(), per, rng);
        // AddPrefix("Prefix3", high.Concat(med).ToList(), per, rng);
        // AddFuzzy("Fuzzy1", med, 1, per, rng);
        // AddFuzzy("Fuzzy2", med, 2, per, rng);

        // --- DISABLED (no capability yet): no numeric / points indexing → no numeric range query. ---
        // AddIntNrq("IntNRQ", per, rng);
        // --- DISABLED (no capability yet): no stored primary-key field / point lookup; documents are
        //     addressed by the caller-supplied docId, not looked up by an indexed id term. ---
        // AddPkLookup("PKLookup", n, per, rng);
        // --- DISABLED (no capability yet): no doc-values / sort-by-field. ---
        // AddSort("TermDVSort", high, per, rng);

        // --- Supported: count-only variants. SimdPhrase2 returns the full doc-id set, so a "count"
        //     is just its length; these mirror the legacy count categories for parity. ---
        AddTerms("CountTerm", med, per, rng);
        AddBoolean("CountAndHighHigh", high, high, isAnd: true, per, rng);
        AddBoolean("CountOrHighHigh", high, high, isAnd: false, per, rng);
        AddBoolean("CountOrHighMed", high, med, isAnd: false, per, rng);
        AddPhrases("CountPhrase", bigrams, per, rng);
    }

    // Buckets terms into high / medium / low document-frequency tiers. The legacy engine reads these
    // from its own index via the terms enumerator; SimdPhrase2 does not expose term enumeration to
    // callers, so we compute identical buckets directly from the corpus body text. The synthetic
    // corpus is pure lowercase letters, which SimdPhrase2's BasicTokenizer tokenizes the same way as
    // Tokenize below, so the buckets line up with the actual index terms. Same thresholds as the
    // legacy engine.
    private static (List<string> High, List<string> Med, List<string> Low) BucketTerms(IReadOnlyList<DocItem> docs)
    {
        int n = docs.Count;
        var df = new Dictionary<string, int>();
        foreach (DocItem doc in docs)
        {
            foreach (string tok in Tokenize(doc.Body).Distinct())
            {
                df.TryGetValue(tok, out int c);
                df[tok] = c + 1;
            }
        }

        var terms = df.Select(kv => (Text: kv.Key, Df: kv.Value)).ToList();

        double highMin = 0.05 * n, medMin = 0.005 * n, medMax = 0.05 * n, lowMax = 0.005 * n;
        var high = terms.Where(t => t.Df >= highMin && t.Df <= 0.6 * n).OrderByDescending(t => t.Df).Take(200).Select(t => t.Text).ToList();
        var med = terms.Where(t => t.Df >= medMin && t.Df < medMax).OrderByDescending(t => t.Df).Take(200).Select(t => t.Text).ToList();
        var low = terms.Where(t => t.Df >= 5 && t.Df < lowMax).OrderByDescending(t => t.Df).Take(200).Select(t => t.Text).ToList();

        if (high.Count == 0) high = terms.OrderByDescending(t => t.Df).Take(50).Select(t => t.Text).ToList();
        if (med.Count == 0) med = high;
        if (low.Count == 0) low = med;
        return (high, med, low);
    }

    private void Add(BenchTask task)
    {
        if (!_byCategory.TryGetValue(task.Category, out List<BenchTask>? list))
        {
            list = new List<BenchTask>();
            _byCategory[task.Category] = list;
        }
        list.Add(task);
        _allTasks.Add(task);
    }

    private static string Pick(List<string> list, Random rng) => list[rng.Next(list.Count)];

    private void AddTerms(string cat, List<string> src, int per, Random rng)
    {
        if (src.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            string term = Pick(src, rng);
            Add(new BenchTask { Category = cat, Run = s => s.Search(term).Count });
        }
    }

    private void AddBoolean(string cat, List<string> a, List<string> b, bool isAnd, int per, Random rng)
    {
        if (a.Count == 0 || b.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            Query q = isAnd
                ? new AndQuery(new TermQuery(0, Pick(a, rng)), new TermQuery(0, Pick(b, rng)))
                : new OrQuery(new TermQuery(0, Pick(a, rng)), new TermQuery(0, Pick(b, rng)));
            Add(new BenchTask { Category = cat, Run = s => s.SearchBoolean(q).Count });
        }
    }

    private void AddBoolean3(string cat, List<string> src, bool isAnd, int per, Random rng)
    {
        if (src.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            var t0 = new TermQuery(0, Pick(src, rng));
            var t1 = new TermQuery(0, Pick(src, rng));
            var t2 = new TermQuery(0, Pick(src, rng));
            Query q = isAnd ? new AndQuery(t0, t1, t2) : new OrQuery(t0, t1, t2);
            Add(new BenchTask { Category = cat, Run = s => s.SearchBoolean(q).Count });
        }
    }

    private void AddPhrases(string cat, List<(string A, string B)> bigrams, int per, Random rng)
    {
        if (bigrams.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            var (a, b) = bigrams[rng.Next(bigrams.Count)];
            string phrase = a + " " + b;
            Add(new BenchTask { Category = cat, Run = s => s.Search(phrase).Count });
        }
    }

    private List<(string A, string B)> SampleBigrams(IReadOnlyList<DocItem> docs, HashSet<string> vocab, int want)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>();
        foreach (DocItem doc in docs.Take(2000))
        {
            string[] toks = Tokenize(doc.Body);
            for (int i = 0; i + 1 < toks.Length && result.Count < want; i++)
            {
                string a = toks[i], b = toks[i + 1];
                if (a.Length < 2 || b.Length < 2 || !vocab.Contains(a) || !vocab.Contains(b))
                {
                    continue;
                }
                if (!seen.Add(a + " " + b))
                {
                    continue;
                }
                if (_searcher.Search(a + " " + b).Count > 0)
                {
                    result.Add((a, b));
                }
            }
            if (result.Count >= want)
            {
                break;
            }
        }
        return result;
    }

    private static string[] Tokenize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(char.IsLetter(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    // ---- facets (disabled) ----
    //
    // SimdPhrase2 has no taxonomy faceting, so SupportsFacets is false and the harness never invokes
    // these. They throw rather than silently returning zero, to make an accidental call obvious.

    public void BuildFacetIndex(IReadOnlyList<DocItem> docs, BenchmarkOptions options) =>
        throw new NotSupportedException(
            "SimdPhrase2 has no taxonomy faceting; the facets phase is disabled (SupportsFacets == false).");

    public int CountFacetsRound() =>
        throw new NotSupportedException(
            "SimdPhrase2 has no taxonomy faceting; the facets phase is disabled (SupportsFacets == false).");

    public void Dispose()
    {
        _searcher?.Dispose();
        if (_threadSearchers is not null)
        {
            foreach (Searcher s in _threadSearchers.Values)
            {
                s.Dispose();
            }
            _threadSearchers.Dispose();
        }
        try
        {
            if (Directory.Exists(_indexDir))
            {
                Directory.Delete(_indexDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp index directory.
        }
    }
}
