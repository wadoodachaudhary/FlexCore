using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Fx.ControlKit.Reports.NativeCrystal;

[UnsupportedOSPlatform("browser")]
public static class TslvStreamReader
{
    private static readonly byte[] DefaultKey =
    [
        17, 221, 24, 150, 189, 74, 21, 205,
        191, 242, 84, 53, 3, 230, 118, 15
    ];

    private static readonly byte[] QueryEngineKey =
    [
        42, 188, 223, 31, 214, 248, 172, 108,
        10, 80, 12, 101, 32, 71, 186, 220
    ];

    public static TslvDecodedStream Decode(byte[] bytes, int defaultSchema)
    {
        var streamHeader = ReadHeader(bytes, defaultSchema);

        var body = bytes.AsSpan(streamHeader.BodyOffset).ToArray();
        if (streamHeader.Encrypted && streamHeader.Compressed)
        {
            body = DecryptAndInflate(body, DefaultKey, streamHeader.InitializationVector);
        }
        else
        {
            if (streamHeader.Encrypted)
            {
                body = Decrypt(body, DefaultKey, streamHeader.InitializationVector, BlockByteTransform.None, BlockByteTransform.None, BlockByteTransform.None);
            }

            if (streamHeader.Compressed)
            {
                body = Inflate(body);
            }
        }

        return new TslvDecodedStream(
            streamHeader.Record.Schema,
            streamHeader.Encrypted,
            streamHeader.Compressed,
            streamHeader.KeySize,
            streamHeader.UnknownFlag,
            streamHeader.InitializationVector,
            body,
            ReadRecordHeaders(body, defaultSchema));
    }

    public static TslvStreamHeader ReadHeader(byte[] bytes, int defaultSchema)
    {
        var cursor = new Cursor(bytes, defaultSchema);
        var header = cursor.LoadAnyRecord();
        if (header.Type != 65535)
        {
            throw new InvalidDataException("The stream does not start with a Crystal TSLV header record.");
        }

        var encrypted = cursor.LoadBoolean();
        var keySize = cursor.LoadUInt16();
        var unknownFlag = cursor.LoadBoolean();
        var initializationVector = encrypted ? cursor.LoadBlock(16) : [];
        var compressed = cursor.BytesLeftInRecord > 0 && cursor.LoadBoolean();
        cursor.SkipRestOfRecord(header);

        return new TslvStreamHeader(
            header,
            encrypted,
            compressed,
            keySize,
            unknownFlag,
            initializationVector,
            cursor.Position);
    }

    public static QeDecodedStream DecodeQueryEngineStream(byte[] bytes)
    {
        var header = QueryEngineStreamHeader.Read(bytes);
        var body = bytes.AsSpan(header.BodyOffset).ToArray();

        if (header.Encrypted && header.Compressed)
        {
            body = DecryptAndInflate(body, QueryEngineKey, header.InitializationVector);
        }
        else
        {
            if (header.Encrypted)
            {
                body = Decrypt(body, QueryEngineKey, header.InitializationVector, BlockByteTransform.ReverseWords, BlockByteTransform.ReverseWords, BlockByteTransform.ReverseWords);
            }

            if (header.Compressed)
            {
                body = Inflate(body);
            }
        }

        return new QeDecodedStream(
            header,
            body,
            ReadRecordHeaders(body, defaultSchema: 2304));
    }

    public static bool LooksLikeQueryEngineStream(byte[] bytes)
    {
        return bytes.Length >= 4 &&
               bytes[0] == 0x51 &&
               bytes[1] == 0x45 &&
               bytes[2] == 0x4E &&
               bytes[3] == 0x47;
    }

    public static byte[] DecryptBodyForDiagnostics(byte[] bytes, int defaultSchema)
    {
        var header = ReadHeader(bytes, defaultSchema);
        var body = bytes.AsSpan(header.BodyOffset).ToArray();
        return header.Encrypted
            ? Decrypt(body, DefaultKey, header.InitializationVector, BlockByteTransform.None, BlockByteTransform.None, BlockByteTransform.None)
            : body;
    }

