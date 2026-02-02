using System;
using System.IO;

namespace SimdPhrase2.QueryModel
{
    public abstract class Query
    {
        public abstract Weight CreateWeight(Searcher searcher, bool needsScores);
        public virtual float Boost { get; set; } = 1.0f;
    }
}
