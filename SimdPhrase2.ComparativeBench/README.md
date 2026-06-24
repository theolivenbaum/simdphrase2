# SimdPhrase2.ComparativeBench

A macro-benchmark that runs the **search**, **concurrent-search** and (legacy-only)
**facets** workloads against **two** implementations side-by-side and prints a
comparison:

- **SimdPhrase2** — this repository's SIMD phrase-search library, referenced as a
  project (`../SimdPhrase2`). Namespace root `SimdPhrase2.*`.
- **Lucene.NET 4.8 (legacy)** — the original Apache Lucene.NET port consumed from
  NuGet. Namespace root `Lucene.Net.*`.

It is a direct port of `Lucene.ComparativeBench` from the `lucene-sharp` repo
(which compares the new Lucene# port against legacy Lucene.NET). Here the
"new" engine slot is filled by **SimdPhrase2** instead, and the legacy
Lucene.NET engine is carried over essentially verbatim as the reference.

Both engines index the **same** synthetic corpus, build the **same** luceneutil
task categories, and are timed by the **same** best-of-N harness, so the numbers
are directly comparable.

## Why this is two engines, not one

The two implementations share field names and query *intent* but **not** their
APIs, so the code is intentionally duplicated (`SimdPhraseEngine.cs` vs
`LegacyEngine.cs`) rather than shared:

| Concern              | SimdPhrase2                                  | Lucene.NET 4.8 (legacy)                               |
|----------------------|----------------------------------------------|-------------------------------------------------------|
| Namespace root       | `SimdPhrase2.*`                              | `Lucene.Net.*`                                        |
| Index location       | on-disk index dir (`FileSystemStorage`)      | in-memory `RAMDirectory`                              |
| Indexing             | single-threaded `Indexer.Index(...)`         | multi-threaded `IndexWriter` + `Parallel.ForEach`     |
| Single term          | `Search("term")`                             | `TermQuery`                                           |
| Phrase (slop 0)      | `Search("a b")`                              | `PhraseQuery { Slop = 0 }`                            |
| Boolean AND/OR       | `SearchBoolean(AndQuery/OrQuery of TermQuery)` | `new BooleanQuery()` + `Add(..., Occur.MUST/SHOULD)`  |
| Count                | `Search(...).Count` (full doc-id set)        | `TotalHitCountCollector`                              |
| Concurrent search    | one `Searcher` per thread (`ThreadLocal`)    | shared thread-safe `IndexSearcher`                    |

## Disabled categories (SimdPhrase2 has no capability yet)

The harness runs the full luceneutil category matrix against the legacy engine.
SimdPhrase2 only implements the categories it can answer; the rest are
**disabled** — kept as commented-out calls in `SimdPhraseEngine.BuildTasks` so
the parity with `LegacyEngine` stays visible, and producing zero queries so the
harness skips them. In the search table they show `(disabled)` in the SimdPhrase2
column while still reporting the legacy QPS.

| Category                         | Status on SimdPhrase2 | Reason                                              |
|----------------------------------|-----------------------|-----------------------------------------------------|
| `Term`/`HighTerm`/`MedTerm`/`LowTerm` | ✅ enabled        | single-term phrase search                           |
| `AndHighHigh` … `Or3Terms`       | ✅ enabled            | boolean AND/OR over the Query AST                   |
| `Phrase`, `CountPhrase`          | ✅ enabled            | exact phrase (slop 0) — the core capability         |
| `Count*`                         | ✅ enabled            | result-set length (full enumeration)                |
| `SloppyPhrase`, `SpanNear`       | ❌ disabled           | only adjacency (slop 0); no slop / span proximity   |
| `Wildcard`, `Prefix3`, `Fuzzy1`, `Fuzzy2` | ❌ disabled  | no wildcard / prefix / fuzzy term expansion         |
| `IntNRQ`                         | ❌ disabled           | no numeric / points indexing                        |
| `PKLookup`                       | ❌ disabled           | no stored primary-key field / point lookup          |
| `TermDVSort`                     | ❌ disabled           | no doc-values / sort-by-field                        |
| Facets phase                     | ❌ disabled           | no taxonomy faceting (`SupportsFacets == false`)    |

## Caveats on comparability

- **topN / scoring**: SimdPhrase2's `Search` / `SearchBoolean` return the *full*
  matching doc-id set (unscored), so `--topn` has no effect on it; the legacy
  engine collects top-N with BM25 scoring. The "hit count" for SimdPhrase2 is the
  size of the returned set.
- **Index location**: SimdPhrase2 indexes to a temp directory on disk (it has no
  in-memory directory), while the legacy engine uses an in-memory `RAMDirectory`.
- These differences are inherent to the two designs; only the relative shape of
  the numbers across categories is meaningful, not a single absolute ratio.

## NuGet packages

The legacy side pins the latest published preview (`4.8.0-beta00017`):

- `Lucene.Net`
- `Lucene.Net.Analysis.Common`
- `Lucene.Net.Facet`

## Running

```bash
dotnet run --project SimdPhrase2.ComparativeBench -c Release -- [options]
```

Options:

| Option              | Default       | Meaning                                      |
|---------------------|---------------|----------------------------------------------|
| `--docs N`          | 50000         | synthetic corpus size                        |
| `--queries N`       | 100           | queries per task category                    |
| `--warmup N`        | 2             | warm-up rounds (discarded)                   |
| `--rounds N`        | 5             | measured rounds, best (highest QPS) wins     |
| `--topn N`          | 10            | hits collected per query (legacy only)       |
| `--index-threads N` | CPU count     | indexing threads (legacy only)               |
| `--search-threads N`| CPU count     | concurrent-search threads                    |
| `--ram-mb N`        | 256           | `IndexWriter` RAM buffer MB (legacy only)    |
| `--force-merge N`   | 1             | force-merge before search                    |
| `--facets-docs N`   | 50000         | documents for the facets phase (legacy only) |
| `--simd-only`       | —             | run only the SimdPhrase2 engine              |
| `--legacy-only`     | —             | run only the legacy engine                   |

The report prints indexing throughput, per-category search QPS with a
`simd/legacy` ratio, concurrent-search QPS + parallel speed-up, and facet-count
throughput (legacy only).
