# SimdPhrase2

A C# port of the SimdPhrase library, targeting .NET 8.0. This library provides high-performance phrase search using SIMD optimizations (AVX-512) and compressed bitsets (Roaringish).

## Benchmarks

We compared the performance of `SimdPhrase2` against `Lucene.Net` (4.8.0-beta) using a dataset of 10,000 generated documents. The documents were generated using a vocabulary of 10,000 common English words sampled using Zipf's law to mimic natural language distribution.

### Results

The following results were obtained on an Intel Xeon Processor (2.30GHz). Lower mean time is better.

| Method | N | Mean | Error | StdDev | Allocated |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **SimdPhrase Single Term** | 10,000 | **0.89 ms** | 0.03 ms | 0.07 ms | **102 KB** |
| Lucene Single Term | 10,000 | 51.64 ms | 1.01 ms | 0.99 ms | 40,836 KB |
| | | | | | |
| **SimdPhrase Phrase** | 10,000 | **8.74 ms** | 0.16 ms | 0.18 ms | **244 KB** |
| Lucene Phrase | 10,000 | 61.59 ms | 0.47 ms | 0.41 ms | 45,288 KB |

*Note: Results show `SimdPhrase2` is significantly faster and allocates much less memory per search.*

### Running Benchmarks

To run the benchmarks yourself:

```bash
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj
```
