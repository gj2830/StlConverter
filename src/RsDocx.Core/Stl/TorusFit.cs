namespace RsDocx.Core.Stl;

/// <summary>Ring torus: centre, unit axis, major radius R (centre to tube centre circle), minor radius r (tube).</summary>
public readonly record struct Torus(Vec3 Centre, Vec3 Axis, double R, double r)
{
    /// <summary>Signed distance to the torus surface (positive outside the tube) and its gradient (outward unit normal).</summary>
    public double Distance(Vec3 p, out Vec3 grad)
    {
        var q = p - Centre;
        double h = q.Dot(Axis);
        var radial = q - Axis * h;
        double rho = radial.Length;
        var u = rho > 1e-300 ? radial * (1 / rho) : Perp(Axis);
        var w = u * (rho - R) + Axis * h; // from the nearest point of the tube's centre circle to p
        double d = w.Length;
        grad = d > 1e-300 ? w * (1 / d) : u;
        return d - r;
    }

    /// <summary>(rho, h): distance from the axis and height along it, relative to the centre.</summary>
    public (double rho, double h) Profile(Vec3 p)
    {
        var q = p - Centre;
        double h = q.Dot(Axis);
        return ((q - Axis * h).Length, h);
    }

    internal static Vec3 Perp(Vec3 n)
    {
        var u = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        return (u - n * u.Dot(n)).Normalized();
    }
}

/// <summary>
/// Fits a torus to points on it, with facet normals for the start values. The normal lines of a surface of revolution all
/// meet its axis: they belong to a linear line complex, whose axis is the smallest eigenvector of a 6x6 matrix of Pluecker
/// coordinates. In the (rho, h) half-plane around that axis the points lie on a circle (centre (R, h0), radius r). The
/// estimate is then refined by Levenberg-Marquardt on the exact point distances.
/// </summary>
public static class TorusFit
{
    /// <summary>
    /// The axis of a surface of revolution from its normal lines (point c0 on it, unit direction a): they belong to a
    /// linear line complex, whose axis is the smallest eigenvector of a 6x6 matrix of Pluecker coordinates.
    /// </summary>
    public static bool AxisFromNormals(IReadOnlyList<Vec3> pts, IReadOnlyList<(Vec3 p, Vec3 n)> normals, out Vec3 c0, out Vec3 a)
    {
        // centre and scale for conditioning
        var m0 = new Vec3(0, 0, 0);
        foreach (var p in pts) m0 += p;
        m0 = m0 * (1.0 / pts.Count);
        double scale = Math.Max(1e-12, pts.Max(p => (p - m0).Length));

        // axis from the normal lines: X = (abar, a) with abar.n + a.(p x n) = 0 for all lines
        var M = new double[6, 6];
        foreach (var (p0, n) in normals)
        {
            var p = (p0 - m0) * (1 / scale);
            var mo = p.Cross(n);
            double[] L = [n.X, n.Y, n.Z, mo.X, mo.Y, mo.Z];
            for (int i = 0; i < 6; i++) for (int j = 0; j < 6; j++) M[i, j] += L[i] * L[j];
        }
        var (lam, vec) = EigenSym(M);
        int k = 0;
        for (int i = 1; i < 6; i++) if (lam[i] < lam[k]) k = i;
        a = new Vec3(vec[3, k], vec[4, k], vec[5, k]);
        double al = a.Length;
        if (al < 1e-6) { c0 = default; return false; } // all normals parallel (a plane) or a pure translation complex
        a = a * (1 / al);
        var abar = new Vec3(vec[0, k], vec[1, k], vec[2, k]) * (1 / al);
        c0 = m0 + a.Cross(abar) * scale; // point on the axis
        return true;
    }

    public static bool Fit(IReadOnlyList<Vec3> pts, IReadOnlyList<(Vec3 p, Vec3 n)> normals, out Torus torus, out double maxResid,
        double giveUpRms = double.PositiveInfinity)
    {
        torus = default; maxResid = double.NaN;
        if (pts.Count < 8 || normals.Count < 4) return false;
        if (!AxisFromNormals(pts, normals, out var c0, out var a)) return false;

        // profile circle in (rho, h)
        if (!FitProfile(pts, c0, a, out double R, out double h0, out double r)) return false;
        var t = new Torus(c0 + a * h0, a, R, r);
        if (!Refine(pts, ref t, giveUpRms: giveUpRms)) return false;
        if (!(t.r > 0) || !(t.R > t.r)) return false; // only ring tori
        torus = t;
        maxResid = MaxResidual(pts, t);
        return true;
    }

