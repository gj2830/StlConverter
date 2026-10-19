using System.Buffers.Binary;
using System.IO.Compression;

namespace RsDocx.Core.Revit;

/// <summary>A group of records in a partition: the channel it belongs to (101, 102: elements' data, 103: their saved
/// graphics), where its first marker is, and what its markers declare (objects, body bytes).</summary>
public sealed record RevitGroup(ulong Channel, int Offset, int Segments, ulong DeclaredObjects, ulong DeclaredBodyBytes, byte[]? ContentKey);

/// <summary>A record of a group: the element's id and its body (its first two bytes the tag of its class).</summary>
public readonly record struct RevitRecord(ulong Id, ReadOnlyMemory<byte> Body);

/// <summary>
/// The partitions (Partitions/NN) of a Revit file: after an 8-byte prefix, a run of markers (classes of the schema, by
/// tag): a SegmentMarker (continuation flags, object count, size, body bytes, channel) with a gzip member of that size,
/// groups continued over several segments (flag 1: continues the one before, flag 2: goes on in the next); a
/// SegmentCheckback repeating the last size; a ContentMarker starting a section (with a 16-byte key, or none; none with
/// count FFFFFFFF: the end); a SignatureMarker with bytes and a UTF-16 text. As rvt-rs reads them (native_segments, see
/// THIRD-PARTY-NOTICES.md); every count and size is checked, a misread stops.
/// </summary>
public static class RevitPartitions
{
    /// <summary>Every group of a partition, with its records' bytes (inflated, continuations joined).</summary>
    public static IEnumerable<(RevitGroup Group, byte[] Data)> Groups(byte[] prepared, RevitSchema schema)
    {
        ushort Tag(string name) => schema.ByName(name)?.Tag ?? throw new FormatException($"the schema has no {name}");
        ushort contentTag = Tag("ContentMarker"), keyTag = Tag("ContentKey"), segmentTag = Tag("SegmentMarker"), checkbackTag = Tag("SegmentCheckback");
        ushort? signatureTag = schema.ByName("SignatureMarker")?.Tag;
        int pos = 8;
        ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(Take(2)); return v; }
        uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || pos + n > prepared.Length) throw new FormatException($"partition ends inside a marker at {pos}");
            var s = prepared.AsSpan(pos, n); pos += n; return s;
        }
        byte[]? key = null;
        uint? lastSize = null;
        RevitGroup? active = null;
        MemoryStream? data = null;
        var results = new List<(RevitGroup, byte[])>();
        while (true)
        {
            int offset = pos;
            ushort next = U16();
            if (next == contentTag)
            {
                if (active != null) throw new FormatException($"a section starts inside a continued segment at {offset}");
                uint token = U32();
                uint count;
                if (token == 0) { key = null; count = U32(); }
                else
                {
                    if (token != uint.MaxValue || U16() != keyTag) throw new FormatException($"unknown content key at {offset}");
                    count = U32();
                    key = Take(16).ToArray();
                }
                if (token == 0 && count == uint.MaxValue) break;
            }
            else if (next == segmentTag)
            {
                uint flags = U32(), count = U32(), size = U32(), raw = U32();
                if (size == 0)
                {
                    if (flags != 0 || count != 0 || raw != 0 || active != null) throw new FormatException($"invalid empty segment at {offset}");
                    continue;
                }
                if (flags >> 2 == 0 || size < 18) throw new FormatException($"unsupported segment flags/size at {offset}");
                ulong channel = U64();
                var bytes = Inflate(Take((int)size - 8));
                lastSize = size;
                bool fromPrevious = (flags & 1) != 0, toNext = (flags & 2) != 0;
                if (fromPrevious != (active != null)) throw new FormatException($"segment continuation mismatch at {offset}");
                if (active != null)
                {
                    if (active.Channel != channel) throw new FormatException($"continuation in another channel at {offset}");
                    active = active with { Segments = active.Segments + 1, DeclaredObjects = active.DeclaredObjects + count, DeclaredBodyBytes = active.DeclaredBodyBytes + raw };
                    data!.Write(bytes);
                }
                else
                {
                    active = new RevitGroup(channel, offset, 1, count, raw, key);
                    data = new MemoryStream();
                    data.Write(bytes);
                }
                if (!toNext)
                {
                    results.Add((active, data!.ToArray()));
                    active = null; data = null;
                }
            }
            else if (next == checkbackTag)
            {
                if (lastSize != U32()) throw new FormatException($"segment checkback mismatch at {offset}");
                lastSize = null;
            }
            else if (signatureTag == next)
            {
                if (active != null) throw new FormatException($"signature inside a continued segment at {offset}");
                int count = (int)U32();
                if (count > 1_000_000) throw new FormatException("signature too long");
                Take(count);
                int units = (int)U32();
                if (units > 1_000_000) throw new FormatException("signature text too long");
                Take(units * 2);
            }
            else throw new FormatException($"unknown partition marker {next} at {offset}");
        }
        return results;
    }

    /// <summary>A segment's gzip member inflated (it must be exactly the segment).</summary>
    private static byte[] Inflate(ReadOnlySpan<byte> member)
    {
        using var src = new MemoryStream(member.ToArray());
        using var z = new GZipStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        z.CopyTo(dst);
        return dst.ToArray();
    }

    /// <summary>
    /// The records of a group: per record its id (4 bytes in Revit 2023, else 8), a header (channel 101: 4 more bytes,
    /// 102 and 103: 8, its last 4 the body's length), the body, and the length again. Counts and sizes must agree with
    /// what the group's markers declare.
    /// </summary>
    public static List<RevitRecord> Records(RevitGroup group, byte[] data, int revitVersion)
    {
        int idBytes = revitVersion == 2023 ? 4 : 8;
        int header = group.Channel switch
        {
            101 => idBytes + 4,
            102 or 103 => idBytes + 8,
            _ => throw new FormatException($"unknown record channel {group.Channel}"),
        };
        var records = new List<RevitRecord>();
        int pos = 0;
        ulong bodies = 0;
        while (pos < data.Length)
        {
            if (data.Length - pos < header + 4) throw new FormatException("record header cut off");
            ulong id = idBytes == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) : BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos));
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + header - 4));
            int start = pos + header, end = start + length;
            if (length < 0 || end + 4 > data.Length || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end)) != (uint)length)
                throw new FormatException("a record's two lengths disagree");
            records.Add(new RevitRecord(id, data.AsMemory(start, length)));
            bodies += (ulong)length;
            pos = end + 4;
        }
        if ((ulong)records.Count != group.DeclaredObjects || bodies != group.DeclaredBodyBytes)
            throw new FormatException("a group's records disagree with its markers");
        return records;
    }
}
