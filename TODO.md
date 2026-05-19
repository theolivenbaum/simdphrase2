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

1.  **Thread-Safe Architecture** (Foundation)
2.  **Unified Query & Scoring Model** (Refactoring)
3.  **Fielded Indexing and Search** (Schema)
4.  **Segmented Architecture** (Scalability/Storage)
5.  **Deletions** (Maintenance)
6.  **Extensions** (Numeric, Wildcard, etc.)

---

### Detailed Analysis

#### 1. Thread-Safe Architecture (Critical)
**Current State:**
The `Searcher` class is **not thread-safe**. It relies on a shared `FileStream` (`_packedFile`) and performs stateful `Seek()` operations before reading. This prevents a single `Searcher` instance from serving concurrent requests in a web server environment (e.g., ASP.NET Core) without heavy external locking.

**Proposed Implementation:**
-   Switch from `FileStream.Seek() + Read()` to `RandomAccess.Read()` (stateless file reads).
-   Alternatively, implement a resource pool for `Searcher` instances or file handles.
-   Ensure `TokenStore` and internal dictionaries are accessed safely (mostly read-only during search).

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

---

## Mosaik Lucene Usage Audit (2026-05-19)

This section captures how the **Mosaik Graphs** project (curiosity-ai/mosaik) currently uses Lucene.NET, focusing specifically on **indexing**, **searching**, and **storage**. It is meant as a reference for whichever features `SimdPhrase2` must grow to become a drop-in replacement (or coexist with Lucene) in that codebase. **No code in either repo has been changed** by this audit — it is purely descriptive.

### Audit scope and sources

The Mosaik codebase vendors `Lucene.Net` (LUCENE_48) under `Other/LuceneNet/` and references it from `Mosaik/Graph/Mosaik.Graph.csproj`:

```xml
<ProjectReference Include="..\..\Other\LuceneNet\Lucene.Net\Lucene.Net.csproj" />
<ProjectReference Include="..\..\Other\LuceneNet\Lucene.Net.Queries\Lucene.Net.Queries.csproj" />
<ProjectReference Include="..\..\Other\LuceneNet\Lucene.Net.QueryParser\Lucene.Net.QueryParser.csproj" />
```

There are three concrete Lucene-backed indexes (`Mosaik/Graph/src/Indexes/Lucene/`):

| Class | File | Purpose |
|---|---|---|
| `LuceneTextIndex` | `LuceneTextIndex.cs` | The general full-text index for a (`NodeType`, `FieldName`) pair on the graph. Supports `WaitForNode` and `WaitForDocument` modes. Marked as `IndexTypes.LuceneTextIndex` ("Full Text Search"). |
| `FileContentTextIndex` | `FileContentTextIndex.cs` | A multi-field index dedicated to `_FileEntry` nodes — content, name, source, content-type, extension, optional 3-gram filename index. Default `IndexParallelism = 16`. |
| `LuceneIndex` | `LuceneIndex.cs` | A legacy single-writer text index over a configured `FieldName` (currently excluded from the build via `<Compile Remove="src\Indexes\Lucene\LuceneIndex.cs" />` but kept around for reference; same shape as `LuceneTextIndex` minus parallelism and replication awareness). |

All three plug into a custom storage layer (`Mosaik/Graph/src/Graph/Storage/LuceneOnRocks/`) that hosts the Lucene `Directory` on top of RocksDB.

Together these three areas — **indexing pipeline**, **search pipeline**, and **storage layer** — are where SimdPhrase2 would need to grow capabilities to be considered as a replacement. The findings below are grouped accordingly.

---

### A. Storage layer — `LuceneOnRocks`

Mosaik does **not** use `FSDirectory` or `RAMDirectory`. Instead it implements its own `BaseDirectory` (`Lucene.Net.Store.BaseDirectory`) backed by RocksDB column families. This is by far the heaviest customization and the one with the deepest impact on what a replacement engine would need to support.

#### A.1 `RocksDirectory` — Lucene `Directory` on top of RocksDB
`Mosaik/Graph/src/Graph/Storage/LuceneOnRocks/RocksDirectory.cs`

- Lives inside a shared `RocksDbIndexStorage` (`RocksDbStorage.IndexDB`) and registers itself with the storage so that replication change events can find it (`DB.RegisterDirectory(basePath, this)`).
- Tracks files in a `ConcurrentDictionary<string, RocksFile>` and exposes the standard Lucene operations: `ListAll`, `FileExists`, `FileLength`, `CreateOutput`, `OpenInput`, `DeleteFile`, `Sync`, `Dispose`.
- Uses Lucene's `SingleInstanceLockFactory` (`SetLockFactory(new SingleInstanceLockFactory())`).

Example: opening a directory and writing through Lucene to RocksDB-backed pages:

