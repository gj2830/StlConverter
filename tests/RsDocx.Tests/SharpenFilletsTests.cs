using RsDocx.Core.Stl;

namespace RsDocx.Tests;

public class SharpenFilletsTests
{
    /// <summary>
    /// A box (-h..h) whose edges are rounded with radius r and whose corners are sphere octants, tessellated as CAD does:
    /// a grid on the sides with n rows across every rounding, each point taken onto the rounded surface (the inner box plus
    /// r in the direction away from it). The flat parts lie exactly on x, y, z = ±h.
    /// </summary>
    internal static List<StlTriangle> RoundedBox(double h, double r, int n)
    {
        var c = new List<double>();
        for (int i = 0; i <= n; i++) c.Add(-h + r * i / n);
        for (int i = 0; i <= n; i++) c.Add(h - r + r * i / n);
        Vec3 Round(Vec3 p)
        {
            double m = h - r;
            var inner = new Vec3(Math.Clamp(p.X, -m, m), Math.Clamp(p.Y, -m, m), Math.Clamp(p.Z, -m, m));
            return inner + (p - inner).Normalized() * r;
        }
        var tris = new List<StlTriangle>();
        for (int axis = 0; axis < 3; axis++)
            foreach (double s in new[] { -h, h })
            {
                Vec3 At(double u, double v) => axis switch { 0 => new(s, u, v), 1 => new(v, s, u), _ => new(u, v, s) };
                var outward = axis switch { 0 => new Vec3(s, 0, 0), 1 => new Vec3(0, s, 0), _ => new Vec3(0, 0, s) };
                for (int i = 0; i + 1 < c.Count; i++)
                    for (int j = 0; j + 1 < c.Count; j++)
                    {
                        if (c[i + 1] - c[i] <= 0 || c[j + 1] - c[j] <= 0) continue;
                        Vec3 a = At(c[i], c[j]), b = At(c[i + 1], c[j]), d = At(c[i + 1], c[j + 1]), e = At(c[i], c[j + 1]);
                        if ((b - a).Cross(d - a).Dot(outward) < 0) (b, e) = (e, b);
                        tris.Add(new(Round(a), Round(b), Round(d)));
                        tris.Add(new(Round(a), Round(d), Round(e)));
                    }
            }
        return tris;
    }

    private static StlFile File(List<StlTriangle> tris) => new() { Header = "", Binary = true, Triangles = tris };

    [Theory]
    [InlineData(0.2, 0.5)]
    [InlineData(0.45, 0.5)]
    [InlineData(1, 2)]
    [InlineData(1.8, 2)]
    public void Rounded_box_becomes_a_sharp_box(double radius, double limit)
    {
        var stl = File(RoundedBox(5, radius, 6));
        // the rounded box itself converts, but every rounding is a strip of facets
        Assert.True(MeshConverter.Convert(stl, new MeshOptions { MergeCoplanar = true }, out _).Single().Faces.Count > 1000);

        var sharp = MeshRepair.Repair(stl, new MeshRepairOptions { SharpenBelow = limit }, out var rs);
        Assert.True(rs.Sharpened > 0);
        Assert.Equal(0, rs.OpenEdges);
        var body = MeshConverter.Convert(sharp, new MeshOptions { MergeCoplanar = true }, out _).Single();
        Assert.Equal(6, body.Faces.Count);
        Assert.Equal(8, body.Vertices.Count);
        // the corners are where the sides meet: ±5 mm, scaled to metres
        Assert.All(body.Vertices, v => Assert.All(new[] { v.X, v.Y, v.Z }, x => Assert.Equal(0.005, Math.Abs(x), 9)));
    }

    [Theory]
    [InlineData(0.6, 0.5)]
    [InlineData(1, 0.5)]
    [InlineData(2.5, 2)]
    public void Roundings_wider_than_the_limit_stay(double radius, double limit)
    {
        var stl = File(RoundedBox(5, radius, 6));
        var kept = MeshRepair.Repair(stl, new MeshRepairOptions { SharpenBelow = limit }, out var rs);
        Assert.Equal(0, rs.Sharpened);
        Assert.Equal(stl.Triangles.Count, kept.Triangles.Count);
    }
}
