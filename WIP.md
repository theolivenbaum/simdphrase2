# Work In Progress

## Current Status
Refactoring architecture for production readiness.

## Completed Tasks
*   Implemented `Utils` (Tokenizer, Normalizer).
*   Implemented `RoaringishPacked` data structure.
*   Implemented `AlignedBuffer` for memory alignment.
*   Implemented `NaiveIntersect` intersection algorithm.
*   Implemented `SimdIntersect` with AVX-512 intrinsics and fallbacks.
*   Implemented `DocumentStore` and `TokenStore` for storage.
*   Implemented `Indexer` with batch merging.
*   Implemented `Searcher` with 2-pass phrase search logic.
*   Verified with Unit Tests and End-to-End Tests.
*   Implemented `Gallop` intersection (First and Second pass).
*   Implemented `CommonTokens` optimization (Indexer merging, Searcher DP).
*   **Thread-Safe Architecture**: Refactored `Searcher` and `DocumentStore` to use stateless `RandomAccess.Read`, enabling concurrent searches.

## Next Steps
*   Unified Query & Scoring Model.
