# SimdPhrase2

This project is a modern C# port of the SimdPhrase Rust library.
It implements a fast phrase search algorithm using SIMD (AVX-512) where available, with fallbacks.

## Project Structure

*   `SimdPhrase2/`: The main Class Library.
    *   `BasicTokenizer.cs`, `NGramTokenizer.cs`, `BreakingNGramTokenizer.cs`: Tokenizer implementations.
    *   `Roaringish/`: Core data structures and intersection logic.
        *   `RoaringishPacked.cs`: Packed representation of document IDs and positions.
        *   `AlignedBuffer.cs`: Memory management for SIMD.
        *   `Intersect/`: Intersection algorithms (Naive, Simd, Gallop).
    *   `Db/`: Persistent stores.
        *   `DocumentStore.cs`: Cross-segment document blob store.
        *   `TokenStore.cs`: Per-segment token -> offset mapping.
        *   `DocLengthsStore.cs`: Per-segment, per-field document lengths.
        *   `LiveDocs.cs`: Sparse deleted-doc set.
        *   `SegmentManifest.cs`: Index-level list of active segments.
        *   `SegmentReader.cs`: Read-only handle to one segment on disk.
        *   `FieldRegistry.cs`: Registered fields and their boosts.
    *   `Document.cs`: `IndexDocument` and `FieldOptions`.
    *   `Indexer.cs`: Multi-field, segmented indexing with auto-compaction.
    *   `Searcher.cs`: Cross-segment query execution.
*   `SimdPhrase2.Tests/`: Unit and integration tests.
*   `SimdPhrase2.Benchmarks/`: BenchmarkDotNet vs Lucene.Net.
*   `reference/`: The original Rust codebase for reference.

## Storage layout

```
<indexName>/
    field_meta.json           # registered fields + boosts
    segments.json             # list of active segments
    deleted_docs.bin          # global soft-delete set
    index_stats.json          # aggregated TotalDocs/TotalTokens
    doc_offsets.bin           # cross-segment DocumentStore index
    documents.bin             # cross-segment DocumentStore data
    segments/
        seg_000000/
            roaringish_packed.bin
            token_map.bin     # tokens are encoded "field<US>token"
            doc_lengths.bin
            common_tokens.bin (optional)
            live_docs.bin
        seg_000001/...
```

New batches are written to fresh segment directories on `Indexer.Commit()`.
When the segment count exceeds `IndexerOptions.MaxSegmentsBeforeCompact`, all
segments are merged into one and physically deleted documents are dropped.

## Key Concepts

*   **RoaringishPacked**: A compressed storage format for posting lists, optimizing for SIMD processing.
*   **SIMD Intersection**: Using AVX-512 (or fallback) to intersect posting lists very quickly.
*   **Phrase Search**: The goal is to find documents containing a specific sequence of words.

## Instructions for Agents

*   Follow the C# coding standards.
*   Ensure tests are written for new functionality.
*   When porting, try to maintain the performance characteristics of the Rust code but use idiomatic C#.
*   Use `Vector512<T>` and `System.Runtime.Intrinsics.X86` for SIMD.
*   Ensure the code is compatible with .NET 8.0+.
