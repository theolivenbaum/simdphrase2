using SimdPhrase2.Roaringish;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SimdPhrase2.QueryModel
{
    public class PhraseQuery : Query
    {
        public List<string> Terms { get; }

        public PhraseQuery(IEnumerable<string> terms)
        {
            Terms = terms.ToList();
        }

        public override Weight CreateWeight(Searcher searcher, bool needsScores)
        {
            return new PhraseWeight(this, searcher, needsScores);
        }

        public override string ToString() => string.Join(" ", Terms);
    }

    public class PhraseWeight : Weight
    {
        private readonly PhraseQuery _query;
        private readonly Searcher _searcher;
        private readonly bool _needsScores;
        private readonly RoaringishPacked _packed;
        private readonly float _idf;
        private readonly float _avgDocLen;

        public PhraseWeight(PhraseQuery query, Searcher searcher, bool needsScores)
        {
            _query = query;
            _searcher = searcher;
            _needsScores = needsScores;

            _packed = ComputeIntersection();

            if (_packed != null && needsScores)
            {
                // Calculate pseudo-IDF for phrase
                long docCount = CountDocs(_packed);
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

        private long CountDocs(RoaringishPacked packed)
        {
            long count = 0;
            var span = packed.AsSpan();
            if (span.Length == 0) return 0;

            uint lastDocId = RoaringishPacked.UnpackDocId(span[0]);
            count = 1;

            for(int i=1; i<span.Length; i++)
            {
                uint docId = RoaringishPacked.UnpackDocId(span[i]);
                if (docId != lastDocId)
                {
                    count++;
                    lastDocId = docId;
                }
            }
            return count;
        }

        private RoaringishPacked ComputeIntersection()
        {
            var terms = _query.Terms;
            if (terms.Count == 0) return null;

            var packedTokens = new List<(string Token, RoaringishPacked Packed)>();

            try
            {
                foreach(var t in terms)
                {
                    var p = _searcher.GetPackedForTerm(t, out _);
                    if (p == null) return null;
                    packedTokens.Add((t, p));
                }

                if (packedTokens.Count == 1)
                {
                    // If we return one of the input packs, we must ensure we don't dispose it in finally block
                    // But we DO dispose in finally block.
                    // So we must CLONE it or create a new reference.
                    // Or, simply, don't dispose the one we return.

                    var result = packedTokens[0].Packed;
                    // We remove it from the list so finally block doesn't dispose it?
                    // But finally block iterates packedTokens.

                    // Let's create a NEW RoaringishPacked that shares the buffer?
                    // No, RoaringishPacked owns buffer.
                    // We can steal ownership?
                    // RoaringishPacked doesn't have "ReleaseBuffer".

                    // Simple solution: If count is 1, return it, and clear the list so finally doesn't dispose it.
                    packedTokens.Clear();
                    return result;
                }

                // Intersection Logic
                int bestIdx = 0;
                long minLen = long.MaxValue;

                for (int i = 0; i < packedTokens.Count - 1; i++)
                {
                    long len = packedTokens[i].Packed.Length + packedTokens[i+1].Packed.Length;
                    if (len < minLen)
                    {
                        minLen = len;
                        bestIdx = i;
                    }
                }

                var lhsItem = packedTokens[bestIdx];
                var rhsItem = packedTokens[bestIdx + 1];

                var resultPacked = _searcher.Intersect(lhsItem.Packed, rhsItem.Packed, 1);

                int leftI = bestIdx - 1;
                int rightI = bestIdx + 2;

                int resultPhraseLen = 2;

                while (true)
                {
                    RoaringishPacked nextLhs = leftI >= 0 ? packedTokens[leftI].Packed : null;
                    RoaringishPacked nextRhs = rightI < packedTokens.Count ? packedTokens[rightI].Packed : null;

                    if (nextLhs == null && nextRhs == null) break;

                    RoaringishPacked oldResult = resultPacked;

                    if (nextLhs != null && (nextRhs == null || nextLhs.Length <= nextRhs.Length))
                    {
                        resultPacked = _searcher.Intersect(nextLhs, resultPacked, (ushort)resultPhraseLen);
                        resultPhraseLen++;
                        leftI--;
                    }
                    else
                    {
                         resultPacked = _searcher.Intersect(resultPacked, nextRhs, 1);
                         resultPhraseLen++;
                         rightI++;
                    }

                    oldResult.Dispose();

                    if (resultPacked.Length == 0) break;
                }

                return resultPacked;
            }
            finally
            {
                foreach(var pt in packedTokens)
                {
                    pt.Packed.Dispose();
                }
            }
        }

        public override Scorer GetScorer()
        {
            if (_packed == null) return null;
            return new TermScorer(_packed, _idf, _avgDocLen, _searcher);
        }
    }
}
