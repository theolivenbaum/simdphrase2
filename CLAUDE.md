# CLAUDE.md

Guidance for Claude (and other agents) working in this repository. The single
most important thing to understand about SimdPhrase2 is that **its entire
reason for existing is raw search performance**. The benchmarks in
`README.md` show speedups of **45x to ~4000x over Lucene.Net** on cold-start
queries. Those numbers do not come from algorithmic cleverness alone — they
come from a very specific stack of low-level techniques that are easy to
accidentally undo with otherwise-reasonable C# refactoring.

Before changing any code in `SimdPhrase2/Roaringish/`, `Indexer.cs`, or
`Searcher.cs`, read this document end-to-end.

## How the library achieves its performance

The library is a port of the Rust `SimdPhrase` library. Performance comes from
the *combination* of the techniques below, not any single one of them.

### 1. Roaringish packed posting lists (`Roaringish/RoaringishPacked.cs`)

Posting lists are stored as a flat array of `ulong` values, where each 64-bit
word packs three things:

```
| 63 ............ 32 | 31 ........ 16 | 15 .......... 0 |
|       doc_id       |     group      |   value bitmap  |
```

- **`doc_id`** (32 bits): which document.
- **`group`** (16 bits): which 16-token "group" within the document
  (`position / 16`).
- **`value`** (16 bits): a *bitmap* of which of the 16 positions in that group
  hold this token (`1 << (position % 16)`).

This single layout is what makes everything else fast:

- **Sorted by `(doc_id, group)`** — so intersection is a merge over sorted
  `ulong` streams.
- **Up to 16 positions per `ulong`** — one machine word can represent a token
  occurring at 16 different positions in a document. Intersection only needs
  to compare the high 48 bits (doc_id+group), and phrase matching becomes a
  shift-and-AND on the low 16 bits.
- **Cache- and SIMD-friendly** — a flat `ulong[]` is exactly what AVX-512
  wants to consume 8 elements at a time.

**Do not change this layout without changing every consumer.** The packing
functions (`Pack`, `PackDocId`, `PackGroup`, `PackValue`, `ClearValues`,
`UnpackDocId`, etc.) are `AggressiveInlining` and assumed everywhere — the
SIMD code reproduces these masks inline (e.g. `Vector512.Create(~0xFFFFUL)`,
`Vector512.Create(0xFFFFUL)`). Changing bit widths or shifts in one place
silently corrupts the others.

### 2. AVX-512 SIMD intersection (`Roaringish/Intersect/SimdIntersect.cs`)

The hot path of phrase search is `SimdIntersect.InnerIntersect`. It processes
**8 `ulong` posting entries per loop iteration** with AVX-512:

- Loads `Vector512<ulong>` from both posting lists.
- Uses a `vp2intersect` fallback (`Vp2IntersectFallback`) to compute the
  intersection mask between two vectors of 8 doc_id+group keys. The fallback
  uses `PermuteVar8x64` + `Vector512.Equals` + `ExtractMostSignificantBits` to
  emulate the AVX-512 `vp2intersectq` instruction (which is not yet exposed
  via `System.Runtime.Intrinsics`).
- Phrase matching is a single `Avx512F.ShiftLeftLogical` + `Avx512F.And` on
  the value bitmaps — what would be a per-position loop in a typical engine
  is one fused vector instruction here.
- Writes results with `StoreUnsafe` directly into a 64-byte-aligned
  `AlignedBuffer<ulong>`, advancing the write index by `popcount(mask)`.
- Falls back to `NaiveIntersect` *only for the tail* (the
  `lhs.Length % 8` and `rhs.Length % 8` remainder).

If `Avx512F.IsSupported` is false the entire body delegates to
`NaiveIntersect`. **Removing the fallback is fine; removing the AVX-512 path
is not.**

The MSB-carry analysis (`AnalyzeMsb`) and the two-pass intersection scheme
(see `Searcher.Intersect`) are what allow phrase matches to cross 16-position
group boundaries without leaving the SIMD pipeline.

### 3. Galloping intersection for very skewed lists (`GallopIntersectFirst.cs`, `GallopIntersectSecond.cs`)