```csharp
// LuceneIndex.cs : InitializeIndex()
var indexID = $"L-{UID}";
luceneDirectory = new LuceneOnRocks.RocksDirectory(Parent.Storage.IndexDB, indexID);

var indexConfig = new IndexWriterConfig(LuceneVersion.LUCENE_48, luceneAnalyzer);
indexConfig.Similarity              = new BM25Similarity();
indexConfig.RAMBufferSizeMB         = 128;
indexConfig.RAMPerThreadHardLimitMB = 16;
indexConfig.OpenMode                = OpenMode.CREATE_OR_APPEND;

luceneWriter = new IndexWriter(luceneDirectory, indexConfig);
using (var reader = luceneWriter.GetReader(false)) ; // NOP — forces NRT reader init
```

#### A.2 `RocksFile` — paged file with reference-counted page cache
`Mosaik/Graph/src/Graph/Storage/LuceneOnRocks/RocksFile.cs`

- Page size: **64 KB** (`PAGE_SIZE = 64 * 1024`).
- Page pool: a 128-slot `ObjectPool<byte[]>` to amortize allocation across reads.
- Each `PagesRef` carries a SHA-256 hash of the page; `FlushPage` only writes back when `Changed()` returns true (`tempHash != Hash`).
- Reference counting on pages so that concurrent `RocksInputStream` clones do not free a page out from under each other (`IncrementReferenceCounter`, `DecrementReferenceCounter`).
- Bookkeeping is delegated to `RocksDbIndexStorage` (`GetFilePageCount`, `SetFilePageCount`, `GetFileLength`, `SetFileLength`, `IncrementFileSize`, `GetFilePage`, `SetFilePage`, `SetFilePages`).

#### A.3 `RocksInputStream` / `RocksOutputStream` — Lucene `IndexInput` / `IndexOutput`
`RocksInputStream.cs`, `RocksOutputStream.cs`

- `RocksInputStream` extends `Lucene.Net.Store.IndexInput`: implements `ReadByte`, `ReadBytes`, `Seek`, `GetFilePointer`, `Length`, `Clone` (with `IncrementReferenceCounter`).
- Pages are pulled on demand via `_file.GetPage(_currentPageIndex, usePool: true)` and released on page-cross via `_file.ReleasePage(currentPageBefore)`.
- A `DisposedHandle` is used so that cloning a stream (Lucene clones heavily for parallel cursors) doesn't accidentally see a disposed root stream.
- `RocksOutputStream` extends `IndexOutput`, buffering writes into the active page (`_currentPage[_pagePosition++] = b`) and flushing only when the page changed (`_currentPageChanged`). It also computes a `BufferedChecksum(new CRC32())` because Lucene's segment infos require it.

#### A.4 RocksDB column families dedicated to Lucene
`Mosaik/Graph/src/Graph/Storage/RocksDbStorage.cs` (around lines 50–202) declares four families just for the Lucene directory:

```csharp
internal ColumnFamilyHandle LuceneFileLengthFamily;       // "lucene-file-length"
internal ColumnFamilyHandle LuceneFileSizeFamily;         // "lucene-file-size"
internal ColumnFamilyHandle LuceneFilePagesCountFamily;   // "lucene-file-pages-count"
internal ColumnFamilyHandle LuceneFilePagesFamily;        // "lucene-file-pages"
```

Reads and writes go through `RocksDbIndexStorage` (`Mosaik/Graph/src/Graph/Storage/RocksDbIndexStorage.cs`). Example of a page write (batched with the page-count update):

```csharp
internal void LuceneSetFilePages(string path, int[] index, byte[][] buffer, int pageCount)
{
    using var batch = new WriteBatch();
    for (int i = 0; i < index.Length; i++)
    {
        var bytes = EncodePath(path, index[i]);
        batch.Put(bytes, buffer[i], _rocksDbStorage.LuceneFilePagesFamily);
    }
    var countBytes = EncodePath(path);
    batch.Put(countBytes, BitConverter.GetBytes(pageCount), _rocksDbStorage.LuceneFilePagesCountFamily);
    _rocksDbStorage.DB.Write(batch);
    _rocksDbStorage.TrackChanges(RocksDbChangeType.LuceneFileChanged, path);
}
```

Path encoding uses two static dictionaries to cache UTF-8 byte arrays for `(path, pageIndex)` keys — see `EncodePath` in the same file.

#### A.5 Replication-aware cache invalidation
`RocksDbIndexStorage.HandleChangesFromPrimary` and `RocksDirectory.RaisePathChanged` / `RaisePathDeleted`:

```csharp
case RocksDbChangeType.LuceneFileChanged:
{
    var changedPath = MemoryMarshal.Cast<byte, char>(data);
    foreach (var (directoryPath, directory) in _activeRocksDirectories)
    {
        if (changedPath.StartsWith(directoryPath.AsSpan()))
        {
            directory.RaisePathChanged(changedPath);
        }
    }
    break;
}
```

