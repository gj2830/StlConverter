using RsDocx.Core.Stl;

namespace RsDocx.Core.Brep;

/// <summary>
/// Non-rational B-spline curve with clamped ends (end knots of multiplicity degree + 1), as SpaceClaim writes them
/// (NURBS_CURVE knot_type 6 = bezier ends). <see cref="Knots"/> are the distinct knot values, <see cref="Mults"/> their
/// multiplicities. The curve runs from the first to the last control point; <see cref="Closed"/> when they coincide
/// (a ring edge).
/// </summary>
/// <param name="Weights">Rational curve: one weight per control point (null: non-rational).</param>
/// <param name="Helix">The curve is (an approximation of) a helix: written with Parasolid's HELIX_CU_FORM.</param>
public sealed record BrepBSpline(int Degree, IReadOnlyList<Vec3> ControlPoints, IReadOnlyList<double> Knots, IReadOnlyList<int> Mults, bool Closed,
    IReadOnlyList<double>? Weights = null, HelixForm? Helix = null) : BrepCurve
{
    private double[]? _full;
    public double[] FullKnots => _full ??= BSpline.Expand(Knots, Mults);
    public double Start => Knots[0];
    public double End => Knots[^1];
    public Vec3 At(double t) => Weights == null ? BSpline.Eval(Degree, ControlPoints, FullKnots, t) : BSpline.EvalRational(Degree, ControlPoints, Weights, FullKnots, t);
}

/// <summary>
/// The helical shape of a curve or surface (Parasolid HELIX_CU_FORM / HELIX_SU_FORM): turning counter-clockwise around
/// <see cref="AxisDir"/> it advances along it for a right-handed helix. <see cref="Point"/> is the curve's start (curves
/// only); <see cref="Turns0"/>..<see cref="Turns1"/> its extent in turns from there.
/// </summary>
public sealed record HelixForm(Vec3 AxisPoint, Vec3 AxisDir, Vec3 Point, bool RightHanded, double Turns0, double Turns1, double Pitch, double Tol);

/// <summary>
/// How Parasolid stores a NURBS surface besides its shape (fields of NURBS_SURF and SURFACE_DATA), kept from a file that
/// was read so it is written back the same. DesignSpark's revolved spline (testdata/kubus_getrokken.rsdocx): periodic and
/// closed in v, knot types 6 (u) and 5 (v), surface_form 8, ends "DDPPDDPP". <see cref="Ends"/>: the eight characters
/// original_u_start, _u_end, _v_start, _v_end, then the same four extended.
/// </summary>
public sealed record NurbsFormat(bool UPeriodic, bool VPeriodic, bool UClosed, bool VClosed, byte UKnotType, byte VKnotType,
    byte SurfaceForm, bool Rational, string Ends, byte SelfInt)
{
    /// <summary>As PushPullMe writes its own surfaces (testdata/helix.rsdocx): rational, knot types 1, surface_form 12.</summary>
    public static readonly NurbsFormat Default = new(false, false, false, false, 1, 1, 12, true, "BBBBBBBB", 2);
}

