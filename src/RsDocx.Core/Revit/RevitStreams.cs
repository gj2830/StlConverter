using System.IO.Compression;

namespace RsDocx.Core.Revit;

/// <summary>
/// How Revit packs its streams (as rvt-rs documents it, see THIRD-PARTY-NOTICES.md): a gzip header (magic 1F 8B 08)
/// followed by raw DEFLATE, without gzip's closing checksum and length; a stream may hold several such members. The
/// database streams (Partitions/*, Global/*, Formats/Latest) are stored in pages of 65 249 bytes whose last 353 bytes are
/// a checksum, cut off before inflating (a last, shorter page has none).
/// </summary>
public static class RevitStreams
{
    public const int StoredPage = 65_249, PagePayload = 64_896;

    /// <summary>Whether a stream is stored in checksum pages.</summary>
    public static bool IsPaged(string path) =>
        path.StartsWith("Partitions/", StringComparison.Ordinal) && !path["Partitions/".Length..].Contains('/')
        || path is "Formats/Latest" or "Global/ContentDocuments" or "Global/DocumentIncrementTable" or "Global/ElemTable"
            or "Global/History" or "Global/Latest" or "Global/PartitionTable";

    /// <summary>A paged stream with the checksum of every full page cut off.</summary>
    public static byte[] StripPages(byte[] stored)
    {
        int full = stored.Length / StoredPage;
        if (full == 0) return stored;
        int rest = stored.Length - full * StoredPage;
        var r = new byte[full * PagePayload + rest];
        for (int p = 0; p < full; p++) Array.Copy(stored, p * StoredPage, r, p * PagePayload, PagePayload);
        Array.Copy(stored, full * StoredPage, r, full * PagePayload, rest);
        return r;
    }

    /// <summary>Where gzip members may start (every 1F 8B 08; in compressed data some are chance).</summary>
    public static IEnumerable<int> GzipStarts(byte[] data)
    {
        for (int i = 0; i + 3 <= data.Length; i++)
            if (data[i] == 0x1F && data[i + 1] == 0x8B && data[i + 2] == 0x08) yield return i;
    }

    /// <summary>The gzip member at <paramref name="at"/> inflated (its header as RFC 1952 says, then DEFLATE up to its
    /// last block); null when it is none.</summary>
    public static byte[]? Inflate(byte[] data, int at, long limit = 512L * 1024 * 1024)
    {
        if (at + 10 > data.Length || data[at] != 0x1F || data[at + 1] != 0x8B || data[at + 2] != 0x08) return null;
        int flags = data[at + 3];
        int p = at + 10;
        try
        {
            if ((flags & 0x04) != 0) p += 2 + BitConverter.ToUInt16(data, p);
            if ((flags & 0x08) != 0) { while (data[p] != 0) p++; p++; }
            if ((flags & 0x10) != 0) { while (data[p] != 0) p++; p++; }
            if ((flags & 0x02) != 0) p += 2;
            using var src = new MemoryStream(data, p, data.Length - p, writable: false);
            using var z = new DeflateStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = z.Read(buffer)) > 0)
            {
                dst.Write(buffer, 0, n);
                if (dst.Length > limit) return null;
            }
            return dst.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    /// <summary>A stream's first gzip member inflated, checksum pages cut off first when it has them.</summary>
    public static byte[]? InflateFirst(string path, byte[] stored)
    {
        var data = IsPaged(path) ? StripPages(stored) : stored;
        foreach (int at in GzipStarts(data))
            if (Inflate(data, at) is { } r) return r;
        return null;
    }
}