The directory then clears the page cache for the changed file and pulls in any new files. There is no equivalent surface in SimdPhrase2 today.

**Implications for SimdPhrase2**
- `Storage/ISimdStorage.cs` only exposes stream-style file APIs (`OpenRead`, `OpenWrite`, `OpenReadWrite`, `CreateDirectory`, `DeleteDirectory`, `FileExists`, …). To be hostable inside Mosaik's storage layer SimdPhrase2 would either need (a) a page-block abstraction similar to `RocksFile`, or (b) an `ISimdStorage` implementation that delegates Stream IO to RocksDB. A page-block model fits the existing on-disk roaringish layout better (64-byte aligned pages already; `RocksFile` is 64 KB pages, an exact integer multiple).
- A replication change-notification hook (path-changed / path-deleted) is required if SimdPhrase2 indexes are to be served by read-replicas. Today there is none — files are assumed to be locally consistent.
- The reference-counted page cache + SHA-256 dirty-detection in `RocksFile` is finer-grained than anything SimdPhrase2 needs in its current single-writer model; if/when segments land, a similar cache would let many readers share buffers cheaply.

---

### B. Indexing pipeline

#### B.1 `IndexWriter` configuration (per Lucene bucket)
Both `LuceneTextIndex.InitializeForIndex` and `FileContentTextIndex.InitializeForIndex` use the same shape:

```csharp
// FileContentTextIndex.cs : InitializeForIndex(int i)
_luceneDirectories[i] = new LuceneOnRocks.RocksDirectory(Parent.Storage.IndexDB, indexID);
_fieldCaches[i]       = new FieldCacheImpl();
_luceneAnalyzers[i]   = new StandardAnalyzer(LuceneVersion.LUCENE_48, CharArraySet.EMPTY_SET);

var indexConfig = new IndexWriterConfig(LuceneVersion.LUCENE_48, _luceneAnalyzers[i])
{
    Similarity              = new BM25Similarity(),
    RAMBufferSizeMB         = 256,            // 128 for LuceneTextIndex
    RAMPerThreadHardLimitMB = 64,             // 16  for LuceneTextIndex
    OpenMode                = OpenMode.CREATE_OR_APPEND,
    MergeScheduler          = new ConcurrentMergeScheduler(),
    MergePolicy             = new LogByteSizeMergePolicy(),
    MergedSegmentWarmer     = new SimpleMergedSegmentWarmer(InfoStream.NO_OUTPUT),
};

if (!Parent.ReadOnly)
{
    _luceneWriters[i] = new IndexWriter(_luceneDirectories[i], indexConfig);
    using var reader = _luceneWriters[i].GetReader(false); // force NRT reader init
}
```

Notes worth keeping for any replacement:
- The analyzer is `StandardAnalyzer` with **empty stopwords** (`CharArraySet.EMPTY_SET`) — Mosaik does its own stopword filtering at tokenization time.
- BM25 scoring is fixed; no DFR/LMJelinekMercer/Boolean similarity.
- `RAMBufferSizeMB` and `RAMPerThreadHardLimitMB` are the only memory knobs in use.
- `LogByteSizeMergePolicy` + `ConcurrentMergeScheduler` is the default policy; no tiered merge.
- `OpenMode = CREATE_OR_APPEND` — indexes survive process restarts.

#### B.2 Parallelism / sharding by UID
`LuceneTextIndex` and `FileContentTextIndex` keep arrays of writers/directories/analyzers/field caches and dispatch by hash:

```csharp
// LuceneTextIndex.cs : GetIndex
private int GetIndex(UID128 uid)
{
    if (UnsafeData.IndexParallelism == 1) return 0;
    return (int)(ulong)(Hashes.Combine(uid.High, uid.Low) % UnsafeData.IndexParallelism);
}

private string GetDirectoryNameFor(int i)
    => $"LUC-{UID}" + (i > 0 ? $"-SEG-{i}" : ""); // bucket 0 keeps its old name for compat
```

`LuceneTextIndexOptions.IndexParallelism` defaults to `1`; `FileContentTextIndexOptions.IndexParallelism` defaults to `16`. Changing the value triggers a full rebuild of the index (`Configure` in `LuceneTextIndex.cs`):

```csharp
if (paralellismBefore != paralellismAfter)
{
    DeleteLuceneStorage(paralellismBefore);
    InitializeIndex(paralellismAfter);
    Parent.NonBlockingQueue.QueueAsyncTask(
        "Recreate LuceneTextIndex due to changed parallelism",
        (g) => g.Indexes.RecreateIndexAsync(this));
}
```