/// <summary>
/// A rational B-spline surface with clamped knots. <see cref="Points"/> row by row: for each u index the <see cref="NV"/>
/// points along v (as SpaceClaim stores them: testdata/helix.rsdocx). The natural normal is dS/du x dS/dv.
/// <see cref="Format"/>: how it was stored in the file it came from (null: <see cref="NurbsFormat.Default"/>).
/// </summary>
public sealed record BrepNurbsSurface(int UDegree, int VDegree, int NU, int NV, IReadOnlyList<Vec3> Points, IReadOnlyList<double> Weights,
    IReadOnlyList<double> UKnots, IReadOnlyList<int> UMults, IReadOnlyList<double> VKnots, IReadOnlyList<int> VMults, HelixForm? Helix = null,
    NurbsFormat? Format = null) : BrepSurface
{
    private double[]? _u, _v;
    public double[] FullU => _u ??= BSpline.Expand(UKnots, UMults);
    public double[] FullV => _v ??= BSpline.Expand(VKnots, VMults);

    public Vec3 At(double u, double v)
    {
        int iu = BSpline.Span(UDegree, FullU, u), iv = BSpline.Span(VDegree, FullV, v);
        var Nu = BSpline.Basis(UDegree, FullU, iu, u); var Nv = BSpline.Basis(VDegree, FullV, iv, v);
        var s = new Vec3(0, 0, 0); double w = 0;
        for (int a = 0; a <= UDegree; a++)
            for (int b = 0; b <= VDegree; b++)
            {
                int k = (iu - UDegree + a) * NV + (iv - VDegree + b);
                double f = Nu[a] * Nv[b] * Weights[k];
                s += Points[k] * f; w += f;
            }
        return s * (1 / w);
    }

    /// <summary>Distance from q to the surface (see <see cref="Parameters"/>).</summary>
    public double Distance(Vec3 q)
    {
        var (u, v) = Parameters(q);
        return (At(u, v) - q).Length;
    }

    /// <summary>The parameters of the point of the surface nearest to q: the nearest of a grid of samples, then Newton
    /// steps on (u, v); when those do not reach the surface, Newton steps from the next nearest samples too (each at
    /// another place: where a revolved surface comes to a point, a whole row of samples is that point, and Newton
    /// steps from there do not move).</summary>
    public (double U, double V) Parameters(Vec3 q)
    {
        double u0 = UKnots[0], u1 = UKnots[^1], v0 = VKnots[0], v1 = VKnots[^1];
        int nu = Math.Max(4, 4 * NU), nv = Math.Max(16, 8 * NV);
        var samples = new List<(double D, double U, double V, Vec3 P)>((nu + 1) * (nv + 1));
        for (int i = 0; i <= nu; i++)
            for (int j = 0; j <= nv; j++)
            {
                double u = u0 + (u1 - u0) * i / nu, v = v0 + (v1 - v0) * j / nv;
                var p = At(u, v);
                samples.Add(((p - q).Length, u, v, p));
            }
        samples.Sort((a, b) => a.D.CompareTo(b.D));
        var (bd, bu, bv, _) = samples[0];
        var tried = new List<Vec3>();
        foreach (var s in samples)
        {
            if (tried.Count == 4) break;
            if (tried.Any(t => (t - s.P).Length < 1e-12)) continue;
            tried.Add(s.P);
            var (nu1, nv1, _) = Newton(q, s.U, s.V);
            double d = (At(nu1, nv1) - q).Length;
            if (d < bd) { bd = d; bu = nu1; bv = nv1; }
            if (bd < 1e-12) break;
        }
        return (bu, bv);
    }

    /// <summary>As <see cref="Parameters(Vec3)"/>, starting from parameters near the answer (the previous point along
    /// an edge): Newton steps only; the grid only when they do not settle, or end farther off than <paramref name="tol"/>.</summary>
    public (double U, double V) Parameters(Vec3 q, (double U, double V) near, double tol = 1e-7)
    {
        var (u, v, settled) = Newton(q, near.U, near.V);
        return settled && (At(u, v) - q).Length <= tol ? (u, v) : Parameters(q);
    }

    /// <summary>Where a few Newton steps from parameters near q end (settled or not; no search over the surface): for
    /// whether a point near them is on the surface (one that is, they reach).</summary>
    public (double U, double V) Near(Vec3 q, (double U, double V) near)
    {
        var (u, v, _) = Newton(q, near.U, near.V, 8);
        return (u, v);
    }

    /// <summary>Newton steps towards the nearest point from (bu, bv); whether they settled.</summary>
    private (double U, double V, bool Settled) Newton(Vec3 q, double bu, double bv, int steps = 30)
    {
        double u0 = UKnots[0], u1 = UKnots[^1], v0 = VKnots[0], v1 = VKnots[^1];
        double hu = (u1 - u0) * 1e-7, hv = (v1 - v0) * 1e-7;
        for (int it = 0; it < steps; it++)
        {
            var p = At(bu, bv);
            var su = (At(Math.Min(u1, bu + hu), bv) - At(Math.Max(u0, bu - hu), bv)) * (1 / (Math.Min(u1, bu + hu) - Math.Max(u0, bu - hu)));
            var sv = (At(bu, Math.Min(v1, bv + hv)) - At(bu, Math.Max(v0, bv - hv))) * (1 / (Math.Min(v1, bv + hv) - Math.Max(v0, bv - hv)));
            var r = p - q;
            double a11 = su.Dot(su), a12 = su.Dot(sv), a22 = sv.Dot(sv), b1 = -r.Dot(su), b2 = -r.Dot(sv);
            double det = a11 * a22 - a12 * a12;
            if (Math.Abs(det) < 1e-300) break;
            double du = (b1 * a22 - b2 * a12) / det, dv = (a11 * b2 - a12 * b1) / det;
            bu = Math.Clamp(bu + du, u0, u1); bv = Math.Clamp(bv + dv, v0, v1);
            if (Math.Abs(du) < (u1 - u0) * 1e-12 && Math.Abs(dv) < (v1 - v0) * 1e-12) return (bu, bv, true);
        }
        return (bu, bv, false);
    }
}

