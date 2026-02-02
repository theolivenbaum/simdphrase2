using SimdPhrase2.Roaringish;
using System;
using System.Collections.Generic;

namespace SimdPhrase2.QueryModel
{
    public class TermQuery : Query
    {
        public string Term { get; }

        public TermQuery(string term)
        {
            Term = term;
        }

        public override Weight CreateWeight(Searcher searcher, bool needsScores)
        {
            return new TermWeight(this, searcher, needsScores);
        }

        public override string ToString() => Term;
    }

    public class TermWeight : Weight
    {
        private readonly TermQuery _query;
        private readonly Searcher _searcher;
        private readonly bool _needsScores;
        private readonly float _idf;
        private readonly float _avgDocLen;
        private readonly RoaringishPacked _packed;

        public TermWeight(TermQuery query, Searcher searcher, bool needsScores)
        {
            _query = query;
            _searcher = searcher;
            _needsScores = needsScores;

            _packed = searcher.GetPackedForTerm(_query.Term, out long docCount);

            if (_packed != null && needsScores)
            {
                long totalDocs = searcher.TotalDocs;
                _idf = MathF.Log(1f + (totalDocs - docCount + 0.5f) / (docCount + 0.5f));
                if (_idf < 0) _idf = 0;
                _avgDocLen = searcher.AvgDocLength;
            }
        }

        public override void Dispose()
        {
            _packed?.Dispose();
        }

        public override Scorer GetScorer()
        {
            if (_packed == null) return null;
            return new TermScorer(_packed, _idf, _avgDocLen, _searcher);
        }
    }

    public class TermScorer : Scorer
    {
        private readonly RoaringishPacked _packed;
        private readonly float _idf;
        private readonly float _avgDocLen;
        private readonly Searcher _searcher;

        private int _idx;
        private int _limit;
        private uint _currentDocId;
        private int _currentFreq;

        public TermScorer(RoaringishPacked packed, float idf, float avgDocLen, Searcher searcher)
        {
            _packed = packed;
            _idf = idf;
            _avgDocLen = avgDocLen;
            _searcher = searcher;
            _idx = 0;
            _limit = packed.Length;
            _currentDocId = uint.MaxValue;
        }

        public override int NextDoc()
        {
             while (true)
             {
                 if (_idx >= _limit)
                 {
                     _currentDocId = (uint)NO_MORE_DOCS;
                     return NO_MORE_DOCS;
                 }

                 var span = _packed.AsSpan();
                 ulong packedVal = span[_idx];
                 uint docId = RoaringishPacked.UnpackDocId(packedVal);

                 _currentDocId = docId;
                 _currentFreq = 0;

                 while (_idx < _limit)
                 {
                     packedVal = span[_idx];
                     if (RoaringishPacked.UnpackDocId(packedVal) != docId) break;

                     ushort values = RoaringishPacked.UnpackValues(packedVal);
                     _currentFreq += System.Numerics.BitOperations.PopCount(values);
                     _idx++;
                 }

                 if (_currentFreq > 0) return (int)_currentDocId;
             }
        }

        public override int Advance(int target)
        {
             int doc;
             // Basic implementation: call NextDoc until we reach target
             while ((doc = NextDoc()) != NO_MORE_DOCS)
             {
                 if (doc >= target) return doc;
             }
             return NO_MORE_DOCS;
        }

        public override int DocID() => _currentDocId == uint.MaxValue ? -1 : (int)_currentDocId;

        public override float Score()
        {
             float k1 = 1.2f;
             float b = 0.75f;
             int docLen = _searcher.GetDocLength(_currentDocId);
             float score = _idf * (_currentFreq * (k1 + 1f)) / (_currentFreq + k1 * (1f - b + b * (docLen / _avgDocLen)));
             return score;
        }
    }
}
