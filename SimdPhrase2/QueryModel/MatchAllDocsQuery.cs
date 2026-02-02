using System;

namespace SimdPhrase2.QueryModel
{
    public class MatchAllDocsQuery : Query
    {
        public override Weight CreateWeight(Searcher searcher, bool needsScores)
        {
            return new MatchAllDocsWeight(this, searcher);
        }
        public override string ToString() => "*:*";
    }

    public class MatchAllDocsWeight : Weight
    {
        private readonly MatchAllDocsQuery _query;
        private readonly Searcher _searcher;

        public MatchAllDocsWeight(MatchAllDocsQuery query, Searcher searcher)
        {
            _query = query;
            _searcher = searcher;
        }

        public override Scorer GetScorer()
        {
            return new MatchAllDocsScorer((int)_searcher.TotalDocs);
        }
    }

    public class MatchAllDocsScorer : Scorer
    {
        private int _maxDoc;
        private int _current = -1;

        public MatchAllDocsScorer(int maxDoc)
        {
            _maxDoc = maxDoc;
        }

        public override int NextDoc()
        {
            _current++;
            if (_current >= _maxDoc)
            {
                _current = NO_MORE_DOCS;
                return NO_MORE_DOCS;
            }
            return _current;
        }

        public override int Advance(int target)
        {
            if (target >= _maxDoc)
            {
                _current = NO_MORE_DOCS;
                return NO_MORE_DOCS;
            }
            _current = target;
            return _current;
        }

        public override int DocID() => _current;

        public override float Score() => 1.0f;
    }
}
