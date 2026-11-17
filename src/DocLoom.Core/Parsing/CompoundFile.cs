using System.Text;

namespace DocLoom.Core.Parsing;

/// <summary>
/// 复合文档（OLE2 / CFB）读取器。.doc 就是装在这种容器里的，
/// 这里只实现读取所需的 FAT、miniFAT、目录与流，不写回。
/// </summary>
internal sealed class CompoundFile : IDisposable
{
    private const uint HeaderSignature = 0xE011CFD0; // D0 CF 11 E0 A1 B1 1A E1
    private const int HeaderSize = 512;

    private readonly Stream _stream;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniStreamCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly List<DirectoryEntry> _entries = new();
    private readonly Dictionary<string, int> _entryIndex = new(StringComparer.OrdinalIgnoreCase);
    private byte[] _miniStream = Array.Empty<byte>();

    private CompoundFile(Stream stream)
    {
        _stream = stream;
        var header = new byte[HeaderSize];
        stream.Position = 0;
        if (stream.Read(header, 0, HeaderSize) != HeaderSize)
        {
            throw new InvalidDataException("文件过短，不是复合文档");
        }

        if (BitConverter.ToUInt32(header, 0) != HeaderSignature)
        {
            throw new InvalidDataException("缺少 OLE2 复合文档签名");
        }

        _sectorSize = 1 << BitConverter.ToUInt16(header, 0x1E);
        _miniSectorSize = 1 << BitConverter.ToUInt16(header, 0x20);
        if (_sectorSize is not (512 or 4096) || _miniSectorSize is not (64 or 128))
        {
            throw new InvalidDataException("复合文档扇区参数异常");
        }

        _miniStreamCutoff = BitConverter.ToUInt32(header, 0x38);
        var fatSectorCount = BitConverter.ToUInt32(header, 0x2C);
        var firstDirectorySector = BitConverter.ToUInt32(header, 0x30);
        var firstMiniFatSector = BitConverter.ToUInt32(header, 0x3C);
        var miniFatSectorCount = BitConverter.ToUInt32(header, 0x40);
        var firstDifatSector = BitConverter.ToUInt32(header, 0x44);
        var difatSectorCount = BitConverter.ToUInt32(header, 0x48);

        var fatSectors = new List<uint>();
        for (var i = 0; i < 109; i++)
        {
            var sector = BitConverter.ToUInt32(header, 0x4C + i * 4);
            if (sector == 0xFFFFFFFF)
            {
                break;
            }

            fatSectors.Add(sector);
        }

        var perDifat = _sectorSize / 4 - 1;
        var difat = firstDifatSector;
        var guard = 0;
        while (difat != 0xFFFFFFFF && difat != 0xFFFFFFFE && guard++ < difatSectorCount + 8)
        {
            var buffer = ReadSector((long)difat);
            for (var i = 0; i < perDifat; i++)
            {
                var sector = BitConverter.ToUInt32(buffer, i * 4);
                if (sector == 0xFFFFFFFF)
                {
                    continue;
                }

                fatSectors.Add(sector);
            }

            difat = BitConverter.ToUInt32(buffer, perDifat * 4);
        }

        if (fatSectors.Count > fatSectorCount)
        {
            fatSectors = fatSectors.Take((int)fatSectorCount).ToList();
        }

        // FAT 表本身
        var fat = new List<uint>(fatSectors.Count * (_sectorSize / 4));
        foreach (var sector in fatSectors)
        {
            var buffer = ReadSector((long)sector);
            for (var offset = 0; offset + 4 <= buffer.Length; offset += 4)
            {
                fat.Add(BitConverter.ToUInt32(buffer, offset));
            }
        }

        _fat = fat.ToArray();

        // 目录
        var dirSectors = FollowChain((long)firstDirectorySector, _fat, null).ToList();
        var perSector = _sectorSize / 128;
        foreach (var dirSector in dirSectors)
        {
            var buffer = ReadSector(dirSector);
            for (var i = 0; i < perSector; i++)
            {
                var entry = DirectoryEntry.Parse(buffer, i * 128);
                if (entry.Name.Length > 0 && entry.Type != 0)
                {
                    _entryIndex[entry.Name] = _entries.Count;
                }

                _entries.Add(entry);
            }
        }

        // miniFAT 与 mini stream
        if (miniFatSectorCount > 0 && firstMiniFatSector != 0xFFFFFFFE)
        {
            var miniFatBytes = new List<byte>();
            var sector = (long)firstMiniFatSector;
            var visited = 0;
            while (sector >= 0 && sector < _fat.Length && visited++ < _fat.Length)
            {
                miniFatBytes.AddRange(ReadSector(sector));
                var next = _fat[sector];
                if (next is 0xFFFFFFFE or 0xFFFFFFFF)
                {
                    break;
                }

                sector = next;
            }

            var miniFatArray = new uint[miniFatBytes.Count / 4];
            for (var i = 0; i < miniFatArray.Length; i++)
            {
                miniFatArray[i] = BitConverter.ToUInt32(miniFatBytes.ToArray(), i * 4);
            }

            _miniFat = miniFatArray;
        }
        else
        {
            _miniFat = Array.Empty<uint>();
        }

        var root = _entries.FirstOrDefault(e => e.Type == 5);
        if (root is not null && root.StartSector >= 0 && root.StreamSize > 0)
        {
            _miniStream = ReadFromFat((long)root.StartSector, (long)root.StreamSize);
        }
    }

