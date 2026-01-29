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

| Method                      | N             | Mean           | Error         | StdDev        | Gen0           | Gen1          | Gen2          | Allocated        |
|-----------------------------|---------------|--:-------------|--:------------|--:------------|--:-------------|--:------------|--:------------|--:---------------|
| Lucene_SingleTerm           | 10,000        | 6.854 ms       | 0.1981 ms     | 0.5841 ms     | 2484.3750      | 406.2500      | -             | 19.94 MB         |
| Lucene_Phrase_Len2          | 10,000        | 6.042 ms       | 0.1078 ms     | 0.2127 ms     | 2765.6250      | 1039.0625     | 7.8125        | 22.08 MB         |
| Lucene_Phrase_Len3          | 10,000        | 5.097 ms       | 0.0416 ms     | 0.0325 ms     | 2828.1250      | 726.5625      | 7.8125        | 22.59 MB         |
| Lucene_SingleTerm           | 100,000       | 111.093 ms     | 1.6543 ms     | 1.4665 ms     | 24800.0000     | 12400.0000    | 12400.0000    | 191.81 MB        |
| Lucene_Phrase_Len2          | 100,000       | 145.352 ms     | 1.9042 ms     | 1.6880 ms     | 25500.0000     | 14750.0000    | 12000.0000    | 194.08 MB        |
| Lucene_Phrase_Len3          | 100,000       | 111.899 ms     | 0.9946 ms     | 0.9303 ms     | 24800.0000     | 12400.0000    | 12400.0000    | 194.33 MB        |
| Lucene_SingleTerm           | 1,000,000     | 2,414.744 ms   | 47.1559 ms    | 46.3134 ms    | 259000.0000    | 255000.0000   | 68000.0000    | 1912.49 MB       |
| Lucene_Phrase_Len2          | 1,000,000     | 2,835.566 ms   | 55.0043 ms    | 78.8855 ms    | 256000.0000    | 251000.0000   | 63000.0000    | 1931.66 MB       |
| Lucene_Phrase_Len3          | 1,000,000     | 2,532.128 ms   | 44.7933 ms    | 41.8997 ms    | 261000.0000    | 260000.0000   | 78000.0000    | 1932.43 MB       |
| --------------------------- | ------------- | --:----------- | --:---------- | --:---------- | --:----------- | --:---------- | --:---------- | --:------------- |
| SimdPhrase_SingleTerm       | 10,000        | 150.1 μs       | 2.94 μs       | 3.50 μs       | 6.1035         | -             | -             | 50.75 KB         |
| SimdPhrase_Phrase_Len2      | 10,000        | 1,391.1 μs     | 21.88 μs      | 20.47 μs      | 7.8125         | -             | -             | 76.06 KB         |
| SimdPhrase_Phrase_Len3      | 10,000        | 2,080.6 μs     | 33.76 μs      | 29.93 μs      | 9.7656         | -             | -             | 89.39 KB         |
| SimdPhrase_SingleTerm       | 100,000       | 242.1 μs       | 3.16 μs       | 2.80 μs       | 41.5039        | 41.5039       | 41.5039       | 395.8 KB         |
| SimdPhrase_Phrase_Len2      | 100,000       | 16,979.2 μs    | 298.20 μs     | 278.94 μs     | 62.5000        | 31.2500       | 31.2500       | 479.13 KB        |
| SimdPhrase_Phrase_Len3      | 100,000       | 20,423.7 μs    | 403.01 μs     | 413.86 μs     | -              | -             | -             | 98.77 KB         |
| SimdPhrase_SingleTerm       | 1,000,000     | 611.0 μs       | 6.94 μs       | 6.15 μs       | 166.0156       | 83.0078       | 83.0078       | 1499.14 KB       |
| SimdPhrase_Phrase_Len2      | 1,000,000     | 294,534.8 μs   | 4,740.89 μs   | 4,434.63 μs   | 500.0000       | 500.0000      | 500.0000      | 4017.24 KB       |
| SimdPhrase_Phrase_Len3      | 1,000,000     | 220,357.7 μs   | 4,282.07 μs   | 5,861.35 μs   | -              | -             | -             | 116.41 KB        |


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