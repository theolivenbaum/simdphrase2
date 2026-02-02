using System;

namespace SimdPhrase2.QueryModel
{
    public abstract class Scorer : IDisposable
    {
        public const int NO_MORE_DOCS = int.MaxValue;

        public abstract int NextDoc();
        public abstract int Advance(int target);
        public abstract int DocID();
        public abstract float Score();

        public virtual void Dispose() { }
    }
}