    public static IReadOnlyList<string> DecryptPrefixesForDiagnostics(byte[] bytes, int defaultSchema)
    {
        var header = ReadHeader(bytes, defaultSchema);
        var body = bytes.AsSpan(header.BodyOffset).ToArray();
        var prefixes = new List<string>();
        foreach (var keyTransform in Enum.GetValues<BlockByteTransform>())
        foreach (var inputTransform in Enum.GetValues<BlockByteTransform>())
        foreach (var outputTransform in Enum.GetValues<BlockByteTransform>())
        {
            var decrypted = Decrypt(body, DefaultKey, header.InitializationVector, keyTransform, inputTransform, outputTransform);
            prefixes.Add($"{keyTransform}/{inputTransform}/{outputTransform}:{Convert.ToHexString(decrypted.AsSpan(0, Math.Min(8, decrypted.Length)))}");
        }

        return prefixes;
    }

    private static IReadOnlyList<TslvRecordHeader> ReadRecordHeaders(byte[] body, int defaultSchema)
    {
        var cursor = new Cursor(body, defaultSchema);
        var records = new List<TslvRecordHeader>();
        while (cursor.Position < body.Length)
        {
            var position = cursor.Position;
            var record = cursor.LoadAnyRecord();
            records.Add(record with { Offset = position });
            cursor.SkipRestOfRecord(record);
        }

        return records;
    }