#### B.3 Document shape
The Lucene `Document` is intentionally narrow. Example from `LuceneTextIndex.AddDocumentToLuceneIndex`:

```csharp
var doc = new Lucene.Net.Documents.Document();
doc.AddStringField(_LUCENE_DOCUID_FIELD,             docOrNodeUIDasString, Field.Store.NO);
doc.AddNumericDocValuesField(_LUCENE_DOCNODEUID_LOW_FIELD,  (long)nodeUID.Low);
doc.AddNumericDocValuesField(_LUCENE_DOCNODEUID_HIGH_FIELD, (long)nodeUID.High);
doc.Add(new TextField(_LUCENE_VALUE_FIELD, docStream)); // docStream is a pre-tokenized TokenStream
luceneWriter.UpdateDocument(new Term(_LUCENE_DOCUID_FIELD, docOrNodeUIDasString), doc);
```

Field types in use:
- `StringField` — for the doc UID key (not stored — only indexed as a term). Used as the `UpdateDocument` term.
- `NumericDocValuesField` — for the high/low 64-bit halves of the graph `UID128`. Read at search time via `FieldCache.GetInt64s` to reconstruct the UID without loading the full document.
- `TextField` (with an explicit `TokenStream`) — the actual indexed text.
- `StoredField` (only in the legacy `LuceneIndex`) — for storing the binary UID bytes via `BytesRef`.

`FileContentTextIndex.GetLuceneDocumentForFile` adds more field types:

```csharp
doc.AddStringField(_LUCENE_FILEUID_FIELD, fileUIDs, Field.Store.NO);
doc.AddNumericDocValuesField(_LUCENE_FILEUID_LOW_FIELD,  (long)fileUID.Low);
doc.AddNumericDocValuesField(_LUCENE_FILEUID_HIGH_FIELD, (long)fileUID.High);
doc.Add(new TextField  (_LUCENE_FILENAME_FIELD,   filePathStream));
doc.Add(new StringField(_LUCENE_FILETYPE_FIELD,   fileEntry.ContentType ?? "", Field.Store.NO));
doc.Add(new StringField(_LUCENE_FILESOURCE_FIELD, fileEntry.Source      ?? "", Field.Store.NO));
if (enableNgramIndexForTitle)
{
    var stream      = new WhitespaceTokenizer(LuceneVersion.LUCENE_48, new StringReader(filenameDoc.Value));
    var lowerFilter = new LowerCaseFilter   (LuceneVersion.LUCENE_48, stream);
    var ngrams      = new NGramTokenFilter  (LuceneVersion.LUCENE_48, lowerFilter, 3, 3);
    doc.Add(new TextField(_LUCENE_FILENAME_NGRAM_FIELD, ngrams));
}
doc.Add(new StringField(_LUCENE_FILEEXTENSION_FIELD, extension, Field.Store.NO));
```

#### B.4 Upsert / Delete semantics
Mosaik never calls `IndexWriter.AddDocument`. The upsert is always done via:

```csharp
luceneWriter.UpdateDocument(new Term(_LUCENE_DOCUID_FIELD, uid.ToString()), doc);
```

Deletions go via `Term`-keyed delete:

```csharp
// LuceneTextIndex.cs : UpdateDeletedOnLucene
foreach (var uid in deleted)
{
    luceneWriter.DeleteDocuments(new Term(_LUCENE_DOCUID_FIELD, uid.ToString()));
}
_luceneWriters[group.Key].Commit();
```

A SimdPhrase2 replacement needs a stable per-document key (effectively a primary-key field) and atomic delete-then-add by that key. Today there is neither.

#### B.5 Token streams — pre-tokenized, language-aware
The text is **not** tokenized by Lucene. It is parsed in advance by the Catalyst NLP pipeline, then handed to Lucene through a custom `TokenStream` subclass that emits each token plus optional lemma + URL/`+` sub-token overlays with `PositionIncrement = 0`.

`DocumentTokenStream.IncrementToken` (`Mosaik/Graph/src/Indexes/Lucene/Base/DocumentTokenStream.cs`) handles:
- A stop-word filter using a `HashSet<ulong>` of `IgnoreCaseHash64()` values (Snowball list, per language).
- `IgnoreCase` (lowercase via `char.ToLowerInvariant`).
- `IndexLemma` — emits the lemma as an overlapping token at the same position when it differs from the surface form.
- `NormalizeDiacritics` — Unicode `FormD` normalize + non-spacing-mark filter.
- `MaximumTokensCount` per document (`LuceneTextIndexOptions.MaximumTokensCount = 500_000`, `FileContentTextIndexOptions.MaximumTokensCount = 100_000`).
- URL-shaped tokens get split into host/path/query parts as positional overlays.

