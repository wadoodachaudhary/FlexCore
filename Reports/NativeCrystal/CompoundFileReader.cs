using System.Buffers.Binary;
using System.Text;

namespace Fx.ControlKit.Reports.NativeCrystal;

internal sealed class CompoundFileReader
{
    private const int FreeSector = -1;
    private const int EndOfChain = -2;
    private const int FatSector = -3;
    private const int DifatSector = -4;
    private const int DirectoryEntrySize = 128;

    private static readonly byte[] Signature =
    [
        0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1
    ];

    private readonly byte[] _fileBytes;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly int _miniStreamCutoffSize;
    private readonly int _firstDirectorySector;
    private readonly int _firstMiniFatSector;
    private readonly int _miniFatSectorCount;
    private readonly int _firstDifatSector;
    private readonly int _difatSectorCount;
    private readonly int[] _headerDifat;
    private readonly List<int> _fat;
    private readonly List<int> _miniFat;
    private readonly List<CompoundFileEntry> _entries;
    private readonly byte[] _miniStream;

    private CompoundFileReader(byte[] fileBytes)
    {
        if (fileBytes.Length < 512 || !fileBytes.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("The input is not an OLE compound document.");
        }

        _fileBytes = fileBytes;
        _sectorSize = 1 << ReadInt16(0x1E);
        _miniSectorSize = 1 << ReadInt16(0x20);
        _firstDirectorySector = ReadInt32(0x30);
        _miniStreamCutoffSize = ReadInt32(0x38);
        _firstMiniFatSector = ReadInt32(0x3C);
        _miniFatSectorCount = ReadInt32(0x40);
        _firstDifatSector = ReadInt32(0x44);
        _difatSectorCount = ReadInt32(0x48);

        var fatSectorCount = ReadInt32(0x2C);
        _headerDifat = ReadHeaderDifat();
        _fat = ReadFat(fatSectorCount);
        _entries = ReadDirectoryEntries();
        _miniFat = ReadMiniFat();
        _miniStream = ReadRootMiniStream();
    }

    public IReadOnlyList<CompoundFileEntry> Entries => _entries;

    public static CompoundFileReader Open(string path)
    {
        return new CompoundFileReader(File.ReadAllBytes(path));
    }

    public CompoundFileEntry? FindEntry(string name)
    {
        return _entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] ReadStream(CompoundFileEntry entry)
    {
        if (entry.Type != CompoundFileEntryType.Stream)
        {
            throw new InvalidOperationException($"'{entry.Name}' is not a stream.");
        }

        if (entry.Size < _miniStreamCutoffSize)
        {
            return ReadMiniStream(entry.StartSector, checked((int)entry.Size));
        }

        return ReadRegularStream(entry.StartSector, entry.Size);
    }

    public IReadOnlyList<CompoundFileEntry> EnumerateTree()
    {
        var root = _entries.FirstOrDefault(entry => entry.Type == CompoundFileEntryType.RootStorage);
        if (root is null)
        {
            return _entries;
        }

        var ordered = new List<CompoundFileEntry>();
        VisitDirectory(root.ChildId, parentPath: "", ordered);
        return ordered;
    }

    private void VisitDirectory(int entryId, string parentPath, List<CompoundFileEntry> ordered)
    {
        if (entryId < 0 || entryId >= _entries.Count)
        {
            return;
        }

        var entry = _entries[entryId];
        VisitDirectory(entry.LeftSiblingId, parentPath, ordered);

        var fullPath = string.IsNullOrEmpty(parentPath)
            ? entry.Name
            : parentPath + "/" + entry.Name;
        ordered.Add(entry with { FullPath = fullPath });

        if (entry.Type is CompoundFileEntryType.Storage or CompoundFileEntryType.RootStorage)
        {
            VisitDirectory(entry.ChildId, fullPath, ordered);
        }

        VisitDirectory(entry.RightSiblingId, parentPath, ordered);
    }

    private List<int> ReadFat(int fatSectorCount)
    {
        var fatSectors = new List<int>(_headerDifat.Where(sector => sector >= 0));
        var nextDifatSector = _firstDifatSector;
        for (var i = 0; i < _difatSectorCount && nextDifatSector >= 0; i++)
        {
            var sector = GetSector(nextDifatSector);
            var entriesPerDifatSector = (_sectorSize / 4) - 1;
            for (var entryIndex = 0; entryIndex < entriesPerDifatSector; entryIndex++)
            {
                var value = BinaryPrimitives.ReadInt32LittleEndian(sector.Slice(entryIndex * 4, 4));
                if (value >= 0)
                {
                    fatSectors.Add(value);
                }
            }

            nextDifatSector = BinaryPrimitives.ReadInt32LittleEndian(
                sector.Slice(entriesPerDifatSector * 4, 4));
        }

        if (fatSectors.Count < fatSectorCount)
        {
            throw new InvalidDataException("The compound document FAT is incomplete.");
        }

        var fat = new List<int>(fatSectorCount * (_sectorSize / 4));
        foreach (var fatSector in fatSectors.Take(fatSectorCount))
        {
            var sector = GetSector(fatSector);
            for (var offset = 0; offset < _sectorSize; offset += 4)
            {
                fat.Add(BinaryPrimitives.ReadInt32LittleEndian(sector.Slice(offset, 4)));
            }
        }

        return fat;
    }

