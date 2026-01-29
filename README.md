# SimdPhrase2

A C# port of the SimdPhrase library, targeting .NET 10.0. This library provides high-performance phrase search using SIMD optimizations (AVX-512) and compressed bitsets (Roaringish).

## Benchmarks

We compared the performance of `SimdPhrase2` against `Lucene.Net` (4.8.0-beta) using datasets of generated documents. The documents were generated using a vocabulary of 10,000 common English words sampled using Zipf's law to mimic natural language distribution.

The benchmark suite includes scenarios for 10,000, 100,000, and 1,000,000 documents, and tests Single Term, 2-word Phrase, and 3-word Phrase queries.

### Results

The following results were obtained on an Intel Xeon Processor (2.30GHz). Lower mean time is better.

| Method | N | Mean | Allocated |
| :--- | :--- | :--- | :--- |
| **SimdPhrase Single Term** | 10,000 | **0.43 ms** | **0.05 MB** |
| Lucene Single Term | 10,000 | 24.29 ms | 20.42 MB |
| **SimdPhrase Single Term** | 100,000 | **13.17 ms** | **0.40 MB** |
| Lucene Single Term | 100,000 | 148.9 ms | 20.10 MB |
| **SimdPhrase Single Term** | 1,000,000 | **16.29 ms** | **1.50 MB** |
| Lucene Single Term | 1,000,000 | 337.8 ms | 23.85 MB |
| | | | |
| **SimdPhrase Phrase (Len 2)** | 10,000 | **3.07 ms** | **0.09 MB** |
| Lucene Phrase (Len 2) | 10,000 | 21.49 ms | 22.62 MB |
| **SimdPhrase Phrase (Len 2)** | 100,000 | **61.10 ms** | **0.49 MB** |
| Lucene Phrase (Len 2) | 100,000 | 461.5 ms | 22.31 MB |
| **SimdPhrase Phrase (Len 2)** | 1,000,000 | **646.83 ms** | **4.03 MB** |
| Lucene Phrase (Len 2) | 1,000,000 | 1277.4 ms | 40.91 MB |
| | | | |
| **SimdPhrase Phrase (Len 3)** | 10,000 | **4.45 ms** | **0.11 MB** |
| Lucene Phrase (Len 3) | 10,000 | 19.47 ms | 23.14 MB |
| **SimdPhrase Phrase (Len 3)** | 100,000 | **71.36 ms** | **0.12 MB** |
| Lucene Phrase (Len 3) | 100,000 | 303.5 ms | 22.68 MB |
| **SimdPhrase Phrase (Len 3)** | 1,000,000 | **428.00 ms** | **0.14 MB** |
| Lucene Phrase (Len 3) | 1,000,000 | 777.3 ms | 44.12 MB |

*Note: Results show `SimdPhrase2` is consistently faster and allocates significantly less memory per search across all dataset sizes.*

### Running Benchmarks

To run the benchmarks yourself:

```bash
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj
```