`FilePathTokenStream` is a sibling stream that emits both the original tokens and an additional stream of "path" tokens split on `\/`; the file-name portion is split again on `FileNameSplitChars` (`{' ', '!', '&', '(', ')', '+', ',', '-', '.', ':', ';', '?', '[', ']', '_', '|', '~'}`).

There's also a `DocumentsTokenStream` (`DocumentTokenStream.cs` line 404) which iterates over several `ImmutableDocument`s and tracks a `baseOffset` so that combined-page content has correct offsets.

#### B.6 Per-batch indexing loop (`ProcessNewNodesAsync`)
For each batch of nodes the index:
1. Filters out nodes that no longer exist, queuing them as deletes.
2. Runs the Catalyst pipeline (`pipeline.ProcessSingle(doc)`) on documents that are in `WaitForNode` mode.
3. Groups documents by `GetIndex(uid)` and uses the bucket's `IndexWriter` to upsert.
4. Commits per bucket: `lw.Commit()`.
5. Wraps long sections with `Measure` and emits partial logs every 5 000 docs.

Key reliability behaviors worth noting:
- `OutOfMemoryException` -> dispose this bucket's writer and re-throw so the host can recover.
- `ObjectDisposedException` (writer torn down concurrently) -> return the remaining `toIndex` so the queue retries.
- Memory pressure (`memoryLimit < 0`) -> spill remainder back into a `pending` list.
- Per-document yielding via `Parent.WaitCacheSync` / `Parent.ShouldYieldIndexing()` so indexing doesn't starve the rest of the graph.

#### B.7 Commit, flush, merge, trim, check
- `IndexWriter.Flush(triggerMerge, applyAllDeletes)` and `Commit()` after each batch.
- `Trim()` / `TrimAsync()` calls `ForceMerge(1)`.
- `FlushDirectories(int? targetSegments)` — `ForceMergeDeletes()` + `ForceMerge(target, doWait: true)` (or `MaybeMerge()`) + `Flush(true, true)`.
- `CheckIndexAsync(bool fixAnyErrors)` runs `new CheckIndex(directory).DoCheckIndex()`, optionally `FixIndex(indexStatus)` if `NumBadSegments > 0`.
- `EstimateDiskSize()` sums `RocksDirectory.GetSizeInBytes()` per bucket.
- `GetMemoryUsage()` sums `IndexWriter.RamSizeInBytes()` for writers and `RocksDirectory.GetMemoryUsage()` for pages.
- `DisposeWritersImmediatelly()` is an emergency dispose-and-reopen path used when the writer arrays are torn down under memory pressure.

**Implications for SimdPhrase2**
- `Indexer.cs` today re-creates the index from scratch on construction (`if (_storage.DirectoryExists(_indexName)) _storage.DeleteDirectory(_indexName);`) — Mosaik would need `OpenMode.CreateOrAppend`, per-batch commits, and segment merges to be usable.
- No per-document delete or update; the replacement primitive (delete-by-key, upsert-by-key) is missing.
- No multi-field document type; today the indexer accepts only `(content, docId)`.
- No multi-bucket parallelism; in Mosaik the file-content index is sharded 16-way by UID hash.
- No `CheckIndex` equivalent; corruption detection/repair would have to be added.
- The `ITextTokenizer` abstraction is fine, but to match Mosaik it'd need to ingest pre-tokenized streams (with `PositionIncrement = 0` overlays for lemmas/synonyms/URL parts) instead of always tokenizing the raw string itself.

---

### C. Search pipeline

#### C.1 Reader lifecycle
Mosaik uses `IndexWriter.GetReader(false)` for the primary (NRT reader) and `DirectoryReader.Open(directory)` for read-only replicas:

```csharp
private DirectoryReader GetReaderForBucket(int bucketIndex)
{
    if (Parent.ReadOnly)
    {
        if (_luceneDirectories?[bucketIndex] is object)
            return DirectoryReader.Open(_luceneDirectories[bucketIndex]);
        return null;
    }
    return _luceneWriters is object ? _luceneWriters[bucketIndex]?.GetReader(false) : null;
}
```

There is no `SearcherManager` pooling. A new `DirectoryReader` is obtained per search (and disposed via `using`) — that is the reason the index keeps its own `FieldCacheImpl` per bucket: each reader's atomic readers retain the field cache so subsequent searches don't re-decode the numeric DocValues.

#### C.2 Query construction — `ISearchExpression.ToLucene(...)`
Mosaik builds its Lucene queries by walking its own `ISearchExpression` tree (defined in `Mosaik/Graph/src/Indexes/Base/IIndex.cs`):

```csharp
public Lucene.Net.Search.Query ToLucene(
    string                    candidateField,
    bool                      ignoreCase,
    HashSet<ulong>            stopWordsSet,
    bool                      alwaysIgnoreStopWords,
    Func<string, ulong>       hasher,
    Func<string, string>      filterFieldMapper,
    Func<string[], string[]>  tokensTransformer = null,
    bool                      forceShouldMatch  = false,
    int?                      customFuziness    = null);
```

