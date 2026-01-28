# SimdPhrase2

This project is a modern C# port of the SimdPhrase Rust library.
It implements a fast phrase search algorithm using SIMD (AVX-512) where available, with fallbacks.

## Project Structure

*   `SimdPhrase2/`: The main Class Library.
    *   `Utils.cs`: Tokenization and normalization.
    *   `Roaringish/`: Core data structures and intersection logic.
        *   `RoaringishPacked.cs`: Packed representation of document IDs and positions.
        *   `AlignedBuffer.cs`: Memory management for SIMD.
        *   `Intersect/`: Intersection algorithms (Naive, Simd).
    *   `Db/`: Database and storage logic.
        *   `DocumentStore.cs`: Storage for document content.
        *   `TokenStore.cs`: Storage for token -> offset mapping.
    *   `Indexer.cs`: Indexing logic.
    *   `Searcher.cs`: Search logic.
*   `SimdPhrase2.Tests/`: Unit and integration tests.
*   `reference/`: The original Rust codebase for reference.

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
