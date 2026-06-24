using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SimdPhrase2.ComparativeBench;

/// <summary>
/// A single document to index: a primary key, free text, a numeric attribute and a low-cardinality
/// label used as a facet dimension (mirroring luceneutil's "random label" column). This type is
/// implementation-agnostic — both the SimdPhrase2 engine and the legacy Lucene.NET engine consume
/// the same corpus so the comparison is apples-to-apples. (SimdPhrase2 only indexes the free text;
/// the numeric and label columns exist for the categories the legacy engine can run.)
/// </summary>
internal readonly record struct DocItem(int Id, string Title, string Body, int Rand, string Category);

/// <summary>Source of documents for the benchmark.</summary>
internal interface IDocSource
{
    /// <summary>Human-readable description of the corpus, recorded in the report.</summary>
    string Description { get; }

    /// <summary>Total number of documents this source will yield.</summary>
    int Count { get; }

    /// <summary>Streams the documents in id order.</summary>
    IEnumerable<DocItem> Documents();
}

/// <summary>
/// Deterministic synthetic corpus with a Zipfian term distribution, so that the index has a
/// realistic spread of high-, medium- and low-frequency terms to build the standard task
/// categories from. Reproducible for a given seed and document count; needs no network or
/// multi-gigabyte Wikipedia download. Copied verbatim (intentionally, not referenced) from the
/// Lucene.ComparativeBench synthetic source so the two harnesses stay independent.
/// </summary>
internal sealed class SyntheticDocSource : IDocSource
{
    private const int VocabularySize = 8_000;
    private const int LabelCardinality = 256;
    private readonly int _count;
    private readonly int _seed;
    private readonly string[] _vocab;

    public SyntheticDocSource(int count, int seed = 17)
    {
        _count = count;
        _seed = seed;
        _vocab = new string[VocabularySize];
        var rng = new Random(1234567);
        for (int i = 0; i < VocabularySize; i++)
        {
            _vocab[i] = MakeWord(rng);
        }
    }

    public string Description =>
        FormattableString.Invariant($"synthetic Zipfian corpus, {_count} docs, vocab {VocabularySize}");

    public int Count => _count;

    public IEnumerable<DocItem> Documents()
    {
        for (int id = 0; id < _count; id++)
        {
            // Per-doc deterministic RNG so a given id always produces the same document.
            var rng = new Random(_seed + id);
            int bodyLen = 24 + rng.Next(80);
            var body = new StringBuilder(bodyLen * 7);
            for (int w = 0; w < bodyLen; w++)
            {
                if (w > 0)
                {
                    body.Append(' ');
                }
                body.Append(_vocab[ZipfRank(rng, 1.05)]);
            }
            var title = new StringBuilder();
            for (int w = 0; w < 4; w++)
            {
                if (w > 0)
                {
                    title.Append(' ');
                }
                title.Append(_vocab[ZipfRank(rng, 0.7)]);
            }
            string category = "label" + rng.Next(0, LabelCardinality).ToString(CultureInfo.InvariantCulture);
            yield return new DocItem(id, title.ToString(), body.ToString(), rng.Next(0, 1_000_000), category);
        }
    }

    // Maps a uniform draw to a rank in [0, V): smaller ranks (earlier vocab) are much more likely,
    // producing a Zipf-like frequency curve. Larger 'skew' => steeper curve.
    private static int ZipfRank(Random rng, double skew)
    {
        int r = (int)(VocabularySize * Math.Pow(rng.NextDouble(), 1.0 + skew));
        return r >= VocabularySize ? VocabularySize - 1 : r;
    }

    private static string MakeWord(Random rng)
    {
        const string consonants = "bcdfghjklmnpqrstvwxz";
        const string vowels = "aeiou";
        int syllables = 2 + rng.Next(3);
        var sb = new StringBuilder(syllables * 2);
        for (int s = 0; s < syllables; s++)
        {
            sb.Append(consonants[rng.Next(consonants.Length)]);
            sb.Append(vowels[rng.Next(vowels.Length)]);
        }
        return sb.ToString();
    }
}
