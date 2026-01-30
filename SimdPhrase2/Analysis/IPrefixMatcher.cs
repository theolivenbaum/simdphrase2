using System.Collections.Generic;

namespace SimdPhrase2.Analysis
{
    public interface IPrefixMatcher
    {
        IEnumerable<string> Match(string prefix);
    }
}
