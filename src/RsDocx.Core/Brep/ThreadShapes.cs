using RsDocx.Core.Stl;

namespace RsDocx.Core.Brep;

/// <summary>
/// Screw-thread geometry as SpaceClaim/DesignSpark writes it (testdata/helix.rsdocx, a thread profile swept 14 turns):
/// helices as rational quadratic B-splines of arcs of at most 60° (the projection on the axis plane is an exact circle,
/// the climb is interpolated; tolerance 1e-5 in its HELIX form), flanks as ruled B-surfaces between two such helices,
/// crest and root on cylinders.
/// </summary>
public static class ThreadShapes
{
    /// <summary>
    /// A helix around the axis through <paramref name="c"/> along <paramref name="a"/>: radius <paramref name="r"/>, at
    /// angle φ (counter-clockwise around a from <paramref name="x"/>) at height z0 + hand·pitch·φ/2π along a.
    /// </summary>
    public static BrepBSpline Helix(Vec3 c, Vec3 a, Vec3 x, double r, double z0, double pitch, bool right, double turns)
    {
        a = a.Normalized(); x = (x - a * x.Dot(a)).Normalized();
        var y = a.Cross(x);
        double total = 2 * Math.PI * turns, s = right ? 1 : -1;
        int n = Math.Max(1, (int)Math.Ceiling(turns * 6 - 1e-9));
        double step = total / n, w = Math.Cos(step / 2);
        Vec3 At(double phi, double radius, double z) => c + (x * Math.Cos(phi) + y * Math.Sin(phi)) * radius + a * z;
        double Z(double phi) => z0 + s * pitch * phi / (2 * Math.PI);
        var pts = new List<Vec3>(2 * n + 1); var wts = new List<double>(2 * n + 1);
        for (int k = 0; k <= n; k++)
        {
            pts.Add(At(k * step, r, Z(k * step))); wts.Add(1);
            if (k == n) break;
            // the arc's middle control point: on the bisector at r / cos(half the arc), half-way up
            pts.Add(At((k + 0.5) * step, r / w, (Z(k * step) + Z((k + 1) * step)) / 2)); wts.Add(w);
        }
        var knots = Enumerable.Range(0, n + 1).Select(k => (double)k / n).ToList();
        var mults = Enumerable.Range(0, n + 1).Select(k => k == 0 || k == n ? 3 : 2).ToList();
        var form = new HelixForm(c, a, pts[0], right, 0, turns, pitch, 1e-5);
        return new BrepBSpline(2, pts, knots, mults, false, wts, form);
    }

    /// <summary>The ruled surface between two helices with the same knots and weights (u = 0 on the first, 1 on the second).</summary>
    public static BrepNurbsSurface Ruled(BrepBSpline h0, BrepBSpline h1)
    {
        if (h0.ControlPoints.Count != h1.ControlPoints.Count || h0.Weights == null || h1.Weights == null)
            throw new ArgumentException("both rows need the same number of weighted control points");
        int nv = h0.ControlPoints.Count;
        var pts = h0.ControlPoints.Concat(h1.ControlPoints).ToList();
        var wts = h0.Weights.Concat(h1.Weights).ToList();
        var f = h0.Helix!;
        var form = f with { Point = default };
        return new BrepNurbsSurface(1, 2, 2, nv, pts, wts, [0, 1], [2, 2], h0.Knots, h0.Mults, form);
    }

