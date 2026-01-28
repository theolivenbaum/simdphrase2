using System.Threading;

namespace SimdPhrase2
{
    public class Stats
    {
        public long FirstBinarySearch;
        public long FirstIntersect;
        public long FirstIntersectNaive;
        public long FirstIntersectSimd;
        public long SecondBinarySearch;
        public long SecondIntersect;
        public long SecondIntersectNaive;
        public long SecondIntersectSimd;
        public long MergePhasesFirstPass;
        public long MergePhasesSecondPass;
        public long GetDocIds;
        public long NormalizeTokenize;
        public long MergeMinimize;
        public long Iters;

        public void Add(ref long field, long value)
        {
            Interlocked.Add(ref field, value);
        }
    }
}
