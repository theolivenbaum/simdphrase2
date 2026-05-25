using System;
using System.Collections.Generic;
using RocksDbSharp;

namespace SimdPhrase2.Storage
{
    /// <summary>
    /// Single RocksDB handle that backs an index. Owns the column family layout
    /// used throughout the library:
    ///
    ///   meta              - small singletons: next_segment_id, stats, common_tokens,
    ///                       field_count. Keyed by short utf-8 strings.
    ///   docs              - docId(4 BE)            -> utf-8 document bytes.
    ///   doc_lengths       - docId(4 BE)            -> int32[fieldCount] LE.
    ///   postings          - segId(8 BE)|field(1)|token-utf8 -> packed posting ulongs (raw bytes).
    ///   seg_meta          - segId(8 BE)            -> binary SegmentInfo blob.
    ///   seg_tokens        - segId(8 BE)            -> serialised token map (FieldToken -> count info).
    ///   seg_deletes       - segId(8 BE)            -> RoaringBitmap.
    ///   seg_live_docs     - segId(8 BE)            -> RoaringBitmap.
    ///
    /// Open in read/write mode by default. <see cref="OpenReadOnly"/> opens a
    /// read-only handle suitable for a Searcher when an Indexer is not active.
    /// </summary>
    public sealed class SimdPhraseDb : IDisposable
    {
        public const string CfMeta = "meta";
        public const string CfDocs = "docs";
        public const string CfDocLengths = "doc_lengths";
        public const string CfPostings = "postings";
        public const string CfSegMeta = "seg_meta";
        public const string CfSegTokens = "seg_tokens";
        public const string CfSegDeletes = "seg_deletes";
        public const string CfSegLiveDocs = "seg_live_docs";

        // Meta keys (utf-8 strings).
        public const string MetaKeyNextSegmentId = "next_segment_id";
        public const string MetaKeyStats = "stats";
        public const string MetaKeyCommonTokens = "common_tokens";
        public const string MetaKeyFieldCount = "field_count";

        public RocksDb Db { get; }
        public string Path { get; }
        public bool ReadOnly { get; }

        public ColumnFamilyHandle Meta { get; }
        public ColumnFamilyHandle Docs { get; }
        public ColumnFamilyHandle DocLengths { get; }
        public ColumnFamilyHandle Postings { get; }
        public ColumnFamilyHandle SegMeta { get; }
        public ColumnFamilyHandle SegTokens { get; }
        public ColumnFamilyHandle SegDeletes { get; }
        public ColumnFamilyHandle SegLiveDocs { get; }

        private static readonly string[] AllCfNames = new[]
        {
            CfMeta, CfDocs, CfDocLengths, CfPostings, CfSegMeta, CfSegTokens, CfSegDeletes, CfSegLiveDocs,
        };

        private SimdPhraseDb(RocksDb db, string path, bool readOnly)
        {
            Db = db;
            Path = path;
            ReadOnly = readOnly;
            Meta = db.GetColumnFamily(CfMeta);
            Docs = db.GetColumnFamily(CfDocs);
            DocLengths = db.GetColumnFamily(CfDocLengths);
            Postings = db.GetColumnFamily(CfPostings);
            SegMeta = db.GetColumnFamily(CfSegMeta);
            SegTokens = db.GetColumnFamily(CfSegTokens);
            SegDeletes = db.GetColumnFamily(CfSegDeletes);
            SegLiveDocs = db.GetColumnFamily(CfSegLiveDocs);
        }

