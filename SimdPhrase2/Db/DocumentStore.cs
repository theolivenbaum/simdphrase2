using System;
using System.Buffers.Binary;
using System.Text;
using RocksDbSharp;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Db
{
    // Read-only document content store backed by the `docs` column family. The
    // Indexer writes content directly into the commit-time WriteBatch; this class
    // is only used by the Searcher to fetch document text on demand.
    public sealed class DocumentStore
    {
        private readonly SimdPhraseDb _db;
        private static readonly Utf8StringDeserializer _deserializer = new();

        public DocumentStore(SimdPhraseDb db)
        {
            _db = db;
        }

        public string GetDocument(uint docId)
        {
            Span<byte> keyBuf = stackalloc byte[4];
            Keys.WriteDocId(keyBuf, docId);
            // ISpanDeserializer decodes directly from the native value buffer -
            // saves an intermediate managed byte[] vs Get(byte[]).
            return _db.Db.Get(keyBuf, _deserializer, _db.Docs);
        }

        private sealed class Utf8StringDeserializer : ISpanDeserializer<string>
        {
            public string Deserialize(ReadOnlySpan<byte> buffer)
                => Encoding.UTF8.GetString(buffer);
        }
    }

    // Per-doc field length store backed by the doc_lengths CF.
    public sealed class DocLengthStore
    {
        private readonly SimdPhraseDb _db;
        private readonly int _fieldCount;

        public DocLengthStore(SimdPhraseDb db, int fieldCount)
        {
            _db = db;
            _fieldCount = fieldCount;
        }

        public void AddToBatch(WriteBatch batch, uint docId, ReadOnlySpan<int> lengthsPerField)
        {
            if (lengthsPerField.Length != _fieldCount) throw new ArgumentException("Field count mismatch");
            int bytes = 4 * _fieldCount;
            var valBuf = new byte[bytes];
            for (int i = 0; i < _fieldCount; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(valBuf.AsSpan(i * 4, 4), lengthsPerField[i]);
            }
            var keyBuf = Keys.DocIdKey(docId);
            batch.Put(keyBuf, valBuf, _db.DocLengths);
        }

        // Returns the length of `field` for `docId`, or 0 if not present.
        // Uses GetFixedSizeValue to fill a stack buffer directly, avoiding the
        // intermediate byte[] that the plain Get returns.
        public int GetLength(uint docId, byte field)
        {
            Span<byte> keyBuf = stackalloc byte[4];
            Keys.WriteDocId(keyBuf, docId);

            int bytes = 4 * _fieldCount;
            Span<byte> valBuf = bytes <= 64 ? stackalloc byte[bytes] : new byte[bytes];
            if (!_db.Db.GetFixedSizeValue(keyBuf, valBuf, _db.DocLengths)) return 0;

            int offset = field * 4;
            if (offset + 4 > valBuf.Length) return 0;
            return BinaryPrimitives.ReadInt32LittleEndian(valBuf.Slice(offset, 4));
        }
    }
}
