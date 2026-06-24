namespace SimdPhrase2.ComparativeBench;

/// <summary>Field names shared by both engines so the two indexes are structurally comparable.</summary>
internal static class Fields
{
    public const string Id = "id";
    public const string Title = "title";
    public const string Body = "body";
    public const string Rand = "rand";
    public const string RandDv = "randdv";
    public const string FacetDim = "label";
}

/// <summary>The luceneutil-style task categories, in display order. The same full list is used by
/// both engines; categories SimdPhrase2 cannot run are simply left empty for that engine (see
/// <c>SimdPhraseEngine</c>) and therefore skipped, while still being shown in the report for the
/// legacy engine.</summary>
internal static class Categories
{
    public static readonly string[] All =
    [
        "Term", "HighTerm", "MedTerm", "LowTerm",
        "AndHighHigh", "AndHighMed", "OrHighHigh", "OrHighMed", "OrHighRare",
        "And3Terms", "Or3Terms",
        "Phrase", "SloppyPhrase", "SpanNear",
        "Wildcard", "Prefix3", "Fuzzy1", "Fuzzy2",
        "IntNRQ", "PKLookup", "TermDVSort",
        "CountTerm", "CountAndHighHigh", "CountOrHighHigh", "CountOrHighMed", "CountPhrase",
    ];
}
