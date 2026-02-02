# TODO

## Completed (Initial Port)
- [x] Implement `Utils` (Tokenizer, Normalizer)
- [x] Implement `AlignedBuffer`
- [x] Implement `RoaringishPacked`
- [x] Implement `NaiveIntersect`
- [x] Implement `SimdIntersect` (with Fallback)
- [x] Implement `DocumentStore`
- [x] Implement `TokenStore`
- [x] Implement `Indexer`
- [x] Implement `Searcher`
- [x] Add Tests
- [x] Implement `CommonTokens` optimization (from Rust codebase)
- [x] Implement `Gallop` intersection (from Rust codebase)
- [ ] Verify Performance (optional but recommended)

## Roadmap (Proposed Features)

Based on a review of the current `SimdPhrase2` implementation, the following features are proposed to bridge the gap between this library and a production-grade search engine like Lucene.

### Implementation Order

The features are ordered to minimize technical debt and refactoring churn: **Stability -> Architecture -> Schema -> Scalability**.

1.  **Thread-Safe Architecture** (Foundation) [COMPLETED]
2.  **Unified Query & Scoring Model** (Refactoring)
3.  **Fielded Indexing and Search** (Schema)
4.  **Segmented Architecture** (Scalability/Storage)
5.  **Deletions** (Maintenance)
6.  **Extensions** (Numeric, Wildcard, etc.)

---

### Detailed Analysis

#### 1. Thread-Safe Architecture (Critical) [COMPLETED]
**Current State:**
The `Searcher` class is now thread-safe. It uses `RandomAccess.Read` for stateless file reads and ensures read-only access to storage components.

#### 2. Unified Query & Scoring Model
**Current State:**
There are two disconnected search paths:
-   `Search(string query)`: Supports Boolean logic and Phrases but returns `List<uint>` (no scoring).
-   `SearchBM25(string query)`: Supports BM25 scoring but has no Boolean support and returns `List<(uint, float)>`.
Combining them (e.g., "Boolean filter + BM25 ranking") is currently impossible without significant hacks.

**Proposed Implementation:**
-   Create a composable **Query Object Model** (`Query` base class).
-   Implement subclasses: `TermQuery`, `PhraseQuery`, `BooleanQuery`.
-   Implement a `Scorer` / `Weight` iterator pattern (similar to Lucene) to unify matching and scoring into a single pipeline.
-   This refactor is a prerequisite for adding Fields and Segments effectively.

#### 3. Fielded Indexing and Search
**Current State:**
`Indexer.AddDocument` accepts a single `content` string. All text is treated as one large "body". It is impossible to distinguish between hits in a "Title" vs. a "Description".

**Proposed Implementation:**
-   Update `Indexer` to accept a `Document` object containing multiple fields.
-   Modify `TokenStore` to handle field-scoped tokens (e.g., store keys as `title:search_term` and `body:search_term`).
-   Update `BooleanQueryParser` to support field syntax (e.g., `title:foo AND body:bar`).
-   **Dependency:** Easier to implement after the Unified Query Model is in place.

#### 4. Segmented Architecture (Incremental Updates)
**Current State:**
The `Indexer` wipes the entire index (`Directory.Delete`) on initialization. To add a single document, the entire dataset must be re-indexed.

**Proposed Implementation:**
-   Adopt a **Segment-based** architecture.
-   `Indexer` writes new, immutable segments (mini-indexes) for every batch/commit.
-   `Searcher` manages a collection of `SegmentReader` objects and merges results.
-   Implement a background **Merge Policy** to combine small segments into larger ones for read efficiency.

#### 5. Deletions (LiveDocs)
**Current State:**
No mechanism exists to remove or update documents.

**Proposed Implementation:**
-   Implement a **LiveDocs** bitset (e.g., using `RoaringishPacked` or `BitArray`).
-   Mask results against this bitset during search to exclude deleted documents.
-   Reclaim space during the segment merge process.
-   **Dependency:** Requires Segmented Architecture.

#### 6. Numeric and Range Queries
**Current State:**
Only text tokenization is supported.

**Proposed Implementation:**
-   Support numeric fields (`int`, `long`, `double`).
-   Implement specialized data structures (e.g., BKD-trees or trie-based encoding) for efficient range searches (`price:[10 TO 50]`).

#### 7. Wildcard and Fuzzy Search
**Current State:**
Only exact token matches are supported.

**Proposed Implementation:**
-   Implement an **FST (Finite State Transducer)** or ensure the dictionary is sorted to allow efficient prefix lookups.
-   Enable `term*` queries without scanning the entire token dictionary.
