using System;
using System.Collections.Generic;
using System.IO;

namespace SimdPhrase2
{
    public abstract class CommonTokensConfig
    {
        public static CommonTokensConfig None => new NoneConfig();
        public static CommonTokensConfig FromList(HashSet<string> tokens) => new ListConfig(tokens);
        public static CommonTokensConfig FromFixedNum(int num) => new FixedNumConfig(num);
        public static CommonTokensConfig FromPercentage(double percentage) => new PercentageConfig(percentage);

        private class NoneConfig : CommonTokensConfig { }

        public class ListConfig : CommonTokensConfig
        {
            public HashSet<string> Tokens { get; }
            public ListConfig(HashSet<string> tokens) { Tokens = tokens; }
        }

        public class FixedNumConfig : CommonTokensConfig
        {
            public int Num { get; }
            public FixedNumConfig(int num) { Num = num; }
        }

        public class PercentageConfig : CommonTokensConfig
        {
            public double Percentage { get; }
            public PercentageConfig(double percentage) { Percentage = percentage; }
        }
    }

    public static class CommonTokensPersistence
    {
        public static void Save(string path, HashSet<string> tokens)
        {
            using var fs = new FileStream(path, FileMode.Create);
            using var writer = new BinaryWriter(fs);
            writer.Write(tokens.Count);
            foreach (var token in tokens)
            {
                writer.Write(token);
            }
        }

        public static HashSet<string> Load(string path)
        {
            var tokens = new HashSet<string>();
            if (!File.Exists(path)) return tokens;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            try
            {
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    tokens.Add(reader.ReadString());
                }
            }
            catch (EndOfStreamException) { }

            return tokens;
        }
    }
}