    private static byte[] DecryptAndInflate(byte[] bytes, byte[] key, byte[] initializationVector)
    {
        Exception? lastError = null;
        foreach (var keyTransform in Enum.GetValues<BlockByteTransform>())
        foreach (var inputTransform in Enum.GetValues<BlockByteTransform>())
        foreach (var outputTransform in Enum.GetValues<BlockByteTransform>())
        {
            var decrypted = Decrypt(bytes, key, initializationVector, keyTransform, inputTransform, outputTransform);
            try
            {
                return Inflate(decrypted);
            }
            catch (InvalidDataException ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidDataException("Could not decrypt and inflate the Crystal TSLV stream.", lastError);
    }

    private static byte[] Decrypt(
        byte[] bytes,
        byte[] key,
        byte[] initializationVector,
        BlockByteTransform keyTransform,
        BlockByteTransform inputTransform,
        BlockByteTransform outputTransform)
    {
        using var aes = Aes.Create();
        aes.Key = Transform(key, keyTransform);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var state = initializationVector.ToArray();
        var keyStream = new byte[16];
        var transformedState = new byte[16];
        var transformedKeyStream = new byte[16];
        var output = new byte[bytes.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            Transform(state, transformedState, inputTransform);
            encryptor.TransformBlock(transformedState, 0, transformedState.Length, transformedKeyStream, 0);
            Transform(transformedKeyStream, keyStream, outputTransform);
            var count = Math.Min(keyStream.Length, bytes.Length - offset);
            for (var i = 0; i < count; i++)
            {
                var cipher = bytes[offset + i];
                output[offset + i] = (byte)(cipher ^ keyStream[i]);
                state[i] = cipher;
            }

            offset += count;
        }

        return output;
    }

    private static byte[] Transform(byte[] bytes, BlockByteTransform transform)
    {
        var copy = bytes.ToArray();
        Transform(bytes, copy, transform);
        return copy;
    }

    private static void Transform(byte[] source, byte[] destination, BlockByteTransform transform)
    {
        switch (transform)
        {
            case BlockByteTransform.None:
                source.CopyTo(destination, 0);
                break;
            case BlockByteTransform.ReverseWords:
                for (var i = 0; i < source.Length; i += 4)
                {
                    destination[i + 0] = source[i + 3];
                    destination[i + 1] = source[i + 2];
                    destination[i + 2] = source[i + 1];
                    destination[i + 3] = source[i + 0];
                }
                break;
            case BlockByteTransform.ReverseAll:
                for (var i = 0; i < source.Length; i++)
                {
                    destination[i] = source[source.Length - 1 - i];
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transform), transform, null);
        }
    }

    private static byte[] Inflate(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private sealed class Cursor
    {
        private readonly byte[] _bytes;
        private readonly int _defaultSchema;
        private int _simpleKey;

        public Cursor(byte[] bytes, int defaultSchema)
        {
            _bytes = bytes;
            _defaultSchema = defaultSchema;
        }

        public int Position { get; private set; }

        public int BytesLeftInRecord { get; private set; }

        public TslvRecordHeader LoadAnyRecord()
        {
            var first = LoadByte();
            var second = LoadByte();
            var lengthSize = (first & 0x80) != 0
                ? (first & 0x40) != 0 ? 4 : 2
                : (first & 0x40) != 0 ? 1 : 0;
            var hasSchema = (first & 0x20) != 0;
            var enhancedStrings = (first & 0x10) != 0;
            var simpleEncrypted = (first & 0x08) != 0;
            var extendedType = (first & 0x04) != 0;

            var type = extendedType
                ? LoadRawUInt16()
                : ((first & 0x03) << 8) + second;

            var schema = hasSchema ? LoadRawUInt16() : _defaultSchema;
            var length = lengthSize switch
            {
                0 => 0,
                1 => LoadByte(),
                2 => LoadRawUInt16(),
                4 => LoadRawInt32(),
                _ => throw new InvalidDataException("Invalid TSLV record length field.")
            };

            if (simpleEncrypted)
            {
                _simpleKey ^= type & 0xFF;
            }

            BytesLeftInRecord = length;
            return new TslvRecordHeader(
                Offset: Position,
                Type: type,
                Schema: schema,
                Length: length,
                LengthSize: lengthSize,
                HasSchema: hasSchema,
                EnhancedStrings: enhancedStrings,
                SimpleEncrypted: simpleEncrypted);
        }

        public bool LoadBoolean()
        {
            return LoadUInt16() > 0;
        }

        public int LoadUInt16()
        {
            EnsureRecordBytes(2);
            return LoadRawUInt16();
        }

        public byte[] LoadBlock(int length)
        {
            EnsureRecordBytes(length);
            var block = new byte[length];
            for (var i = 0; i < length; i++)
            {
                block[i] = (byte)LoadByte();
            }

            return block;
        }

        public void SkipRestOfRecord(TslvRecordHeader record)
        {
            if (BytesLeftInRecord > 0)
            {
                Position += BytesLeftInRecord;
                BytesLeftInRecord = 0;
            }

            if (record.SimpleEncrypted)
            {
                _simpleKey ^= record.Type & 0xFF;
            }
        }

        private void EnsureRecordBytes(int count)
        {
            if (BytesLeftInRecord < count)
            {
                throw new InvalidDataException("The TSLV record ended unexpectedly.");
            }

            BytesLeftInRecord -= count;
        }

        private int LoadRawUInt16()
        {
            var high = LoadByte();
            var low = LoadByte();
            return (high << 8) | low;
        }

        private int LoadRawInt32()
        {
            Span<byte> raw = stackalloc byte[4];
            for (var i = 0; i < raw.Length; i++)
            {
                raw[i] = (byte)LoadByte();
            }

            return BinaryPrimitives.ReadInt32BigEndian(raw);
        }

        private int LoadByte()
        {
            if (Position >= _bytes.Length)
            {
                throw new EndOfStreamException("Unexpected end of TSLV data.");
            }

            return _bytes[Position++] ^ _simpleKey;
        }
    }
}

public sealed record TslvDecodedStream(
    int HeaderSchema,
    bool Encrypted,
    bool Compressed,
    int KeySize,
    bool UnknownFlag,
    byte[] InitializationVector,
    byte[] Body,
    IReadOnlyList<TslvRecordHeader> Records);

public sealed record TslvStreamHeader(
    TslvRecordHeader Record,
    bool Encrypted,
    bool Compressed,
    int KeySize,
    bool UnknownFlag,
    byte[] InitializationVector,
    int BodyOffset);

public sealed record QeDecodedStream(
    QueryEngineStreamHeader Header,
    byte[] Body,
    IReadOnlyList<TslvRecordHeader> Records);

[UnsupportedOSPlatform("browser")]
public sealed record QueryEngineStreamHeader(
    bool HasHeader,
    int Version,
    int HeaderLength,
    bool Compressed,
    bool Encrypted,
    int OriginalLength,
    byte[] InitializationVector,
    int BodyOffset)
{
    public static QueryEngineStreamHeader Read(byte[] bytes)
    {
        if (!TslvStreamReader.LooksLikeQueryEngineStream(bytes))
        {
            return new QueryEngineStreamHeader(
                HasHeader: false,
                Version: 0,
                HeaderLength: 0,
                Compressed: false,
                Encrypted: false,
                OriginalLength: bytes.Length,
                InitializationVector: [],
                BodyOffset: 0);
        }

        if (bytes.Length < 18)
        {
            throw new InvalidDataException("The Query Engine stream header is incomplete.");
        }

        var version = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(4, 2));
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(6, 4));
        var flags = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(10, 4));
        var originalLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(14, 4));
        var compressed = (flags & 1) != 0;
        var encrypted = (flags & 2) != 0;
        var offset = 18;
        if (compressed)
        {
            offset += 2;
        }

        byte[] initializationVector = [];
        if (encrypted)
        {
            if (bytes.Length < offset + 18)
            {
                throw new InvalidDataException("The encrypted Query Engine stream header is incomplete.");
            }

            offset += 2;
            initializationVector = bytes.AsSpan(offset, 16).ToArray();
            offset += 16;
        }

        if (headerLength < offset || headerLength > bytes.Length)
        {
            throw new InvalidDataException("The Query Engine stream header length is invalid.");
        }

        return new QueryEngineStreamHeader(
            HasHeader: true,
            Version: version,
            HeaderLength: headerLength,
            Compressed: compressed,
            Encrypted: encrypted,
            OriginalLength: originalLength,
            InitializationVector: initializationVector,
            BodyOffset: headerLength);
    }
}