public static class BSpline
{
    public static double[] Expand(IReadOnlyList<double> knots, IReadOnlyList<int> mults)
    {
        var u = new List<double>();
        for (int i = 0; i < knots.Count; i++) for (int k = 0; k < mults[i]; k++) u.Add(knots[i]);
        return u.ToArray();
    }

    public static (List<double> knots, List<int> mults) Compress(double[] full)
    {
        var k = new List<double>(); var m = new List<int>();
        foreach (double x in full)
        {
            if (k.Count > 0 && x == k[^1]) m[^1]++;
            else { k.Add(x); m.Add(1); }
        }
        return (k, m);
    }

    /// <summary>Knot span index i with U[i] &lt;= t &lt; U[i+1] (the last non-empty span for t = end).</summary>
    public static int Span(int p, double[] U, double t)
    {
        int n = U.Length - p - 2; // last control point index
        if (t >= U[n + 1]) return n;
        if (t <= U[p]) return p;
        int lo = p, hi = n + 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (t < U[mid]) hi = mid; else lo = mid;
        }
        return lo;
    }

    /// <summary>Non-zero basis functions N[i-p..i] at t (The NURBS Book, A2.2).</summary>
    public static double[] Basis(int p, double[] U, int i, double t)
    {
        var N = new double[p + 1]; var left = new double[p + 1]; var right = new double[p + 1];
        N[0] = 1;
        for (int j = 1; j <= p; j++)
        {
            left[j] = t - U[i + 1 - j]; right[j] = U[i + j] - t;
            double saved = 0;
            for (int r = 0; r < j; r++)
            {
                double tmp = N[r] / (right[r + 1] + left[j - r]);
                N[r] = saved + right[r + 1] * tmp;
                saved = left[j - r] * tmp;
            }
            N[j] = saved;
        }
        return N;
    }

    /// <summary>A rational curve: control points with weights (the homogeneous sum divided by the weighted one).</summary>
    public static Vec3 EvalRational(int p, IReadOnlyList<Vec3> P, IReadOnlyList<double> W, double[] U, double t)
    {
        int i = Span(p, U, t);
        var N = Basis(p, U, i, t);
        var c = new Vec3(0, 0, 0); double w = 0;
        for (int j = 0; j <= p; j++) { double f = N[j] * W[i - p + j]; c += P[i - p + j] * f; w += f; }
        return c * (1 / w);
    }

    public static Vec3 Eval(int p, IReadOnlyList<Vec3> P, double[] U, double t)
    {
        int i = Span(p, U, t);
        var N = Basis(p, U, i, t);
        var c = new Vec3(0, 0, 0);
        for (int j = 0; j <= p; j++) c += P[i - p + j] * N[j];
        return c;
    }

    /// <summary>
    /// Cubic interpolation through the points (chord-length parameters, knots by averaging; The NURBS Book 9.2.1).
    /// Parameters are in metres along the polyline, so the knots are a length scale.
    /// </summary>
    public static BrepBSpline Interpolate(IReadOnlyList<Vec3> Q, bool closed)
    {
        const int p = 3;
        int n = Q.Count - 1;
        if (n < p) throw new ArgumentException("cubic interpolation needs at least 4 points");
        var u = new double[n + 1];
        for (int k = 1; k <= n; k++) u[k] = u[k - 1] + (Q[k] - Q[k - 1]).Length;
        if (!(u[n] > 0)) throw new ArgumentException("points coincide");
        var U = new double[n + p + 2];
        for (int k = 0; k <= p; k++) { U[k] = u[0]; U[n + 1 + k] = u[n]; }
        for (int j = 1; j <= n - p; j++)
        {
            double s = 0;
            for (int k = j; k < j + p; k++) s += u[k];
            U[j + p] = s / p;
        }
        // banded system: row k has N_{span-p..span}(u_k)
        int size = n + 1;
        var rows = new (int first, double[] vals)[size];
        for (int k = 0; k < size; k++)
        {
            int span = Span(p, U, u[k]);
            rows[k] = (span - p, Basis(p, U, span, u[k]));
        }
        var A = new double[size, 2 * p + 1]; // A[r, c - r + p]
        for (int k = 0; k < size; k++)
            for (int j = 0; j <= p; j++)
            {
                int col = rows[k].first + j;
                A[k, col - k + p] += rows[k].vals[j];
            }
        var bx = Q.Select(q => q.X).ToArray(); var by = Q.Select(q => q.Y).ToArray(); var bz = Q.Select(q => q.Z).ToArray();
        SolveBanded(A, p, size, [bx, by, bz]);
        var P = Enumerable.Range(0, size).Select(k => new Vec3(bx[k], by[k], bz[k])).ToList();
        if (closed) P[^1] = P[0];
        var (knots, mults) = Compress(U);
        return new BrepBSpline(p, P, knots, mults, closed);
    }

    /// <summary>Gaussian elimination on a banded matrix (half bandwidth w) without pivoting: fine for B-spline
    /// collocation matrices (totally positive).</summary>
    private static void SolveBanded(double[,] A, int w, int n, double[][] rhs)
    {
        double Get(int r, int c) => Math.Abs(c - r) > w ? 0 : A[r, c - r + w];
        void Set(int r, int c, double v) => A[r, c - r + w] = v;
        for (int k = 0; k < n; k++)
        {
            double piv = Get(k, k);
            if (Math.Abs(piv) < 1e-300) throw new ArgumentException("singular interpolation matrix");
            for (int r = k + 1; r <= Math.Min(n - 1, k + w); r++)
            {
                double f = Get(r, k) / piv;
                if (f == 0) continue;
                for (int c = k; c <= Math.Min(n - 1, k + w); c++) Set(r, c, Get(r, c) - f * Get(k, c));
                foreach (var b in rhs) b[r] -= f * b[k];
            }
        }
        for (int r = n - 1; r >= 0; r--)
            foreach (var b in rhs)
            {
                double s = b[r];
                for (int c = r + 1; c <= Math.Min(n - 1, r + w); c++) s -= Get(r, c) * b[c];
                b[r] = s / Get(r, r);
            }
    }

    /// <summary>Parameter of the point of the curve nearest to q: coarse search, then golden section and Newton, both
    /// kept inside the bracket around the best sample (an unguarded Newton step can jump to the other end of a ring).</summary>
    public static double ClosestParam(BrepBSpline c, Vec3 q, int samples = 0)
    {
        double t0 = c.Start, t1 = c.End;
        if (samples <= 0) samples = Math.Max(64, 4 * c.ControlPoints.Count);
        int bi = 0; double bd = double.MaxValue;
        for (int i = 0; i <= samples; i++)
        {
            double d = (c.At(t0 + (t1 - t0) * i / samples) - q).Length;
            if (d < bd) { bd = d; bi = i; }
        }
        double lo = t0 + (t1 - t0) * Math.Max(0, bi - 1) / samples, hi = t0 + (t1 - t0) * Math.Min(samples, bi + 1) / samples;
        double D(double t) { var d = c.At(t) - q; return d.Dot(d); }
        const double g = 0.6180339887498949;
        double a = lo, b = hi, x1 = b - g * (b - a), x2 = a + g * (b - a), f1 = D(x1), f2 = D(x2);
        for (int it = 0; it < 80 && b - a > (t1 - t0) * 1e-15; it++)
        {
            if (f1 < f2) { b = x2; x2 = x1; f2 = f1; x1 = b - g * (b - a); f1 = D(x1); }
            else { a = x1; x1 = x2; f1 = f2; x2 = a + g * (b - a); f2 = D(x2); }
        }
        double best = (a + b) / 2;
        // a few guarded Newton steps for the last digits
        double h = (hi - lo) * 1e-6;
        for (int it = 0; it < 5; it++)
        {
            Vec3 Pt(double t) => c.At(Math.Clamp(t, lo, hi));
            var p0 = Pt(best); var d1 = (Pt(best + h) - Pt(best - h)) * (1 / (2 * h));
            var d2 = (Pt(best + h) - p0 * 2 + Pt(best - h)) * (1 / (h * h));
            double f = (p0 - q).Dot(d1), fp = d1.Dot(d1) + (p0 - q).Dot(d2);
            if (!(Math.Abs(fp) > 1e-300)) break;
            double next = Math.Clamp(best - f / fp, lo, hi);
            if (D(next) > D(best)) break;
            best = next;
        }
        foreach (double end in new[] { lo, hi }) if (D(end) < D(best)) best = end;
        return best;
    }
}
