# SIMD benchmark results across history

This file records a per-commit run of the **SIMD search path only**
(`SimdPhrase2.Benchmarks.SimdPhraseBenchmark`, i.e. `SimdPhraseService` with
`forceNaive: false` — the path that goes through `SimdIntersect`/AVX-512), plus
a single Lucene.Net run on the current commit for comparison.

## Methodology

- **What is measured:** the three SIMD search benchmarks at **N = 10 000 docs**:
  `SimdPhrase_Search_SingleTerm`, `_Phrase_Len2`, `_Phrase_Len3`. Each benchmark
  method runs **50 queries per invocation**, so the means below are the *sum of
  50 queries*, not a single query.
- **Commits:** every first-parent (main-line) commit that contains the benchmark
  project, from its introduction (`#3`, `d3ce6a4`) to `HEAD` (`f8165d2`),
  oldest → newest. Each commit was checked out, rebuilt in `Release`, and only
  run if it compiled. **All 15 commits compiled and ran** — no skips.
- **Config:** BenchmarkDotNet `ShortRun` (1 launch × 3 warmup × 3 iterations).
  Short run ⇒ wide confidence intervals; treat these as *relative trend*
  numbers, not publication-grade absolutes.
- **Why N = 10 000:** N = 1 000 000 reproducibly fails on this 4-core / limited-RAM
  box (BenchmarkDotNet reports `NA`), so it was excluded to keep the comparison
  consistent across every commit.
- **Machine:** Intel Xeon @ 2.80 GHz, 4 cores, .NET 10.0.9, x86-64-v4.
  `HardwareIntrinsics = AVX512 F+BW+CD+DQ+VL` — the AVX-512 path **was** taken.

Absolute numbers are CPU-specific; only the deltas between commits are meaningful.

## SIMD path — mean time (µs) per 50-query invocation, N = 10 000

| commit  | SingleTerm (µs) | Phrase2 (µs) | Phrase3 (µs) | Alloc ST / P2 / P3 | subject |
|---------|----------------:|-------------:|-------------:|--------------------|---------|
| d3ce6a4 |           67.81 |      1662.71 |      2267.62 | 50.75 / 78.05 / 98.79 KB | Merge #3 add-benchmarks |
| 3ab4260 |           66.52 |      1831.19 |      2551.24 | 50.75 / 81.34 / 99.04 KB | Merge #5 add-benchmarks |
| 915b14e |           63.95 |      1867.44 |      2634.53 | 50.75 / 81.34 / 99.04 KB | Merge #6 bm25-boolean-search |
| 5b51076 |           57.30 |      1731.18 |      2537.43 | 31.13 / 49.70 / 57.24 KB | Merge #7 tokenizer-abstraction |
| be6f2fa |           47.29 |      2412.65 |      1751.20 | 23.70 / 57.68 / 56.63 KB | Multiple tokens per index (lemmatization) |
| 2216ea9 |           50.13 |      2359.56 |      1764.27 | 23.70 / 57.68 / 56.63 KB | Update README.md |
| 47ee972 |           52.35 |      2317.03 |      1947.43 | 23.70 / 57.68 / 56.63 KB | Merge #9 ngram-tokenizer |
| fcdb449 |           46.09 |      2311.80 |      1750.34 | 23.70 / 57.68 / 56.63 KB | Merge #11 roadmap-analysis |
| b442875 |           44.97 |      2333.63 |      1765.00 | 23.70 / 57.68 / 56.63 KB | Merge #12 thread-safe-architecture |
| 8945d65 |           52.43 |      2518.98 |      1769.62 | 23.70 / 57.68 / 56.63 KB | Revert #12 thread-safe-architecture |
| 871761f |           49.33 |      2444.54 |      1726.94 | 23.70 / 57.68 / 56.63 KB | Merge #10 abstract-storage |
| fe32ea6 |           54.74 |      2377.88 |      1714.48 | 23.70 / 57.68 / 56.63 KB | Merge #16 document-performance-simd |
| bac68fb |           49.79 |      2424.98 |      1829.22 | 23.70 / 57.64 / 56.25 KB | Merge #17 blissful-keller |
| 37e75ba |           47.73 |      2361.91 |      1776.29 | 23.70 / 57.64 / 56.25 KB | Merge #18 simdphrase2-scoring-fields |
| f8165d2 |           50.16 |      2441.13 |      1784.52 | 23.70 / 57.64 / 56.30 KB | Merge #19 boolean-search-bm25 (HEAD) |

## Lucene.Net baseline (current HEAD `f8165d2`, same N / config)

| engine          | SingleTerm (µs) | Phrase2 (µs) | Phrase3 (µs) |
|-----------------|----------------:|-------------:|-------------:|
| Lucene.Net      |        11106.00 |     12594.00 |      8663.00 |
| SimdPhrase2 SIMD |           50.16 |      2441.13 |      1784.52 |
| **speedup**     |       **~221×** |     **~5.2×** |     **~4.9×** |

## Observations

- **Single-term search got steadily faster:** ~68 µs → ~45–50 µs, with the big
  step at `5b51076`/`be6f2fa` (tokenizer abstraction + the multi-token-per-index
  change), which also roughly halved per-query allocations (50.75 → 23.70 KB).
- **`be6f2fa` flipped Phrase2 vs Phrase3:** before it, Phrase3 was the slowest
  (~2.5 ms) and Phrase2 ~1.7 ms; after it, Phrase3 dropped to ~1.75 ms and
  Phrase2 rose to ~2.4 ms. This is the "multiple tokens at the same index"
  (lemmatization) change altering the posting-list layout / intersection shape.
- **Recent commits are flat** (within ShortRun noise) — the scoring/boolean/field
  work in `#16`–`#19` did not regress the core SIMD search path.
