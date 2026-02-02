using System;
using System.Collections.Generic;
using SimdPhrase2.QueryModel;

namespace SimdPhrase2
{
    public class BooleanQueryParser
    {
        private List<string> _tokens;
        private int _pos;

        public Query Parse(string query)
        {
            _tokens = Tokenize(query);
            _pos = 0;
            if (_tokens.Count == 0) return null;
            return ParseOr();
        }

        private List<string> Tokenize(string query)
        {
            var tokens = new List<string>();
            var currentToken = "";
            for (int i = 0; i < query.Length; i++)
            {
                char c = query[i];
                if (c == '(' || c == ')')
                {
                    if (currentToken.Length > 0) tokens.Add(currentToken);
                    currentToken = "";
                    tokens.Add(c.ToString());
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (currentToken.Length > 0) tokens.Add(currentToken);
                    currentToken = "";
                }
                else
                {
                    currentToken += c;
                }
            }
            if (currentToken.Length > 0) tokens.Add(currentToken);
            return tokens;
        }

        private Query ParseOr()
        {
            var left = ParseAnd();
            while (_pos < _tokens.Count && _tokens[_pos].Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                _pos++;
                var right = ParseAnd();
                var bq = new BooleanQuery();
                bq.Add(left, Occur.SHOULD);
                bq.Add(right, Occur.SHOULD);
                left = bq;
            }
            return left;
        }

        private Query ParseAnd()
        {
            var left = ParseNot();
            while (_pos < _tokens.Count)
            {
                var token = _tokens[_pos];
                if (token.Equals("AND", StringComparison.OrdinalIgnoreCase))
                {
                    _pos++;
                    var right = ParseNot();
                    var bq = new BooleanQuery();
                    bq.Add(left, Occur.MUST);
                    bq.Add(right, Occur.MUST);
                    left = bq;
                }
                else if (token.Equals("OR", StringComparison.OrdinalIgnoreCase) || token == ")")
                {
                    break;
                }
                else
                {
                    // Implicit AND
                    var right = ParseNot();
                    var bq = new BooleanQuery();
                    bq.Add(left, Occur.MUST);
                    bq.Add(right, Occur.MUST);
                    left = bq;
                }
            }
            return left;
        }

        private Query ParseNot()
        {
            if (_pos < _tokens.Count && _tokens[_pos].Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                _pos++;
                var child = ParseNot();
                var bq = new BooleanQuery();
                bq.Add(new MatchAllDocsQuery(), Occur.MUST);
                bq.Add(child, Occur.MUST_NOT);
                return bq;
            }
            return ParsePrimary();
        }

        private Query ParsePrimary()
        {
            if (_pos >= _tokens.Count) throw new Exception("Unexpected end of query");

            if (_tokens[_pos] == "(")
            {
                _pos++;
                var node = ParseOr();
                if (_pos >= _tokens.Count || _tokens[_pos] != ")") throw new Exception("Missing closing parenthesis");
                _pos++;
                return node;
            }

            var term = _tokens[_pos++];
            return new TermQuery(term);
        }
    }
}
