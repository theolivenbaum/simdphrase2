using System;
using System.Collections.Generic;
using System.Linq;

namespace SimdPhrase2.Analysis
{
    public class FstPrefixMatcher : IPrefixMatcher
    {
        private class Node
        {
            public char[] Keys;
            public Node[] Children;
            public bool IsFinal;
            public string Token;
        }

        private readonly Node _root;

        private class BuilderNode
        {
            public SortedDictionary<char, BuilderNode> Children = new SortedDictionary<char, BuilderNode>();
            public bool IsFinal;
            public string Token;
        }

        public FstPrefixMatcher(IEnumerable<string> tokens)
        {
            var builderRoot = new BuilderNode();
            foreach (var token in tokens)
            {
                Add(builderRoot, token);
            }
            _root = Convert(builderRoot);
        }

        private void Add(BuilderNode node, string token)
        {
            var current = node;
            foreach (var c in token)
            {
                if (!current.Children.TryGetValue(c, out var next))
                {
                    next = new BuilderNode();
                    current.Children[c] = next;
                }
                current = next;
            }
            current.IsFinal = true;
            current.Token = token;
        }

        private Node Convert(BuilderNode builderNode)
        {
            var node = new Node();
            node.IsFinal = builderNode.IsFinal;
            node.Token = builderNode.Token;

            if (builderNode.Children.Count > 0)
            {
                node.Keys = new char[builderNode.Children.Count];
                node.Children = new Node[builderNode.Children.Count];
                int i = 0;
                foreach (var kvp in builderNode.Children)
                {
                    node.Keys[i] = kvp.Key;
                    node.Children[i] = Convert(kvp.Value);
                    i++;
                }
            }
            return node;
        }

        public IEnumerable<string> Match(string prefix)
        {
            var current = _root;
            foreach (var c in prefix)
            {
                if (current.Keys == null) return Enumerable.Empty<string>();

                int idx = Array.BinarySearch(current.Keys, c);
                if (idx < 0) return Enumerable.Empty<string>();

                current = current.Children[idx];
            }

            var results = new List<string>();
            Collect(current, results);
            return results;
        }

        private void Collect(Node node, List<string> results)
        {
            if (node.IsFinal)
            {
                results.Add(node.Token);
            }

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    Collect(child, results);
                }
            }
        }
    }
}