public sealed record TslvRecordHeader(
    int Offset,
    int Type,
    int Schema,
    int Length,
    int LengthSize,
    bool HasSchema,
    bool EnhancedStrings,
    bool SimpleEncrypted,
    int ObjectId = 0);

internal enum BlockByteTransform
{
    None,
    ReverseWords,
    ReverseAll
}

public sealed class TslvArchiveReader
{
    private readonly byte[] _bytes;
    private readonly int _defaultSchema;
    private readonly Stack<TslvRecordFrame> _records = new();
    private int _simpleKey;
    private bool _enhancedStrings = true;
    private bool _readObjectIds;
    private bool _readEnumsAsInt32;

    public TslvArchiveReader(byte[] bytes, int defaultSchema)
    {
        _bytes = bytes;
        _defaultSchema = defaultSchema;
    }

    public int Position { get; private set; }

    public TslvRecordHeader? CurrentRecord => _records.Count == 0 ? null : _records.Peek().Header;

    public int BytesLeftInRecord
    {
        get
        {
            if (_records.Count == 0)
            {
                return _bytes.Length - Position;
            }

            var frame = _records.Peek();
            return Math.Max(0, frame.DataStart + frame.Header.Length - Position);
        }
    }

    public bool ReadObjectIds
    {
        get => _readObjectIds;
        set => _readObjectIds = value;
    }

