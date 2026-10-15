namespace RsDocx.Core.Stl;

/// <summary>
/// Cone: <see cref="Radius"/> at <see cref="Origin"/> (a point on the axis), growing by <see cref="Slope"/> = tan(half
/// angle) per unit length along the unit <see cref="Axis"/> (Slope &gt; 0). The natural normal points away from the axis
/// and back along it, as in SpaceClaim's CONE.
/// </summary>
public readonly record struct Cone(Vec3 Origin, Vec3 Axis, double Radius, double Slope)
{
    /// <summary>Signed distance (positive outside, away from the axis) and its gradient (the natural unit normal).</summary>
    public double Distance(Vec3 p, out Vec3 grad)
    {
        var q = p - Origin;
        double h = q.Dot(Axis);
        var radial = q - Axis * h;
        double rho = radial.Length, c = 1 / Math.Sqrt(1 + Slope * Slope);
        var u = rho > 1e-300 ? radial * (1 / rho) : Torus.Perp(Axis);
        grad = (u - Axis * Slope) * c;
        return (rho - Radius - Slope * h) * c;
    }

    /// <summary>(rho, h): distance from the axis and height along it, relative to the origin.</summary>
    public (double rho, double h) Profile(Vec3 p)
    {
        var q = p - Origin;
        double h = q.Dot(Axis);
        return ((q - Axis * h).Length, h);
    }
}

/// <summary>
/// Fits a cone to points on it, with facet normals for the start values: the axis from the normal lines (they all meet
/// the axis of a surface of revolution, as in <see cref="TorusFit"/>), a straight line through the profile in the
/// (rho, h) half-plane, then Levenberg-Marquardt on the exact point distances (axis position and direction, radius,
/// slope). Points on just two circles fit a cone and a sphere alike: callers look for cones first.
/// </summary>
public static class ConeFit
{
    /// <summary>
    /// Below this slope (1 degree) a cone is not told apart from a cylinder: with one more degree of freedom it fits the
    /// noise of a meshed cylinder (yubikey: "cones" of 0.1 degree where the cylinder fit failed by a few nanometres).
    /// </summary>
    public const double MinSlope = 0.017455;

    /// <summary>
    /// Above this slope (half angle 80 degrees) a "cone" is nearly a plane and is not trusted: countersinks are 45 degrees,
    /// drill points 59, shallow chamfers rarely beyond 75, while bits of a slightly curved surface fit such flat cones
    /// within a tenth of a micrometre (bigbox: about 100 a round).
    /// </summary>
    public const double MaxSlope = 5.6713;

    public static bool Fit(IReadOnlyList<Vec3> pts, IReadOnlyList<(Vec3 p, Vec3 n)> normals, out Cone cone, out double maxResid,
        double giveUpRms = double.PositiveInfinity)
    {
        cone = default; maxResid = double.NaN;
        if (pts.Count < 6 || normals.Count < 3) return false;
        // two starts for the axis: the normal vectors of a cone all make the same angle with it (they lie on a circle on
        // the unit sphere: the axis is that circle's plane normal), and the normal lines all meet it (line complex, as
        // for tori). The first also works on a narrow band of strips whose centroids are all at one height (their normal
        // lines then meet in one point, which leaves the line complex's axis undetermined).
        var starts = new List<(Vec3 c0, Vec3 a)>();
        // quick refusals (most seeds on an organic mesh, or on cylinders): the normals are not on one circle (more than
        // about 3 degrees off), or the circle is a great circle (slope below the minimum: a cylinder)
        if (AxisFromNormalCircle(normals, out var ac, out double spread, out double offset))
        {
            if (spread > 0.05 || Math.Abs(offset) < Math.Sin(Math.Atan(MinSlope)) * 0.5 || Math.Abs(offset) > Math.Sin(Math.Atan(MaxSlope) + 0.05)) return false;
            if (AxisPosition(normals, ac, out var cc)) starts.Add((cc, ac));
        }
        if (TorusFit.AxisFromNormals(pts, normals, out var c1, out var a1)) starts.Add((c1, a1));
        bool found = false;
        foreach (var (c0, a0) in starts)
        {
            if (!ProfileLine(pts, c0, a0, out var t)) continue;
            if (!Refine(pts, ref t, giveUpRms: giveUpRms) || !(t.Slope > MinSlope) || !(t.Slope < MaxSlope) || !double.IsFinite(t.Radius)) continue;
            var tt = t;
            double res = pts.Max(p => Math.Abs(tt.Distance(p, out _)));
            if (!found || res < maxResid) { cone = t; maxResid = res; found = true; }
        }
        return found;
    }

