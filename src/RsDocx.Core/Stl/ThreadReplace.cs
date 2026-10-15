using RsDocx.Core.Brep;

namespace RsDocx.Core.Stl;

/// <summary>The result of <see cref="ThreadReplace.Replace"/>: the mesh with a smooth cylinder where the thread was, and
/// the thread as a separate exact body standing on that cylinder (to be combined in DesignSpark).</summary>
/// <param name="Mesh">The new mesh (file units).</param>
/// <param name="Band">Its triangles that form the smooth cylinder (indices into <paramref name="Mesh"/>).</param>
/// <param name="Coil">The thread: the profile swept along the helix (metres).</param>
/// <param name="CoilTriangles">The coil as triangles, for display (file units).</param>
/// <param name="Removed">How many triangles of the original mesh were replaced (the selection and what it enclosed).</param>
/// <param name="NewIndexOf">For every triangle of the original mesh its index in <paramref name="Mesh"/>, or -1 when it was
/// replaced (the kept triangles keep their order: groups and recognised faces can be carried over).</param>
/// <param name="Added">The triangles that are new: the band and the skirts joining it to the rest.</param>
public sealed record ThreadReplacement(StlFile Mesh, IReadOnlyList<int> Band, BrepBody Coil, IReadOnlyList<StlTriangle> CoilTriangles, int Removed,
    IReadOnlyList<int> NewIndexOf, IReadOnlyList<int> Added);

