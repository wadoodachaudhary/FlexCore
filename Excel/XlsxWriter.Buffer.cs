namespace Fx.ControlKit.Excel;

public static partial class XlsxWriter
{
    /// <summary>
    /// A seekable, write-only in-memory stream kept in fixed 1 MB chunks, for ZipArchive's create
    /// mode (it seeks back to finish each entry's header). Unlike a MemoryStream it never copies
    /// itself to grow, so a large workbook is held once while it is written and once more in
    /// <see cref="ToArray"/>. Only the first chunk grows (by doubling), so a small workbook stays small.
    /// </summary>
    private sealed class ChunkedBuffer : Stream
    {
        private const int Shift = 20;
        private const int ChunkSize = 1 << Shift;
        private const int Mask = ChunkSize - 1;

        private readonly List<byte[]> _chunks = new();
        private long _length;
        private long _position;

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void WriteByte(byte value) => Write([value]);

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_position + buffer.Length > Array.MaxLength)
                throw new IOException("Stream was too long.");
            while (!buffer.IsEmpty)
            {
                var offset = (int)(_position & Mask);
                var count = Math.Min(buffer.Length, ChunkSize - offset);
                buffer[..count].CopyTo(Chunk((int)(_position >> Shift), offset + count).AsSpan(offset));
                buffer = buffer[count..];
                _position += count;
            }
            _length = Math.Max(_length, _position);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public byte[] ToArray()
        {
            var result = GC.AllocateUninitializedArray<byte>((int)_length);
            for (long done = 0; done < _length;)
            {
                var count = (int)Math.Min(ChunkSize, _length - done);
                _chunks[(int)(done >> Shift)].AsSpan(0, count).CopyTo(result.AsSpan((int)done));
                done += count;
            }
            return result;
        }

        // The chunk at index, holding at least `end` bytes. Chunks before it are full size. Every byte
        // past the length is zero (new chunks are, and nothing shrinks), so a write past the end
        // leaves a zero gap, as a MemoryStream does.
        private byte[] Chunk(int index, int end)
        {
            while (_chunks.Count <= index)
            {
                if (_chunks.Count == 1)
                    Grow(ChunkSize);
                _chunks.Add(_chunks.Count == 0 && index == 0 ? new byte[Math.Max(end, 4096)] : new byte[ChunkSize]);
            }
            if (_chunks[index].Length < end)
                Grow(end);
            return _chunks[index];
        }

        private void Grow(int end)
        {
            var first = _chunks[0];
            if (first.Length >= end)
                return;
            var grown = new byte[Math.Min(ChunkSize, Math.Max(end, first.Length * 2))];
            first.CopyTo(grown, 0);
            _chunks[0] = grown;
        }
    }
}
