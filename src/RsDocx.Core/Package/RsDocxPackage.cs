using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace RsDocx.Core.Package;

public sealed record PackageEntry(string Path, long Length, long CompressedLength, bool Stored);

public sealed record PackageRelationship(string Source, string Id, string Type, string Target);

/// <summary>An .rsdocx file (an OPC zip package), kept in memory as an ordered list of parts.</summary>
public sealed class RsDocxPackage
{
    private sealed class Part
    {
        public required string Path;
        public required byte[] Data;
        public required bool Stored;
        public long CompressedLength;
    }

    private readonly List<Part> _parts;

    private RsDocxPackage(List<Part> parts) { _parts = parts; }

    public IReadOnlyList<PackageEntry> Entries =>
        _parts.Select(p => new PackageEntry(p.Path, p.Data.Length, p.CompressedLength, p.Stored)).ToList();

    public static RsDocxPackage Open(string path)
    {
        using var fs = File.OpenRead(path);
        return Open(fs);
    }

    public static RsDocxPackage Open(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var parts = new List<Part>();
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            // Heuristic: an entry whose compressed size equals its size was stored (all such entries in kubus.rsdocx are).
            parts.Add(new Part { Path = e.FullName, Data = ms.ToArray(), Stored = e.CompressedLength == e.Length && e.Length > 0, CompressedLength = e.CompressedLength });
        }
        return new RsDocxPackage(parts);
    }

    private Part? Find(string path)
    {
        path = path.TrimStart('/');
        return _parts.Find(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public bool Contains(string path) => Find(path) != null;

    public byte[] GetPart(string path) => Find(path)?.Data ?? throw new FileNotFoundException($"Part not found: {path}");

    public XDocument GetXml(string path)
    {
        using var ms = new MemoryStream(GetPart(path));
        return XDocument.Load(ms, LoadOptions.PreserveWhitespace);
    }

    /// <summary>Replaces an existing part (keeping its position and compression) or appends a new one.</summary>
    public void SetPart(string path, byte[] data, bool? stored = null)
    {
        var p = Find(path);
        if (p != null) { p.Data = data; if (stored.HasValue) p.Stored = stored.Value; }
        else _parts.Add(new Part { Path = path.TrimStart('/'), Data = data, Stored = stored ?? false });
    }

    /// <summary>Writes XML the way SpaceClaim does for most parts: UTF-8 with BOM, no reformatting.</summary>
    public void SetXml(string path, XDocument doc, bool bom = true)
    {
        using var ms = new MemoryStream();
        var settings = new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(bom), Indent = false };
        using (var w = System.Xml.XmlWriter.Create(ms, settings)) doc.Save(w);
        SetPart(path, ms.ToArray());
    }

    public bool RemovePart(string path)
    {
        var p = Find(path);
        return p != null && _parts.Remove(p);
    }

    /// <summary>Removes relationships whose target is the given part from all .rels parts.</summary>
    public void RemoveRelationshipsTo(string target)
    {
        target = "/" + target.TrimStart('/');
        foreach (var rels in _parts.Where(p => p.Path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var doc = GetXml(rels.Path);
            var hits = doc.Root!.Elements().Where(e => string.Equals((string?)e.Attribute("Target"), target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) continue;
            hits.ForEach(h => h.Remove());
            SetXml(rels.Path, doc);
        }
    }

    public void Save(string path)
    {
        using var fs = File.Create(path);
        Save(fs);
    }

    public void Save(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var p in _parts)
        {
            var e = zip.CreateEntry(p.Path, p.Stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var s = e.Open();
            s.Write(p.Data);
        }
    }

    /// <summary>All relationships from all .rels parts. Source is the part the .rels file belongs to ("" for the package).</summary>
    public IEnumerable<PackageRelationship> Relationships()
    {
        foreach (var path in _parts.Select(p => p.Path).Where(p => p.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)).Order())
        {
            // "SpaceClaim/_rels/document.xml.rels" -> "SpaceClaim/document.xml"
            string dir = Path.GetDirectoryName(Path.GetDirectoryName(path))?.Replace('\\', '/') ?? "";
            string name = Path.GetFileName(path)[..^".rels".Length];
            string source = name.Length == 0 ? "" : (dir.Length > 0 ? dir + "/" : "") + name;
            foreach (var r in GetXml(path).Root!.Elements())
                yield return new PackageRelationship(source, (string)r.Attribute("Id")!, (string)r.Attribute("Type")!, (string)r.Attribute("Target")!);
        }
    }
}