    /// <summary>Best torus with a given axis line (through o, direction a): a geometric circle fit in the (rho, h) half-plane.</summary>
    public static bool FitWithAxis(IReadOnlyList<Vec3> pts, Vec3 o, Vec3 a, out Torus torus, out double maxResid)
    {
        torus = default; maxResid = double.NaN;
        if (pts.Count < 3 || !FitProfile(pts, o, a, out double R, out double h0, out double r)) return false;
        var prof = pts.Select(p => { var q = p - o; double h = q.Dot(a); return ((q - a * h).Length, h); }).ToList();
        for (int it = 0; it < 30; it++)
        {
            var m = new double[3, 3]; var b = new double[3];
            foreach (var (rho, h) in prof)
            {
                double dr = rho - R, dh = h - h0, d = Math.Sqrt(dr * dr + dh * dh);
                if (d < 1e-300) continue;
                double f = d - r;
                double[] J = [-dr / d, -dh / d, -1];
                for (int i = 0; i < 3; i++) { b[i] -= J[i] * f; for (int j = 0; j < 3; j++) m[i, j] += J[i] * J[j]; }
            }
            if (!Solve(m, b, out var x)) break;
            R += x[0]; h0 += x[1]; r += x[2];
            if (Math.Abs(x[0]) + Math.Abs(x[1]) + Math.Abs(x[2]) < 1e-16) break;
        }
        if (!(r > 0) || !(R > r)) return false;
        torus = new Torus(o + a * h0, a, R, r);
        maxResid = MaxResidual(pts, torus);
        return true;
    }

    public static double MaxResidual(IEnumerable<Vec3> pts, Torus t) => pts.Max(p => Math.Abs(t.Distance(p, out _)));

    private static bool FitProfile(IReadOnlyList<Vec3> pts, Vec3 c0, Vec3 a, out double R, out double h0, out double r)
    {
        // Kasa: rho^2 + h^2 + D rho + E h + F = 0
        var m = new double[3, 3]; var b = new double[3];
        foreach (var p in pts)
        {
            var q = p - c0; double h = q.Dot(a); double rho = (q - a * h).Length;
            double[] row = [rho, h, 1];
            double rhs = -(rho * rho + h * h);
            for (int i = 0; i < 3; i++) { b[i] += row[i] * rhs; for (int j = 0; j < 3; j++) m[i, j] += row[i] * row[j]; }
        }
        R = h0 = r = double.NaN;
        if (!Solve(m, b, out var x)) return false;
        R = -x[0] / 2; h0 = -x[1] / 2;
        double r2 = R * R + h0 * h0 - x[2];
        if (!(r2 > 0)) return false;
        r = Math.Sqrt(r2);
        return true;
    }

    /// <summary>Levenberg-Marquardt on f_i = |p_i - tube centre| - r over centre (3), axis tilt (2), R and r.</summary>
    /// <param name="giveUpRms">Stops early (from iteration 15 on) when the RMS distance is still above this and the cost fell
    /// less than 10 % over the last 5 iterations: a patch that is no torus creeps on to a poor minimum (organic meshes:
    /// most fits used all 60 iterations), a real torus converges within about 16.</param>
    public static bool Refine(IReadOnlyList<Vec3> pts, ref Torus t, int iterations = 60, double giveUpRms = double.PositiveInfinity)
    {
        double lambda = 1e-6;
        double Cost(Torus tt) { double s = 0; foreach (var p in pts) { double f = tt.Distance(p, out _); s += f * f; } return s; }
        double cost = Cost(t);
        var history = new double[iterations + 1];
        for (int it = 0; it < iterations; it++)
        {
            history[it] = cost;
            if (it >= 15 && Math.Sqrt(cost / pts.Count) > giveUpRms && cost > 0.9 * history[it - 5]) break;
            var e1 = Torus.Perp(t.Axis); var e2 = t.Axis.Cross(e1);
            var JtJ = new double[7, 7]; var Jtf = new double[7];
            foreach (var p in pts)
            {
                double f = t.Distance(p, out var nw);
                var q = p - t.Centre;
                double h = q.Dot(t.Axis);
                var radial = q - t.Axis * h; double rho = radial.Length;
                var u = rho > 1e-300 ? radial * (1 / rho) : e1;
                var qxn = q.Cross(nw);
                double[] J = [-nw.X, -nw.Y, -nw.Z, -e1.Dot(qxn), -e2.Dot(qxn), -nw.Dot(u), -1];
                for (int i = 0; i < 7; i++) { Jtf[i] += J[i] * f; for (int j = 0; j < 7; j++) JtJ[i, j] += J[i] * J[j]; }
            }
            bool improved = false;
            for (int tries = 0; tries < 10 && !improved; tries++)
            {
                var A = (double[,])JtJ.Clone();
                for (int i = 0; i < 7; i++) A[i, i] += lambda * (JtJ[i, i] + 1e-30);
                var rhs = Jtf.Select(v => -v).ToArray();
                if (!Solve(A, rhs, out var d)) { lambda *= 10; continue; }
                var w = e1 * d[3] + e2 * d[4];
                var nt = new Torus(t.Centre + new Vec3(d[0], d[1], d[2]), (t.Axis + w.Cross(t.Axis)).Normalized(), t.R + d[5], t.r + d[6]);
                double nc = Cost(nt);
                if (nc <= cost)
                {
                    double stepLen = Math.Sqrt(d.Sum(x => x * x));
                    t = nt; cost = nc; lambda = Math.Max(lambda / 10, 1e-12); improved = true;
                    if (stepLen < 1e-15) return true;
                }
                else lambda *= 10;
            }
            if (!improved) break;
        }
        return double.IsFinite(cost);
    }

