using System;
using System.Collections.Generic;

namespace SimdPhrase2.Queries
{
    // Composable query AST used by Searcher.Search(Query) and Searcher.SearchBM25(Query).
    //
    // Each leaf query targets a specific field (by byte index, 0..fieldCount-1).
    // Combinators (And/Or/Not/Boost) compose leaves into structured queries. The
    // intent is to model the same shape as Lucene's BooleanQuery without inheriting
    // its complexity: AND = MUST, OR = SHOULD, NOT = MUST_NOT, Boost = float boost.
    //
    // There is deliberately no parser yet - constructors compose the tree
    // programmatically.
    public abstract class Query
    {
        public abstract IEnumerable<Query> Children { get; }
    }

    // Matches docs that contain the given token in the given field.
    public sealed class TermQuery : Query
    {
        public byte Field { get; }
        public string Term { get; }

        public TermQuery(byte field, string term)
        {
            Field = field;
            Term = term ?? throw new ArgumentNullException(nameof(term));
        }

        public override IEnumerable<Query> Children => Array.Empty<Query>();
        public override string ToString() => $"field{Field}:{Term}";
    }

    // Matches docs that contain the given phrase in the given field. The phrase is
    // tokenized at search time by the searcher's tokenizer; an empty tokenization
    // matches no docs.
    public sealed class PhraseQuery : Query
    {
        public byte Field { get; }
        public string Phrase { get; }

        public PhraseQuery(byte field, string phrase)
        {
            Field = field;
            Phrase = phrase ?? throw new ArgumentNullException(nameof(phrase));
        }

        public override IEnumerable<Query> Children => Array.Empty<Query>();
        public override string ToString() => $"field{Field}:\"{Phrase}\"";
    }

    // Intersection of clauses (MUST). Empty clause list matches nothing.
    public sealed class AndQuery : Query
    {
        public IReadOnlyList<Query> Clauses { get; }

        public AndQuery(params Query[] clauses) : this((IReadOnlyList<Query>)clauses) { }

        public AndQuery(IReadOnlyList<Query> clauses)
        {
            Clauses = clauses ?? throw new ArgumentNullException(nameof(clauses));
        }

        public override IEnumerable<Query> Children => Clauses;
        public override string ToString() => "(" + string.Join(" AND ", Clauses) + ")";
    }

    // Union of clauses (SHOULD). Empty clause list matches nothing.
    public sealed class OrQuery : Query
    {
        public IReadOnlyList<Query> Clauses { get; }

        public OrQuery(params Query[] clauses) : this((IReadOnlyList<Query>)clauses) { }

        public OrQuery(IReadOnlyList<Query> clauses)
        {
            Clauses = clauses ?? throw new ArgumentNullException(nameof(clauses));
        }

        public override IEnumerable<Query> Children => Clauses;
        public override string ToString() => "(" + string.Join(" OR ", Clauses) + ")";
    }

    // Negation (MUST_NOT). Useful only inside an AndQuery, in which case the And's
    // doc set is restricted to docs not matched by the child. As a top-level query,
    // NotQuery matches every live doc not matched by the child.
    public sealed class NotQuery : Query
    {
        public Query Child { get; }

        public NotQuery(Query child)
        {
            Child = child ?? throw new ArgumentNullException(nameof(child));
        }

        public override IEnumerable<Query> Children { get { yield return Child; } }
        public override string ToString() => $"(NOT {Child})";
    }

    // Multiplies the child's contribution to BM25 score by Boost. The matching doc
    // set is unaffected; only the score is scaled.
    public sealed class BoostQuery : Query
    {
        public Query Child { get; }
        public float Boost { get; }

        public BoostQuery(Query child, float boost)
        {
            Child = child ?? throw new ArgumentNullException(nameof(child));
            Boost = boost;
        }

        public override IEnumerable<Query> Children { get { yield return Child; } }
        public override string ToString() => $"({Child})^{Boost}";
    }
}