    /// <summary>
    /// A thread profile (a trapezoid: <paramref name="baseWidth"/> along the axis at radius <paramref name="rIn"/>,
    /// <paramref name="crestWidth"/> at <paramref name="rOut"/>, symmetric) swept along a helix for
    /// <paramref name="turns"/> turns, as DesignSpark builds it: 6 faces (two flanks, crest and base on cylinders, two end
    /// faces through the axis), 12 edges, 8 vertices.
    /// </summary>
    public static BrepBody Coil(Vec3 c, Vec3 a, Vec3 x, double rIn, double rOut, double baseWidth, double crestWidth, double pitch, bool right, double turns)
    {
        a = a.Normalized(); x = (x - a * x.Dot(a)).Normalized();
        var y = a.Cross(x);
        double d1 = (baseWidth - crestWidth) / 2, total = 2 * Math.PI * turns, s = right ? 1 : -1;
        Vec3 P(double phi, double r, double z) => c + (x * Math.Cos(phi) + y * Math.Sin(phi)) * r + a * (z + s * pitch * phi / (2 * Math.PI));
        // profile corners: 0 base low, 1 base high, 2 crest high, 3 crest low
        (double r, double z)[] prof = [(rIn, 0), (rIn, baseWidth), (rOut, d1 + crestWidth), (rOut, d1)];
        var verts = prof.Select(q => P(0, q.r, q.z)).Concat(prof.Select(q => P(total, q.r, q.z))).ToList();
        var hInLow = Helix(c, a, x, rIn, 0, pitch, right, turns);
        var hInHigh = Helix(c, a, x, rIn, baseWidth, pitch, right, turns);
        var hOutHigh = Helix(c, a, x, rOut, d1 + crestWidth, pitch, right, turns);
        var hOutLow = Helix(c, a, x, rOut, d1, pitch, right, turns);
        var edges = new List<BrepEdge>
        {
            new(0, 4, hInLow), new(1, 5, hInHigh), new(2, 6, hOutHigh), new(3, 7, hOutLow),            // 0..3 helices
            new(0, 1, new BrepLine()), new(1, 2, new BrepLine()), new(2, 3, new BrepLine()), new(3, 0, new BrepLine()), // 4..7 start
            new(4, 5, new BrepLine()), new(5, 6, new BrepLine()), new(6, 7, new BrepLine()), new(7, 4, new BrepLine()), // 8..11 end
        };
        BrepCoedge F(int e) => new(e, true);
        BrepCoedge B(int e) => new(e, false);
        // outward normals: crest +r, base -r, lower flank down, upper flank up, start cap -φ, end cap +φ (see NOTES.md)
        var endNormal = x * -Math.Sin(total) + y * Math.Cos(total);
        var endRadial = x * Math.Cos(total) + y * Math.Sin(total);
        var faces = new List<BrepFace>
        {
            new(new BrepCylinder(c, a, x, rOut), false, [[F(3), B(10), B(2), F(6)]]),
            new(new BrepCylinder(c, a, x, rIn), true, [[F(4), F(1), B(8), B(0)]]),
            new(Ruled(hInLow, hOutLow), true, [[F(0), B(11), B(3), F(7)]]),
            new(Ruled(hInHigh, hOutHigh), false, [[F(5), F(2), B(9), B(1)]]),
            new(new BrepPlane(c, y * -1, x), false, [[B(7), B(6), B(5), B(4)]]),
            new(new BrepPlane(c, endNormal, endRadial), false, [[F(8), F(9), F(10), F(11)]]),
        };
        return new BrepBody { Vertices = verts, Edges = edges, Faces = faces };
    }

    /// <summary>The coil of <see cref="Coil"/> as triangles (for display): the profile swept in steps of 5°, and its ends.</summary>
    public static List<(Vec3, Vec3, Vec3)> CoilTriangles(Vec3 c, Vec3 a, Vec3 x, double rIn, double rOut, double baseWidth, double crestWidth,
        double pitch, bool right, double turns)
    {
        a = a.Normalized(); x = (x - a * x.Dot(a)).Normalized();
        var y = a.Cross(x);
        double d1 = (baseWidth - crestWidth) / 2, total = 2 * Math.PI * turns, s = right ? 1 : -1;
        (double r, double z)[] prof = [(rIn, 0), (rIn, baseWidth), (rOut, d1 + crestWidth), (rOut, d1)];
        Vec3 P(double phi, (double r, double z) q) => c + (x * Math.Cos(phi) + y * Math.Sin(phi)) * q.r + a * (q.z + s * pitch * phi / (2 * Math.PI));
        int n = Math.Max(4, (int)Math.Ceiling(turns * 72));
        var res = new List<(Vec3, Vec3, Vec3)>();
        for (int i = 0; i < n; i++)
        {
            double f0 = total * i / n, f1 = total * (i + 1) / n;
            for (int k = 0; k < 4; k++)
            {
                var q0 = prof[k]; var q1 = prof[(k + 1) % 4];
                Vec3 a0 = P(f0, q0), b0 = P(f0, q1), a1 = P(f1, q0), b1 = P(f1, q1);
                res.Add((a0, a1, b1)); res.Add((a0, b1, b0));
            }
        }
        foreach (double f in new[] { 0.0, total })
        {
            res.Add((P(f, prof[0]), P(f, prof[1]), P(f, prof[2])));
            res.Add((P(f, prof[0]), P(f, prof[2]), P(f, prof[3])));
        }
        return res;
    }

    /// <summary>The ISO metric basic profile for a pitch: (depth, width at the root diameter, width at the crest).</summary>
    public static (double depth, double baseWidth, double crestWidth) MetricProfile(double pitch)
    {
        double H = Math.Sqrt(3) / 2 * pitch;
        return (5.0 / 8 * H, pitch - pitch / 4, pitch / 8);
    }
}
