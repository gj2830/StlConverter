using System.Buffers.Binary;
using System.Text;

namespace RsDocx.Core.Revit;

/// <summary>Why a compound file cannot be read (shown to the user as it is).</summary>
public sealed class CompoundFileException(string message) : Exception(message);

/// <summary>
/// A Microsoft Compound File (MS-CFB, the container of old Office files and of Revit's .rvt/.rfa): a little file system
/// of storages and streams in one file. Read only, all in memory. Sector size 512 (version 3) or 4096 (version 4), the
/// sector table through the header's DIFAT and its chain, small streams in the mini stream.
/// </summary>
public sealed class CompoundFile
{
    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private const uint EndOfChain = 0xFFFFFFFE, MaxSector = 0xFFFFFFFA;

    private readonly byte[] _data;
    private readonly int _sector, _mini;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat, _miniFat;
    private readonly byte[] _miniStream;
    private readonly Dictionary<string, (uint Start, long Size)> _streams = new(StringComparer.Ordinal);

    /// <summary>Every stream by its path ("Formats/Latest": storages and the stream, joined by '/').</summary>
    public IReadOnlyCollection<string> Streams => _streams.Keys;

    public static CompoundFile Open(string path) => new(File.ReadAllBytes(path));

    public CompoundFile(byte[] data)
    {
        _data = data;
        if (data.Length < 512 || !data.AsSpan(0, 8).SequenceEqual(Signature))
            throw new CompoundFileException("Not a compound file (no D0 CF 11 E0 signature).");
        int sectorShift = U16(0x1E), miniShift = U16(0x20);
        if (sectorShift is not (9 or 12) || miniShift != 6) throw new CompoundFileException($"Unexpected sector sizes (2^{sectorShift}, 2^{miniShift}).");
        _sector = 1 << sectorShift; _mini = 1 << miniShift;
        uint fatSectors = U32(0x2C), firstDir = U32(0x30), firstMiniFat = U32(0x3C), firstDifat = U32(0x44), difatSectors = U32(0x48);
        _miniCutoff = U32(0x38);

        // the sector table: its sectors listed in the header (109) and in the DIFAT chain
        var fatList = new List<uint>();
        for (int i = 0; i < 109; i++) fatList.Add(U32(0x4C + i * 4));
        uint d = firstDifat;
        for (uint n = 0; n < difatSectors && d < MaxSector; n++)
        {
            long off = Offset(d);
            int per = _sector / 4 - 1;
            for (int i = 0; i < per; i++) fatList.Add(U32(off + i * 4));
            d = U32(off + per * 4);
        }
        _fat = new uint[fatSectors * (_sector / 4)];
        for (int s = 0; s < fatSectors; s++)
        {
            if (fatList[s] >= MaxSector) throw new CompoundFileException("Broken sector table.");
            long off = Offset(fatList[s]);
            for (int i = 0; i < _sector / 4; i++) _fat[s * (_sector / 4) + i] = U32(off + i * 4);
        }
        _miniFat = ToUInts(Chain(firstMiniFat));

        // the directory: 128-byte entries, a red-black tree per storage (left, right, child)
        var dir = Chain(firstDir);
        int count = dir.Length / 128;
        var names = new string[count]; var types = new byte[count]; var left = new uint[count]; var right = new uint[count];
        var child = new uint[count]; var start = new uint[count]; var size = new long[count];
        for (int i = 0; i < count; i++)
        {
            var e = dir.AsSpan(i * 128, 128);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(e[64..]);
            names[i] = nameLen >= 2 ? Encoding.Unicode.GetString(e[..Math.Min(nameLen - 2, 62)]) : "";
            types[i] = e[66];
            left[i] = BinaryPrimitives.ReadUInt32LittleEndian(e[68..]);
            right[i] = BinaryPrimitives.ReadUInt32LittleEndian(e[72..]);
            child[i] = BinaryPrimitives.ReadUInt32LittleEndian(e[76..]);
            start[i] = BinaryPrimitives.ReadUInt32LittleEndian(e[116..]);
            // (version 3 files use only the low 32 bits of the size)
            size[i] = sectorShift == 9 ? BinaryPrimitives.ReadUInt32LittleEndian(e[120..]) : BinaryPrimitives.ReadInt64LittleEndian(e[120..]);
        }
        if (count == 0 || types[0] != 5) throw new CompoundFileException("No root entry.");
        _miniStream = Read(start[0], size[0]);
        var seen = new HashSet<uint>();
        void Walk(uint i, string prefix)
        {
            if (i >= count || !seen.Add(i)) return;
            Walk(left[i], prefix); Walk(right[i], prefix);
            string p = prefix + names[i];
            if (types[i] == 2) _streams[p] = (start[i], size[i]);
            else if (types[i] == 1) Walk(child[i], p + "/");
        }
        Walk(child[0], "");
    }

    public bool Contains(string path) => _streams.ContainsKey(path);

    /// <summary>A stream's bytes.</summary>
    public byte[] Stream(string path)
    {
        if (!_streams.TryGetValue(path, out var s)) throw new CompoundFileException($"No stream {path}.");
        if (s.Size < _miniCutoff)
        {
            var result = new byte[s.Size];
            int at = 0;
            for (uint m = s.Start; m < MaxSector && at < result.Length; m = m < _miniFat.Length ? _miniFat[m] : EndOfChain)
            {
                int n = (int)Math.Min(_mini, result.Length - at);
                Array.Copy(_miniStream, m * _mini, result, at, n);
                at += n;
            }
            return result;
        }
        return Read(s.Start, s.Size);
    }

    /// <summary>A stream's size in bytes.</summary>
    public long Size(string path) => _streams.TryGetValue(path, out var s) ? s.Size : throw new CompoundFileException($"No stream {path}.");

    private long Offset(uint sector) => (sector + 1L) * _sector;
    private int U16(long at) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan((int)at));
    private uint U32(long at) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)at));

    private static uint[] ToUInts(byte[] b)
    {
        var r = new uint[b.Length / 4];
        for (int i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i * 4));
        return r;
    }

    /// <summary>A whole chain of sectors.</summary>
    private byte[] Chain(uint first)
    {
        var parts = new List<uint>();
        for (uint s = first; s < MaxSector; s = _fat[s])
        {
            if (s >= _fat.Length || parts.Count > _fat.Length) throw new CompoundFileException("Broken sector chain.");
            parts.Add(s);
        }
        var r = new byte[parts.Count * (long)_sector];
        for (int i = 0; i < parts.Count; i++) Array.Copy(_data, Offset(parts[i]), r, (long)i * _sector, Math.Min(_sector, _data.Length - Offset(parts[i])));
        return r;
    }

    /// <summary>The first <paramref name="size"/> bytes of a chain of sectors.</summary>
    private byte[] Read(uint first, long size)
    {
        var r = new byte[size];
        long at = 0;
        int guard = 0;
        for (uint s = first; s < MaxSector && at < size; s = _fat[s])
        {
            if (s >= _fat.Length || ++guard > _fat.Length) throw new CompoundFileException("Broken sector chain.");
            int n = (int)Math.Min(_sector, size - at);
            Array.Copy(_data, Offset(s), r, at, n);
            at += n;
        }
        if (at < size) throw new CompoundFileException("A stream ends before its size.");
        return r;
    }
}