        public static SimdPhraseDb Open(string path)
        {
            System.IO.Directory.CreateDirectory(path);

            var options = new DbOptions()
                .SetCreateIfMissing(true)
                .SetCreateMissingColumnFamilies(true);

            var (cfOptionsHot, cfOptionsDefault) = BuildCfOptions();
            var declared = BuildColumnFamilies(path, options, cfOptionsHot, cfOptionsDefault);

            var db = RocksDb.Open(options, path, declared);

            // If the existing DB on disk was missing any of our declared CFs we just
            // skipped them above; create them now (RocksDB only auto-creates missing
            // CFs that were listed in the open call).
            if (RocksDb.TryListColumnFamilies(options, path, out var existing))
            {
                var existingSet = new HashSet<string>(existing);
                foreach (var name in AllCfNames)
                {
                    if (!existingSet.Contains(name))
                    {
                        var opts = name == CfPostings ? cfOptionsHot : cfOptionsDefault;
                        db.CreateColumnFamily(opts, name);
                    }
                }
            }
            return new SimdPhraseDb(db, path, readOnly: false);
        }

        // 256MB block cache shared across all column families. RocksDB caches
        // decompressed block contents here, so repeated reads to the same posting
        // lists / doc lengths come back from RAM. The cache is process-wide and
        // shared so multiple SimdPhraseDb instances in the same process can reuse it.
        private static readonly Cache _sharedBlockCache = Cache.CreateLru(256UL * 1024 * 1024);

        private static (ColumnFamilyOptions hot, ColumnFamilyOptions @default) BuildCfOptions()
        {
            // Tuned BlockBasedTable for posting-list lookups: 16KB block size keeps
            // per-Get bytes-read low, and bloom filters short-circuit non-existent tokens.
            var hot = new ColumnFamilyOptions();
            hot.SetBlockBasedTableFactory(new BlockBasedTableOptions()
                .SetBlockSize(16 * 1024)
                .SetBlockCache(_sharedBlockCache)
                .SetFilterPolicy(BloomFilterPolicy.Create(10, false))
                .SetCacheIndexAndFilterBlocks(true));
            var @default = new ColumnFamilyOptions();
            @default.SetBlockBasedTableFactory(new BlockBasedTableOptions()
                .SetBlockCache(_sharedBlockCache));
            return (hot, @default);
        }

        // Declare just the column families that already exist on disk - RocksDB errors
        // if we open with a CF that the DB doesn't know about, so we pass existing only
        // and create the rest right after open.
        private static ColumnFamilies BuildColumnFamilies(string path, DbOptions options, ColumnFamilyOptions hot, ColumnFamilyOptions @default)
        {
            var declared = new ColumnFamilies();
            declared.Add("default", @default);

            HashSet<string> existingSet;
            if (RocksDb.TryListColumnFamilies(options, path, out var existing) && existing.Length > 0)
            {
                existingSet = new HashSet<string>(existing);
            }
            else
            {
                // Fresh DB - declare everything; SetCreateMissingColumnFamilies will create them.
                existingSet = new HashSet<string>(AllCfNames) { "default" };
            }

            foreach (var name in AllCfNames)
            {
                if (existingSet.Contains(name))
                {
                    var opts = name == CfPostings ? hot : @default;
                    declared.Add(name, opts);
                }
            }
            return declared;
        }

        public static SimdPhraseDb OpenReadOnly(string path)
        {
            if (!System.IO.Directory.Exists(path))
                throw new System.IO.DirectoryNotFoundException(path);

            var options = new DbOptions()
                .SetCreateIfMissing(false);

            var cfOptionsHot = new ColumnFamilyOptions();
            cfOptionsHot.SetBlockBasedTableFactory(new BlockBasedTableOptions()
                .SetBlockSize(16 * 1024)
                .SetFilterPolicy(BloomFilterPolicy.Create(10, false)));
            var cfOptionsDefault = new ColumnFamilyOptions();

            var declared = new ColumnFamilies();
            declared.Add("default", cfOptionsDefault);
            foreach (var name in AllCfNames)
            {
                var opts = name == CfPostings ? cfOptionsHot : cfOptionsDefault;
                declared.Add(name, opts);
            }

            var db = RocksDb.OpenReadOnly(options, path, declared, errIfLogFileExists: false);
            return new SimdPhraseDb(db, path, readOnly: true);
        }

        public ColumnFamilyHandle ColumnFamily(string name) => Db.GetColumnFamily(name);

        public void Dispose() => Db.Dispose();
    }
}