/// <summary>
/// Replaces a measured thread by a smooth cylinder at its root (the core of a bolt, the bore of a nut) and a separate
/// exact thread coil on it. The selected triangles are removed (with small islands of unselected triangles inside them,
/// and neighbours that would fold over); the two boundary rings left are moved radially onto the root cylinder and joined
/// by a band of triangles. The coil follows the measured pitch, hand and phase, with 60° flanks over the measured depth,
/// between where the crests begin and end; its ends are flat faces through the axis (as DesignSpark's own sweep).
/// </summary>
public static class ThreadReplace
{
    public static ThreadReplacement Replace(StlFile stl, IReadOnlyCollection<int> selection, ThreadMeasure m, double scale)
    {
        if (m.Internal) throw new MeshException("Binnendraad vervangen kan nog niet (alleen buitendraad).");
        // vertices by position, joined within 1e-7 file units (as the converter welds: a seam computed twice differs in
        // the last bits)
        var ids = new Dictionary<(long, long, long), int>(); var pos = new List<Vec3>();
        int Id(Vec3 p)
        {
            var key = ((long)Math.Round(p.X * 1e7), (long)Math.Round(p.Y * 1e7), (long)Math.Round(p.Z * 1e7));
            if (!ids.TryGetValue(key, out int i)) { i = pos.Count; ids[key] = i; pos.Add(p); }
            return i;
        }
        var tris = stl.Triangles.Select(t => new[] { Id(t.A), Id(t.B), Id(t.C) }).ToList();
        var removed = new HashSet<int>(selection);

        // cylindrical frame (file units)
        var c = m.AxisPoint * (1 / scale); var a = m.Axis.Normalized(); var x = m.XAxis.Normalized(); var y = a.Cross(x);
        double rBase = m.MinorDiameter / 2 / scale;
        (double h, double r, double th) Cyl(Vec3 p) { var d = p - c; double h = d.Dot(a); var w = d - a * h; return (h, w.Length, Math.Atan2(w.Dot(y), w.Dot(x))); }
        double rTop = m.MajorDiameter / 2 / scale, slack = 0.15 * (rTop - rBase);
        // the crests as they lie in the selection (the measured outer diameter is a median: a printed thread's crests
        // reach a little beyond it in places, and those stayed, the user's selection on the screw ball: 13 rings)
        var crestR = selection.SelectMany(t => tris[t]).Select(v => Cyl(pos[v]).r).Where(r => r > rTop - slack && r <= rTop + 0.5 * (rTop - rBase)).OrderBy(r => r).ToList();
        // (and the roots likewise: the core is made at the measured root, but what is removed reaches the deepest)
        var rootR = selection.SelectMany(t => tris[t]).Select(v => Cyl(pos[v]).r).Where(r => r < rBase + slack && r >= rBase - 0.5 * (rTop - rBase)).OrderBy(r => r).ToList();
        double rCut = rootR.Count > 0 ? Math.Min(rBase, rootR[(int)(0.01 * (rootR.Count - 1))]) : rBase;
        if (crestR.Count > 0) rTop = Math.Max(rTop, crestR[(int)(0.99 * (crestR.Count - 1))]);
        // only what lies on the thread goes: a selection that runs on a little (into the shoulder, the body, the bore)
        // keeps that part
        removed.RemoveWhere(t => tris[t].Any(v => { double r = Cyl(pos[v]).r; return r < rCut - slack || r > rTop + slack; }));
        if (removed.Count == 0) throw new MeshException("Er ligt niets van de selectie op de draad.");

        // islands: unselected pieces enclosed by the selection (a few triangles it missed) go too
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (int t = 0; t < tris.Count; t++)
            for (int k = 0; k < 3; k++)
            {
                int u = tris[t][k], v = tris[t][(k + 1) % 3];
                var key = u < v ? (u, v) : (v, u);
                if (!byEdge.TryGetValue(key, out var l)) byEdge[key] = l = new();
                l.Add(t);
            }
        IEnumerable<int> Nb(int t) => Enumerable.Range(0, 3).SelectMany(k =>
        {
            int u = tris[t][k], v = tris[t][(k + 1) % 3];
            return byEdge[u < v ? (u, v) : (v, u)];
        }).Where(o => o != t);
        // islands: unselected pieces enclosed by the removed part (a few triangles the selection missed) go too; only a
        // piece that lies on the thread itself, between root and crest: not a cap that the thread alone joins to the rest
        void AbsorbIslands()
        {
            var comp = new int[tris.Count]; Array.Fill(comp, -1);
            var sizes = new List<int>();
            for (int s0 = 0; s0 < tris.Count; s0++)
            {
                if (removed.Contains(s0) || comp[s0] >= 0) continue;
                int id = sizes.Count, n = 0;
                var stack = new Stack<int>(); stack.Push(s0); comp[s0] = id;
                while (stack.TryPop(out int t)) { n++; foreach (int o in Nb(t)) if (!removed.Contains(o) && comp[o] < 0) { comp[o] = id; stack.Push(o); } }
                sizes.Add(n);
            }
            var offThread = new bool[sizes.Count];
            for (int t = 0; t < tris.Count; t++)
                if (comp[t] >= 0 && tris[t].Any(v => { double r = Cyl(pos[v]).r; return r < rCut - slack || r > rTop + slack; })) offThread[comp[t]] = true;
            int keep = Enumerable.Range(0, sizes.Count).MaxBy(i => sizes[i]);
            for (int t = 0; t < tris.Count; t++) if (comp[t] >= 0 && comp[t] != keep && !offThread[comp[t]]) removed.Add(t);
        }
        // only the one connected piece of the thread goes: stray selected triangles elsewhere stay (the user's selection on
        // the screw ball had a few on the shoulder: holes of one triangle each)
        void KeepMainPiece()
        {
            var pieceOf = new Dictionary<int, int>(); var pieceSize = new List<int>();
            foreach (int s0 in removed)
            {
                if (pieceOf.ContainsKey(s0)) continue;
                int id = pieceSize.Count, n = 0;
                var st = new Stack<int>(); st.Push(s0); pieceOf[s0] = id;
                while (st.TryPop(out int t)) { n++; foreach (int o in Nb(t)) if (removed.Contains(o) && pieceOf.TryAdd(o, id)) st.Push(o); }
                pieceSize.Add(n);
            }
            int main = Enumerable.Range(0, pieceSize.Count).MaxBy(i => pieceSize[i]);
            removed.RemoveWhere(t => pieceOf[t] != main);
        }
        AbsorbIslands();
        // gaps: unselected triangles that lie wholly on the thread (between root and crest, within the selection's height)
        // go too, a selection made by hand misses a few here and there (the user's on the screw ball: 54 rings)
        var hSel = selection.SelectMany(t => tris[t]).Select(v => Cyl(pos[v]).h).ToList();
        double hMin = hSel.Min(), hMax = hSel.Max();
        // (not flat faces across the axis: a shoulder or the top end lies within that height and radius as well)
        bool Across(int t)
        {
            var n = (pos[tris[t][1]] - pos[tris[t][0]]).Cross(pos[tris[t][2]] - pos[tris[t][0]]);
            return n.Length > 0 && Math.Abs(n.Normalized().Dot(a)) > 0.95;
        }
        for (int t = 0; t < tris.Count; t++)
            if (!removed.Contains(t) && !Across(t) && tris[t].All(v => { var q = Cyl(pos[v]); return q.r >= rCut - slack && q.r <= rTop + slack && q.h >= hMin && q.h <= hMax; }))
                removed.Add(t);
        KeepMainPiece();
        AbsorbIslands();

        var loops = Loops(tris, removed);
        // an end that does not lie in one plane across the axis (a chamfer cut the thread off: the screw ball's
        // top) gives a slanted skirt that fits nothing; the slanted triangles beyond it go too, until the end lies on a
        // flat face across the axis (the core then reaches it, the skirt lies in that face; the chamfer is made again in
        // DesignSpark, as the thread now runs to the end)
        double flatTol = 0.05 * (rTop - rBase);
        for (int round = 0; round < 200 && loops.Count == 2; round++)
        {
            var uneven = loops.Where(l => { var hs = l.Select(v => Cyl(pos[v]).h).ToList(); return hs.Max() - hs.Min() > flatTol; }).ToList();
            if (uneven.Count == 0) break;
            var at = uneven.SelectMany(l => l).ToHashSet();
            int before = removed.Count;
            for (int t = 0; t < tris.Count; t++)
                // (inward without limit: a chamfer runs on inside the core's radius, to the flat end face, which stops it)
                if (!removed.Contains(t) && !Across(t) && tris[t].Any(at.Contains) && tris[t].All(v => Cyl(pos[v]).r <= rTop + slack))
                    removed.Add(t);
            if (removed.Count == before)
            {
                if (Environment.GetEnvironmentVariable("RSTHREADDBG") == "1")
                    foreach (int t in Enumerable.Range(0, tris.Count).Where(t => !removed.Contains(t) && tris[t].Any(at.Contains)).Take(12))
                    {
                        var n = (pos[tris[t][1]] - pos[tris[t][0]]).Cross(pos[tris[t][2]] - pos[tris[t][0]]).Normalized();
                        Console.Error.WriteLine($"[thread] stuck at {t}: n·a {n.Dot(a):0.###}, r {string.Join("/", tris[t].Select(v => Cyl(pos[v]).r.ToString("0.###")))}, h {string.Join("/", tris[t].Select(v => Cyl(pos[v]).h.ToString("0.###")))}");
                    }
                break;
            }
            AbsorbIslands();
            loops = Loops(tris, removed);
        }
        if (Environment.GetEnvironmentVariable("RSTHREADDBG") == "1")
            foreach (var l in loops.OrderByDescending(l => l.Count))
            {
                var q = l.Select(v => Cyl(pos[v])).ToList();
                Console.Error.WriteLine($"[thread] loop {l.Count} vertices: h {q.Min(z => z.h):0.###}..{q.Max(z => z.h):0.###}, r {q.Min(z => z.r):0.###}..{q.Max(z => z.r):0.###}");
            }
        if (loops.Count != 2)
            throw new MeshException($"De selectie moet één ring om de as vormen (nu {loops.Count} randen). Selecteer het hele draadgedeelte, rondom.");

        // the existing boundaries stay as they are (moving them onto the cylinder folded their neighbours over); a ring of
        // new vertices on the root cylinder per boundary, at the boundary's height nearest the middle and the angles of its
        // vertices; a skirt joins each boundary to its ring (on a shoulder it lies in the shoulder's plane), the band
        // joins the two rings
        var sorted = loops.OrderBy(l => l.Average(v => Cyl(pos[v]).h)).ToList();
        var rings = new List<List<int>>();
        var ringH = new double[2];
        var added = new List<int[]>();
        double Th(int v) => Cyl(pos[v]).th;
        double Hh(int v) => Cyl(pos[v]).h;
        for (int i = 0; i < 2; i++)
        {
            var l = sorted[i];
            double hr = i == 0 ? l.Max(Hh) : l.Min(Hh);
            ringH[i] = hr;
            var ring = new List<int>();
            foreach (int v in l)
            {
                double th = Th(v);
                int id = Id(c + a * hr + (x * Math.Cos(th) + y * Math.Sin(th)) * rBase); // (joins one already there)
                if (ring.Count == 0 || ring[^1] != id) ring.Add(id);
            }
            if (ring.Count > 1 && ring[0] == ring[^1]) ring.RemoveAt(ring.Count - 1);
            rings.Add(ring);
            added.AddRange(Zip(l, ring, Th, Hh));
        }
        int skirtCount = added.Count;
        added.AddRange(Zip(rings[0], rings[1], Th, Hh));
        // (a vertex twice: where the boundary already lay on the cylinder)
        var keepAdded = Enumerable.Range(0, added.Count).Where(k => added[k].Distinct().Count() == 3).ToList();
        var bandIdx = keepAdded.Where(k => k >= skirtCount).ToHashSet();

        // orientation: each new triangle runs along an existing boundary edge against the kept triangle there, and
        // against its new neighbours; what touches neither (a band far from both) faces outward
        var kept = tris.Where((_, t) => !removed.Contains(t)).ToList();
        var halfKept = new HashSet<(int, int)>();
        foreach (var t in kept) for (int k = 0; k < 3; k++) halfKept.Add((t[k], t[(k + 1) % 3]));
        var newT = keepAdded.Select(k => added[k]).ToList();
        var sign = new int[newT.Count];
        var byE = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < newT.Count; i++)
            for (int k = 0; k < 3; k++)
            {
                int u = newT[i][k], v = newT[i][(k + 1) % 3];
                var key = u < v ? (u, v) : (v, u);
                if (!byE.TryGetValue(key, out var l)) byE[key] = l = new();
                l.Add(i);
            }
        var queue = new Queue<int>();
        for (int i = 0; i < newT.Count; i++)
            for (int k = 0; k < 3; k++)
            {
                int u = newT[i][k], v = newT[i][(k + 1) % 3];
                if (halfKept.Contains((v, u))) { sign[i] = 1; break; }
                if (halfKept.Contains((u, v))) { sign[i] = -1; break; }
            }
        for (int i = 0; i < newT.Count; i++) if (sign[i] != 0) queue.Enqueue(i);
        if (queue.Count == 0) throw new MeshException("De nieuwe kern sluit niet op de rest aan.");
        while (queue.TryDequeue(out int i))
        {
            var t = sign[i] > 0 ? newT[i] : new[] { newT[i][0], newT[i][2], newT[i][1] };
            for (int k = 0; k < 3; k++)
            {
                int u = t[k], v = t[(k + 1) % 3];
                foreach (int j2 in byE[u < v ? (u, v) : (v, u)])
                {
                    if (j2 == i || sign[j2] != 0) continue;
                    // the neighbour must run v -> u
                    var o = newT[j2];
                    bool same = Enumerable.Range(0, 3).Any(q => o[q] == u && o[(q + 1) % 3] == v);
                    sign[j2] = same ? -1 : 1;
                    queue.Enqueue(j2);
                }
            }
        }
        var newTris = new List<int[]>(kept);
        var newIndexOf = new int[tris.Count];
        for (int t = 0, k = 0; t < tris.Count; t++) newIndexOf[t] = removed.Contains(t) ? -1 : k++;
        int firstNew = newTris.Count;
        var band = new List<int>();
        for (int i = 0; i < newT.Count; i++)
        {
            var t = sign[i] >= 0 ? newT[i] : new[] { newT[i][0], newT[i][2], newT[i][1] };
            if (bandIdx.Contains(keepAdded[i])) band.Add(newTris.Count);
            newTris.Add(t);
        }
        var mesh = new StlFile
        {
            Header = stl.Header, Binary = stl.Binary,
            Triangles = newTris.Select(t => new StlTriangle(pos[t[0]], pos[t[1]], pos[t[2]])).ToList(),
        };

        // the coil
        double p = m.Pitch, depth = (m.MajorDiameter - m.MinorDiameter) / 2;
        double flank = Math.Tan(m.ProfileAngleDeg is > 20 and < 100 ? m.ProfileAngleDeg / 2 * Math.PI / 180 : Math.PI / 6);
        double wc = p / 8, wb = Math.Min(wc + 2 * depth * flank, 0.95 * p);
        var (coil, coilTris) = Coil(m, depth, wb, wc, coreFrom: ringH[0] * scale, coreTo: ringH[1] * scale);
        return new ThreadReplacement(mesh, band, coil, coilTris.Select(t => new StlTriangle(t.Item1 * (1 / scale), t.Item2 * (1 / scale), t.Item3 * (1 / scale))).ToList(), removed.Count, newIndexOf, Enumerable.Range(firstNew, newTris.Count - firstNew).ToList());
    }

