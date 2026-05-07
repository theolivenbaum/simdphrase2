# SimdPhrase2

A C# port of the SimdPhrase library, targeting .NET 10.0. This library provides
high-performance phrase search using SIMD optimizations (AVX-512) and
compressed bitsets (Roaringish).

## Features

- **Single-term and phrase search** with SIMD-accelerated posting-list intersection.
- **BM25 scoring** with optional **per-field boosts**.
- **Boolean queries** with `AND`, `OR`, `NOT`, parentheses, and `field:term` syntax.
- **Multi-field indexing** — store and search distinct fields like `title` / `body` per document.
- **Immutable segment architecture** with **auto-compaction** to keep segment count bounded.
- **Soft deletions** via a `LiveDocs` set; physically reclaimed on the next compaction.
- Pluggable `ITextTokenizer` and `ISimdStorage` abstractions.

## Quick example

```csharp
using SimdPhrase2;

// Index three documents with two fields each, applying a 3x boost to "title"
// for BM25 scoring.
var options = new IndexerOptions
{
    Fields = new() {
        new FieldOptions("title", boost: 3.0f),
        new FieldOptions("body",  boost: 1.0f),
    }
};

using (var indexer = new Indexer("./my_index", options))
{
    indexer.AddDocument(new IndexDocument(0)
        .Add("title", "Quick brown fox")
        .Add("body",  "A lazy dog watches a fox jump over."));
    indexer.AddDocument(new IndexDocument(1)
        .Add("title", "Sleeping dog")
        .Add("body",  "Apple bananas cherry."));
    indexer.Commit();
}

using (var searcher = new Searcher("./my_index"))
{
    var byTitle = searcher.Search("fox", "title");                  // [0]
    var ranked  = searcher.SearchBM25("fox dog", "body");           // BM25 over body
    var bool_   = searcher.SearchBoolean("title:fox AND body:dog"); // [0]
}
```

### Deletions

```csharp
using (var indexer = new Indexer("./my_index", new IndexerOptions { ClearExisting = false }))
{
    indexer.DeleteDocument(0);  // soft delete
    indexer.Commit();           // persist deletion set
}
```

Deleted documents are filtered out of all searches. Disk reclamation happens
during the next segment compaction (auto or forced via `Indexer.CompactAll()`).

## Benchmarks

Performance numbers vs Lucene.Net 4.8.0-beta — including search, NGram,
indexing, and deletion throughput — live in [benchmark.md](./benchmark.md).