    public bool ReadEnumsAsInt32
    {
        get => _readEnumsAsInt32;
        set => _readEnumsAsInt32 = value;
    }

    public TslvRecordHeader LoadAnyRecord()
    {
        var recordOffset = Position;
        var first = ReadByte();
        var second = ReadByte();
        var lengthSize = (first & 0x80) != 0
            ? (first & 0x40) != 0 ? 4 : 2
            : (first & 0x40) != 0 ? 1 : 0;
        var hasSchema = (first & 0x20) != 0;
        _enhancedStrings = (first & 0x10) != 0;
        var simpleEncrypted = (first & 0x08) != 0;
        var extendedType = (first & 0x04) != 0;

        var type = extendedType
            ? ReadUInt16Unchecked()
            : ((first & 0x03) << 8) + second;

        var schema = hasSchema ? ReadUInt16Unchecked() : _defaultSchema;
        var length = lengthSize switch
        {
            0 => 0,
            1 => ReadByte(),
            2 => ReadUInt16Unchecked(),
            4 => ReadInt32Unchecked(),
            _ => throw new InvalidDataException("Invalid TSLV record length field.")
        };

        if (simpleEncrypted)
        {
            _simpleKey ^= type & 0xFF;
        }

        var dataStart = Position;
        var objectId = 0;
        if (_readObjectIds)
        {
            if (length < 4)
            {
                throw new InvalidDataException("A TSLV record with object IDs enabled must contain at least four bytes.");
            }

            objectId = ReadInt32Unchecked();
        }

        var header = new TslvRecordHeader(
            Offset: recordOffset,
            Type: type,
            Schema: schema,
            Length: length,
            LengthSize: lengthSize,
            HasSchema: hasSchema,
            EnhancedStrings: _enhancedStrings,
            SimpleEncrypted: simpleEncrypted,
            ObjectId: objectId);
        _records.Push(new TslvRecordFrame(header, dataStart, simpleEncrypted ? type & 0xFF : 0));

        return header;
    }

    public TslvRecordHeader LoadNextRecord(int type, int maxSchema, int stopType)
    {
        while (true)
        {
            var record = LoadAnyRecord();
            if (record.Type == type)
            {
                if (record.Schema > maxSchema)
                {
                    throw new InvalidDataException($"TSLV record {type} has unsupported schema {record.Schema}; expected <= {maxSchema}.");
                }

                return record;
            }

            if (record.Type == stopType)
            {
                throw new InvalidDataException($"TSLV record {type} was not found before stop record {stopType}.");
            }

            SkipRestOfRecord();
        }
    }

    public void SkipRestOfRecord()
    {
        if (_records.Count == 0)
        {
            throw new InvalidOperationException("There is no current TSLV record to skip.");
        }

        var frame = _records.Pop();
        if (frame.SimpleEncryptionKey != 0)
        {
            _simpleKey ^= frame.SimpleEncryptionKey;
        }

        var recordEnd = frame.DataStart + frame.Header.Length;
        if (Position > recordEnd)
        {
            throw new InvalidDataException($"Read past the end of TSLV record {frame.Header.Type}.");
        }

        Position = recordEnd;
    }

    public string? LoadString()
    {
        if (_enhancedStrings)
        {
            var length = LoadInt32();
            switch (length)
            {
                case 0:
                    return null;
                case 1:
                    _ = LoadUInt8();
                    return string.Empty;
            }

            if (length < 0)
            {
                throw new InvalidDataException("Crystal TSLV string length cannot be negative.");
            }

            var bytes = LoadBlock(length);
            return System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Max(0, length - 1));
        }

