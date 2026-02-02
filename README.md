# SimdPhrase2

A C# port of the SimdPhrase library, targeting .NET 10.0. This library provides high-performance phrase search using SIMD optimizations (AVX-512) and compressed bitsets (Roaringish).

## Benchmarks

We compared the performance of `SimdPhrase2` against `Lucene.Net` (4.8.0-beta) using datasets of generated documents. The documents were generated using a vocabulary of 10,000 common English words sampled using Zipf's law to mimic natural language distribution.

The benchmark suite includes scenarios for 10,000, 100,000, and 1,000,000 documents, and tests Single Term, 2-word Phrase, and 3-word Phrase queries.

**Note:** The results below represent a "Cold Start" scenario (single iteration) to simulate uncached performance and fit within environment constraints for large datasets. Warm performance (multi-iteration) is typically significantly faster (e.g., ~0.4ms for 10k Single Term). Lucene results include full enumeration of hits to ensure a fair comparison.

### Benchmark Results

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

## NGram Benchmarks

Two additional scenarios test NGram-based tokenization performance:

1.  **Identifier Search (Non-breaking):** 10-digit random numbers. Tests `NGramTokenizer` (3-grams) vs Lucene's `NGramTokenizer`.
2.  **Text Search (Breaking):** Standard text dataset. Tests `BreakingNGramTokenizer` (3-grams, break on whitespace) vs Lucene's `WhitespaceTokenizer` + `NGramTokenFilter`.

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

### Running Benchmarks

To run the benchmarks yourself:

```bash
# Run all benchmarks (may take a long time for large N)
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj

# Run specific scenarios (e.g. N=10000)
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- --filter "*N=10000*"

# Run NGram benchmarks
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- --filter "*NGram*"

# Validate hit counts between engines
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- validate
```
