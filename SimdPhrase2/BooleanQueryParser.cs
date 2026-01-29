using System;
using System.Collections.Generic;
using System.Linq;

namespace SimdPhrase2
{
    public abstract class QueryNode { }

    public class TermNode : QueryNode
    {
        public string Term { get; set; }
        public TermNode(string term) => Term = term;
        public override string ToString() => Term;
    }

    public class AndNode : QueryNode
    {
        public QueryNode Left { get; set; }
        public QueryNode Right { get; set; }
        public AndNode(QueryNode left, QueryNode right) { Left = left; Right = right; }
        public override string ToString() => $"({Left} AND {Right})";
    }

    public class OrNode : QueryNode
    {
        public QueryNode Left { get; set; }
        public QueryNode Right { get; set; }
        public OrNode(QueryNode left, QueryNode right) { Left = left; Right = right; }
        public override string ToString() => $"({Left} OR {Right})";
    }

    public class NotNode : QueryNode
    {
        public QueryNode Child { get; set; }
        public NotNode(QueryNode child) => Child = child;
        public override string ToString() => $"(NOT {Child})";
    }

    public class BooleanQueryParser
    {
        private List<string> _tokens;
        private int _pos;

        public QueryNode Parse(string query)
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

        private QueryNode ParseOr()
        {
            var left = ParseAnd();
            while (_pos < _tokens.Count && _tokens[_pos].Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                _pos++;
                var right = ParseAnd();
                left = new OrNode(left, right);
            }
            return left;
        }

        private QueryNode ParseAnd()
        {
            var left = ParseNot();
            while (_pos < _tokens.Count)
            {
                var token = _tokens[_pos];
                if (token.Equals("AND", StringComparison.OrdinalIgnoreCase))
                {
                    _pos++;
                    var right = ParseNot();
                    left = new AndNode(left, right);
                }
                else if (token.Equals("OR", StringComparison.OrdinalIgnoreCase) || token == ")")
                {
                    break;
                }
                else
                {
                    // Implicit AND
                    var right = ParseNot();
                    left = new AndNode(left, right);
                }
            }
            return left;
        }

        private QueryNode ParseNot()
        {
            if (_pos < _tokens.Count && _tokens[_pos].Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                _pos++;
                var child = ParseNot();
                return new NotNode(child);
            }
            return ParsePrimary();
        }

        private QueryNode ParsePrimary()
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
            return new TermNode(term);
        }
    }
}
