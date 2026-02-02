using System;
using System.IO;

namespace SimdPhrase2.Storage
{
    public class FileSystemStorage : ISimdStorage
    {
        public Stream OpenRead(string path) => File.OpenRead(path);

        public Stream OpenWrite(string path) => new FileStream(path, FileMode.Create, FileAccess.Write);

        public Stream OpenReadWrite(string path) => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        public void DeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public string Combine(string path1, string path2) => Path.Combine(path1, path2);

        public string ReadAllText(string path) => File.ReadAllText(path);

        public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    }
}
