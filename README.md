# SimdPhrase2

A C# port of the SimdPhrase library, targeting .NET 10.0. This library provides high-performance phrase search using SIMD optimizations (AVX-512) and compressed bitsets (Roaringish).

## Benchmarks

We compared the performance of `SimdPhrase2` against `Lucene.Net` (4.8.0-beta) using datasets of generated documents. The documents were generated using a vocabulary of 10,000 common English words sampled using Zipf's law to mimic natural language distribution.

The benchmark suite includes scenarios for 10,000, 100,000, and 1,000,000 documents, and tests Single Term, 2-word Phrase, and 3-word Phrase queries.

**Note:** The results below represent a "Cold Start" scenario (single iteration) to simulate uncached performance and fit within environment constraints for large datasets. Warm performance (multi-iteration) is typically significantly faster (e.g., ~0.4ms for 10k Single Term). Lucene results include full enumeration of hits to ensure a fair comparison.

### Results (Cold Start / Dry Run)

Results show the total time to execute 50 queries. Lower mean time is better.

| Method | N | Mean | Allocated |
| :--- | :--- | :--- | :--- |
| **SimdPhrase Single Term** | 10,000 | **17.11 ms** | **0.06 MB** |
| Lucene Single Term | 10,000 | 112.3 ms | 19.96 MB |
| **SimdPhrase Single Term** | 100,000 | **13.17 ms** | **0.40 MB** |
| Lucene Single Term | 100,000 | 540.9 ms | 191.83 MB |
| **SimdPhrase Single Term** | 1,000,000 | **16.29 ms** | **1.50 MB** |
| Lucene Single Term | 1,000,000 | 6,183.6 ms | 1912.48 MB |
| | | | |
| **SimdPhrase Phrase (Len 2)** | 10,000 | **28.22 ms** | **0.09 MB** |
| Lucene Phrase (Len 2) | 10,000 | 141.7 ms | 22.10 MB |
| **SimdPhrase Phrase (Len 2)** | 100,000 | **61.10 ms** | **0.49 MB** |
| Lucene Phrase (Len 2) | 100,000 | 918.6 ms | 194.09 MB |
| **SimdPhrase Phrase (Len 2)** | 1,000,000 | **646.83 ms** | **4.03 MB** |
| Lucene Phrase (Len 2) | 1,000,000 | 7,593.7 ms | 1931.65 MB |
| | | | |
| **SimdPhrase Phrase (Len 3)** | 10,000 | **31.81 ms** | **0.11 MB** |
| Lucene Phrase (Len 3) | 10,000 | 145.7 ms | 22.61 MB |
| **SimdPhrase Phrase (Len 3)** | 100,000 | **71.36 ms** | **0.12 MB** |
| Lucene Phrase (Len 3) | 100,000 | 735.8 ms | 194.35 MB |
| **SimdPhrase Phrase (Len 3)** | 1,000,000 | **428.00 ms** | **0.14 MB** |
| Lucene Phrase (Len 3) | 1,000,000 | 6,185.2 ms | 1932.44 MB |

*Note: Results demonstrate that SimdPhrase2 scales significantly better than Lucene.Net as document count increases, maintaining low latency and memory usage.*

### Running Benchmarks

To run the benchmarks yourself:

```bash
# Run all benchmarks (may take a long time for large N)
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj

# Run specific scenarios (e.g. N=10000)
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- --filter "*N=10000*"

# Validate hit counts between engines
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- validate
```