    /// <summary>The plane through the tips of the unit normals (n.a = const): its normal is the axis direction.</summary>
    private static bool AxisFromNormalCircle(IReadOnlyList<(Vec3 p, Vec3 n)> normals, out Vec3 a, out double spread, out double offset)
    {
        a = default; spread = double.NaN; offset = double.NaN;
        var m = new Vec3(0, 0, 0);
        foreach (var (_, n) in normals) m += n;
        m = m * (1.0 / normals.Count);
        var cov = new double[3, 3];
        foreach (var (_, n) in normals)
        {
            var d = n - m; double[] v = [d.X, d.Y, d.Z];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) cov[i, j] += v[i] * v[j];
        }
        var (lam, u) = MeshConverter.Eigen3(cov);
        var order = Enumerable.Range(0, 3).OrderBy(i => lam[i]).ToArray();
        // the tips must spread along a curve (two clearly larger eigenvalues), not lie in one point or on a line
        if (!(lam[order[1]] > 1e-10)) return false;
        a = new Vec3(u[0, order[0]], u[1, order[0]], u[2, order[0]]).Normalized();
        spread = Math.Sqrt(Math.Max(0, lam[order[0]]) / normals.Count); // RMS distance of the tips from the plane
        offset = m.Dot(a);                                                // = -sin(half angle) for a cone
        return true;
    }

    /// <summary>With the axis direction known, the point c on it (perpendicular to it) that all normal lines meet: each
    /// line through p with direction n meets the axis when (c - p).(n x a) = 0; least squares in the two directions.</summary>
    private static bool AxisPosition(IReadOnlyList<(Vec3 p, Vec3 n)> normals, Vec3 a, out Vec3 c)
    {
        var e1 = Torus.Perp(a); var e2 = a.Cross(e1);
        double m11 = 0, m12 = 0, m22 = 0, b1 = 0, b2 = 0;
        var o = normals[0].p;
        foreach (var (p, n) in normals)
        {
            var w = n.Cross(a);
            double w1 = w.Dot(e1), w2 = w.Dot(e2), rhs = (p - o).Dot(w);
            m11 += w1 * w1; m12 += w1 * w2; m22 += w2 * w2; b1 += w1 * rhs; b2 += w2 * rhs;
        }
        double det = m11 * m22 - m12 * m12;
        c = default;
        if (Math.Abs(det) < 1e-30 * Math.Max(1, m11 * m22)) return false;
        double x = (b1 * m22 - b2 * m12) / det, y = (m11 * b2 - m12 * b1) / det;
        c = o + e1 * x + e2 * y;
        return true;
    }

    /// <summary>Straight profile line rho = r0 + k h around the axis (least squares); the axis turned to the wide end.</summary>
    private static bool ProfileLine(IReadOnlyList<Vec3> pts, Vec3 c0, Vec3 a, out Cone cone)
    {
        cone = default;
        double sh = 0, sr = 0, shh = 0, shr = 0; int n = pts.Count;
        foreach (var p in pts)
        {
            var q = p - c0; double h = q.Dot(a), rho = (q - a * h).Length;
            sh += h; sr += rho; shh += h * h; shr += h * rho;
        }
        double det = n * shh - sh * sh;
        if (Math.Abs(det) < 1e-30) return false; // all points at one height: no slope to find
        double k = (n * shr - sh * sr) / det, r0 = (sr - k * sh) / n;
        if (k < 0) { a = a * -1; k = -k; }
        cone = new Cone(c0, a, r0, k);
        return true;
    }

    /// <summary>
    /// Levenberg-Marquardt over the axis position (2, perpendicular to it), its direction (2 tilts), the radius at the
    /// origin and the slope; numerical Jacobian. <paramref name="giveUpRms"/>: as in <see cref="TorusFit.Refine(IReadOnlyList{Vec3}, ref Torus, int, double)"/>.
    /// </summary>
    /// <param name="fixAxis">Keep the axis direction (it was put on a plane's normal); only its position, the radius and
    /// the slope change.</param>
    public static bool Refine(IReadOnlyList<Vec3> pts, ref Cone c, int iterations = 60, double giveUpRms = double.PositiveInfinity, bool fixAxis = false)
    {
        double lambda = 1e-6;
        var e1 = Torus.Perp(c.Axis); var e2 = c.Axis.Cross(e1);
        var baseCone = c;
        // parameters relative to the start: offsets along e1, e2; tilts about e1, e2; radius; slope
        Cone Make(double[] x)
        {
            var w = e1 * x[2] + e2 * x[3];
            var axis = (baseCone.Axis + w.Cross(baseCone.Axis)).Normalized();
            return new Cone(baseCone.Origin + e1 * x[0] + e2 * x[1], axis, baseCone.Radius + x[4], baseCone.Slope + x[5]);
        }
        double[] Res(Cone cc) { var r = new double[pts.Count]; for (int i = 0; i < pts.Count; i++) r[i] = cc.Distance(pts[i], out _); return r; }
        double Cost(double[] r) { double s = 0; foreach (double v in r) s += v * v; return s; }
        var x = new double[6];
        var f = Res(c);
        double cost = Cost(f);
        var origin0 = c.Origin;
        double scale = Math.Max(1e-9, Math.Abs(c.Radius) + pts.Max(p => (p - origin0).Length));
        var history = new double[iterations + 1];
        for (int it = 0; it < iterations; it++)
        {
            history[it] = cost;
            if (it >= 15 && Math.Sqrt(cost / pts.Count) > giveUpRms && cost > 0.9 * history[it - 5]) break;
            // numerical Jacobian (central differences)
            var J = new double[pts.Count, 6];
            for (int j = 0; j < 6; j++)
            {
                double step = (j is 2 or 3 or 5 ? 1e-7 : 1e-7 * scale);
                var xp = (double[])x.Clone(); xp[j] += step;
                var xm = (double[])x.Clone(); xm[j] -= step;
                var fp = Res(Make(xp)); var fm = Res(Make(xm));
                for (int i = 0; i < pts.Count; i++) J[i, j] = (fp[i] - fm[i]) / (2 * step);
            }
            var JtJ = new double[6, 6]; var Jtf = new double[6];
            for (int i = 0; i < pts.Count; i++)
                for (int a = 0; a < 6; a++)
                {
                    Jtf[a] += J[i, a] * f[i];
                    for (int b = 0; b < 6; b++) JtJ[a, b] += J[i, a] * J[i, b];
                }
            if (fixAxis)
                foreach (int j in new[] { 2, 3 })
                {
                    for (int b = 0; b < 6; b++) { JtJ[j, b] = 0; JtJ[b, j] = 0; }
                    JtJ[j, j] = 1; Jtf[j] = 0;
                }
            bool improved = false;
            for (int tries = 0; tries < 10 && !improved; tries++)
            {
                var A = (double[,])JtJ.Clone();
                for (int i = 0; i < 6; i++) A[i, i] += lambda * (JtJ[i, i] + 1e-30);
                if (!TorusFit.Solve(A, Jtf.Select(v => -v).ToArray(), out var d)) { lambda *= 10; continue; }
                var nx = x.Select((v, i) => v + d[i]).ToArray();
                var nf = Res(Make(nx));
                double nc = Cost(nf);
                if (nc <= cost)
                {
                    double stepLen = Math.Sqrt(d.Sum(v => v * v));
                    x = nx; f = nf; cost = nc; lambda = Math.Max(lambda / 10, 1e-12); improved = true;
                    if (stepLen < 1e-15) { c = Make(x); return true; }
                }
                else lambda *= 10;
            }
            if (!improved) break;
        }
        c = Make(x);
        return double.IsFinite(cost);
    }
}
