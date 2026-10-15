using System.Globalization;

namespace RsDocx.Core.Stl;

/// <summary>A screw thread measured from mesh triangles (metres; diameters across the axis).</summary>
/// <param name="AxisPoint">A point on the thread's axis.</param>
/// <param name="Axis">Unit axis direction.</param>
/// <param name="MajorDiameter">Outer diameter: the crests of an external thread, the roots of an internal one.</param>
/// <param name="MinorDiameter">Inner diameter: the roots of an external thread, the crests of an internal one.</param>
/// <param name="Pitch">Distance along the axis between two neighbouring crests.</param>
/// <param name="RightHanded">Turning clockwise (seen along the axis direction) moves it along the axis.</param>
/// <param name="Internal">A thread in a hole (nut); otherwise on a rod (bolt).</param>
/// <param name="ProfileAngleDeg">Angle between the two flanks (60 for metric, 55 for Whitworth, 30 for trapezoidal).</param>
/// <param name="Length">Length along the axis of the measured triangles.</param>
/// <param name="Fit">How well the crests line up on one helix: 1 = perfectly, below ~0.6 hardly a thread.</param>
/// <param name="Standard">The nearest ISO metric size that matches pitch and diameter ("M10" or "M10×1,25"), or null.</param>
/// <param name="XAxis">The direction (across the axis) where the angle θ around the axis is 0.</param>
/// <param name="BasePhase">Where along one pitch the middle of the root lies (the flat the thread stands on: smallest
/// radius on a bolt, largest in a nut), as a fraction of the pitch: the root's middle is at h = (BasePhase + k)·Pitch +
/// hand·Pitch·θ/2π along the axis from <see cref="AxisPoint"/>.</param>
/// <param name="CrestFrom">Along the axis from <see cref="AxisPoint"/>: where the crests begin (1st percentile).</param>
/// <param name="CrestTo">Where the crests end (99th percentile).</param>
public sealed record ThreadMeasure(Vec3 AxisPoint, Vec3 Axis, double MajorDiameter, double MinorDiameter, double Pitch, bool RightHanded,
    bool Internal, double ProfileAngleDeg, double Length, double Fit, string? Standard,
    Vec3 XAxis = default, double BasePhase = 0, double CrestFrom = 0, double CrestTo = 0)
{
    public double Turns => Pitch > 0 ? Length / Pitch : 0;

    /// <summary>"M10 buitendraad, rechts: buiten-Ø 9,97 mm, kern-Ø 8,16 mm, spoed 1,5 mm, flankhoek 60°, 12 mm lang (8 gangen)".</summary>
    public string Describe(CultureInfo c) => string.Format(c,
        "{0}{1}, {2}: buiten-Ø {3:0.##} mm, kern-Ø {4:0.##} mm, spoed {5:0.###} mm, flankhoek {6:0}°, {7:0.#} mm lang ({8:0.#} gangen)",
        Standard != null ? Standard + " " : "", Internal ? "binnendraad" : "buitendraad", RightHanded ? "rechts" : "links",
        MajorDiameter * 1000, MinorDiameter * 1000, Pitch * 1000, ProfileAngleDeg, Length * 1000, Turns);
}

/// <summary>
/// Measures a screw thread from its triangles. The axis comes from the facet normals: the normals of a helical surface
/// form a linear line complex just like those of a surface of revolution (<see cref="TorusFit.AxisFromNormals"/>). The
/// diameters are the outer and inner radii of the points; the pitch and hand from the crest points, which lie on one
/// helix: their phase 2π·(h/p) ∓ θ is the same for all of them at the right pitch p. The profile angle from the flank
/// normals' angle to the axis.
/// </summary>
public static class ThreadFit
{
    /// <summary>ISO metric threads: nominal diameter (mm), coarse pitch, then the common fine pitches.</summary>
    private static readonly (double d, double[] pitches)[] Metric =
    [
        (1, [0.25, 0.2]), (1.2, [0.25, 0.2]), (1.6, [0.35, 0.2]), (2, [0.4, 0.25]), (2.5, [0.45, 0.35]), (3, [0.5, 0.35]),
        (4, [0.7, 0.5]), (5, [0.8, 0.5]), (6, [1, 0.75]), (8, [1.25, 1, 0.75]), (10, [1.5, 1.25, 1, 0.75]),
        (12, [1.75, 1.5, 1.25, 1]), (14, [2, 1.5, 1.25, 1]), (16, [2, 1.5, 1]), (18, [2.5, 2, 1.5, 1]), (20, [2.5, 2, 1.5, 1]),
        (22, [2.5, 2, 1.5, 1]), (24, [3, 2, 1.5, 1]), (27, [3, 2, 1.5, 1]), (30, [3.5, 3, 2, 1.5, 1]), (33, [3.5, 3, 2, 1.5]),
        (36, [4, 3, 2, 1.5]), (39, [4, 3, 2, 1.5]), (42, [4.5, 4, 3, 2, 1.5]), (45, [4.5, 4, 3, 2, 1.5]), (48, [5, 4, 3, 2, 1.5]),
        (52, [5, 4, 3, 2, 1.5]), (56, [5.5, 4, 3, 2, 1.5]), (60, [5.5, 4, 3, 2, 1.5]), (64, [6, 4, 3, 2, 1.5]),
    ];

