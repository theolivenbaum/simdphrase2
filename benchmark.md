# SimdPhrase2 Benchmarks

This document collects performance measurements for `SimdPhrase2` against
[Lucene.Net 4.8.0-beta](https://www.nuget.org/packages/Lucene.Net/) on
synthetically-generated documents. The corpus uses a 10,000-word vocabulary
sampled with Zipf's law (s=1.0) to mimic natural-language distribution.

The benchmark suite covers single-term, 2-word and 3-word phrase queries,
boolean queries, NGram tokenization, indexing throughput, and deletion
throughput.

> **Note:** Cold-start scenarios run a single iteration to simulate uncached
> performance and to fit within environment constraints for large datasets.
> Warm performance (multi-iteration) is typically significantly faster
> (e.g., ~0.4ms for 10k Single Term). Lucene results include full enumeration
> of hits to ensure a fair comparison.

## Search benchmarks

Results show the total time to execute 50 queries. Lower mean time is better.

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7171/24H2/2024Update/HudsonValley)
AMD Ryzen AI 9 HX 370 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
.NET SDK 10.0.102
  [Host]     : .NET 10.0.2 (10.0.2, 10.0.225.61305), X64 RyuJIT x86-64-v4
```

| Method                      | N             | Mean           | Allocated        |
|-----------------------------|---------------|----------------|------------------|
| Lucene_SingleTerm           | 10,000        | 6.854 ms       | 19.94 MB         |
| Lucene_Phrase_Len2          | 10,000        | 6.042 ms       | 22.08 MB         |
| Lucene_Phrase_Len3          | 10,000        | 5.097 ms       | 22.59 MB         |
| Lucene_SingleTerm           | 100,000       | 111.093 ms     | 191.81 MB        |
| Lucene_Phrase_Len2          | 100,000       | 145.352 ms     | 194.08 MB        |
| Lucene_Phrase_Len3          | 100,000       | 111.899 ms     | 194.33 MB        |
| Lucene_SingleTerm           | 1,000,000     | 2,414.744 ms   | 1912.49 MB       |
| Lucene_Phrase_Len2          | 1,000,000     | 2,835.566 ms   | 1931.66 MB       |
| Lucene_Phrase_Len3          | 1,000,000     | 2,532.128 ms   | 1932.43 MB       |
| SimdPhrase_SingleTerm       | 10,000        | 150.1 μs       | 50.75 KB         |
| SimdPhrase_Phrase_Len2      | 10,000        | 1,391.1 μs     | 76.06 KB         |
| SimdPhrase_Phrase_Len3      | 10,000        | 2,080.6 μs     | 89.39 KB         |
| SimdPhrase_SingleTerm       | 100,000       | 242.1 μs       | 395.8 KB         |
| SimdPhrase_Phrase_Len2      | 100,000       | 16,979.2 μs    | 479.13 KB        |
| SimdPhrase_Phrase_Len3      | 100,000       | 20,423.7 μs    | 98.77 KB         |
| SimdPhrase_SingleTerm       | 1,000,000     | 611.0 μs       | 1499.14 KB       |
| SimdPhrase_Phrase_Len2      | 1,000,000     | 294,534.8 μs   | 4017.24 KB       |
| SimdPhrase_Phrase_Len3      | 1,000,000     | 220,357.7 μs   | 116.41 KB        |

### Comparison

| Query Type  | N         | Lucene Time (ms) | SIMD Time (ms) | Speedup    |
| ----------- | --------- | ---------------- | -------------- | ---------- |
| SingleTerm  | 10,000    | 6.854            | 0.150          | **45.7×**  |
| Phrase Len2 | 10,000    | 6.042            | 1.391          | **4.3×**   |
| Phrase Len3 | 10,000    | 5.097            | 2.081          | **2.45×**  |
| SingleTerm  | 100,000   | 111.093          | 0.242          | **459×**   |
| Phrase Len2 | 100,000   | 145.352          | 16.979         | **8.56×**  |
| Phrase Len3 | 100,000   | 111.899          | 20.424         | **5.48×**  |
| SingleTerm  | 1,000,000 | 2,414.744        | 0.611          | **3,952×** |
| Phrase Len2 | 1,000,000 | 2,835.566        | 294.535        | **9.63×**  |
| Phrase Len3 | 1,000,000 | 2,532.128        | 220.358        | **11.5×**  |

## NGram benchmarks

Two additional scenarios test NGram-based tokenization performance:

1.  **Identifier search (non-breaking):** 10-digit random numbers. Tests `NGramTokenizer` (3-grams) vs Lucene's `NGramTokenizer`.
2.  **Text search (breaking):** Standard text dataset. Tests `BreakingNGramTokenizer` (3-grams, break on whitespace) vs Lucene's `WhitespaceTokenizer` + `NGramTokenFilter`.

| Scenario   | Method                    | N      | Mean         | Speedup vs Lucene |
|------------|-------------------------- |------- |-------------:|------------------:|
| Identifier | Lucene_Search             | 10,000 |  62.264 ms   | -                 |
| Identifier | SimdPhrase_Search         | 10,000 |   0.937 ms   | **66.4x**         |
| Identifier | Lucene_Search             | 100,000| 780.546 ms   | -                 |
| Identifier | SimdPhrase_Search         | 100,000|   1.842 ms   | **423.7x**        |
| Text Term  | Lucene_Search_Term        | 10,000 | 232.107 ms   | -                 |
| Text Term  | SimdPhrase_Search_Term    | 10,000 |   3.934 ms   | **59.0x**         |
| Text Phrase| Lucene_Search_Phrase2     | 10,000 |  82.544 ms   | -                 |
| Text Phrase| SimdPhrase_Search_Phrase2 | 10,000 |   4.997 ms   | **16.5x**         |
| Text Term  | Lucene_Search_Term        | 100,000| 2,248.081 ms | -                 |
| Text Term  | SimdPhrase_Search_Term    | 100,000|    23.042 ms | **97.6x**         |
| Text Phrase| Lucene_Search_Phrase2     | 100,000| 1,267.550 ms | -                 |
| Text Phrase| SimdPhrase_Search_Phrase2 | 100,000|    71.745 ms | **17.7x**         |

## Indexing benchmarks

Each iteration builds a fresh on-disk index from the same corpus.

```
.NET SDK 10.0.107 / Linux container, AMD64 (results captured during
development on a shared host; absolute numbers are illustrative).
```

| Method           | N       | Mean      | Allocated  |
|------------------|---------|----------:|-----------:|
| Lucene_Index     | 10,000  | 487.4 ms  | 59.32 MB   |
| SimdPhrase_Index | 10,000  | 307.8 ms  | 111.85 MB  |
| Lucene_Index     | 100,000 | 2,150.3 ms| 442.15 MB  |
| SimdPhrase_Index | 100,000 | 1,907.3 ms| 1072.01 MB |

| N       | Lucene (ms) | SimdPhrase2 (ms) | Ratio        |
|---------|------------:|-----------------:|-------------:|
| 10,000  | 487         | 308              | **1.58× faster** |
| 100,000 | 2,150       | 1,907            | **1.13× faster** |

SimdPhrase2's indexer trades higher peak heap allocation (it builds the
inverted-index batch in memory before flushing to a segment) for faster
overall throughput.

## Deletion benchmarks

The benchmark deletes 1,000 random doc ids from a pre-built index. The
index is constructed once during `[GlobalSetup]` and copied into a fresh
working directory in `[IterationSetup]`; only the open + delete + commit
path is timed.

For SimdPhrase2 this exercises the `LiveDocs` deletion path. Lucene
calls `DeleteDocuments` against the `id` field, which writes a
deletions file and runs the configured merge policy.

| Method            | N       | Mean        | StdDev     | Allocated  |
|-------------------|---------|------------:|-----------:|-----------:|
| Lucene_Delete     | 10,000  |  20,812.6 µs |   779.9 µs | 2307.33 KB |
| SimdPhrase_Delete | 10,000  |     769.5 µs |    40.7 µs |   90.48 KB |
| Lucene_Delete     | 100,000 |  51,624.5 µs | 2,698.3 µs | 2468.61 KB |
| SimdPhrase_Delete | 100,000 |     777.6 µs |    35.3 µs |   90.53 KB |

| N       | Lucene (µs) | SimdPhrase2 (µs) | Speedup       |
|---------|------------:|-----------------:|--------------:|
| 10,000  |     20,813  |              770 | **27× faster** |
| 100,000 |     51,625  |              778 | **66× faster** |

SimdPhrase2 stays essentially constant in N for soft delete: the cost is
proportional to the number of deletions, not the index size. Physical
reclamation of disk space happens during the next segment compaction.

## Running benchmarks

```bash
# Run all benchmarks (may take a long time for large N)
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj

# Run specific scenarios (e.g. N=10000)
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- --filter "*N=10000*"

# Run NGram benchmarks
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- --filter "*NGram*"

# Indexing-only / deletion-only
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- --filter "*IndexingBenchmark*"
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- --filter "*DeletionBenchmark*"

# Validate hit counts between engines
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- validate
```
