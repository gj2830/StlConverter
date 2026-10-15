namespace RsDocx.Core.Xt;

/// <summary>Evaluation helpers for the analytic XT curves and surfaces we read and write (metres).</summary>
public static class XtGeometry
{
    public static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    public static double[] Add(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
    public static double[] Mul(double[] a, double s) => [a[0] * s, a[1] * s, a[2] * s];
    public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    public static double Len(double[] a) => Math.Sqrt(Dot(a, a));
    public static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    public static double[] Unit(double[] a) => Mul(a, 1 / Len(a));

    /// <summary>Distance from p to the surface node (PLANE, CYLINDER, CONE, SPHERE, TORUS; others -&gt; NaN).</summary>
    public static double SurfaceDistance(XtNode s, double[] p) => s.NodeType switch
    {
        XtNodeTypes.Plane => Math.Abs(Dot(Sub(p, s.Vec("pvec")), s.Vec("normal"))),
        XtNodeTypes.Cylinder => Math.Abs(Len(Cross(Sub(p, s.Vec("pvec")), s.Vec("axis"))) - s.Double("radius")),
        XtNodeTypes.Sphere => Math.Abs(Len(Sub(p, s.Vec("centre"))) - s.Double("radius")),
        XtNodeTypes.Torus => Math.Abs(Len(Sub(Sub(p, s.Vec("centre")), TubeCentre(s, p))) - s.Double("minor_radius")),
        XtNodeTypes.Cone => Math.Abs(ConeOffset(s, p, out _)),
        _ => double.NaN,
    };

    /// <summary>
    /// CONE (as SpaceClaim writes it, schaaltje.rsdocx): radius at pvec, growing along the axis by tan(half angle); the
    /// natural normal points away from the axis and back along it. Signed distance (positive outside) and that normal.
    /// </summary>
    private static double ConeOffset(XtNode s, double[] p, out double[] normal)
    {
        var a = s.Vec("axis"); var d = Sub(p, s.Vec("pvec"));
        double h = Dot(d, a);
        var radial = Sub(d, Mul(a, h));
        double rho = Len(radial), sin = s.Double("sin_half_angle"), cos = s.Double("cos_half_angle");
        var u = rho > 0 ? Mul(radial, 1 / rho) : Cross(a, s.Vec("x_axis"));
        normal = Sub(Mul(u, cos), Mul(a, sin));
        return (rho - (s.Double("radius") + h * sin / cos)) * cos;
    }

    private static double[] ConeNormal(XtNode s, double[] p) { ConeOffset(s, p, out var n); return n; }

    /// <summary>Point of the torus' centre circle nearest to p, relative to the torus centre.</summary>
    private static double[] TubeCentre(XtNode s, double[] p)
    {
        var d = Sub(p, s.Vec("centre")); var a = s.Vec("axis");
        var radial = Sub(d, Mul(a, Dot(d, a)));
        return Mul(Unit(radial), s.Double("major_radius"));
    }

    /// <summary>Outward surface normal at p (before applying surface/face sense).</summary>
    public static double[] SurfaceNormal(XtNode s, double[] p) => s.NodeType switch
    {
        XtNodeTypes.Plane => s.Vec("normal"),
        XtNodeTypes.Cylinder => Unit(Sub(Sub(p, s.Vec("pvec")), Mul(s.Vec("axis"), Dot(Sub(p, s.Vec("pvec")), s.Vec("axis"))))),
        XtNodeTypes.Sphere => Unit(Sub(p, s.Vec("centre"))),
        XtNodeTypes.Torus => Unit(Sub(Sub(p, s.Vec("centre")), TubeCentre(s, p))),
        XtNodeTypes.Cone => ConeNormal(s, p),
        _ => throw new NotSupportedException($"surface type {s.NodeType}"),
    };

    /// <summary>A B_CURVE node (with its NURBS_CURVE children) as a curve; rational ones from their homogeneous
    /// vertices (w·x, w·y, w·z, w).</summary>
    public static Brep.BrepBSpline ReadBCurve(XtNode c, IReadOnlyDictionary<int, XtNode> map)
    {
        var n = map[c.Ptr("nurbs")];
        var v = map[n.Ptr("bspline_vertices")].Array("vertices").Select(x => (double)x).ToArray();
        var (pts, w) = Vertices(v, (short)n["vertex_dim"], n.Byte("rational") != 0);
        var mult = map[n.Ptr("knot_mult")].Array("mult").Select(x => (int)(short)x).ToList();
        var knots = map[n.Ptr("knots")].Array("knots").Select(x => (double)x).ToList();
        return new Brep.BrepBSpline((short)n["degree"], pts, knots, mult, n.Byte("closed") != 0, w);
    }

    private static (List<Stl.Vec3> pts, List<double>? w) Vertices(double[] v, int dim, bool rational)
    {
        if (!rational && dim == 3) return (Enumerable.Range(0, v.Length / 3).Select(i => new Stl.Vec3(v[3 * i], v[3 * i + 1], v[3 * i + 2])).ToList(), null);
        if (!rational || dim != 4) throw new NotSupportedException($"B-spline vertices of dimension {dim}");
        var pts = new List<Stl.Vec3>(v.Length / 4); var w = new List<double>(v.Length / 4);
        for (int i = 0; i < v.Length / 4; i++)
        {
            double wi = v[4 * i + 3];
            pts.Add(new Stl.Vec3(v[4 * i] / wi, v[4 * i + 1] / wi, v[4 * i + 2] / wi)); w.Add(wi);
        }
        return (pts, w);
    }

    /// <summary>A B_SURFACE node (with its NURBS_SURF children) as a surface; rational, vertices row by row (v fastest).</summary>
    public static Brep.BrepNurbsSurface ReadBSurface(XtNode s, IReadOnlyDictionary<int, XtNode> map)
    {
        var n = map[s.Ptr("nurbs")];
        var v = map[n.Ptr("bspline_vertices")].Array("vertices").Select(x => (double)x).ToArray();
        var (pts, w) = Vertices(v, (short)n["vertex_dim"], n.Byte("rational") != 0);
        List<int> M(string f) => map[n.Ptr(f)].Array("mult").Select(x => (int)(short)x).ToList();
        List<double> K(string f) => map[n.Ptr(f)].Array("knots").Select(x => (double)x).ToList();
        return new Brep.BrepNurbsSurface((short)n["u_degree"], (short)n["v_degree"], (int)n["n_u_vertices"], (int)n["n_v_vertices"], pts,
            w ?? Enumerable.Repeat(1.0, pts.Count).ToList(), K("u_knots"), M("u_knot_mult"), K("v_knots"), M("v_knot_mult"));
    }

    /// <summary>Distance from p to the surface node, B_SURFACE included (needs the map); NaN for others.</summary>
    public static double SurfaceDistance(XtNode s, double[] p, IReadOnlyDictionary<int, XtNode> map) =>
        s.NodeType == XtNodeTypes.BSurface ? ReadBSurface(s, map).Distance(new Stl.Vec3(p[0], p[1], p[2])) : SurfaceDistance(s, p);

    /// <summary>Distance from p to the curve node (LINE, CIRCLE, ELLIPSE, B_CURVE with map; NaN for others).</summary>
    public static double CurveDistance(XtNode c, double[] p, IReadOnlyDictionary<int, XtNode>? map = null)
    {
        switch (c.NodeType)
        {
            case XtNodeTypes.BCurve when map != null:
            {
                var bs = ReadBCurve(c, map);
                var q = new Stl.Vec3(p[0], p[1], p[2]);
                return (bs.At(Brep.BSpline.ClosestParam(bs, q)) - q).Length;
            }
            case XtNodeTypes.Line:
                return Len(Cross(Sub(p, c.Vec("pvec")), c.Vec("direction")));
            case XtNodeTypes.Circle:
            {
                var d = Sub(p, c.Vec("centre"));
                double h = Dot(d, c.Vec("normal"));
                double rho = Len(Sub(d, Mul(c.Vec("normal"), h)));
                return Math.Sqrt(h * h + (rho - c.Double("radius")) * (rho - c.Double("radius")));
            }
            case XtNodeTypes.Ellipse:
            {
                // distance estimate: off-plane part plus radial deviation at the point's own angle
                var d = Sub(p, c.Vec("centre"));
                var n = c.Vec("normal"); var x = c.Vec("x_axis"); var y = Cross(n, x);
                double h = Dot(d, n), u = Dot(d, x), v = Dot(d, y);
                double a = c.Double("major_radius"), b = c.Double("minor_radius");
                double t = Math.Atan2(v * a, u * b);
                double du = u - a * Math.Cos(t), dv = v - b * Math.Sin(t);
                return Math.Sqrt(h * h + du * du + dv * dv);
            }
            default: return double.NaN;
        }
    }

    /// <summary>
    /// Points along an edge from <paramref name="from"/> to <paramref name="to"/> (both included), following the curve
    /// in its parameter direction when <paramref name="forward"/> is true, else against it. Lines give just the two ends.
    /// </summary>
    public static List<double[]> SampleEdge(XtNode c, double[] from, double[] to, bool forward, int n = 8, IReadOnlyDictionary<int, XtNode>? map = null)
    {
        if (c.NodeType == XtNodeTypes.Line) return [from, to];
        if (c.NodeType == XtNodeTypes.BCurve && map != null)
        {
            var bs = ReadBCurve(c, map);
            var f = new Stl.Vec3(from[0], from[1], from[2]); var t = new Stl.Vec3(to[0], to[1], to[2]);
            double b0, b1;
            // a ring runs over the whole curve, in the fin's direction; otherwise simply from 'from' to 'to'
            if (bs.Closed && (f - t).Length < 1e-12) (b0, b1) = forward ? (bs.Start, bs.End) : (bs.End, bs.Start);
            else { b0 = Brep.BSpline.ClosestParam(bs, f); b1 = Brep.BSpline.ClosestParam(bs, t); }
            int m = Math.Max(n, 4 * bs.ControlPoints.Count);
            var res = new List<double[]> { from };
            for (int i = 1; i < m; i++)
            {
                double tt = b0 + (b1 - b0) * i / m; // b0/b1 already swapped when walking against the curve
                var q = bs.At(tt);
                res.Add([q.X, q.Y, q.Z]);
            }
            res.Add(to);
            return res;
        }
        if (c.NodeType is not (XtNodeTypes.Circle or XtNodeTypes.Ellipse)) return [from, to];
        var centre = c.Vec("centre"); var nrm = c.Vec("normal"); var x = c.Vec("x_axis"); var y = Cross(nrm, x);
        double a, b;
        if (c.NodeType == XtNodeTypes.Circle) a = b = c.Double("radius");
        else { a = c.Double("major_radius"); b = c.Double("minor_radius"); }
        double Param(double[] p) { var d = Sub(p, centre); return Math.Atan2(Dot(d, y) / b, Dot(d, x) / a); }
        double t0 = Param(from), t1 = Param(to);
        double span = t1 - t0;
        if (forward) { while (span <= 1e-12) span += 2 * Math.PI; }
        else { while (span >= -1e-12) span -= 2 * Math.PI; }
        var pts = new List<double[]> { from };
        for (int i = 1; i < n; i++)
        {
            double t = t0 + span * i / n;
            pts.Add(Add(centre, Add(Mul(x, a * Math.Cos(t)), Mul(y, b * Math.Sin(t)))));
        }
        pts.Add(to);
        return pts;
    }
}
