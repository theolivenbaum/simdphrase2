using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SimdPhrase2.QueryModel
{
    public enum Occur
    {
        MUST,
        SHOULD,
        MUST_NOT
    }

    public class BooleanClause
    {
        public Query Query { get; }
        public Occur Occur { get; }
        public BooleanClause(Query query, Occur occur)
        {
            Query = query;
            Occur = occur;
        }
    }

    public class BooleanQuery : Query
    {
        public List<BooleanClause> Clauses { get; } = new List<BooleanClause>();

        public void Add(Query query, Occur occur)
        {
            Clauses.Add(new BooleanClause(query, occur));
        }

        public override Weight CreateWeight(Searcher searcher, bool needsScores)
        {
            return new BooleanWeight(this, searcher, needsScores);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("(");
            for(int i=0; i<Clauses.Count; i++)
            {
                var c = Clauses[i];
                if (c.Occur == Occur.MUST) sb.Append("+");
                if (c.Occur == Occur.MUST_NOT) sb.Append("-");
                sb.Append(c.Query);
                if (i < Clauses.Count - 1) sb.Append(" ");
            }
            sb.Append(")");
            return sb.ToString();
        }
    }

    public class BooleanWeight : Weight
    {
        private readonly BooleanQuery _query;
        private readonly Searcher _searcher;
        private readonly bool _needsScores;
        private readonly List<Weight> _weights;

        public BooleanWeight(BooleanQuery query, Searcher searcher, bool needsScores)
        {
            _query = query;
            _searcher = searcher;
            _needsScores = needsScores;
            _weights = new List<Weight>();
            foreach(var c in query.Clauses)
            {
                _weights.Add(c.Query.CreateWeight(searcher, needsScores));
            }
        }

        public override void Dispose()
        {
            foreach(var w in _weights) w.Dispose();
        }

        public override Scorer GetScorer()
        {
            var must = new List<Scorer>();
            var should = new List<Scorer>();
            var mustNot = new List<Scorer>();

            for(int i=0; i<_query.Clauses.Count; i++)
            {
                var clause = _query.Clauses[i];
                var w = _weights[i];
                var s = w.GetScorer();

                if (s == null)
                {
                    if (clause.Occur == Occur.MUST) return null; // Missing required clause
                    continue;
                }

                if (clause.Occur == Occur.MUST) must.Add(s);
                else if (clause.Occur == Occur.SHOULD) should.Add(s);
                else if (clause.Occur == Occur.MUST_NOT) mustNot.Add(s);
            }

            // Composition Logic

            // 1. Handle MUST
            Scorer result = null;
            if (must.Count > 0)
            {
                if (must.Count == 1) result = must[0];
                else result = new ConjunctionScorer(must);
            }

            // 2. Handle SHOULD
            // If we have MUST clauses, SHOULD clauses are optional (they boost score).
            // If we have NO MUST clauses, at least one SHOULD clause is required.

            if (should.Count > 0)
            {
                var disjunction = (should.Count == 1) ? should[0] : new DisjunctionScorer(should);

                if (result == null)
                {
                    // Only SHOULD clauses
                    result = disjunction;
                }
                else
                {
                    // MUST + SHOULD
                    // Result must be in 'result' (MUST). 'disjunction' adds to score.
                    // Effectively, we iterate 'result', and check 'disjunction' for score.
                    // This is "Conjunction of (result) and (Optional(disjunction))".
                    // But simpler: We can just use BooleanScorer logic or treat it as Conjunction where one side is optional?
                    // Lucene puts optional clauses in a separate bucket used for scoring only.

                    // For simplicity in this port:
                    // If we have MUST, we iterate MUST.
                    // To include SHOULD scores, we wrap it in a scorer that advances MUST,
                    // and checks SHOULD for the same docId to add score.

                    result = new RequiredOptionalScorer(result, disjunction);
                }
            }

            if (result == null) return null; // No matching clauses

            // 3. Handle MUST_NOT
            if (mustNot.Count > 0)
            {
                var exclusion = (mustNot.Count == 1) ? mustNot[0] : new DisjunctionScorer(mustNot);
                result = new ReqExclScorer(result, exclusion);
            }

            return result;
        }
    }
}