        var buffer = new List<byte>();
        while (true)
        {
            var value = LoadUInt8();
            if (value == 0)
            {
                return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            }

            buffer.Add((byte)value);
        }
    }

    public void SkipString()
    {
        if (_enhancedStrings)
        {
            var length = LoadInt32();
            if (length > 0)
            {
                SkipBytes(length);
            }

            return;
        }

        while (LoadUInt8() != 0)
        {
        }
    }

    public bool LoadBoolean()
    {
        return LoadUInt16() > 0;
    }

    public int LoadInt8()
    {
        var value = LoadUInt8();
        return value > sbyte.MaxValue ? value - 256 : value;
    }

    public int LoadUInt8()
    {
        EnsureCurrentRecordBytes(1);
        return ReadByte();
    }

    public int LoadInt16()
    {
        var value = LoadUInt16();
        return value > short.MaxValue ? value - 65536 : value;
    }

    public int LoadUInt16()
    {
        EnsureCurrentRecordBytes(2);
        return ReadUInt16Unchecked();
    }

    public int LoadInt32()
    {
        EnsureCurrentRecordBytes(4);
        return ReadInt32Unchecked();
    }

    public int PeekInt32()
    {
        EnsureCurrentRecordBytes(4);
        Span<byte> raw = stackalloc byte[4];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(_bytes[Position + i] ^ _simpleKey);
        }

        return BinaryPrimitives.ReadInt32BigEndian(raw);
    }

    public long LoadInt64()
    {
        EnsureCurrentRecordBytes(8);
        Span<byte> raw = stackalloc byte[8];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)ReadByte();
        }

        return BinaryPrimitives.ReadInt64BigEndian(raw);
    }

    public double LoadDouble()
    {
        return BitConverter.Int64BitsToDouble(LoadInt64());
    }

    public int LoadInt16Compressed()
    {
        EnsureCurrentRecordBytes(1);
        var value = ReadByte();
        if ((value & 0x80) != 0)
        {
            EnsureCurrentRecordBytes(1);
            value = ((value & 0x7F) << 8) + ReadByte();
        }

        return value;
    }

    public int LoadInt32Compressed()
    {
        EnsureCurrentRecordBytes(2);
        var value = ReadUInt16Unchecked();
        if ((value & 0x8000) != 0)
        {
            EnsureCurrentRecordBytes(2);
            value = ((value & short.MaxValue) << 16) + ReadUInt16Unchecked();
        }

        return value;
    }

    public int LoadEnum()
    {
        return _readEnumsAsInt32 ? LoadInt32() : LoadInt16Compressed();
    }

    public byte[] LoadBinary()
    {
        var length = LoadInt32();
        return LoadBlock(length);
    }

    public byte[] LoadBlock(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Block length cannot be negative.");
        }

        EnsureCurrentRecordBytes(length);
        var block = new byte[length];
        for (var i = 0; i < length; i++)
        {
            block[i] = (byte)ReadByte();
        }

        return block;
    }

    public void SkipBytes(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Skip count cannot be negative.");
        }

        EnsureCurrentRecordBytes(count);
        Position += count;
    }

    private void EnsureCurrentRecordBytes(int count)
    {
        if (_records.Count == 0)
        {
            if (Position + count > _bytes.Length)
            {
                throw new EndOfStreamException("Unexpected end of TSLV data.");
            }

            return;
        }

        if (BytesLeftInRecord < count)
        {
            throw new InvalidDataException("The TSLV record ended unexpectedly.");
        }
    }

    private int ReadUInt16Unchecked()
    {
        var high = ReadByte();
        var low = ReadByte();
        return (high << 8) | low;
    }

    private int ReadInt32Unchecked()
    {
        Span<byte> raw = stackalloc byte[4];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)ReadByte();
        }

        return BinaryPrimitives.ReadInt32BigEndian(raw);
    }

    private int ReadByte()
    {
        if (Position >= _bytes.Length)
        {
            throw new EndOfStreamException("Unexpected end of TSLV data.");
        }

        return _bytes[Position++] ^ _simpleKey;
    }

    private sealed record TslvRecordFrame(
        TslvRecordHeader Header,
        int DataStart,
        int SimpleEncryptionKey);
}
