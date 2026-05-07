using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Db
{
    /// <summary>
    /// Persistent registry of fields seen across the index. Stores per-field
    /// configuration (e.g. boost) and assigns a stable short id to each field.
    /// </summary>
    public class FieldRegistry
    {
        public const string DefaultField = "_default";
        public const char FieldSeparator = '\u001F'; // ASCII unit separator

        private readonly Dictionary<string, FieldOptions> _fields = new();

        public IReadOnlyDictionary<string, FieldOptions> Fields => _fields;

        public FieldOptions GetOrAdd(string name)
        {
            if (_fields.TryGetValue(name, out var opts)) return opts;
            opts = new FieldOptions(name);
            _fields[name] = opts;
            return opts;
        }

        public void RegisterAll(IEnumerable<FieldOptions> fields)
        {
            if (fields == null) return;
            foreach (var f in fields)
            {
                if (f == null || string.IsNullOrEmpty(f.Name)) continue;
                _fields[f.Name] = f;
            }
        }

        public void SetBoost(string field, float boost)
        {
            var opts = GetOrAdd(field);
            opts.Boost = boost;
        }

        public float GetBoost(string field)
        {
            return _fields.TryGetValue(field, out var opts) ? opts.Boost : 1.0f;
        }

        public bool Contains(string field) => _fields.ContainsKey(field);

        /// <summary>Encode "field:token" using the unit-separator delimiter.</summary>
        public static string EncodeToken(string field, string token) => field + FieldSeparator + token;

        public static (string Field, string Token) DecodeToken(string encoded)
        {
            int idx = encoded.IndexOf(FieldSeparator);
            if (idx < 0) return (DefaultField, encoded);
            return (encoded.Substring(0, idx), encoded.Substring(idx + 1));
        }

        public void Save(ISimdStorage storage, string path)
        {
            var json = JsonSerializer.Serialize(_fields.Values, new JsonSerializerOptions { WriteIndented = false });
            storage.WriteAllText(path, json);
        }

        public static FieldRegistry Load(ISimdStorage storage, string path)
        {
            var reg = new FieldRegistry();
            if (!storage.FileExists(path)) return reg;
            try
            {
                var json = storage.ReadAllText(path);
                var arr = JsonSerializer.Deserialize<List<FieldOptions>>(json);
                if (arr != null)
                {
                    foreach (var f in arr)
                    {
                        if (f != null && !string.IsNullOrEmpty(f.Name))
                            reg._fields[f.Name] = f;
                    }
                }
            }
            catch { }
            return reg;
        }
    }
}