    /// <summary>
    /// The coil of the measured thread: base on the root cylinder, its lower base corner where the root flat ends
    /// (BasePhase + half the root flat), from where the crests begin to where they end.
    /// </summary>
    /// <param name="coreFrom">Where the smooth core begins along the axis (metres from AxisPoint), if known.</param>
    /// <param name="coreTo">Where it ends.</param>
    public static (BrepBody body, List<(Vec3, Vec3, Vec3)> triangles) Coil(ThreadMeasure m, double depth, double wb, double wc,
        double? coreFrom = null, double? coreTo = null)
    {
        double p = m.Pitch, s = m.RightHanded ? 1 : -1;
        var a = m.Axis.Normalized(); var x = m.XAxis.Normalized(); var y = a.Cross(x);
        double rIn = m.MinorDiameter / 2, rOut = rIn + depth;
        // the profile's lower base corner at θ = 0: h0 + k·p
        double h0 = (m.BasePhase + (1 - wb / p) / 2) * p;
        // where the coil runs: from the first to the last full crest; at an end where those stop more than half a pitch
        // short of the core's end (a chamfer cut the thread off: the screw ball's top), on to the core's end
        // (the user chamfers it again in DesignSpark, which cuts the thread as the original did)
        double from = m.CrestFrom, to = m.CrestTo;
        if (coreFrom is double cf && from - cf > 0.5 * p) from = cf;
        if (coreTo is double ct && ct - to > 0.5 * p) to = ct;
        double turns = Math.Max(0.25, (to - from - wb) / p);
        // start where that corner is at (or just above) the first crest: for a left-handed thread the coil climbs while θ
        // decreases, so it starts at the top instead
        double hStart = s > 0 ? from : to - wb;
        double k = Math.Ceiling((hStart - h0) / p - 1e-9);
        double hc = h0 + k * p;                        // corner height at θ = 0 nearest above hStart
        double phi = -s * (hc - hStart) / p * 2 * Math.PI; // turning back to reach hStart exactly
        var xs = x * Math.Cos(phi) + y * Math.Sin(phi);
        var start = m.AxisPoint + a * hStart;
        var body = ThreadShapes.Coil(start, a, xs, rIn, rOut, wb, wc, p, m.RightHanded, turns);
        return (body, ThreadShapes.CoilTriangles(start, a, xs, rIn, rOut, wb, wc, p, m.RightHanded, turns));
    }

