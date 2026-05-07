using System;
using System.Collections.Generic;
using System.IO;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Db
{
    /// <summary>
    /// Per-segment, per-field document length store. On-disk format is a simple
    /// table of (uint docId, ushort fieldId, int length) records, preceded by a
    /// header listing the field names. The on-disk file is loaded into memory
    /// at open time for fast access during scoring.
    /// </summary>
    public class DocLengthsStore
    {
        private readonly Dictionary<string, ushort> _fieldIds = new();
        private readonly List<string> _fieldNames = new();
        // docId -> fieldId -> length
        private readonly Dictionary<uint, Dictionary<ushort, int>> _lengths = new();

        public IReadOnlyList<string> Fields => _fieldNames;

        private ushort GetOrAddFieldId(string field)
        {
            if (_fieldIds.TryGetValue(field, out var id)) return id;
            id = (ushort)_fieldNames.Count;
            _fieldNames.Add(field);
            _fieldIds[field] = id;
            return id;
        }

        public void Set(uint docId, string field, int length)
        {
            var fid = GetOrAddFieldId(field);
            if (!_lengths.TryGetValue(docId, out var d))
            {
                d = new Dictionary<ushort, int>();
                _lengths[docId] = d;
            }
            d[fid] = length;
        }

        public int Get(uint docId, string field)
        {
            if (!_fieldIds.TryGetValue(field, out var fid)) return 0;
            if (!_lengths.TryGetValue(docId, out var d)) return 0;
            return d.TryGetValue(fid, out var len) ? len : 0;
        }

        public int Get(uint docId, ushort fieldId)
        {
            if (!_lengths.TryGetValue(docId, out var d)) return 0;
            return d.TryGetValue(fieldId, out var len) ? len : 0;
        }

        public bool TryGetFieldId(string field, out ushort fid) => _fieldIds.TryGetValue(field, out fid);

        public ulong SumForField(string field)
        {
            if (!_fieldIds.TryGetValue(field, out var fid)) return 0UL;
            ulong total = 0;
            foreach (var d in _lengths.Values)
            {
                if (d.TryGetValue(fid, out var len)) total += (ulong)len;
            }
            return total;
        }

        public int CountDocsWithField(string field)
        {
            if (!_fieldIds.TryGetValue(field, out var fid)) return 0;
            int n = 0;
            foreach (var d in _lengths.Values)
            {
                if (d.ContainsKey(fid)) n++;
            }
            return n;
        }

        public IEnumerable<uint> AllDocIds() => _lengths.Keys;

        public void Save(ISimdStorage storage, string path)
        {
            using var fs = storage.OpenWrite(path);
            using var bw = new BinaryWriter(fs);
            // Header: field count + names
            bw.Write(_fieldNames.Count);
            foreach (var n in _fieldNames) bw.Write(n);
            // Records: docCount, then [docId, fieldEntryCount, [fieldId, length]...]
            bw.Write(_lengths.Count);
            foreach (var (docId, perField) in _lengths)
            {
                bw.Write(docId);
                bw.Write(perField.Count);
                foreach (var (fid, len) in perField)
                {
                    bw.Write(fid);
                    bw.Write(len);
                }
            }
        }

        public static DocLengthsStore Load(ISimdStorage storage, string path)
        {
            var s = new DocLengthsStore();
            if (!storage.FileExists(path)) return s;
            using var fs = storage.OpenRead(path);
            using var br = new BinaryReader(fs);
            try
            {
                int fieldCount = br.ReadInt32();
                for (int i = 0; i < fieldCount; i++)
                {
                    string name = br.ReadString();
                    s._fieldNames.Add(name);
                    s._fieldIds[name] = (ushort)i;
                }
                int docCount = br.ReadInt32();
                for (int i = 0; i < docCount; i++)
                {
                    uint docId = br.ReadUInt32();
                    int n = br.ReadInt32();
                    var d = new Dictionary<ushort, int>(n);
                    for (int j = 0; j < n; j++)
                    {
                        ushort fid = br.ReadUInt16();
                        int len = br.ReadInt32();
                        d[fid] = len;
                    }
                    s._lengths[docId] = d;
                }
            }
            catch (EndOfStreamException) { }
            return s;
        }
    }
}