The token-level conversion at the leaves of the tree (`SearchToken.ToLucene` around `IIndex.cs:1398`) dispatches on `MatchTypeEnum`:

| MatchType | Lucene query emitted |
|---|---|
| `StartsWith` | `PrefixQuery(new Term(field, tok))` |
| `Contains` | `WildcardQuery(new Term(field, "*" + tok + "*"))` |
| `Exact` | `MultiPhraseQuery` for a phrase, or `TermQuery` / `FuzzyQuery` if fuzziness > 0 |
| `StartsWithAny` | `PrefixQuery` per token, all `SHOULD` |
| `ContainsAny` | `WildcardQuery` per token, all `SHOULD` |
| `ExactAny` | `TermQuery` (or `FuzzyQuery` when fuzziness > 0) per token, all `SHOULD` |

It also computes `MinimumNumberShouldMatch` from either an explicit count or a percentage (`MinimumShouldMatch{,Percent}`), applies per-clause `Boost`, and respects auto-fuzziness:

```csharp
private int GetFuzziness(string tk, int? fuzziness)
{
    if (fuzziness is null || fuzziness == 0) return 0;
    if (fuzziness      > 0)                  return fuzziness.Value;
    else if (tk.Length < 2)                  return 0;
    else if (tk.Length < 5)                  return 1;
    else                                     return 2;
}
```

Higher-level expressions combine into `BooleanQuery` with `Occur.MUST` / `Occur.SHOULD` / `Occur.MUST_NOT`. For `_FileEntry` the search composes per-field queries with a per-field `Boost` (the filename gets `SearchBoostFactorTitle`, default 10x):

```csharp
// FileContentTextIndex.cs : DoInnerSearch
var contentQuery = query.ToLucene(_LUCENE_FILECONTENT_FIELD, …);
var nameQuery    = query.ToLucene(_LUCENE_FILENAME_FIELD,    …);
var sourceQuery  = query.ToLucene(_LUCENE_FILESOURCE_FIELD,  …);
var extQuery     = query.ToLucene(_LUCENE_FILEEXTENSION_FIELD, …);
nameQuery.Boost  = GetDataValue(static d => d.SearchBoostFactorTitle);

luceneQuery = new BooleanQuery();
luceneQuery.Add(contentQuery, Occur.SHOULD);
luceneQuery.Add(nameQuery,    Occur.SHOULD);
luceneQuery.Add(sourceQuery,  Occur.SHOULD);
if (ngramQuery is object) luceneQuery.Add(ngramQuery, Occur.SHOULD);
luceneQuery.Boost = GetDataValue(static d => d.SearchBoostFactor.Value);
```

The whole query is wrapped in a `MatchAllDocsQuery`/`BooleanQuery` if `NeedToStartWithMatchAll` is set (used by negation-only queries so that they have a base set to subtract from):

```csharp
if (query.NeedToStartWithMatchAll)
{
    var boolQuery = new BooleanQuery();
    boolQuery.Add(new MatchAllDocsQuery(), Occur.SHOULD);
    boolQuery.Add(luceneQuery,             Occur.SHOULD);
    luceneQuery = boolQuery;
}
```

There's also a "RFO" `lock (query)` around the conversion to work around a multi-thread issue:

```csharp
lock (query) // RFO: There is a strange multi-threading issue if we don't lock around the query here - but I don't understand why
{
    luceneQuery = query.ToLucene(...);
}
```

#### C.3 Parallel multi-bucket search + `ICollector`
A search fans out across the buckets, each bucket using its own reader+field-cache, results are merged via `KeyedScoredUIDs.Concat_ReturnOne`:

```csharp
// LuceneTextIndex.SearchAsync
for (int index = 0; index < indexParallelism; index++)
{
    var capturedIndex = index;
    allTasks[index] = SearchThreads.DoWorkAsync(NodeTypeUID, (cancellationToken) =>
    {
        using var reader = GetReaderForBucket(capturedIndex);
        if (reader is null) return;
        var cache = _fieldCaches[capturedIndex];
        var results = DoInnerSearch(query, reader, cache, cancellationToken);
        lock (finalLock)
        {
            finalResults = finalResults is null
                ? results
                : KeyedScoredUIDs.Concat_ReturnOne(finalResults, results);
        }
    }, cancellationToken);
}
await Task.WhenAll(allTasks);
```

Inside each bucket a custom `ICollector` is used (rather than `TopScoreDocCollector`), so the collector can:

- Read the doc's UID **from the field cache (NumericDocValues)** rather than fetching the full document:

