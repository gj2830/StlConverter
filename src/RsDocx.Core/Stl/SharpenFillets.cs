namespace RsDocx.Core.Stl;

/// <summary>
/// Replaces small roundings (fillets and chamfers narrower than a given size) by sharp edges, for a print where they
/// cannot be made anyway (an FDM nozzle is 0.4 mm). Voetstuk: 95 % of its 163000 triangles lie in 0.2 mm fillets along
/// almost every edge, 22 % of the area; their corner blends are neither cylinder nor torus and recognition ran into
/// conflict after conflict.
/// <para>Wide flat pieces of the mesh are the faces that stay. A vertex of a narrow piece is moved onto the line where the
/// two nearest faces (along the surface) meet, or onto the point where three meet at a corner. Every target lies on
/// both faces, so their triangles stay in their planes and the fillet's triangles collapse onto the edge; the repair
/// after it welds them and flips the flat ones away. A vertex with only one face nearby (a hole, a rounded body) stays.</para>
/// </summary>
public static class SharpenFillets
{
    private static readonly bool Debug = Environment.GetEnvironmentVariable("RSDEBUG") == "1";

    /// <summary>Faces at less than this angle (degrees) to each other have no edge between them: a gentle blend stays, and
    /// two strips of one wider rounding are no faces with a rounding between them (they are 7.5 degrees apart on a box
    /// rounded by 1 mm in 12 strips, and over so small an angle the radius estimate is mostly tessellation noise).</summary>
    private const double ParallelDeg = 30;