When one posting list is dramatically smaller than the other, linear merge
wastes cycles. `Searcher.Intersect` picks between the SIMD merge and a
galloping (exponential-then-binary) search based on a length ratio:

- First pass: gallop if `max/min >= 650`.
- Second pass: gallop if `max/min >= 120`.

These thresholds were tuned and **should not be changed casually**. They are
the difference between "scan a million entries" and "skip directly to the few
that matter".

### 4. 64-byte aligned memory (`Roaringish/AlignedBuffer.cs`)

`AlignedBuffer<T>` allocates with `NativeMemory.AlignedAlloc(byteCount, 64)`.
The `Alignment = 64` constant is a hard requirement, not a hint:

- AVX-512 loads (`Vector512.Load`) want 64-byte alignment to avoid split-cache-line penalties.
- The packed file on disk is also padded to 64-byte boundaries when batches
  are merged (`Indexer.MergeBatches`: `alignedPos = (currentPos + 63) & ~63`).
  This lets the search-time loader memory-read directly into an aligned
  buffer with no copy-and-realign step.

**Never replace `AlignedBuffer` with `T[]`, `List<T>`, `ArrayPool<T>`, or
`Memory<T>` in any hot path.** Doing so silently demotes 64-byte-aligned
loads to unaligned ones and loses the no-copy load behavior. The disk
alignment in `Indexer.MergeBatches` must stay in sync with `AlignedBuffer`'s
alignment.

### 5. Two-pass phrase intersection (`Searcher.Intersect`)

Phrase search is performed as two passes over the same data: one for the
in-group case and one for matches that cross a 16-position group boundary
(MSB carry). `RoaringishPacked.MergeResults` then merges the two streams. The
shape of this algorithm is what lets the SIMD inner loop stay branch-free —
**do not "simplify" it into a single pass**.

### 6. Smart phrase-token expansion (`CommonTokens.cs`, `Indexer.cs`, `Searcher.MergeAndMinimizeTokens`)

For phrases involving common words, the indexer stores precomputed n-grams
(up to length 3) for *rare+common* and *common+rare* combinations. At query
time, the searcher solves a small dynamic-programming problem
(`MergeAndMinimizeTokens`) to pick the cheapest set of stored tokens that
covers the query. The cost metric is *posting-list length*, which is exactly
what drives intersection cost. This is why a 3-word phrase containing "the"
can be faster than a 2-word phrase containing two rare words.

### 7. Stateless, no-alloc query path (where possible)

- `BasicTokenizer` / `NGramTokenizer` / `BreakingNGramTokenizer` yield
  `ReadOnlySpan<char>` tokens directly off the input string. They allocate
  only when the caller materializes (`.ToString()`).
- `RoaringishPacked.LoadPacked` reads the on-disk bytes *directly* into an
  `AlignedBuffer<ulong>` via `MemoryMarshal.Cast<ulong, byte>` — no managed
  copy, no parsing.
- Intersection writes into pre-sized `AlignedBuffer<ulong>` instances; only
  the final result is converted to `List<uint>` for the public API.

The benchmark tables in `README.md` show single-term queries on 1M docs
allocating ~1.5 MB total — that budget is consumed almost entirely by the
final result list. **Watch allocations in hot paths.**

## Things that look harmless but are not

When changing this codebase, the following "small cleanups" will cause large
performance regressions. Please don't do them without measuring first.

