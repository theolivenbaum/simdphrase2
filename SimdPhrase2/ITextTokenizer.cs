using System;
using System.Collections.Generic;

namespace SimdPhrase2
{
    public interface ITextTokenizer
    {
        IEnumerable<ReadOnlyMemory<char>> Tokenize(ReadOnlyMemory<char> text);
    }
}
