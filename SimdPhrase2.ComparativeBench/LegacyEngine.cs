using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Spans;
using Lucene.Net.Store;
using Lucene.Net.Util;
using LDoc = Lucene.Net.Documents.Document;

namespace SimdPhrase2.ComparativeBench;

/// <summary>
/// Benchmark engine backed by the legacy Lucene.NET 4.8 packages. Everything in this file binds to
/// the <c>Lucene.Net.*</c> namespace root, never the <c>SimdPhrase2.*</c> library. This is the
/// reference implementation the comparison measures SimdPhrase2 against; it runs the full
/// luceneutil task matrix (term, boolean, phrase, sloppy phrase, span-near, wildcard, prefix,
/// fuzzy, numeric range, PK lookup, sort, count and facets).
/// </summary>
internal sealed class LegacyEngine : IBenchEngine
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    private sealed class BenchTask
    {
        public required string Category { get; init; }
        public required Query Query { get; init; }
        public Sort? Sort { get; init; }
        public bool CountOnly { get; init; }
    }

    private readonly List<IDisposable> _disposables = new();
    private IndexSearcher _searcher = null!;
    private readonly Dictionary<string, List<BenchTask>> _byCategory = new();
    private readonly List<BenchTask> _allTasks = new();

    private RAMDirectory? _facetIndexDir;
    private RAMDirectory? _facetTaxoDir;
    private DirectoryReader? _facetReader;
    private Lucene.Net.Facet.Taxonomy.TaxonomyReader? _facetTaxoReader;
    private IndexSearcher? _facetSearcher;
    private Lucene.Net.Facet.FacetsConfig? _facetConfig;

    public string Name => "Lucene.NET 4.8 (legacy)";

    public bool SupportsFacets => true;

    public IndexStats BuildSearchIndex(IReadOnlyList<DocItem> docs, BenchmarkOptions options)
    {
        var dir = new RAMDirectory();
        _disposables.Add(dir);

        var config = new IndexWriterConfig(Version, new StandardAnalyzer(Version))
        {
            MergeScheduler = new ConcurrentMergeScheduler(),
            RAMBufferSizeMB = options.RamBufferMb,
        };

        long contentBytes = 0;
        var sw = Stopwatch.StartNew();
        double mergeSeconds = 0;
        using (var writer = new IndexWriter(dir, config))
        {
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.IndexThreads) };
            Parallel.ForEach(docs, parallel, item =>
            {
                writer.AddDocument(ToDocument(item));
                Interlocked.Add(ref contentBytes, item.Body.Length + item.Title.Length);
            });
            writer.Commit();
            sw.Stop();

            if (options.ForceMergeSegments > 0)
            {
                var mergeSw = Stopwatch.StartNew();
                writer.ForceMerge(options.ForceMergeSegments);
                writer.Commit();
                mergeSw.Stop();
                mergeSeconds = mergeSw.Elapsed.TotalSeconds;
            }
        }

        DirectoryReader reader = DirectoryReader.Open(dir);
        _disposables.Add(reader);
        _searcher = new IndexSearcher(reader);

        BuildTasks(reader, docs, options);

        return new IndexStats(docs.Count, sw.Elapsed.TotalSeconds, mergeSeconds, contentBytes);
    }

    private static LDoc ToDocument(in DocItem item)
    {
        var doc = new LDoc();
        doc.Add(new StringField(Fields.Id, item.Id.ToString(CultureInfo.InvariantCulture), Field.Store.YES));
        doc.Add(new TextField(Fields.Title, item.Title, Field.Store.NO));
        doc.Add(new TextField(Fields.Body, item.Body, Field.Store.NO));
        // 4.8 has no points; an indexed Int32Field powers NumericRangeQuery.
        doc.Add(new Int32Field(Fields.Rand, item.Rand, Field.Store.NO));
        doc.Add(new NumericDocValuesField(Fields.RandDv, item.Rand));
        return doc;
    }

    public IReadOnlyList<string> Categories => SimdPhrase2.ComparativeBench.Categories.All;

    public int CategoryQueryCount(string category) =>
        _byCategory.TryGetValue(category, out List<BenchTask>? list) ? list.Count : 0;

    public int TaskCount => _allTasks.Count;

    public long RunCategoryRound(string category, int topN) =>
        _byCategory.TryGetValue(category, out List<BenchTask>? list) ? Execute(list, topN) : 0;

    public long RunAllTasksRound(int topN) => Execute(_allTasks, topN);

    private long Execute(List<BenchTask> tasks, int topN)
    {
        long hits = 0;
        foreach (BenchTask task in tasks)
        {
            if (task.CountOnly)
            {
                var collector = new TotalHitCountCollector();
                _searcher.Search(task.Query, collector);
                hits += collector.TotalHits;
            }
            else if (task.Sort is not null)
            {
                hits += _searcher.Search(task.Query, topN, task.Sort).TotalHits;
            }
            else
            {
                hits += _searcher.Search(task.Query, topN).TotalHits;
            }
        }
        return hits;
    }

    // ---- task building (mirrors luceneutil categories) ----

    private void BuildTasks(DirectoryReader reader, IReadOnlyList<DocItem> docs, BenchmarkOptions options)
    {
        int n = reader.NumDocs;
        (List<string> high, List<string> med, List<string> low) = BucketTerms(reader, n);
        var rng = new Random(20240601);
        int per = options.QueriesPerCategory;

        AddTerms("Term", med, per, rng);
        AddTerms("HighTerm", high, per, rng);
        AddTerms("MedTerm", med, per, rng);
        AddTerms("LowTerm", low, per, rng);

        AddBoolean("AndHighHigh", high, high, Occur.MUST, per, rng);
        AddBoolean("AndHighMed", high, med, Occur.MUST, per, rng);
        AddBoolean("OrHighHigh", high, high, Occur.SHOULD, per, rng);
        AddBoolean("OrHighMed", high, med, Occur.SHOULD, per, rng);
        AddBoolean("OrHighRare", high, low, Occur.SHOULD, per, rng);
        AddBoolean3("And3Terms", med, Occur.MUST, per, rng);
        AddBoolean3("Or3Terms", med, Occur.SHOULD, per, rng);

        List<(string A, string B)> bigrams = SampleBigrams(docs, high.Concat(med).ToHashSet(), 120);
        AddPhrases("Phrase", bigrams, 0, per, rng);
        AddPhrases("SloppyPhrase", bigrams, 2, per, rng);
        AddSpanNear("SpanNear", bigrams, 4, per, rng);

        AddWildcard("Wildcard", high.Concat(med).ToList(), per, rng);
        AddPrefix("Prefix3", high.Concat(med).ToList(), per, rng);
        AddFuzzy("Fuzzy1", med, 1, per, rng);
        AddFuzzy("Fuzzy2", med, 2, per, rng);

        AddIntNrq("IntNRQ", per, rng);
        AddPkLookup("PKLookup", n, per, rng);
        AddSort("TermDVSort", high, per, rng);

        AddTerms("CountTerm", med, per, rng, countOnly: true);
        AddBoolean("CountAndHighHigh", high, high, Occur.MUST, per, rng, countOnly: true);
        AddBoolean("CountOrHighHigh", high, high, Occur.SHOULD, per, rng, countOnly: true);
        AddBoolean("CountOrHighMed", high, med, Occur.SHOULD, per, rng, countOnly: true);
        AddPhrases("CountPhrase", bigrams, 0, per, rng, countOnly: true);
    }

    private static (List<string> High, List<string> Med, List<string> Low) BucketTerms(DirectoryReader reader, int n)
    {
        var terms = new List<(string Text, int Df)>();
        Terms? bodyTerms = MultiFields.GetTerms(reader, Fields.Body);
        if (bodyTerms is not null)
        {
            TermsEnum te = bodyTerms.GetEnumerator();
            while (te.MoveNext())
            {
                terms.Add((te.Term.Utf8ToString(), te.DocFreq));
            }
        }

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

    private void AddTerms(string cat, List<string> src, int per, Random rng, bool countOnly = false)
    {
        if (src.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            Add(new BenchTask { Category = cat, Query = new TermQuery(new Term(Fields.Body, Pick(src, rng))), CountOnly = countOnly });
        }
    }

    private void AddBoolean(string cat, List<string> a, List<string> b, Occur occur, int per, Random rng, bool countOnly = false)
    {
        if (a.Count == 0 || b.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            var q = new BooleanQuery();
            q.Add(new TermQuery(new Term(Fields.Body, Pick(a, rng))), occur);
            q.Add(new TermQuery(new Term(Fields.Body, Pick(b, rng))), occur);
            Add(new BenchTask { Category = cat, Query = q, CountOnly = countOnly });
        }
    }

    private void AddBoolean3(string cat, List<string> src, Occur occur, int per, Random rng)
    {
        if (src.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            var q = new BooleanQuery();
            q.Add(new TermQuery(new Term(Fields.Body, Pick(src, rng))), occur);
            q.Add(new TermQuery(new Term(Fields.Body, Pick(src, rng))), occur);
            q.Add(new TermQuery(new Term(Fields.Body, Pick(src, rng))), occur);
            Add(new BenchTask { Category = cat, Query = q });
        }
    }

    private void AddPhrases(string cat, List<(string A, string B)> bigrams, int slop, int per, Random rng, bool countOnly = false)
    {
        if (bigrams.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            var (a, b) = bigrams[rng.Next(bigrams.Count)];
            var pq = new PhraseQuery { Slop = slop };
            pq.Add(new Term(Fields.Body, a));
            pq.Add(new Term(Fields.Body, b));
            Add(new BenchTask { Category = cat, Query = pq, CountOnly = countOnly });
        }
    }

    private void AddSpanNear(string cat, List<(string A, string B)> bigrams, int slop, int per, Random rng)
    {
        if (bigrams.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            var (a, b) = bigrams[rng.Next(bigrams.Count)];
            var clauses = new SpanQuery[]
            {
                new SpanTermQuery(new Term(Fields.Body, a)),
                new SpanTermQuery(new Term(Fields.Body, b)),
            };
            Add(new BenchTask { Category = cat, Query = new SpanNearQuery(clauses, slop, inOrder: true) });
        }
    }

    private void AddWildcard(string cat, List<string> src, int per, Random rng)
    {
        if (src.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            string t = Pick(src, rng);
            int keep = Math.Min(4, Math.Max(1, t.Length - 1));
            Add(new BenchTask { Category = cat, Query = new WildcardQuery(new Term(Fields.Body, t[..keep] + "*")) });
        }
    }

    private void AddPrefix(string cat, List<string> src, int per, Random rng)
    {
        if (src.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            string t = Pick(src, rng);
            int keep = Math.Min(3, t.Length);
            Add(new BenchTask { Category = cat, Query = new PrefixQuery(new Term(Fields.Body, t[..keep])) });
        }
    }

    private void AddFuzzy(string cat, List<string> src, int maxEdits, int per, Random rng)
    {
        if (src.Count == 0) return;
        for (int i = 0; i < per; i++)
        {
            Add(new BenchTask { Category = cat, Query = new FuzzyQuery(new Term(Fields.Body, Pick(src, rng)), maxEdits) });
        }
    }

    private void AddIntNrq(string cat, int per, Random rng)
    {
        for (int i = 0; i < per; i++)
        {
            int lo = rng.Next(0, 950_000);
            int hi = lo + 50_000;
            Add(new BenchTask { Category = cat, Query = NumericRangeQuery.NewInt32Range(Fields.Rand, lo, hi, minInclusive: true, maxInclusive: true) });
        }
    }

    private void AddPkLookup(string cat, int n, int per, Random rng)
    {
        for (int i = 0; i < per; i++)
        {
            string id = rng.Next(0, n).ToString(CultureInfo.InvariantCulture);
            Add(new BenchTask { Category = cat, Query = new TermQuery(new Term(Fields.Id, id)) });
        }
    }

    private void AddSort(string cat, List<string> src, int per, Random rng)
    {
        if (src.Count == 0) return;
        var sort = new Sort(new SortField(Fields.RandDv, SortFieldType.INT64));
        for (int i = 0; i < per; i++)
        {
            Add(new BenchTask { Category = cat, Query = new TermQuery(new Term(Fields.Body, Pick(src, rng))), Sort = sort });
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
                var pq = new PhraseQuery();
                pq.Add(new Term(Fields.Body, a));
                pq.Add(new Term(Fields.Body, b));
                var collector = new TotalHitCountCollector();
                _searcher.Search(pq, collector);
                if (collector.TotalHits > 0)
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

    // ---- facets ----

    public void BuildFacetIndex(IReadOnlyList<DocItem> docs, BenchmarkOptions options)
    {
        int docCount = Math.Min(docs.Count, options.FacetsDocs);
        _facetIndexDir = new RAMDirectory();
        _facetTaxoDir = new RAMDirectory();
        _facetConfig = new Lucene.Net.Facet.FacetsConfig();

        var iwc = new IndexWriterConfig(Version, new StandardAnalyzer(Version))
        {
            RAMBufferSizeMB = options.RamBufferMb,
        };

        using (var writer = new IndexWriter(_facetIndexDir, iwc))
        using (var taxoWriter = new Lucene.Net.Facet.Taxonomy.Directory.DirectoryTaxonomyWriter(_facetTaxoDir, OpenMode.CREATE))
        {
            foreach (DocItem item in docs.Take(docCount))
            {
                var doc = new LDoc();
                doc.Add(new TextField(Fields.Body, item.Body, Field.Store.NO));
                doc.Add(new Lucene.Net.Facet.FacetField(Fields.FacetDim, item.Category));
                writer.AddDocument(_facetConfig.Build(taxoWriter, doc));
            }
            writer.Commit();
            taxoWriter.Commit();
        }

        _facetReader = DirectoryReader.Open(_facetIndexDir);
        _facetTaxoReader = new Lucene.Net.Facet.Taxonomy.Directory.DirectoryTaxonomyReader(_facetTaxoDir);
        _facetSearcher = new IndexSearcher(_facetReader);
    }

    public int CountFacetsRound()
    {
        var fc = new Lucene.Net.Facet.FacetsCollector();
        _facetSearcher!.Search(new MatchAllDocsQuery(), fc);
        var facets = new Lucene.Net.Facet.Taxonomy.FastTaxonomyFacetCounts(_facetTaxoReader!, _facetConfig!, fc);
        Lucene.Net.Facet.FacetResult? top = facets.GetTopChildren(10, Fields.FacetDim);
        return top?.ChildCount ?? 0;
    }

    public void Dispose()
    {
        _facetReader?.Dispose();
        _facetTaxoReader?.Dispose();
        _facetIndexDir?.Dispose();
        _facetTaxoDir?.Dispose();
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            _disposables[i].Dispose();
        }
    }
}