    public static CompoundFile Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return new CompoundFile(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public IEnumerable<string> StreamNames => _entries.Where(e => e.Type == 2).Select(e => e.Name);

    public byte[]? GetStream(string name)
    {
        if (!_entryIndex.TryGetValue(name, out var index))
        {
            return null;
        }

        var entry = _entries[index];
        if (entry.Type != 2 || entry.StreamSize <= 0)
        {
            return Array.Empty<byte>();
        }

        if (entry.StreamSize < _miniStreamCutoff)
        {
            return ReadFromMiniStream(entry);
        }

        return ReadFromFat((long)entry.StartSector, (long)entry.StreamSize);
    }

    private byte[] ReadFromMiniStream(DirectoryEntry entry)
    {
        var result = new List<byte>((int)Math.Min(entry.StreamSize, 1 << 24));
        var sector = entry.StartSector;
        var visited = new HashSet<uint>();
        while (sector >= 0 && sector != 0xFFFFFFFF && visited.Add(sector) && visited.Count < 1 << 20)
        {
            var offset = (long)sector * _miniSectorSize;
            if (offset >= _miniStream.Length)
            {
                break;
            }

            var count = Math.Min(_miniSectorSize, _miniStream.Length - (int)offset);
            result.AddRange(_miniStream.AsSpan((int)offset, count).ToArray());
            if (sector >= _miniFat.Length)
            {
                break;
            }

            sector = _miniFat[sector];
        }

        return TrimTo(result, (long)entry.StreamSize);
    }

    private byte[] ReadFromFat(long startSector, long size)
    {
        var result = new List<byte>((int)Math.Min(size, 1 << 26));
        var chain = FollowChain(startSector, _fat, null);
        foreach (var sector in chain)
        {
            result.AddRange(ReadSector(sector));
            if (result.Count >= size)
            {
                break;
            }
        }

        return TrimTo(result, size);
    }

    private static byte[] TrimTo(List<byte> data, long size)
    {
        if (size <= 0)
        {
            return Array.Empty<byte>();
        }

        if (data.Count <= size)
        {
            return data.ToArray();
        }

        return data.GetRange(0, (int)size).ToArray();
    }

    private IEnumerable<long> FollowChain(long start, uint[] table, HashSet<long>? seen)
    {
        seen ??= new HashSet<long>();
        var sector = start;
        var guard = 0;
        while (sector >= 0 && sector != 0xFFFFFFFE && sector != 0xFFFFFFFF && sector < table.Length && guard++ < table.Length + 8)
        {
            if (!seen.Add(sector))
            {
                yield break;
            }

            yield return sector;
            sector = table[sector];
        }
    }

    private byte[] ReadSector(long sector)
    {
        var offset = (sector + 1) * _sectorSize;
        if (offset < 0 || offset >= _stream.Length)
        {
            return new byte[_sectorSize];
        }

        var buffer = new byte[_sectorSize];
        _stream.Position = offset;
        var read = 0;
        while (read < _sectorSize)
        {
            var chunk = _stream.Read(buffer, read, _sectorSize - read);
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        return buffer;
    }

    public void Dispose() => _stream.Dispose();

    private sealed class DirectoryEntry
    {
        public string Name { get; private init; } = string.Empty;
        public byte Type { get; private init; }
        public uint StartSector { get; private init; }
        public ulong StreamSize { get; private init; }

        public static DirectoryEntry Parse(byte[] buffer, int offset)
        {
            var nameLength = BitConverter.ToUInt16(buffer, offset + 0x40);
            var name = string.Empty;
            if (nameLength is > 2 and <= 64)
            {
                name = Encoding.Unicode.GetString(buffer, offset, nameLength - 2);
            }

            return new DirectoryEntry
            {
                Name = name,
                Type = buffer[offset + 0x42],
                StartSector = BitConverter.ToUInt32(buffer, offset + 0x74),
                StreamSize = BitConverter.ToUInt64(buffer, offset + 0x78),
            };
        }
    }
}