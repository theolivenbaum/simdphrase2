using System.Collections.Generic;

namespace SimdPhrase2
{
    /// <summary>
    /// A document composed of one or more named fields. Multiple values for the same
    /// field are concatenated (with a position gap between them) for indexing.
    /// </summary>
    public class IndexDocument
    {
        public uint Id { get; set; }
        public List<(string Field, string Value)> Fields { get; } = new();

        public IndexDocument() { }

        public IndexDocument(uint id) { Id = id; }

        public IndexDocument Add(string field, string value)
        {
            Fields.Add((field, value));
            return this;
        }

        public string GetFirst(string field)
        {
            foreach (var (f, v) in Fields)
            {
                if (f == field) return v;
            }
            return null;
        }
    }

    /// <summary>
    /// Per-field configuration: name and optional indexing/search-time boost.
    /// </summary>
    public class FieldOptions
    {
        public string Name { get; set; }
        /// <summary>Score multiplier applied to BM25 contributions of this field.</summary>
        public float Boost { get; set; } = 1.0f;
        /// <summary>If true, raw text for this field is retrievable via GetField().</summary>
        public bool Stored { get; set; } = true;

        public FieldOptions() { }
        public FieldOptions(string name, float boost = 1.0f, bool stored = true)
        {
            Name = name;
            Boost = boost;
            Stored = stored;
        }
    }
}