    /// <summary>Measures the thread the triangles (corners in metres, wound outward) lie on; null when they do not look like
    /// one (no axis, no depth, or crests that do not line up on a helix).</summary>
    public static ThreadMeasure? Measure(IReadOnlyList<(Vec3 a, Vec3 b, Vec3 c)> tris)
    {
        var m = MeasureAll(tris);
        // (twice: the second round starts from the better axis of the first)
        for (int round = 0; round < 2; round++)
        {
            var again = MeasureNear(tris, m);
            if (ReferenceEquals(again, m)) break;
            m = again;
        }
        if (m == null) return null;
        // the axis from where the crests are whole: the run-out at an end (where the thread fades into a shoulder: the
        // screw ball's lowest millimetre) tilts it (1°); the rest is measured on everything around that axis
        var (cA, aA) = (m.AxisPoint, m.Axis.Normalized());
        double Hof(Vec3 p) => (p - cA).Dot(aA);
        double Rof(Vec3 p) { var d = p - cA; return (d - aA * d.Dot(aA)).Length; }
        double dep = (m.MajorDiameter - m.MinorDiameter) / 2, rLo = m.MinorDiameter / 2 - 0.5 * dep, rHi = m.MajorDiameter / 2 + 0.5 * dep;
        double h0 = m.CrestFrom + m.Pitch, h1 = m.CrestTo - m.Pitch;
        if (h1 - h0 < 2 * m.Pitch) return m;
        var whole = tris.Where(t => new[] { t.a, t.b, t.c }.All(p => Rof(p) >= rLo && Rof(p) <= rHi && Hof(p) >= h0 && Hof(p) <= h1)).ToList();
        var mc = whole.Count >= 12 ? MeasureAll(whole) : null;
        if (mc == null) return m;
        var near = tris.Where(t => new[] { t.a, t.b, t.c }.All(p => Rof(p) >= rLo && Rof(p) <= rHi)).ToList();
        var m3 = MeasureAll(near, (mc.AxisPoint, mc.Axis));
        return m3 != null && m3.Fit >= 0.9 * m.Fit ? m3 : m;
    }

    private static ThreadMeasure? MeasureNear(IReadOnlyList<(Vec3 a, Vec3 b, Vec3 c)> tris, ThreadMeasure? m)
    {
        // a selection with pieces elsewhere (made through the model: the far side of the screw ball's sphere, the
        // bore inside it) spoils the depth and the axis: measured again on what lies near the typical radius around a
        // first axis
        Vec3 c0, axis;
        if (m != null) { c0 = m.AxisPoint; axis = m.Axis; }
        else
        {
            var nn = tris.Select(t => (p: (t.a + t.b + t.c) * (1.0 / 3), n: (t.b - t.a).Cross(t.c - t.a))).Where(q => q.n.Length > 0 && double.IsFinite(q.n.Length))
                .Select(q => (q.p, q.n.Normalized())).ToList();
            if (nn.Count < 12 || !TorusFit.AxisFromNormals(tris.SelectMany(t => new[] { t.a, t.b, t.c }).Distinct().ToList(), nn, out c0, out axis)) return null;
        }
        double R(Vec3 p) { var d = p - c0; return (d - axis * d.Dot(axis)).Length; }
        var radii = tris.Select(t => R((t.a + t.b + t.c) * (1.0 / 3))).OrderBy(r => r).ToList();
        double med = radii[radii.Count / 2];
        // (around the thread found already: between its root and crest, with half its depth to spare)
        double lo = 0.75 * med, hi = 1.25 * med;
        if (m != null) { double d = (m.MajorDiameter - m.MinorDiameter) / 2; lo = m.MinorDiameter / 2 - 0.5 * d; hi = m.MajorDiameter / 2 + 0.5 * d; }
        var near = tris.Where(t => new[] { t.a, t.b, t.c }.All(p => R(p) >= lo && R(p) <= hi)).ToList();
        if (near.Count == tris.Count || near.Count < 12) return m;
        var m2 = MeasureAll(near);
        return m2 != null && (m == null || m2.Fit >= m.Fit) ? m2 : m;
    }

