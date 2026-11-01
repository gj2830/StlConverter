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

    /// <summary>lego_Display: a 1 x 8 plate, 3.2 mm thick, hollow underneath (a pocket 1.6 mm deep) with a rim 1.6 mm
    /// wide round it. The rim and the pocket's walls meet their neighbours square: they are faces, not roundings, and
    /// stay under a limit of 2 mm.</summary>
    [Fact]
    public void A_narrow_rim_with_square_walls_stays()
    {
        var tris = new List<StlTriangle>();
        void Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 n)
        {
            if ((b - a).Cross(c - a).Dot(n) < 0) (b, d) = (d, b);
            tris.Add(new(a, b, c)); tris.Add(new(a, c, d));
        }
        const double X = 32, Y = 4, x = 30.4, y = 2.4, top = 0.8, ceiling = -0.8, bottom = -2.4;
        Vec3 up = new(0, 0, 1), down = new(0, 0, -1);
        Quad(new(-X, -Y, top), new(X, -Y, top), new(X, Y, top), new(-X, Y, top), up);
        Quad(new(-x, -y, ceiling), new(x, -y, ceiling), new(x, y, ceiling), new(-x, y, ceiling), down);
        foreach (var s in new[] { 1.0, -1.0 })
        {
            Quad(new(-X, s * Y, bottom), new(X, s * Y, bottom), new(X, s * Y, top), new(-X, s * Y, top), new(0, s, 0));
            Quad(new(s * X, -Y, bottom), new(s * X, Y, bottom), new(s * X, Y, top), new(s * X, -Y, top), new(s, 0, 0));
            Quad(new(-x, s * y, bottom), new(x, s * y, bottom), new(x, s * y, ceiling), new(-x, s * y, ceiling), new(0, -s, 0));
            Quad(new(s * x, -y, bottom), new(s * x, y, bottom), new(s * x, y, ceiling), new(s * x, -y, ceiling), new(-s, 0, 0));
            // the rim, a trapezium on each side
            Quad(new(-X, s * Y, bottom), new(X, s * Y, bottom), new(x, s * y, bottom), new(-x, s * y, bottom), down);
            Quad(new(s * X, -Y, bottom), new(s * X, Y, bottom), new(s * x, y, bottom), new(s * x, -y, bottom), down);
        }
        var stl = File(tris);
        Assert.Equal(11, MeshConverter.Convert(stl, new MeshOptions { MergeCoplanar = true }, out _).Single().Faces.Count);
        var kept = MeshRepair.Repair(stl, new MeshRepairOptions { SharpenBelow = 2 }, out var rs);
        Assert.Equal(0, rs.Sharpened);
        Assert.Equal(11, MeshConverter.Convert(kept, new MeshOptions { MergeCoplanar = true }, out _).Single().Faces.Count);
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