    /// <summary>Jacobi eigenvalue decomposition of a symmetric matrix; eigenvectors in the columns.</summary>
    public static (double[] values, double[,] vectors) EigenSym(double[,] a0)
    {
        int n = a0.GetLength(0);
        var a = (double[,])a0.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) off += a[i, j] * a[i, j];
            if (off < 1e-30) break;
            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        var vals = new double[n];
        for (int i = 0; i < n; i++) vals[i] = a[i, i];
        return (vals, v);
    }

    /// <summary>Gaussian elimination with partial pivoting.</summary>
    public static bool Solve(double[,] m0, double[] b0, out double[] x)
    {
        int n = b0.Length;
        var m = (double[,])m0.Clone(); var b = (double[])b0.Clone();
        x = new double[n];
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < 1e-300) return false;
            if (piv != col)
            {
                for (int k = 0; k < n; k++) (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]);
                (b[col], b[piv]) = (b[piv], b[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = m[r, col] / m[col, col];
                if (f == 0) continue;
                for (int k = col; k < n; k++) m[r, k] -= f * m[col, k];
                b[r] -= f * b[col];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int k = r + 1; k < n; k++) s -= m[r, k] * x[k];
            x[r] = s / m[r, r];
        }
        return x.All(double.IsFinite);
    }
}

/// <summary>Least-squares cylinder refinement (Levenberg-Marquardt on point distances): axis point, axis tilt, radius.</summary>
public static class CylinderFit
{
    public static double Distance(Vec3 p, Vec3 o, Vec3 a, double r, out Vec3 grad)
    {
        var w = p - o; w -= a * w.Dot(a);
        double l = w.Length;
        grad = l > 1e-300 ? w * (1 / l) : Torus.Perp(a);
        return l - r;
    }

    public static bool Refine(IReadOnlyList<Vec3> pts, ref Vec3 o, ref Vec3 a, ref double r, int iterations = 60)
    {
        double lambda = 1e-6;
        double Cost(Vec3 oo, Vec3 aa, double rr) { double s = 0; foreach (var p in pts) { double f = Distance(p, oo, aa, rr, out _); s += f * f; } return s; }
        double cost = Cost(o, a, r);
        for (int it = 0; it < iterations; it++)
        {
            var e1 = Torus.Perp(a); var e2 = a.Cross(e1);
            var JtJ = new double[5, 5]; var Jtf = new double[5];
            foreach (var p in pts)
            {
                double f = Distance(p, o, a, r, out var g);
                var q = p - o;
                var qxg = q.Cross(g);
                // origin moves only perpendicular to the axis (e1, e2); axis tilts about e1, e2; radius
                double[] J = [-g.Dot(e1), -g.Dot(e2), -e1.Dot(qxg), -e2.Dot(qxg), -1];
                for (int i = 0; i < 5; i++) { Jtf[i] += J[i] * f; for (int j = 0; j < 5; j++) JtJ[i, j] += J[i] * J[j]; }
            }
            bool improved = false;
            for (int tries = 0; tries < 10 && !improved; tries++)
            {
                var A = (double[,])JtJ.Clone();
                for (int i = 0; i < 5; i++) A[i, i] += lambda * (JtJ[i, i] + 1e-30);
                if (!TorusFit.Solve(A, Jtf.Select(v => -v).ToArray(), out var d)) { lambda *= 10; continue; }
                var w = e1 * d[2] + e2 * d[3];
                var no = o + e1 * d[0] + e2 * d[1]; var na = (a + w.Cross(a)).Normalized(); double nr = r + d[4];
                double nc = Cost(no, na, nr);
                if (nc <= cost)
                {
                    o = no; a = na; r = nr; cost = nc; lambda = Math.Max(lambda / 10, 1e-12); improved = true;
                    if (d.Sum(x => Math.Abs(x)) < 1e-15) return true;
                }
                else lambda *= 10;
            }
            if (!improved) break;
        }
        return double.IsFinite(cost) && r > 0;
    }
}
