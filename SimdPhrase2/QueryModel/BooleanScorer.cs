using System;
using System.Collections.Generic;
using System.Linq;

namespace SimdPhrase2.QueryModel
{
    public class DisjunctionScorer : Scorer
    {
        private readonly PriorityQueue<Scorer, int> _pq;
        private int _currentDoc = -1;

        public DisjunctionScorer(List<Scorer> subScorers)
        {
            _pq = new PriorityQueue<Scorer, int>(subScorers.Count);
            foreach (var s in subScorers)
            {
                if (s.NextDoc() != NO_MORE_DOCS)
                {
                    _pq.Enqueue(s, s.DocID());
                }
            }
        }

        public override int NextDoc()
        {
            if (_currentDoc != -1)
            {
                // Advance all scorers at _currentDoc
                while (_pq.Count > 0 && _pq.Peek().DocID() == _currentDoc)
                {
                    var s = _pq.Dequeue();
                    if (s.NextDoc() != NO_MORE_DOCS)
                    {
                        _pq.Enqueue(s, s.DocID());
                    }
                }
            }

            if (_pq.Count == 0)
            {
                _currentDoc = NO_MORE_DOCS;
                return NO_MORE_DOCS;
            }

            _currentDoc = _pq.Peek().DocID();
            return _currentDoc;
        }

        public override int Advance(int target)
        {
             while (_pq.Count > 0 && _pq.Peek().DocID() < target)
            {
                var s = _pq.Dequeue();
                if (s.Advance(target) != NO_MORE_DOCS)
                {
                    _pq.Enqueue(s, s.DocID());
                }
            }
            if (_pq.Count == 0)
            {
                _currentDoc = NO_MORE_DOCS;
                return NO_MORE_DOCS;
            }
            _currentDoc = _pq.Peek().DocID();
            return _currentDoc;
        }

        public override int DocID() => _currentDoc;

        public override float Score()
        {
            float score = 0;
            foreach(var (scorer, priority) in _pq.UnorderedItems)
            {
                if (priority == _currentDoc)
                {
                    score += scorer.Score();
                }
            }
            return score;
        }
    }

    public class ConjunctionScorer : Scorer
    {
        private readonly List<Scorer> _scorers;
        private int _currentDoc = -1;

        public ConjunctionScorer(List<Scorer> scorers)
        {
            _scorers = scorers;
        }

        public override int NextDoc()
        {
            if (_currentDoc == NO_MORE_DOCS) return NO_MORE_DOCS;

            if (_currentDoc == -1)
            {
                // Initialize: advance all
                foreach(var s in _scorers)
                {
                    if (s.NextDoc() == NO_MORE_DOCS)
                    {
                        _currentDoc = NO_MORE_DOCS;
                        return NO_MORE_DOCS;
                    }
                }
                int max = _scorers.Max(s => s.DocID());
                return DoNext(max);
            }
            else
            {
                int doc = _scorers[0].NextDoc();
                if (doc == NO_MORE_DOCS)
                {
                    _currentDoc = NO_MORE_DOCS;
                    return NO_MORE_DOCS;
                }
                return DoNext(doc);
            }
        }

        private int DoNext(int target)
        {
            int first = 0;
            int idx = 0;

            while (true)
            {
                var s = _scorers[idx];
                int doc = s.DocID();

                if (doc < target)
                {
                    doc = s.Advance(target);
                    if (doc == NO_MORE_DOCS)
                    {
                        _currentDoc = NO_MORE_DOCS;
                        return NO_MORE_DOCS;
                    }
                }

                if (doc > target)
                {
                    target = doc;
                    first = idx;
                }

                idx = (idx + 1) % _scorers.Count;
                if (idx == first)
                {
                    _currentDoc = target;
                    return target;
                }
            }
        }

        public override int Advance(int target)
        {
             if (_currentDoc == NO_MORE_DOCS) return NO_MORE_DOCS;
             // Ensure all are at least at target
             foreach(var s in _scorers)
             {
                 if (s.DocID() < target)
                 {
                     if (s.Advance(target) == NO_MORE_DOCS)
                     {
                         _currentDoc = NO_MORE_DOCS;
                         return NO_MORE_DOCS;
                     }
                 }
             }
             // Now find the intersection starting from the max of current positions
             int max = _scorers.Max(s => s.DocID());
             return DoNext(max);
        }

        public override int DocID() => _currentDoc;

        public override float Score()
        {
            float score = 0;
            foreach(var s in _scorers) score += s.Score();
            return score;
        }
    }

    public class ReqExclScorer : Scorer
    {
        private readonly Scorer _req;
        private readonly Scorer _excl;
        private int _currentDoc = -1;

        public ReqExclScorer(Scorer req, Scorer excl)
        {
            _req = req;
            _excl = excl;
        }

        public override int NextDoc()
        {
            int doc = _req.NextDoc();
            return ToNextValid(doc);
        }

        public override int Advance(int target)
        {
             int doc = _req.Advance(target);
             return ToNextValid(doc);
        }

        private int ToNextValid(int doc)
        {
            if (doc == NO_MORE_DOCS)
            {
                _currentDoc = NO_MORE_DOCS;
                return NO_MORE_DOCS;
            }

            int exclDoc = _excl.DocID();
            if (exclDoc < doc)
            {
                exclDoc = _excl.Advance(doc);
            }

            if (exclDoc == doc)
            {
                return NextDoc();
            }

            _currentDoc = doc;
            return doc;
        }

        public override int DocID() => _currentDoc;

        public override float Score() => _req.Score();
    }

    public class RequiredOptionalScorer : Scorer
    {
        private readonly Scorer _req;
        private readonly Scorer _opt;

        public RequiredOptionalScorer(Scorer req, Scorer opt)
        {
            _req = req;
            _opt = opt;
        }

        public override int NextDoc() => _req.NextDoc();
        public override int Advance(int target) => _req.Advance(target);
        public override int DocID() => _req.DocID();

        public override float Score()
        {
            float score = _req.Score();
            int doc = _req.DocID();

            int optDoc = _opt.DocID();
            if (optDoc < doc)
            {
                optDoc = _opt.Advance(doc);
            }

            if (optDoc == doc)
            {
                score += _opt.Score();
            }
            return score;
        }
    }
}