    /// <summary>Where each vertex goes (null: it stays).</summary>
    /// <param name="verts">Vertex positions (an index no triangle uses is ignored).</param>
    /// <param name="maxSize">Roundings narrower than this (STL units) become sharp; the search along the surface reaches
    /// twice as far, and no vertex moves further than that.</param>
    internal static Vec3?[] Targets(IReadOnlyList<Vec3> verts, IReadOnlyList<int[]> tris, double maxSize)
    {
        var edgeTris = new Dictionary<(int, int), List<int>>();
        var vertTris = new List<int>[verts.Count];
        for (int i = 0; i < verts.Count; i++) vertTris[i] = new();
        for (int t = 0; t < tris.Count; t++)
            for (int k = 0; k < 3; k++)
            {
                int a = tris[t][k], b = tris[t][(k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (!edgeTris.TryGetValue(key, out var l)) edgeTris[key] = l = new();
                l.Add(t);
                vertTris[a].Add(t);
            }
        var normal = tris.Select(t => (verts[t[1]] - verts[t[0]]).Cross(verts[t[2]] - verts[t[0]])).ToArray();

        // flat pieces: neighbours across an edge within 0.1 degrees of the seed and on its plane
        double cosFlat = Math.Cos(0.1 * Math.PI / 180), planeTol = maxSize * 2e-3;
        var regionOf = Enumerable.Repeat(-1, tris.Count).ToArray();
        var planes = new List<(Vec3 n, double d, bool wide)>();
        var pieces = new List<List<int>>();
        for (int seed = 0; seed < tris.Count; seed++)
        {
            if (regionOf[seed] >= 0 || normal[seed].Length == 0) continue;
            int id = planes.Count;
            var n0 = normal[seed].Normalized(); var p0 = verts[tris[seed][0]];
            var region = new List<int> { seed };
            regionOf[seed] = id;
            for (int qi = 0; qi < region.Count; qi++)
            {
                var t = tris[region[qi]];
                for (int k = 0; k < 3; k++)
                {
                    int a = t[k], b = t[(k + 1) % 3];
                    foreach (int nb in edgeTris[a < b ? (a, b) : (b, a)])
                    {
                        if (regionOf[nb] >= 0 || normal[nb].Length == 0 || normal[nb].Normalized().Dot(n0) < cosFlat) continue;
                        if (tris[nb].Any(v => Math.Abs(n0.Dot(verts[v] - p0)) > planeTol)) continue;
                        regionOf[nb] = id; region.Add(nb);
                    }
                }
            }
            var n = new Vec3(0, 0, 0);
            foreach (int t in region) n += normal[t];
            n = n.Normalized();
            var vs = region.SelectMany(t => tris[t]).Distinct().ToList();
            planes.Add((n, vs.Average(v => n.Dot(verts[v])), false));
            pieces.Add(region);
        }

        // A rounding turns fast: across a good part of its boundary (30 %: the first strip, next to the face it is
        // tangent to, bends only half a step there) the surface bends by more than 1/maxSize, the angle to the
        // neighbouring piece over the distance between them (twice the distance from the nearer piece's centre to the
        // edge: half a strip's width, a third of a single facet's height). Only the nearer piece turns there: a wide face
        // next to the first strip of its rounding does not. A strip of a curved wall (voetstuk: 0.3 mm wide, 5 mm high,
        // round the arms) bends that fast only at its short ends, where its own rounding starts; it stays a face.
        var centre = new Vec3[pieces.Count];
        var boundaryLen = new double[pieces.Count];
        var areaOf = new double[pieces.Count];
        for (int id = 0; id < pieces.Count; id++)
        {
            double area = 0;
            var c = new Vec3(0, 0, 0);
            foreach (int t in pieces[id])
            {
                double at = normal[t].Length / 2;
                area += at;
                c += (verts[tris[t][0]] + verts[tris[t][1]] + verts[tris[t][2]]) * (at / 3);
                for (int k = 0; k < 3; k++)
                {
                    int a = tris[t][k], b = tris[t][(k + 1) % 3];
                    if (edgeTris[a < b ? (a, b) : (b, a)].Any(o => regionOf[o] != id)) boundaryLen[id] += (verts[b] - verts[a]).Length;
                }
            }
            centre[id] = c * (1 / area);
            areaOf[id] = area;
        }
        double ToLine(Vec3 p, Vec3 a, Vec3 b) { var d = (b - a).Normalized(); var q = p - a; return (q - d * q.Dot(d)).Length; }
        var fastLen = new double[pieces.Count];
        foreach (var ((a, b), ts) in edgeTris)
        {
            if (ts.Count != 2) continue;
            int ra = regionOf[ts[0]], rb = regionOf[ts[1]];
            if (ra < 0 || rb < 0 || ra == rb) continue;
            double angle = Math.Acos(Math.Clamp(planes[ra].n.Dot(planes[rb].n), -1, 1));
            double da = ToLine(centre[ra], verts[a], verts[b]), db = ToLine(centre[rb], verts[a], verts[b]);
            if (angle / (2 * Math.Min(da, db)) <= 1 / maxSize) continue;
            double len = (verts[b] - verts[a]).Length;
            if (da <= db || da <= maxSize / 2) fastLen[ra] += len;
            if (db <= da || db <= maxSize / 2) fastLen[rb] += len;
        }
        // Seen over the whole strip as well: the turn between its neighbours on either side over its width (a strip close
        // to where a rounding touches its face turns little against each neighbour on its own; voetstuk's arms)
        var shared = new Dictionary<(int, int), double>();
        foreach (var ((a, b), ts) in edgeTris)
        {
            if (ts.Count != 2) continue;
            int ra = regionOf[ts[0]], rb = regionOf[ts[1]];
            if (ra < 0 || rb < 0 || ra == rb) continue;
            double len = (verts[b] - verts[a]).Length;
            shared[(ra, rb)] = shared.GetValueOrDefault((ra, rb)) + len;
            shared[(rb, ra)] = shared.GetValueOrDefault((rb, ra)) + len;
        }
        var sides = shared.Where(kv => kv.Value >= 0.2 * boundaryLen[kv.Key.Item1]).GroupBy(kv => kv.Key.Item1, kv => kv.Key.Item2)
            .ToDictionary(g => g.Key, g => g.ToList());
        bool TurnsAcross(int id)
        {
            if (!sides.TryGetValue(id, out var nb) || nb.Count < 2) return false;
            double turn = 0;
            for (int i = 0; i < nb.Count; i++)
                for (int j = i + 1; j < nb.Count; j++)
                    turn = Math.Max(turn, Math.Acos(Math.Clamp(planes[nb[i]].n.Dot(planes[nb[j]].n), -1, 1)));
            double width = 2 * areaOf[id] / boundaryLen[id];
            return width <= maxSize && turn / (2 * width) > 1 / maxSize;
        }
        for (int id = 0; id < pieces.Count; id++)
            // (and a face has some size: a few skewed facets at the corner of a rounding can look flat between their
            // neighbours, and an edge between two of them is no edge of the part)
            planes[id] = planes[id] with { wide = fastLen[id] <= 0.3 * boundaryLen[id] && !TurnsAcross(id) && areaOf[id] >= maxSize * maxSize / 4 };
        // What lies between the faces: strips of narrow pieces joined by their edges (never across a face), and the
        // edges where each strip meets a face. A vertex of a strip only looks at the faces its own strip meets, at its
        // distance from where it meets them (that edge itself: a rounding tessellated as needles, 43 mm long and 0.05 mm
        // wide on voetstuk, is 0.9 mm across along its edges). Before, each vertex took the nearest faces it could
        // reach by their planes; on voetstuk the plane of a large wall further on, or the other piece of a wall cut in
        // two, put neighbouring vertices on different edges, and 10000 facets turned over.
        bool Rounding(int t) => regionOf[t] >= 0 && !planes[regionOf[t]].wide;
        var strip = Enumerable.Repeat(-1, tris.Count).ToArray();
        int strips = 0;
        for (int s0 = 0; s0 < tris.Count; s0++)
        {
            if (strip[s0] >= 0 || !Rounding(s0)) continue;
            var stack = new Stack<int>([s0]);
            strip[s0] = strips;
            while (stack.TryPop(out int t))
                for (int k = 0; k < 3; k++)
                {
                    int a = tris[t][k], b = tris[t][(k + 1) % 3];
                    foreach (int o in edgeTris[a < b ? (a, b) : (b, a)])
                        if (strip[o] < 0 && Rounding(o)) { strip[o] = strips; stack.Push(o); }
                }
            strips++;
        }
        double reach = 2 * maxSize;
        var cells = new Dictionary<(long, long, long), List<(int strip, int face, Vec3 a, Vec3 b)>>();
        (long, long, long) Cell(Vec3 p) => ((long)Math.Floor(p.X / reach), (long)Math.Floor(p.Y / reach), (long)Math.Floor(p.Z / reach));
        foreach (var ((a, b), ts) in edgeTris)
        {
            if (ts.Count != 2) continue;
            int round = Rounding(ts[0]) ? ts[0] : Rounding(ts[1]) ? ts[1] : -1, face = round == ts[0] ? ts[1] : ts[0];
            if (round < 0 || Rounding(face) || regionOf[face] < 0) continue;
            var seg = (strip[round], regionOf[face], verts[a], verts[b]);
            // (in every cell along it: a needle's edge is long)
            int steps = (int)Math.Ceiling((verts[b] - verts[a]).Length / reach) + 1;
            var keys = new HashSet<(long, long, long)>();
            for (int i = 0; i <= steps; i++) keys.Add(Cell(verts[a] + (verts[b] - verts[a]) * ((double)i / steps)));
            foreach (var key in keys) { if (!cells.TryGetValue(key, out var l)) cells[key] = l = new(); l.Add(seg); }
        }

        // Faces joined directly at a gentle bend are one smooth surface (the strips of a rounding wider than the limit, the
        // strips of a curved wall); an edge lies only between two different ones. Otherwise the facets at the corner of
        // a wide rounding, between two of its strips, were taken for a small rounding and collapsed (a box rounded by
        // 0.6 mm with a limit of 0.5).
        var smooth = Enumerable.Range(0, pieces.Count).ToArray();
        int Smooth(int x) { while (smooth[x] != x) x = smooth[x] = smooth[smooth[x]]; return x; }
        double cosParallel = Math.Cos(ParallelDeg * Math.PI / 180);
        foreach (var (_, ts) in edgeTris)
        {
            if (ts.Count != 2) continue;
            int ra = regionOf[ts[0]], rb = regionOf[ts[1]];
            if (ra >= 0 && rb >= 0 && ra != rb && planes[ra].wide && planes[rb].wide && planes[ra].n.Dot(planes[rb].n) >= cosParallel)
                smooth[Smooth(ra)] = Smooth(rb);
        }

        // where each vertex would go, and the radius of its rounding seen from there: a vertex where a rounding of
        // radius r touches its face moves r x tan(half the turn) onto the edge
        var candidates = Enumerable.Range(0, verts.Count).Where(v => vertTris[v].Any(Rounding)).ToArray();
        var target = new Vec3?[verts.Count];
        var radius = new double[verts.Count];
        var pairOf = new (int, int)[verts.Count];
        var touches = new bool[verts.Count];
        Parallel.ForEach(candidates, v =>
        {
            var x = verts[v];
            var own = vertTris[v].Where(Rounding).Select(t => strip[t]).ToHashSet();
            var faceDist = new Dictionary<int, double>();
            var (cx, cy, cz) = Cell(x);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var segs))
                            foreach (var (st, face, a, b) in segs)
                            {
                                if (!own.Contains(st)) continue;
                                double d = ToSegment(x, a, b);
                                if (d <= reach && (!faceDist.TryGetValue(face, out double old) || d < old)) faceDist[face] = d;
                            }
            var byDist = faceDist.OrderBy(kv => kv.Value).ToList();
            var near = byDist.Select(kv => planes[kv.Key]).ToList();
            if (near.Count < 2) return;
            var p1 = near[0];
            int i2 = byDist.FindIndex(kv => Math.Abs(planes[kv.Key].n.Dot(p1.n)) < cosParallel && Smooth(kv.Key) != Smooth(byDist[0].Key));
            if (i2 < 0) return;
            var p2 = near[i2];
            // a third face at a corner: the vertex also lies within the rounding of that one
            var p3 = byDist.Skip(i2 + 1).Where(kv => Smooth(kv.Key) != Smooth(byDist[0].Key) && Smooth(kv.Key) != Smooth(byDist[i2].Key))
                .Select(kv => planes[kv.Key]).Where(p => Math.Abs(p.n.Dot(p1.n)) < cosParallel && Math.Abs(p.n.Dot(p2.n)) < cosParallel
                && Math.Abs(p1.n.Cross(p2.n).Normalized().Dot(p.n)) > Math.Sin(ParallelDeg * Math.PI / 180)
                && Math.Abs(p.n.Dot(x) - p.d) <= maxSize).Select(p => ((Vec3 n, double d)?)(p.n, p.d)).FirstOrDefault();
            int k1 = byDist[0].Key, k2 = byDist[i2].Key;
            pairOf[v] = k1 < k2 ? (k1, k2) : (k2, k1);
            touches[v] = byDist[0].Value <= 1e-9 * maxSize;
            target[v] = p3 is var (n3, d3) ? Corner((p1.n, p1.d), (p2.n, p2.d), (n3, d3)) : OnLine((p1.n, p1.d), (p2.n, p2.d), x);
            // (at a corner too from the edge of the two nearest faces: the move to the corner itself is longer)
            radius[v] = (OnLine((p1.n, p1.d), (p2.n, p2.d), x) - x).Length / Math.Tan(Math.Acos(Math.Clamp(p1.n.Dot(p2.n), -1, 1)) / 2);
        });

        // One decision per rounding, the one between two given faces: sharp as a whole when nearly all (98 %) of its
        // radius estimates where it touches its faces are within the limit, otherwise left as it is. There the estimate is
        // the true radius; elsewhere it is less, and a rounding of 0.6 mm on a box passed a limit of 0.5 that way (only
        // when a rounding touches no face, all its estimates count). Per vertex, near the limit, noise made half a rounding sharp and broke the mesh (a box
        // rounded by 0.4 mm); per connected rounding, the small ones on voetstuk went with the larger ones they run into.
        // A vertex at a corner goes with the rounding of its two nearest faces.
        var sharp = candidates.Where(v => target[v] != null).GroupBy(v => pairOf[v])
            .Where(g =>
            {
                var rs = (g.Any(v => touches[v]) ? g.Where(v => touches[v]) : g).Select(v => radius[v]).Order().ToList();
                return rs[(int)(rs.Count * 0.98)] <= maxSize;
            })
            .Select(g => g.Key).ToHashSet();
        var moved = new Vec3?[verts.Count];
        foreach (int v in candidates)
        {
            if (target[v] is not Vec3 q || !sharp.Contains(pairOf[v])) continue;
            // (a vertex already on the edge, at most a rounding error off it, stays as it is)
            double move = (q - verts[v]).Length;
            if (move > maxSize * 1e-7 && move <= reach) moved[v] = q;
        }

        // Where a facet would turn over, its moved corners stay where they were, until none does: there the rounding
        // stays (partly), but the surface never folds onto itself. Facets that collapse onto the edge have no direction
        // and are left to the repair: lower than 5 % of the limit (voetstuk: its three corners on the edge but 3 µm out
        // of line, where a wall in two pieces 0.8 degrees apart meets it, and 10000 such needles were taken for turned
        // over facets).
        Vec3 At(int v) => moved[v] ?? verts[v];
        bool TurnsOver(int t)
        {
            var tv = tris[t];
            Vec3 a = At(tv[0]), b = At(tv[1]), c = At(tv[2]);
            var n = (b - a).Cross(c - a);
            double longest = Math.Max((b - a).Length, Math.Max((c - b).Length, (a - c).Length));
            return longest > 0 && n.Length / longest > 0.05 * maxSize && n.Dot(normal[t]) < 0;
        }
        if (Debug)
        {
            int noFaces = candidates.Count(v => target[v] == null), rejected = candidates.Count(v => target[v] != null && !sharp.Contains(pairOf[v]));
            Console.Error.WriteLine($"[sharpen] {strips} strips, {candidates.Length} vertices in them: {noFaces} without two faces, {rejected} in a rounding too wide, {moved.Count(m => m != null)} to move");
            string Kind(int v) => moved[v] != null ? "moved" : !candidates.Contains(v) ? "face" : target[v] == null ? "nofaces" : !sharp.Contains(pairOf[v]) ? "wide" : "small";
            var kinds = new Dictionary<string, (int n, int t)>();
            for (int t = 0; t < tris.Count; t++)
            {
                var tv = tris[t];
                if (moved[tv[0]] == null && moved[tv[1]] == null && moved[tv[2]] == null) continue;
                if (!TurnsOver(t)) continue;
                string k = string.Join("+", tv.Select(Kind).Order());
                kinds[k] = (kinds.GetValueOrDefault(k).n + 1, kinds.TryGetValue(k, out var e) ? e.t : t);
            }
            foreach (var (k, (c, t)) in kinds.OrderByDescending(x => x.Value.n).Take(6))
                Console.Error.WriteLine($"[sharpen]   turned over, corners {k}: {c}, e.g. " + string.Join(" ", tris[t].Select(v => FormattableString.Invariant($"v{v}({verts[v].X:0.###},{verts[v].Y:0.###},{verts[v].Z:0.###})"))));
        }
        for (int round = 0; round < 50; round++)
        {
            int back = 0;
            for (int t = 0; t < tris.Count; t++)
            {
                var tv = tris[t];
                if (moved[tv[0]] == null && moved[tv[1]] == null && moved[tv[2]] == null) continue;
                if (!TurnsOver(t)) continue;
                foreach (int v in tv) if (moved[v] != null) { moved[v] = null; back++; }
            }
            if (back == 0) break;
        }
        return moved;
    }

    private static double ToSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        var ab = b - a;
        double l2 = ab.Dot(ab), t = l2 > 0 ? Math.Clamp((p - a).Dot(ab) / l2, 0, 1) : 0;
        return (p - (a + ab * t)).Length;
    }

    /// <summary>The point of the line where two planes (n.x = d) meet that is nearest to x.</summary>
    internal static Vec3 OnLine((Vec3 n, double d) a, (Vec3 n, double d) b, Vec3 x)
    {
        var dir = a.n.Cross(b.n).Normalized();
        // x moved onto both planes, keeping its position along the line: three planes, the third through x across the line
        return Corner(a, b, (dir, dir.Dot(x)));
    }

    /// <summary>The point where three planes meet (Cramer's rule).</summary>
    internal static Vec3 Corner((Vec3 n, double d) a, (Vec3 n, double d) b, (Vec3 n, double d) c)
    {
        double det = a.n.Dot(b.n.Cross(c.n));
        return (b.n.Cross(c.n) * a.d + c.n.Cross(a.n) * b.d + a.n.Cross(b.n) * c.d) * (1 / det);
    }
}