    /// <summary>The boundary loops of the triangles that are not removed (each as its vertices in order).</summary>
    private static List<List<int>> Loops(List<int[]> tris, HashSet<int> removed)
    {
        var half = new Dictionary<(int, int), int>();
        for (int t = 0; t < tris.Count; t++)
            if (!removed.Contains(t))
                for (int k = 0; k < 3; k++) half[(tris[t][k], tris[t][(k + 1) % 3])] = t;
        var next = new Dictionary<int, List<int>>();
        foreach (var (u, v) in half.Keys)
            if (!half.ContainsKey((v, u))) { if (!next.TryGetValue(u, out var l)) next[u] = l = new(); l.Add(v); }
        var loops = new List<List<int>>();
        var used = new HashSet<(int, int)>();
        foreach (var (u0, outs) in next)
            foreach (int v0 in outs)
            {
                if (used.Contains((u0, v0))) continue;
                var loop = new List<int>(); int u = u0, v = v0;
                while (used.Add((u, v)))
                {
                    loop.Add(u);
                    u = v;
                    if (!next.TryGetValue(u, out var o)) break;
                    v = o.FirstOrDefault(w => !used.Contains((u, w)), -1);
                    if (v < 0) break;
                }
                loops.Add(loop);
            }
        return loops;
    }

