using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using RocksDbSharp;
using SimdPhrase2.Storage;

namespace SimdPhrase2
{
    public abstract class CommonTokensConfig
    {
        public static CommonTokensConfig None => new NoneConfig();
        public static CommonTokensConfig FromList(HashSet<string> tokens) => new ListConfig(tokens);
        public static CommonTokensConfig FromFixedNum(int num) => new FixedNumConfig(num);
        public static CommonTokensConfig FromPercentage(double percentage) => new PercentageConfig(percentage);

        private class NoneConfig : CommonTokensConfig { }

        public class ListConfig : CommonTokensConfig
        {
            public HashSet<string> Tokens { get; }
            public ListConfig(HashSet<string> tokens) { Tokens = tokens; }
        }

        public class FixedNumConfig : CommonTokensConfig
        {
            public int Num { get; }
            public FixedNumConfig(int num) { Num = num; }
        }

        public class PercentageConfig : CommonTokensConfig
        {
            public double Percentage { get; }
            public PercentageConfig(double percentage) { Percentage = percentage; }
        }
    }

    // The persisted common-token list is a single value in the meta column
    // family: [int32 count] then for each token [int32 lenBytes][utf-8 bytes].
    public static class CommonTokensPersistence
    {
        public static byte[] Serialize(HashSet<string> tokens)
        {
            int size = 4;
            foreach (var t in tokens) size += 4 + Encoding.UTF8.GetByteCount(t);
            var buf = new byte[size];
            var span = buf.AsSpan();
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), tokens.Count);
            int pos = 4;
            foreach (var t in tokens)
            {
                int n = Encoding.UTF8.GetByteCount(t);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, 4), n);
                pos += 4;
                Encoding.UTF8.GetBytes(t, 0, t.Length, buf, pos);
                pos += n;
            }
            return buf;
        }

        public static HashSet<string> Deserialize(ReadOnlySpan<byte> bytes)
        {
            var set = new HashSet<string>();
            if (bytes.Length < 4) return set;
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(0, 4));
            int pos = 4;
            for (int i = 0; i < count; i++)
            {
                if (pos + 4 > bytes.Length) break;
                int n = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4));
                pos += 4;
                if (pos + n > bytes.Length) break;
                set.Add(Encoding.UTF8.GetString(bytes.Slice(pos, n)));
                pos += n;
            }
            return set;
        }

        public static HashSet<string> Load(SimdPhraseDb db)
        {
            var bytes = db.Db.Get(Encoding.UTF8.GetBytes(SimdPhraseDb.MetaKeyCommonTokens), db.Meta);
            if (bytes == null) return new HashSet<string>();
            return Deserialize(bytes);
        }

        public static void Save(SimdPhraseDb db, HashSet<string> tokens)
        {
            db.Db.Put(Encoding.UTF8.GetBytes(SimdPhraseDb.MetaKeyCommonTokens), Serialize(tokens), db.Meta);
        }

        public static void AddToBatch(WriteBatch batch, ColumnFamilyHandle metaCf, HashSet<string> tokens)
        {
            batch.Put(Encoding.UTF8.GetBytes(SimdPhraseDb.MetaKeyCommonTokens), Serialize(tokens), metaCf);
        }
    }
}