```csharp
public void Collect(int doc)
{
    var highCache = _fieldCache.GetInt64s(_readerContext.AtomicReader, _LUCENE_DOCNODEUID_HIGH_FIELD, false);
    var high      = (ulong)highCache.Get(doc);
    if (_uidFilter.IsActive && !_uidFilter.MaybeContains(high)) return;

    var lowCache = _fieldCache.GetInt64s(_readerContext.AtomicReader, _LUCENE_DOCNODEUID_LOW_FIELD, false);
    var low      = (ulong)lowCache.Get(doc);

    var uid = new UID128(high, low);
    if (_uidFilter.IsActive  && !_uidFilter.Contains(uid))         return;
    if (!_graph.HasNode(uid))                                      return;

    if (_cancellationToken.IsCancellationRequested
        || _maximumHits-- <= 0
        || _stopwatch.GetElapsedTime().Ticks > _timeoutTicks)
    {
        _results.Incomplete = true;
        throw new CollectionTerminatedException();
    }

    _results[new TypedUID128(_nodeTypeUID, uid)] = _scorer.GetScore();
}
```

- Apply a `UIDFilter` allowlist (`OnlyTheseUIDs`).
- Apply a probabilistic access filter (`IUID128ProbabilisticFilter`) for permission filtering.
- Stop early via `CollectionTerminatedException` on cancellation, max-hit, or timeout — the collector is the only place the SLA timeout is enforced.

`AcceptsDocsOutOfOrder` is `true` so Lucene is free to skip the priority queue overhead.

#### C.4 Index introspection / autocomplete-adjacent APIs
The indexes expose runtime stats via Lucene's `IndexSearcher.CollectionStatistics` and `TermStatistics`:

```csharp
// LuceneTextIndex.GetStatus
using var reader = GetReaderForBucket(i);
var luceneSearcher = new IndexSearcher(reader);
var stats          = luceneSearcher.CollectionStatistics(_LUCENE_VALUE_FIELD);
status.Set("Lucene.MaxDoc",            stats.MaxDoc.ToString())
      .Set("Lucene.SegmentCount",      string.Join(",", segmentCounts))
      .Set("Lucene.SegmentInfo",       string.Join("; ", segmentStrings))
      .Set("Lucene.DocCount",          stats.DocCount.ToString())
      .Set("Lucene.SumTotalTermFreq",  stats.SumTotalTermFreq.ToString())
      .Set("Lucene.SumDocFreq",        stats.SumDocFreq.ToString());

// GetKeywordStatsAsync
var term         = new Term(_LUCENE_VALUE_FIELD, normalized);
var termContext  = TermContext.Build(reader.Context, term);
var termStats    = luceneSearcher.TermStatistics(term, termContext);
result.DocFreq       = termStats.DocFreq;
result.TotalTermFreq = termStats.TotalTermFreq;
```

`FileContentTextIndex` further exposes:

- `HasIndexed(UID128 uid)` — `TermQuery(new Term(_LUCENE_FILEUID_FIELD, uid))` then `luceneSearcher.Search(q, 1)`.
- N-gram phrase query building (3-gram, `WhitespaceTokenizer` → `LowerCaseFilter` → `NGramTokenFilter(3,3)` → `NGramPhraseQuery`).

**Implications for SimdPhrase2**
- The current `Searcher` has two disjoint paths (`Search` returns `List<uint>`, `SearchBM25` returns `List<(uint, float)>`); Mosaik needs a single path that supports BM25 scoring **and** Boolean composition **and** a custom result sink. The "Unified Query & Scoring Model" item in the roadmap above is what this corresponds to in Lucene terms.
- A per-hit visitor callback (with the ability to early-terminate) is essential: Mosaik bakes its UID filter, access filter, timeout, and max-hits into the collector, not the query. SimdPhrase2 would need an equivalent `ICollector`-style abstraction (or an `IAsyncEnumerable<Hit>` with `CancellationToken`) — list-returning APIs would force every hit to be materialized first.
- DocValues retrieval (reading a per-doc numeric field by doc id without loading the document) is what makes Mosaik's collector cheap — every match resolves to a 128-bit UID through two `GetInt64s(... doc)` lookups. SimdPhrase2's `DocumentStore` would need a similarly cheap "doc id → external id" mapping.
- Wildcard / prefix / fuzzy support is required at the leaf level (`PrefixQuery`, `WildcardQuery`, `FuzzyQuery`), and `MultiPhraseQuery` is used for the common phrase case.
- Multi-field queries with per-field boost are the norm, not the exception — single-field search would already feel like a regression for the file-content case.
- Per-shard parallel search with merged results is required for the 16-bucket file-content path; today SimdPhrase2 has neither sharding nor concurrent search.
- `MatchAllDocsQuery` is used as the base for negation-only queries; the engine must support `MUST_NOT` on top of a match-all set.