| Tempting change | Why it's bad |
|---|---|
| Replace `AlignedBuffer<ulong>` with `ulong[]` or `List<ulong>` | Loses 64-byte alignment → unaligned AVX-512 loads, cache-line splits, and breaks the zero-copy disk load. |
| Replace `unsafe` / `fixed` pointer arithmetic in `SimdIntersect` with `Span<T>` indexing | Defeats JIT vectorization and adds bounds checks inside the inner loop. |
| Remove `[MethodImpl(MethodImplOptions.AggressiveInlining)]` from packing helpers (`ClearValues`, `UnpackValues`, `PackDocId`, etc.) | These are called once per inner-loop iteration. Un-inlining them turns a register operation into a method call. |
| Add `null` / bounds / argument validation inside the inner intersect loop | Each branch is multiplied by hundreds of millions of iterations. Validate at the public API boundary, not inside the kernel. |
| "Generalize" `Vector512<ulong>` to `Vector<T>` or LINQ | `Vector<T>` is sized at runtime by the host and won't pick up AVX-512 reliably; LINQ adds enumerator allocations and virtual calls. |
| Replace `NativeMemory.AlignedAlloc` with `GC.AllocateArray<T>(pinned: true)` | Pinned arrays are not aligned to 64 bytes (only to the object header). Aligned and pinned are different guarantees. |
| Make `Indexer.MergeBatches` skip the 64-byte file padding | Breaks the no-copy aligned load at search time. The padding cost is negligible; the read-side cost of removing it is enormous. |
| Replace the gallop/SIMD dispatch in `Searcher.Intersect` with always-SIMD or always-gallop | Each is catastrophic on the workloads the other was designed for. The 650 and 120 thresholds are tuning, not arbitrary. |
| Merge the two-pass phrase intersection into one pass | Reintroduces branches into the SIMD kernel and forces per-element fallback when phrases cross group boundaries. |
| Replace `MemoryMarshal.Cast<ulong, byte>` with a `BinaryReader` loop | Turns a zero-copy reinterpret into an O(n) parse with allocations. |
| Materialize tokenizer spans to `string` "for clarity" | The tokenizer is designed to be allocation-free. Materializing once per token is the difference between `O(N)` allocations and `O(1)`. |
| Add a feature flag, abstraction layer, or DI seam inside the inner loop | Virtual dispatch in the hot path will erase the SIMD win. Keep abstractions at the `IIntersect` / `ITextTokenizer` boundary, not below it. |

## Working rules

1. **Performance is a correctness property of this library.** A change that
   passes tests but regresses the benchmarks is a regression — treat it like
   a failing test. The benchmark suite lives in `SimdPhrase2.Benchmarks/` and
   should be run on any change that touches:
   - `SimdPhrase2/Roaringish/**`
   - `SimdPhrase2/Searcher.cs`
   - `SimdPhrase2/Indexer.cs` (write-side layout)
   - any tokenizer
2. **Before refactoring, look for an AVX-512 / `Vector512` / `unsafe` /
   `AlignedBuffer` / `AggressiveInlining` / `fixed` annotation.** If you see
   one, assume it's load-bearing. The Rust origin sometimes makes the C#
   shape look "un-idiomatic" — that is intentional.
3. **Preserve the fallback chain.** `SimdIntersect` already falls back to
   `NaiveIntersect` when AVX-512 is unavailable and for tail elements. New
   intrinsics should follow the same pattern: check `IsSupported`, fall back
   gracefully — never delete the SIMD path to "simplify" things.
4. **When in doubt, add — don't replace.** A new alternative implementation
   behind a flag, benchmarked side-by-side, is fine. Editing the existing
   hot-path implementation in place without a benchmark comparison is not.
5. **Measure cold-start performance, not just warm.** The README numbers are
   cold-start because that's the realistic web/CLI workload. JIT warmup can
   hide regressions in steady-state benchmarks.
6. **Allocations matter.** The single-term 1M-doc benchmark allocates ~1.5
   MB total. If your change adds even a few KB per query in the hot path, it
   shows up at scale. Profile with `BenchmarkDotNet`'s
   `[MemoryDiagnoser]` (already enabled in the benchmark project).

## Running benchmarks

```bash
# Full suite (slow on 1M docs)
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj

# Smaller sample
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- --filter "*N=10000*"

# NGram-specific
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- --filter "*NGram*"

# Validate that SIMD and Naive paths return identical hit sets
dotnet run -c Release --project SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj -- validate
```

When benchmarking a change, report **before and after** numbers from the same
machine in the same session. Different CPUs (especially ones without AVX-512)
will produce wildly different absolute numbers; only the delta is meaningful.

## See also

- `AGENTS.md` — high-level project structure and porting notes.
- `README.md` — public-facing benchmark results vs Lucene.Net.
- `reference/` — the original Rust implementation. When unsure whether a
  C# choice is intentional or accidental, check what Rust does.
