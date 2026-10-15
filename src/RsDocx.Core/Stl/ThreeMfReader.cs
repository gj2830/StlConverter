using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace RsDocx.Core.Stl;

/// <summary>
/// Reads the triangle meshes of a 3MF file (zip with XML model parts) as an <see cref="StlFile"/>, in millimetres.
/// Supports meshes, components (also across model parts via the production extension's p:path) and build-item transforms.
/// </summary>
public static class ThreeMfReader
{
    private sealed class Obj
    {
        public List<Vec3>? Vertices;
        public List<int[]>? Triangles;
        public readonly List<(string part, string id, double[] m)> Components = new();
    }

    private static readonly double[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];

    public static StlFile Read(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var objects = new Dictionary<(string, string), Obj>();
        var build = new List<(string part, string id, double[] m)>();
        string unit = "millimeter";
        const string mainPart = "3D/3dmodel.model";

        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase)))
        {
            string part = entry.FullName;
            using var s = entry.Open();
            using var r = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore });
            Obj? cur = null;
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                switch (r.LocalName)
                {
                    case "model":
                        if (part.Equals(mainPart, StringComparison.OrdinalIgnoreCase)) unit = r.GetAttribute("unit") ?? unit;
                        break;
                    case "object":
                        cur = new Obj();
                        objects[(part, r.GetAttribute("id")!)] = cur;
                        break;
                    case "vertex":
                        (cur!.Vertices ??= new()).Add(new Vec3(D(r, "x"), D(r, "y"), D(r, "z")));
                        break;
                    case "triangle":
                        (cur!.Triangles ??= new()).Add([I(r, "v1"), I(r, "v2"), I(r, "v3")]);
                        break;
                    case "component":
                        cur!.Components.Add((Path(r) ?? part, r.GetAttribute("objectid")!, Matrix(r.GetAttribute("transform"))));
                        break;
                    case "item":
                        build.Add((Path(r) ?? part, r.GetAttribute("objectid")!, Matrix(r.GetAttribute("transform"))));
                        break;
                }
            }
        }

        double scale = unit switch
        {
            "micron" => 0.001, "millimeter" => 1, "centimeter" => 10, "inch" => 25.4, "foot" => 304.8, "meter" => 1000,
            _ => throw new StlFormatException($"3MF: unknown unit '{unit}'"),
        };
        var tris = new List<StlTriangle>();
        void Emit((string part, string id) key, double[] m, int depth)
        {
            if (depth > 32) throw new StlFormatException("3MF: component nesting too deep");
            if (!objects.TryGetValue(key, out var o)) throw new StlFormatException($"3MF: object {key.id} in {key.part} not found");
            if (o.Vertices != null && o.Triangles != null)
                foreach (var t in o.Triangles)
                    tris.Add(new StlTriangle(Apply(m, o.Vertices[t[0]]) * scale, Apply(m, o.Vertices[t[1]]) * scale, Apply(m, o.Vertices[t[2]]) * scale));
            foreach (var c in o.Components) Emit((c.part, c.id), Compose(c.m, m), depth + 1);
        }
        foreach (var item in build) Emit((item.part, item.id), item.m, 0);
        return new StlFile { Header = $"{build.Count} build items", Binary = false, Triangles = tris, Format = "3MF" };
    }

    private static string? Path(XmlReader r)
    {
        // production extension: p:path="/3D/Objects/object_1.model"
        for (int i = 0; i < r.AttributeCount; i++)
        {
            r.MoveToAttribute(i);
            if (r.LocalName == "path") { var v = r.Value.TrimStart('/'); r.MoveToElement(); return v; }
        }
        r.MoveToElement();
        return null;
    }

    private static double D(XmlReader r, string a) => double.Parse(r.GetAttribute(a)!, CultureInfo.InvariantCulture);
    private static int I(XmlReader r, string a) => int.Parse(r.GetAttribute(a)!, CultureInfo.InvariantCulture);

    /// <summary>3MF matrix "m00 m01 m02 m10 m11 m12 m20 m21 m22 m30 m31 m32" (row vectors: p' = p M).</summary>
    private static double[] Matrix(string? s) =>
        s is null ? Identity : s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

    private static Vec3 Apply(double[] m, Vec3 p) => new(
        p.X * m[0] + p.Y * m[3] + p.Z * m[6] + m[9],
        p.X * m[1] + p.Y * m[4] + p.Z * m[7] + m[10],
        p.X * m[2] + p.Y * m[5] + p.Z * m[8] + m[11]);

    /// <summary>First apply a, then b.</summary>
    private static double[] Compose(double[] a, double[] b)
    {
        var r = new double[12];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = i == 3 ? b[9 + j] : 0;
                for (int k = 0; k < 3; k++) s += (i == 3 ? a[9 + k] : a[i * 3 + k]) * b[k * 3 + j];
                r[i * 3 + j] = s;
            }
        return r;
    }
}
