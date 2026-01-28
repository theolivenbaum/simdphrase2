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

        public DocumentStore(string basePath)
        {
            string offsetsPath = Path.Combine(basePath, "doc_offsets.bin");
            string dataPath = Path.Combine(basePath, "documents.bin");
            Directory.CreateDirectory(basePath);

            _offsetsStream = new FileStream(offsetsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _dataStream = new FileStream(dataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
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

        public string? GetDocument(uint docId)
        {
            lock (_lock)
            {
                long indexPos = (long)docId * 16;
                if (indexPos + 16 > _offsetsStream.Length)
                    return null;

                _offsetsStream.Seek(indexPos, SeekOrigin.Begin);
                Span<byte> buffer = stackalloc byte[16];
                int read = _offsetsStream.Read(buffer);
                if (read < 16) return null;

                long offset = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                long length = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(8));

                if (length < 0) return null;
                if (length == 0) return string.Empty;

                byte[] data = new byte[length];
                _dataStream.Seek(offset, SeekOrigin.Begin);
                if (_dataStream.Read(data, 0, (int)length) != length)
                    return null;

                return Encoding.UTF8.GetString(data);
            }
        }

        public void Dispose()
        {
            _offsetsStream?.Dispose();
            _dataStream?.Dispose();
        }
    }
}
