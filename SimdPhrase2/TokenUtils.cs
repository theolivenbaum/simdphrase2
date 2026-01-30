using System;

namespace SimdPhrase2
{
    public static class TokenUtils
    {
        public static string NormalizeToString(ReadOnlySpan<char> token)
        {
            if (token.IsEmpty) return string.Empty;

            bool needsLower = false;
            for (int i = 0; i < token.Length; i++)
            {
                if (char.IsUpper(token[i]))
                {
                    needsLower = true;
                    break;
                }
            }

            if (needsLower)
            {
                return token.ToString().ToLowerInvariant();
            }

            return token.ToString();
        }
    }
}
