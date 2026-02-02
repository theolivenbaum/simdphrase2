using System;
using System.IO;
using System.Text;
using System.Buffers.Binary;

namespace SimdPhrase2.Db
{
    public class DocumentStore : IDisposable
    {
        private readonly FileStream _offsetsStream;
        private readonly FileStream _dataStream;
        private readonly object _lock = new object();

        public DocumentStore(string basePath, bool readOnly = false)
        {
            string offsetsPath = Path.Combine(basePath, "doc_offsets.bin");
            string dataPath = Path.Combine(basePath, "documents.bin");
            Directory.CreateDirectory(basePath);

            if (readOnly)
            {
                _offsetsStream = new FileStream(offsetsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _dataStream = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            else
            {
                _offsetsStream = new FileStream(offsetsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                _dataStream = new FileStream(dataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            }
        }

        public void AddDocument(uint docId, string content)
        {
            lock (_lock)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                long offset = _dataStream.Length;
                _dataStream.Seek(0, SeekOrigin.End);
                _dataStream.Write(bytes, 0, bytes.Length);

                // Write index
                long indexPos = (long)docId * 16;
                _offsetsStream.Seek(indexPos, SeekOrigin.Begin);

                Span<byte> buffer = stackalloc byte[16];
                BinaryPrimitives.WriteInt64LittleEndian(buffer, offset);
                BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(8), (long)bytes.Length);
                _offsetsStream.Write(buffer);
            }
        }

        public string GetDocument(uint docId)
        {
            // Use RandomAccess for thread-safe reading without locks
            long indexPos = (long)docId * 16;
            if (indexPos + 16 > _offsetsStream.Length)
                return null;

            Span<byte> buffer = stackalloc byte[16];
            int read = RandomAccess.Read(_offsetsStream.SafeFileHandle, buffer, indexPos);
            if (read < 16) return null;

            long offset = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            long length = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(8));

            if (length < 0) return null;
            if (length == 0) return string.Empty;

            byte[] data = new byte[length];
            int totalRead = 0;
            while (totalRead < length)
            {
                int r = RandomAccess.Read(_dataStream.SafeFileHandle, data.AsSpan(totalRead), offset + totalRead);
                if (r == 0) break; // EOF
                totalRead += r;
            }

            if (totalRead != length)
                return null;

            return Encoding.UTF8.GetString(data);
        }

        public void Dispose()
        {
            _offsetsStream?.Dispose();
            _dataStream?.Dispose();
        }
    }
}