---

### D. Lifecycle / operational surface

Beyond the per-batch indexing and search paths, Mosaik exercises Lucene through a relatively wide operational API:

- `StartAsync` / `StopAsync` per index — flushes and disposes writers, disposes directories.
- `ClearAsync` — `ListAll()` + `DeleteFile()` per file, then reinitialize.
- `DeleteLuceneStorage()` (also `ICanCleanIndex`) — same as Clear but without reinitialize, used when removing an index entirely.
- `Configure(SettingsHolder)` — wires UI/admin settings to `LuceneTextIndexData` (mode, ignore case, index stop words, search boost factor, max tokens, parallelism, normalize diacritics, fuzziness…). Changing `IndexParallelism` triggers a full rebuild.
- `Graph.CreateStartStop.cs:874` calls `LuceneOnRocks.RocksFile.DrainPool()` during shutdown to release pooled buffers.
- `GraphStorage.CreateStartStop.cs:972` (`DisposeAllLuceneWriters`) iterates `Parent.Indexes.OfType<LuceneTextIndex>()` and `OfType<FileContentTextIndex>()` to force-dispose writers under memory pressure.
- `AddIndex.cs:79` (a `IGraphTask`) is the factory that wires user-driven index creation through to `graph.Indexes.AddLuceneIndexAsync(...)`.

Implications:
- Several operations (clear, force-merge, check-index, parallel-rebuild) have no counterpart in SimdPhrase2 today. They become required if SimdPhrase2 indexes are to be administered the same way.
- `Lucene.Net.Index.CheckIndex` + `FixIndex` is the corruption recovery surface. A replacement would need a similar "scan + repair" routine, or — given Roaringish's simpler layout — a fast recompute-from-source path.

---

### E. Cross-reference summary — what to add to SimdPhrase2

The roadmap items earlier in this file already cover the largest pieces. The Mosaik audit suggests a few additional items / refinements:

- **Storage adapter for an embedded KV store.** Today's `ISimdStorage` is stream-based. To live next to Mosaik's RocksDB we need either a page-block backend or a RocksDB-backed `ISimdStorage` implementation, plus a path-changed notification hook for read-replica cache invalidation.
- **Upsert / delete by external id.** Add an indexer-level "primary key field" so that `UpdateDocument(term, doc)` and `DeleteDocuments(term)` semantics are first-class. Mosaik depends on this for every node update.
- **Custom result collector (visitor) with early termination.** Mosaik's `ICollector` carries cancellation, deadline, per-hit allow/deny filters, and a max-hits budget. An `IAsyncEnumerable<Hit>` with `CancellationToken` would be the modern shape; a callback-based collector is fine too.
- **Doc-id → external-id resolution at search time.** Lucene's `NumericDocValuesField` + `FieldCache.GetInt64s` is what avoids per-hit document fetches. A SimdPhrase2 equivalent (e.g. a side table keyed by internal doc id returning a `Span<byte>`) is needed before any practical replacement.
- **Multi-field documents with per-field boost.** The `FileContentTextIndex` shape (content + filename + source + extension + optional n-gram title) is not optional — it's already in production and uses different analyzers per field.
- **Sharding / per-shard parallel search and merge.** Default `IndexParallelism = 16` for files. The Searcher fan-out and result merge model needs to exist at the library level, not pushed onto callers.
- **`MatchAllDocsQuery` semantics + Boolean nesting + `MinimumNumberShouldMatch` + per-clause boost.** All used today; missing in the current `BooleanQueryParser`.
- **Wildcard / Prefix / Fuzzy / MultiPhrase leaves.** All four are emitted by `SearchToken.ToLucene` — none have an analogue in `SimdPhrase2` today.
- **Custom `TokenStream`-style input.** Mosaik never lets Lucene tokenize; it submits pre-tokenized streams with `PositionIncrement = 0` overlays for lemmas, URL parts, and `+`-split tokens. The `ITextTokenizer` abstraction would need to accept (or be replaced by) something that exposes positions and synonym/overlay tokens.
- **Operational surface: ForceMerge / CheckIndex+Fix / EstimateDiskSize / RamSizeInBytes / Trim / DisposeImmediately / Clear / Recreate.** These are referenced by admin UIs, scheduled tasks, and memory-pressure recovery code in Mosaik.
- **Replication-aware reads.** Read-only replicas open a fresh `DirectoryReader` per search and rely on the storage layer raising path-changed events. SimdPhrase2 has no concept of read-only mode or replication today.

These items extend (rather than replace) the existing roadmap; the "Implementation Order" section above remains a reasonable sequence — most additions cluster under items 2 (Unified Query/Scoring), 3 (Fielded), 4 (Segments) and a new item *0. Storage Abstraction* that has to land before Mosaik integration becomes practical.