    private List<CompoundFileEntry> ReadDirectoryEntries()
    {
        var directoryBytes = ReadRegularStream(_firstDirectorySector, expectedSize: null);
        var entries = new List<CompoundFileEntry>(directoryBytes.Length / DirectoryEntrySize);
        for (var offset = 0; offset + DirectoryEntrySize <= directoryBytes.Length; offset += DirectoryEntrySize)
        {
            var entryBytes = directoryBytes.AsSpan(offset, DirectoryEntrySize);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entryBytes.Slice(0x40, 2));
            if (nameLength < 2)
            {
                continue;
            }

            var rawNameBytes = Math.Min(nameLength - 2, 64);
            var name = Encoding.Unicode.GetString(entryBytes.Slice(0, rawNameBytes)).TrimEnd('\0');
            var type = (CompoundFileEntryType)entryBytes[0x42];
            var startSector = BinaryPrimitives.ReadInt32LittleEndian(entryBytes.Slice(0x74, 4));
            var size = BinaryPrimitives.ReadInt64LittleEndian(entryBytes.Slice(0x78, 8));
            entries.Add(new CompoundFileEntry(
                entries.Count,
                name,
                name,
                type,
                BinaryPrimitives.ReadInt32LittleEndian(entryBytes.Slice(0x44, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(entryBytes.Slice(0x48, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(entryBytes.Slice(0x4C, 4)),
                startSector,
                size));
        }

        return entries;
    }

    private List<int> ReadMiniFat()
    {
        if (_firstMiniFatSector < 0 || _miniFatSectorCount <= 0)
        {
            return [];
        }

        var bytes = ReadRegularStream(_firstMiniFatSector, (long)_miniFatSectorCount * _sectorSize);
        var miniFat = new List<int>(bytes.Length / 4);
        for (var offset = 0; offset + 4 <= bytes.Length; offset += 4)
        {
            miniFat.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));
        }

        return miniFat;
    }

    private byte[] ReadRootMiniStream()
    {
        var root = _entries.FirstOrDefault(entry => entry.Type == CompoundFileEntryType.RootStorage);
        if (root is null || root.StartSector < 0 || root.Size <= 0)
        {
            return [];
        }

        return ReadRegularStream(root.StartSector, root.Size);
    }

    private byte[] ReadRegularStream(int startSector, long? expectedSize)
    {
        if (startSector < 0)
        {
            return [];
        }

        using var output = new MemoryStream();
        var seen = new HashSet<int>();
        var sector = startSector;
        while (sector >= 0 && sector != EndOfChain)
        {
            if (!seen.Add(sector))
            {
                throw new InvalidDataException("The compound document contains a cyclic sector chain.");
            }

            output.Write(GetSector(sector));
            if (sector >= _fat.Count)
            {
                throw new InvalidDataException("The compound document references a FAT sector outside the file.");
            }

            sector = _fat[sector];
            if (sector is FatSector or DifatSector or FreeSector)
            {
                break;
            }
        }

        var bytes = output.ToArray();
        if (expectedSize is null)
        {
            return bytes;
        }

        var size = checked((int)Math.Min(expectedSize.Value, bytes.LongLength));
        Array.Resize(ref bytes, size);
        return bytes;
    }

    private byte[] ReadMiniStream(int startMiniSector, int expectedSize)
    {
        if (startMiniSector < 0 || expectedSize == 0)
        {
            return [];
        }

        using var output = new MemoryStream();
        var seen = new HashSet<int>();
        var miniSector = startMiniSector;
        while (miniSector >= 0 && miniSector != EndOfChain)
        {
            if (!seen.Add(miniSector))
            {
                throw new InvalidDataException("The compound document contains a cyclic mini-sector chain.");
            }

            var offset = miniSector * _miniSectorSize;
            if (offset < 0 || offset + _miniSectorSize > _miniStream.Length)
            {
                throw new InvalidDataException("The compound document references a mini-sector outside the mini stream.");
            }

            output.Write(_miniStream, offset, _miniSectorSize);
            if (miniSector >= _miniFat.Count)
            {
                throw new InvalidDataException("The compound document references a mini-FAT sector outside the file.");
            }

            miniSector = _miniFat[miniSector];
        }

        var bytes = output.ToArray();
        Array.Resize(ref bytes, Math.Min(expectedSize, bytes.Length));
        return bytes;
    }

    private ReadOnlySpan<byte> GetSector(int sector)
    {
        var offset = checked((sector + 1) * _sectorSize);
        if (offset < 0 || offset + _sectorSize > _fileBytes.Length)
        {
            throw new InvalidDataException("The compound document references a sector outside the file.");
        }

        return _fileBytes.AsSpan(offset, _sectorSize);
    }

    private int[] ReadHeaderDifat()
    {
        var sectors = new int[109];
        for (var i = 0; i < sectors.Length; i++)
        {
            sectors[i] = ReadInt32(0x4C + (i * 4));
        }

        return sectors;
    }

    private short ReadInt16(int offset)
    {
        return BinaryPrimitives.ReadInt16LittleEndian(_fileBytes.AsSpan(offset, 2));
    }

    private int ReadInt32(int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(_fileBytes.AsSpan(offset, 4));
    }
}

internal enum CompoundFileEntryType
{
    Unknown = 0,
    Storage = 1,
    Stream = 2,
    RootStorage = 5
}

internal sealed record CompoundFileEntry(
    int Id,
    string Name,
    string FullPath,
    CompoundFileEntryType Type,
    int LeftSiblingId,
    int RightSiblingId,
    int ChildId,
    int StartSector,
    long Size);
