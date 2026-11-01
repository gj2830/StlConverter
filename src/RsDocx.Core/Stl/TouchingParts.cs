namespace RsDocx.Core.Stl;

/// <summary>
/// Parts touching along an edge (lego_Display: a plate against a box with the same outline, four triangles on each edge
/// of the contact face, as a slicer gets them from a model built of parts): the triangles round such an edge are paired
/// into the parts they close, and each part gets its own copy of the edge's corners, at the same position. Used by the
/// repair (refusing triangles there tore a face out of both parts, and the fill made a flat piece of it) and by the
/// conversion, which then makes separate bodies of them.
/// </summary>
public static class TouchingParts
{
    /// <summary>Splits every edge of more than two triangles that can be paired; <paramref name="copy"/> makes a new
    /// vertex at the position of the given one. Returns the number of edges split.</summary>
    public static int Split(Func<int, Vec3> pos, IList<int[]> tris, Func<int, int> copy)
    {
        var onEdge = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < tris.Count; i++)
            for (int k = 0; k < 3; k++)
            {
                var e = E(tris[i][k], tris[i][(k + 1) % 3]);
                if (!onEdge.TryGetValue(e, out var l)) onEdge[e] = l = new List<int>(2);
                l.Add(i);
            }
        var paired = new Dictionary<(int, int), List<(int, int)>>();
        foreach (var (e, l) in onEdge)
            if (l.Count > 2 && l.Count % 2 == 0 && Pair(pos, tris, e.Item1, e.Item2, l) is { } p) paired[e] = p;
        if (paired.Count == 0) return 0;

        // corners (triangle * 3 + k) joined across the edges that hold two triangles of one part
        var root = Enumerable.Range(0, 3 * tris.Count).ToArray();
        int Find(int x) { while (root[x] != x) x = root[x] = root[root[x]]; return x; }
        int Slot(int t, int v) => 3 * t + Array.IndexOf(tris[t], v);
        void Join(int t1, int t2, (int, int) e)
        {
            root[Find(Slot(t1, e.Item1))] = Find(Slot(t2, e.Item1));
            root[Find(Slot(t1, e.Item2))] = Find(Slot(t2, e.Item2));
        }
        foreach (var (e, l) in onEdge)
            if (l.Count == 2) Join(l[0], l[1], e);
            else if (paired.TryGetValue(e, out var p)) foreach (var (t1, t2) in p) Join(t1, t2, e);

        // every part round a corner of a split edge after the first gets a copy of that corner
        var split = paired.Keys.SelectMany(e => new[] { e.Item1, e.Item2 }).ToHashSet();
        var copyOf = new Dictionary<int, int>();
        var kept = new HashSet<int>();
        for (int t = 0; t < tris.Count; t++)
            for (int k = 0; k < 3; k++)
            {
                int v = tris[t][k];
                if (!split.Contains(v)) continue;
                int r = Find(3 * t + k);
                if (!copyOf.TryGetValue(r, out int nv)) copyOf[r] = nv = kept.Add(v) ? v : copy(v);
                tris[t][k] = nv;
            }
        return paired.Count;
    }

    private static (int, int) E(int a, int b) => a < b ? (a, b) : (b, a);

    private static bool Forward(int[] t, int a, int b) => (t[0] == a && t[1] == b) || (t[1] == a && t[2] == b) || (t[2] == a && t[0] == b);

    /// <summary>
    /// The triangles round edge a-b in pairs that enclose one part: from each triangle, turning about the edge to the
    /// side behind it (inside the part), the first triangle met. A triangle lying on it (the touching face of the other
    /// part) does not count. Null when the pairing is not mutual or not of opposite orientation.
    /// </summary>
    private static List<(int, int)>? Pair(Func<int, Vec3> pos, IList<int[]> tris, int a, int b, List<int> around)
    {
        Vec3 pa = pos(a), d = (pos(b) - pa).Normalized();
        var fwd = around.Select(t => Forward(tris[t], a, b)).ToArray();
        var dir = around.Select(t =>
        {
            int c = tris[t].First(v => v != a && v != b);
            var w = pos(c) - pa;
            return (w - d * w.Dot(d)).Normalized();
        }).ToArray();
        Vec3 u0 = dir[0], v0 = d.Cross(u0);
        var angle = dir.Select(w => Math.Atan2(w.Dot(v0), w.Dot(u0))).ToArray();
        // a triangle with a-b forward has its normal along d x w (turning up the angle): its inside lies the other way.
        // Two triangles met at the same angle are the touching faces of two parts: the one running the other way closes
        // this part.
        const double same = 1e-7;
        int Next(int i)
        {
            int best = -1; double bestTurn = double.MaxValue;
            for (int j = 0; j < around.Count; j++)
            {
                if (j == i) continue;
                double turn = (fwd[i] ? angle[i] - angle[j] : angle[j] - angle[i]) % (2 * Math.PI);
                if (turn < 0) turn += 2 * Math.PI;
                if (turn <= same || turn >= 2 * Math.PI - same) continue;
                if (turn < bestTurn - same || (turn < bestTurn + same && fwd[j] != fwd[i] && fwd[best] == fwd[i])) { bestTurn = turn; best = j; }
            }
            return best;
        }
        var pairs = new List<(int, int)>();
        for (int i = 0; i < around.Count; i++)
        {
            int j = Next(i);
            if (j < 0 || Next(j) != i || fwd[i] == fwd[j]) return null;
            if (i < j) pairs.Add((around[i], around[j]));
        }
        return pairs;
    }
}