    private static ThreadMeasure? MeasureAll(IReadOnlyList<(Vec3 a, Vec3 b, Vec3 c)> tris, (Vec3 c, Vec3 a)? fixedAxis = null)
    {
        if (tris.Count < 12) return null;
        var normals = new List<(Vec3 p, Vec3 n)>(tris.Count);
        var areas = new List<double>(tris.Count);
        foreach (var (a, b, c) in tris)
        {
            var cr = (b - a).Cross(c - a);
            double l = cr.Length;
            if (l <= 0 || !double.IsFinite(l)) continue;
            normals.Add(((a + b + c) * (1.0 / 3), cr * (1 / l)));
            areas.Add(l / 2);
        }
        var pts = tris.SelectMany(t => new[] { t.a, t.b, t.c }).Distinct().ToList();
        Vec3 c0, axis;
        if (fixedAxis is { } fa) { c0 = fa.c; axis = fa.a.Normalized(); }
        else if (normals.Count < 12 || !TorusFit.AxisFromNormals(pts, normals, out c0, out axis)) return null;
        // flat faces across the axis (a shoulder, the end of the rod) and chamfers fit a surface of revolution, the flanks
        // a helical surface: together they tilt the axis (screw ball with its shoulder and chamfer: 4°). The normal
        // of a helical surface with axis (c, a) satisfies n·(a×(p−c)) + k·(n·a) = 0 for one k (pitch / 2π); a flat face
        // or chamfer does with k = 0 only. So: fit k, leave out the fifth of the facets that fit worst, find the axis
        // again, a few times.
        var use = normals;
        for (int it = 0; it < (fixedAxis == null ? 5 : 0); it++)
        {
            var (cc, aa) = (c0, axis);
            double sxy = 0, sxx = 0;
            foreach (var (p, n) in use) { double na = n.Dot(aa), m = n.Dot(aa.Cross(p - cc)); sxy += na * m; sxx += na * na; }
            double k = sxx > 0 ? -sxy / sxx : 0;
            // (the outliers only: more than 4 x the median misfit; an exact thread keeps all its facets)
            double Misfit((Vec3 p, Vec3 n) q) => Math.Abs(q.n.Dot(aa.Cross(q.p - cc)) + k * q.n.Dot(aa));
            var mis = use.Select(Misfit).OrderBy(v => v).ToList();
            double limit = 4 * mis[mis.Count / 2] + 1e-12;
            var keep = use.Where(q => Misfit(q) <= limit).ToList();
            if (keep.Count == use.Count || keep.Count < 12 || !TorusFit.AxisFromNormals(pts, keep, out var c1, out var a1)) break;
            use = keep;
            c0 = c1; axis = a1.Dot(aa) >= 0 ? a1 : a1 * -1;
            if (Math.Abs(a1.Dot(aa)) > 1 - 1e-10) break;
        }

        // cylindrical coordinates around the axis
        var x = Math.Abs(axis.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        x = (x - axis * x.Dot(axis)).Normalized();
        var y = axis.Cross(x);
        var cyl = pts.Select(p => { var d = p - c0; double h = d.Dot(axis); var w = d - axis * h; return (h, r: w.Length, th: Math.Atan2(w.Dot(y), w.Dot(x))); }).ToList();
        var radii = cyl.Select(q => q.r).OrderBy(r => r).ToList();
        double rMin = radii[(int)(0.01 * (radii.Count - 1))], rMax = radii[(int)(0.99 * (radii.Count - 1))];
        double depth = rMax - rMin;
        if (depth <= 1e-3 * rMax) return null;

        // in a hole the surface faces the axis
        double outward = 0;
        for (int i = 0; i < normals.Count; i++)
        {
            var d = normals[i].p - c0; var w = d - axis * d.Dot(axis);
            if (w.Length > 0) outward += areas[i] * normals[i].n.Dot(w.Normalized());
        }
        bool inner = outward < 0;

        // the crests: the points in the outer quarter of the depth of an external thread, the inner one of an internal one
        var crest = cyl.Where(q => inner ? q.r < rMin + 0.25 * depth : q.r > rMax - 0.25 * depth).ToList();
        // (thinned out at random, not every n-th: the points come in the mesh's order, and every n-th of a sorted
        // selection picked points in step with the thread, the user's selection on the screw ball gave no peak)
        if (crest.Count > 6000)
        {
            var rnd = new Random(12345);
            crest = crest.OrderBy(_ => rnd.Next()).Take(6000).ToList();
        }
        if (crest.Count < 6) return null;
        double Coherence(double p, int hand) => CoherenceOf(crest, p, hand);
        static double CoherenceOf(List<(double h, double r, double th)> set, double p, int hand)
        {
            double sc = 0, ss = 0;
            foreach (var q in set) { double ph = 2 * Math.PI * q.h / p - hand * q.th; sc += Math.Cos(ph); ss += Math.Sin(ph); }
            return Math.Sqrt(sc * sc + ss * ss) / set.Count;
        }
        double length = cyl.Max(q => q.h) - cyl.Min(q => q.h);
        // pitches from 0.05 x depth (fine) to the length (one turn), geometric steps of 0.5 %
        double pLo = Math.Max(0.5 * depth, 1e-5), pHi = Math.Max(length, 2 * pLo);
        // the peaks of the coherence over the pitch, per hand, each refined to its top (golden section between its
        // neighbouring scan steps)
        var peaks = new List<(double p, int hand, double R)>();
        foreach (int hand in new[] { 1, -1 })
        {
            var ps = new List<double>(); var rs = new List<double>();
            for (double p = pLo; p <= pHi; p *= 1.005) { ps.Add(p); rs.Add(Coherence(p, hand)); }
            for (int i = 1; i + 1 < ps.Count; i++)
            {
                if (rs[i] < rs[i - 1] || rs[i] < rs[i + 1]) continue;
                double lo = ps[i - 1], hi = ps[i + 1];
                for (int it = 0; it < 50; it++)
                {
                    double m1 = lo + (hi - lo) * 0.382, m2 = lo + (hi - lo) * 0.618;
                    if (Coherence(m1, hand) > Coherence(m2, hand)) hi = m2; else lo = m1;
                }
                double pp = (lo + hi) / 2;
                peaks.Add((pp, hand, Coherence(pp, hand)));
            }
        }
        if (peaks.Count == 0) return null;
        double best = peaks.Max(s => s.R);
        // p/2, p/3, ... line the crests up as well: the largest pitch whose peak is about as high is the true one
        var pick = peaks.Where(s => s.R >= 0.85 * best).MaxBy(s => s.p);
        double pitch = pick.p, fit = pick.R;
        // once more on the crests of the middle only (one pitch from either end left out): crest points of a shoulder
        // or chamfer at an end pull the peak aside (screw ball, wide selection: 1.467 instead of 1.495 mm)
        {
            var hc = crest.Select(q => q.h).OrderBy(h => h).ToList();
            double c1 = hc[(int)(0.01 * (hc.Count - 1))] + pitch, c2 = hc[(int)(0.99 * (hc.Count - 1))] - pitch;
            var middle = crest.Where(q => q.h >= c1 && q.h <= c2).ToList();
            if (c2 - c1 >= 2 * pitch && middle.Count >= 12)
            {
                double lo = pitch * 0.96, hi = pitch * 1.04;
                // (a coarse scan first: the window holds one peak, golden section then finds its top)
                double bestP = pitch, bestR = -1;
                for (double p = lo; p <= hi; p *= 1.001) { double r = CoherenceOf(middle, p, pick.hand); if (r > bestR) { bestR = r; bestP = p; } }
                lo = bestP / 1.001; hi = bestP * 1.001;
                for (int it = 0; it < 40; it++)
                {
                    double m1 = lo + (hi - lo) * 0.382, m2 = lo + (hi - lo) * 0.618;
                    if (CoherenceOf(middle, m1, pick.hand) > CoherenceOf(middle, m2, pick.hand)) hi = m2; else lo = m1;
                }
                pitch = (lo + hi) / 2; fit = CoherenceOf(middle, pitch, pick.hand);
            }
        }
        // hand: with θ counter-clockwise around the axis, h growing with θ (hand +1 in the phase above) is right-handed
        bool right = pick.hand > 0;

        // the diameters from the profile: every point at its place within one pitch (48 bins), the median radius per
        // bin; crest and root are the highest and lowest bin. A chamfer or end face taken along in the selection has
        // small radii at every place, but only along a short piece of the length: it does not move the medians (the
        // 1st/99th percentile of all radii did: the user's selection of the screw ball gave a core of 8.5 mm).
        // (one pitch at either end is left out: chamfers, a shoulder or the rim of a bore live there)
        // (the length: where most of the points are, the 1st to 99th percentile along the axis)
        var hs = cyl.Select(q => q.h).OrderBy(h => h).ToList();
        double h1 = hs[(int)(0.01 * (hs.Count - 1))], h99 = hs[(int)(0.99 * (hs.Count - 1))];
        length = h99 - h1;
        double hFrom = h1 + pitch, hTo = h99 - pitch;
        if (hTo - hFrom < 2 * pitch) { hFrom = h1; hTo = h99; }
        var bins = Enumerable.Range(0, 48).Select(_ => new List<double>()).ToArray();
        foreach (var q in cyl)
        {
            if (q.h < hFrom || q.h > hTo) continue;
            double s = (q.h - pick.hand * pitch * q.th / (2 * Math.PI)) / pitch;
            s -= Math.Floor(s);
            bins[Math.Min(47, (int)(s * 48))].Add(q.r);
        }
        var medianOf = bins.Select(b => b.Count >= 3 ? b.OrderBy(r => r).ElementAt(b.Count / 2) : double.NaN).ToArray();
        var medians = medianOf.Where(double.IsFinite).ToList();
        if (medians.Count >= 8) { rMin = medians.Min(); rMax = medians.Max(); depth = rMax - rMin; }
        // the root's middle (where a coil of the profile would stand): the circular mean of the bins at the root radius
        double bc = 0, bs = 0;
        for (int k = 0; k < 48; k++)
            if (double.IsFinite(medianOf[k]) && (inner ? medianOf[k] >= rMax - 0.15 * depth : medianOf[k] <= rMin + 0.15 * depth))
            { double ang = 2 * Math.PI * (k + 0.5) / 48; bc += Math.Cos(ang); bs += Math.Sin(ang); }
        double basePhase = Math.Atan2(bs, bc) / (2 * Math.PI); if (basePhase < 0) basePhase += 1;
        var crestH = crest.Select(q => q.h).OrderBy(h => h).ToList();
        double crestFrom = crestH[(int)(0.01 * (crestH.Count - 1))], crestTo = crestH[(int)(0.99 * (crestH.Count - 1))];

        // profile angle: twice the angle between the flank normals and the axis (flanks: middle half of the depth)
        var flank = new List<double>();
        for (int i = 0; i < normals.Count; i++)
        {
            var d = normals[i].p - c0; var w = d - axis * d.Dot(axis); double r = w.Length;
            if (r > rMin + 0.25 * depth && r < rMax - 0.25 * depth) flank.Add(Math.Acos(Math.Min(1, Math.Abs(normals[i].n.Dot(axis)))));
        }
        double profile = flank.Count > 0 ? 2 * flank.OrderBy(v => v).ElementAt(flank.Count / 2) * 180 / Math.PI : double.NaN;

        double major = 2 * rMax, minor = 2 * rMin;
        return new ThreadMeasure(c0, axis, major, minor, pitch, right, inner, profile, length, fit, Standard(major, pitch), x, basePhase, crestFrom, crestTo);
    }

    /// <summary>The ISO metric size with this pitch (within 4 %) whose nominal diameter is nearest to the major diameter
    /// (within 0.6 mm or 5 %: printed threads come out a little thin): "M10" for the coarse pitch, "M10×1,25" for a fine one.</summary>
    public static string? Standard(double major, double pitch)
    {
        double dMm = major * 1000, pMm = pitch * 1000;
        var c = CultureInfo.GetCultureInfo("nl-NL");
        (string name, double off)? best = null;
        foreach (var (d, pitches) in Metric)
        {
            double off = Math.Abs(d - dMm);
            if (off > Math.Max(0.6, 0.05 * d)) continue;
            for (int k = 0; k < pitches.Length; k++)
                if (Math.Abs(pitches[k] - pMm) <= 0.04 * pitches[k] && (best == null || off < best.Value.off))
                    best = (k == 0 ? string.Format(c, "M{0}", d) : string.Format(c, "M{0}×{1}", d, pitches[k]), off);
        }
        return best?.name;
    }
}
