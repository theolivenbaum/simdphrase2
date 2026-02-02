using System;
using System.IO;

namespace SimdPhrase2.Storage
{
    public interface ISimdStorage
    {
        Stream OpenRead(string path);
        Stream OpenWrite(string path);
        Stream OpenReadWrite(string path);
        void CreateDirectory(string path);
        void DeleteDirectory(string path);
        void DeleteFile(string path);
        bool FileExists(string path);
        bool DirectoryExists(string path);
        string Combine(string path1, string path2);
        string ReadAllText(string path);
        void WriteAllText(string path, string contents);
    }
}
