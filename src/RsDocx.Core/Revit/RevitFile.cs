using System.Text;
using System.Text.RegularExpressions;

namespace RsDocx.Core.Revit;

/// <summary>What a Revit file says about itself (its BasicFileInfo): the Revit release that saved it ("2024"), the build,
/// where it was last saved, and the whole text block as written ("Worksharing: …", "Format: …").</summary>
public sealed record RevitInfo(string? Format, string? Build, string? LastSavePath, IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// A Revit file (.rvt project, .rfa family, .rte/.rft template), read without Revit: a compound file (see
/// <see cref="CompoundFile"/>) with BasicFileInfo, a preview picture (RevitPreview4.0), the schema of what it stores
/// (Formats/Latest, see <see cref="RevitSchema"/>), the element table and the partitions with the model itself.
/// </summary>
public sealed class RevitFile
{
    public CompoundFile Container { get; }

    private RevitSchema? _schema;

    private RevitFile(CompoundFile container) => Container = container;

    public static RevitFile Open(string path) => new(CompoundFile.Open(path));

    public static RevitFile Open(byte[] data) => new(new CompoundFile(data));

    /// <summary>The file's own description (BasicFileInfo; the "Key: value" text block every release from 2016 writes,
    /// in UTF-16, after a binary part that differs per release).</summary>
    public RevitInfo Info()
    {
        if (!Container.Contains("BasicFileInfo")) return new RevitInfo(null, null, null, new Dictionary<string, string>());
        var raw = Container.Stream("BasicFileInfo");
        var fields = new Dictionary<string, string>();
        // (the text may start on an odd byte, and shifts a byte halfway in: read it both ways, and keep per key the value
        // that reads as text)
        foreach (int shift in new[] { 0, 1 })
        {
            string text = Encoding.Unicode.GetString(raw, shift, (raw.Length - shift) & ~1);
            // (a key starts a line or follows the binary part: no letter before it)
            foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z ])([A-Z][A-Za-z ]{1,40}): ?([^\r\n\u0000]*)"))
                if (Regex.IsMatch(m.Groups[2].Value, @"^[ -ɏ]*$"))
                    fields.TryAdd(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
        }
        string? Field(string key) => fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        return new RevitInfo(Field("Format"), Field("Build"), Field("Last Save Path"), fields);
    }

    /// <summary>The preview picture (a PNG inside RevitPreview4.0), or null.</summary>
    public byte[]? Preview()
    {
        if (!Container.Contains("RevitPreview4.0")) return null;
        var raw = Container.Stream("RevitPreview4.0");
        int at = raw.AsSpan().IndexOf(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        if (at < 0) return null;
        // (up to and with the IEND chunk: its 4 bytes of CRC after the name)
        int end = raw.AsSpan(at).IndexOf("IEND"u8);
        return end < 0 ? raw[at..] : raw[at..Math.Min(raw.Length, at + end + 8)];
    }

    /// <summary>A stream inflated (see <see cref="RevitStreams"/>), or null when it holds no gzip member.</summary>
    public byte[]? Inflated(string path) => Container.Contains(path) ? RevitStreams.InflateFirst(path, Container.Stream(path)) : null;

    /// <summary>The Revit release that saved the file (2024), or 0 when it does not say.</summary>
    public int Version => int.TryParse(Info().Format, out int v) ? v : 0;

    /// <summary>
    /// The records of a channel of the partitions (101, 102: the elements' data, 103: their saved graphics), per element
    /// its id, class and body; a later partition's record over an earlier one's.
    /// </summary>
    public Dictionary<ulong, (string Class, ReadOnlyMemory<byte> Body)> Records(ulong channel)
    {
        var schema = Schema();
        int version = Version;
        var result = new Dictionary<ulong, (string, ReadOnlyMemory<byte>)>();
        foreach (var name in Container.Streams.Where(s => s.StartsWith("Partitions/", StringComparison.Ordinal))
                     .OrderBy(s => int.TryParse(s["Partitions/".Length..], out int n) ? n : int.MaxValue))
            foreach (var (group, data) in RevitPartitions.Groups(RevitStreams.StripPages(Container.Stream(name)), schema))
            {
                if (group.Channel != channel) continue;
                foreach (var r in RevitPartitions.Records(group, data, version))
                    if (r.Body.Length >= 2) result[r.Id] = (schema.ByTag(BitConverter.ToUInt16(r.Body.Span))?.Name ?? "?", r.Body);
            }
        return result;
    }

    /// <summary>The schema of what the file stores (Formats/Latest).</summary>
    public RevitSchema Schema() => _schema ??= RevitSchema.Parse(Inflated("Formats/Latest") ?? throw new CompoundFileException("This file has no readable schema (Formats/Latest)."));
}
