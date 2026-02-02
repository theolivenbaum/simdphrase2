using System;

namespace SimdPhrase2.QueryModel
{
    public abstract class Weight : IDisposable
    {
        public abstract Scorer GetScorer();
        public virtual void Dispose() { }
    }
}