    /// <summary>
    /// Triangles between two rings around the axis: both taken counter-clockwise (by angle), started at the same angle,
    /// and zipped: each step advances on the ring whose next vertex has the smaller (unwrapped) angle.
    /// </summary>
    private static List<int[]> Zip(List<int> l0, List<int> l1, Func<int, double> theta, Func<int, double> height)
    {
        List<int> Ccw(List<int> l)
        {
            // turning direction: the sum of angle steps
            double turn = 0;
            for (int i = 0; i < l.Count; i++) turn += Wrap(theta(l[(i + 1) % l.Count]) - theta(l[i]));
            var r = turn >= 0 ? new List<int>(l) : Enumerable.Reverse(l).ToList();
            // start at the vertex nearest angle 0 (the same place on both rings)
            int st = Enumerable.Range(0, r.Count).MinBy(i => Math.Abs(Wrap(theta(r[i]))));
            return r.Skip(st).Concat(r.Take(st)).ToList();
        }
        static double Wrap(double d) { while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; return d; }
        List<double> Unwrapped(List<int> l)
        {
            var res = new List<double> { Wrap(theta(l[0])) };
            for (int i = 1; i <= l.Count; i++) res.Add(res[^1] + Wrap(theta(l[i % l.Count]) - theta(l[i - 1])));
            return res;
        }
        var lo = Ccw(l0); var hi = Ccw(l1);
        if (height(lo[0]) > height(hi[0])) (lo, hi) = (hi, lo);
        var t0 = Unwrapped(lo); var t1 = Unwrapped(hi);
        var res = new List<int[]>();
        int i0 = 0, i1 = 0;
        while (i0 < lo.Count || i1 < hi.Count)
        {
            bool adv0 = i1 >= hi.Count || (i0 < lo.Count && t0[i0 + 1] <= t1[i1 + 1]);
            if (adv0) { res.Add([lo[i0], lo[(i0 + 1) % lo.Count], hi[i1 % hi.Count]]); i0++; }
            else { res.Add([hi[i1], lo[i0 % lo.Count], hi[(i1 + 1) % hi.Count]]); i1++; }
        }
        return res;
    }
}
