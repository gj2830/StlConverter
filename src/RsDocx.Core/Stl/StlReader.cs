using System.Globalization;
using System.Text;

namespace RsDocx.Core.Stl;

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Length => Math.Sqrt(Dot(this));
    public Vec3 Normalized() { double l = Length; return l == 0 ? this : this * (1 / l); }
}

public sealed record StlTriangle(Vec3 A, Vec3 B, Vec3 C);

public sealed class StlFile
{
    public required string Header { get; init; }
    public required bool Binary { get; init; }
    /// <summary>"binary STL", "ASCII STL" or "3MF".</summary>
    public string Format { get; init; } = "STL";
    public required List<StlTriangle> Triangles { get; init; }
}

public sealed class StlFormatException(string message) : Exception(message);

public static class StlReader
{
    public static StlFile Read(string path) => Read(File.ReadAllBytes(path));

    public static StlFile Read(byte[] data)
    {
        if (data.Length > 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4)
            return ThreeMfReader.Read(data); // zip: 3MF
        if (data.Length >= 84)
        {
            uint n = BitConverter.ToUInt32(data, 80);
            if (84 + 50L * n == data.Length) return ReadBinary(data, n);
        }
        var text = Encoding.ASCII.GetString(data);
        if (text.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase) && text.Contains("facet", StringComparison.OrdinalIgnoreCase))
            return ReadAscii(text);
        throw new StlFormatException("Not a valid STL file: size does not match a binary STL and no ASCII 'solid ... facet' found");
    }

    private static StlFile ReadBinary(byte[] d, uint n)
    {
        string header = Encoding.ASCII.GetString(d, 0, 80).TrimEnd('\0', ' ');
        var tris = new List<StlTriangle>((int)n);
        int o = 84;
        Vec3 V(int at) => new(BitConverter.ToSingle(d, at), BitConverter.ToSingle(d, at + 4), BitConverter.ToSingle(d, at + 8));
        for (int i = 0; i < n; i++, o += 50)
            tris.Add(new StlTriangle(V(o + 12), V(o + 24), V(o + 36))); // the stored normal (o+0) is ignored
        return new StlFile { Header = header, Binary = true, Triangles = tris, Format = "binary STL" };
    }

    private static StlFile ReadAscii(string text)
    {
        var tris = new List<StlTriangle>();
        var verts = new List<Vec3>(3);
        string header = "";
        int lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "solid": if (lineNo == 1) header = line.Length > 5 ? line[5..].Trim() : ""; break;
                case "vertex":
                    if (parts.Length < 4) throw new StlFormatException($"line {lineNo}: vertex needs 3 coordinates");
                    verts.Add(new Vec3(P(parts[1], lineNo), P(parts[2], lineNo), P(parts[3], lineNo)));
                    break;
                case "endloop":
                    if (verts.Count != 3) throw new StlFormatException($"line {lineNo}: facet with {verts.Count} vertices (expected 3)");
                    tris.Add(new StlTriangle(verts[0], verts[1], verts[2]));
                    verts.Clear();
                    break;
            }
        }
        return new StlFile { Header = header, Binary = false, Triangles = tris, Format = "ASCII STL" };
    }

    private static double P(string s, int line) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
            : throw new StlFormatException($"line {line}: invalid number '{s}'");
}
