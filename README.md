# SimdPhrase2

A C# port of the SimdPhrase library, targeting .NET 10.0. This library provides high-performance phrase search using SIMD optimizations (AVX-512) and compressed bitsets (Roaringish).

## Benchmarks

We compared the performance of `SimdPhrase2` against `Lucene.Net` (4.8.0-beta) using a dataset of generated documents. The documents were generated using a vocabulary of 10,000 common English words sampled using Zipf's law to mimic natural language distribution.

The benchmark suite includes scenarios for 10,000, 100,000, and 1,000,000 documents, and tests Single Term, 2-word Phrase, and 3-word Phrase queries.

### Results (N=10,000)

The following results were obtained on an Intel Xeon Processor (2.30GHz). Lower mean time is better.

| Method | N | Mean | Error | StdDev | Allocated |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **SimdPhrase Single Term** | 10,000 | **0.43 ms** | 0.02 ms | 0.05 ms | **55 KB** |
| Lucene Single Term | 10,000 | 24.29 ms | 0.38 ms | 0.36 ms | 20,426 KB |
| | | | | | |
| **SimdPhrase Phrase (Len 2)** | 10,000 | **3.07 ms** | 0.06 ms | 0.08 ms | **89 KB** |
| Lucene Phrase (Len 2) | 10,000 | 21.49 ms | 0.17 ms | 0.16 ms | 22,623 KB |
| | | | | | |
| **SimdPhrase Phrase (Len 3)** | 10,000 | **4.45 ms** | 0.09 ms | 0.12 ms | **110 KB** |
| Lucene Phrase (Len 3) | 10,000 | 19.47 ms | 0.27 ms | 0.24 ms | 23,139 KB |

*Note: Results show `SimdPhrase2` is significantly faster and allocates much less memory per search. Larger datasets (100k, 1M) are supported in the benchmark project but were not run in this environment due to time constraints.*

### Running Benchmarks

To run the benchmarks yourself:

```bash
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj
```
