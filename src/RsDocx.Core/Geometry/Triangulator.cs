namespace RsDocx.Core.Geometry;

/// <summary>
/// Triangulates a polygon with holes in the plane (ear clipping, holes joined to the outer loop by bridges, after
/// D. Eberly, "Triangulation by Ear Clipping").
/// </summary>
public static class Triangulator
{
    /// <summary>
    /// Triangles (indices into the loops' points, numbered loop after loop) of the region inside the first loop and outside
    /// the others. The loops may run either way round; the triangles run counter-clockwise.
    /// </summary>
    public static List<(int A, int B, int C)> Triangulate(IReadOnlyList<IReadOnlyList<(double X, double Y)>> loops)
    {
        var pts = new List<(double X, double Y)>();
        var rings = new List<List<int>>();
        foreach (var l in loops)
        {
            var ring = new List<int>();
            foreach (var p in l) { ring.Add(pts.Count); pts.Add(p); }
            if (ring.Count >= 3) rings.Add(ring);
        }
        var result = new List<(int, int, int)>();
        if (rings.Count == 0) return result;
        double Area(List<int> r)
        {
            double a = 0;
            for (int i = 0; i < r.Count; i++) { var p = pts[r[i]]; var q = pts[r[(i + 1) % r.Count]]; a += p.X * q.Y - q.X * p.Y; }
            return a / 2;
        }
        var poly = rings[0];
        if (Area(poly) < 0) poly.Reverse();
        var holes = rings.Skip(1).ToList();
        foreach (var h in holes) if (Area(h) > 0) h.Reverse();

        // holes joined to the outer loop, the one reaching furthest right first
        var joined = new HashSet<List<int>>();
        foreach (var h in holes.OrderByDescending(h => h.Max(i => pts[i].X)))
        {
            int mi = Enumerable.Range(0, h.Count).MaxBy(i => pts[h[i]].X);
            var m = pts[h[mi]];
            // the bridge: to the nearest point of the loop so far that m can see (crossing no edge of it, of this hole or of
            // the holes still to come). (Eberly's ray to the right picked a wrong one with a pie and a second hole.)
            var others = holes.Where(o => o != h && !joined.Contains(o)).ToList();
            bool Crosses((double X, double Y) p, (double X, double Y) q, List<int> ring)
            {
                for (int k = 0; k < ring.Count; k++)
                {
                    var a = pts[ring[k]]; var b = pts[ring[(k + 1) % ring.Count]];
                    if (a == p || b == p || a == q || b == q) continue;
                    if (ProperCross(p, q, a, b)) return true;
                }
                return false;
            }
            // (whether m lies in the corner's inside: a point that is in the loop twice, at an earlier bridge, has two
            // corners, and only one of them opens towards m)
            bool Opens(int k)
            {
                var a = pts[poly[(k + poly.Count - 1) % poly.Count]]; var b = pts[poly[k]]; var c = pts[poly[(k + 1) % poly.Count]];
                bool left = Cross(a, b, m) > 0, right = Cross(b, c, m) > 0;
                return Cross(a, b, c) >= 0 ? left && right : left || right;
            }
            int join = -1;
            foreach (int k in Enumerable.Range(0, poly.Count).OrderBy(k => Dist2(pts[poly[k]], m)))
            {
                var p = pts[poly[k]];
                if (!Opens(k) || Crosses(m, p, poly) || Crosses(m, p, h) || others.Any(o => Crosses(m, p, o))) continue;
                join = k; break;
            }
            if (join < 0) join = Enumerable.Range(0, poly.Count).MinBy(k => Dist2(pts[poly[k]], m));
            joined.Add(h);
            var merged = new List<int>(poly.Count + h.Count + 2);
            for (int k = 0; k <= join; k++) merged.Add(poly[k]);
            for (int k = 0; k <= h.Count; k++) merged.Add(h[(mi + k) % h.Count]);
            for (int k = join; k < poly.Count; k++) merged.Add(poly[k]);
            poly = merged;
        }

        // ear clipping
        var v = new List<int>(poly);
        int guard = 0;
        while (v.Count > 3 && guard++ < 100000)
        {
            bool clipped = false;
            for (int i = 0; i < v.Count; i++)
            {
                int ip = (i + v.Count - 1) % v.Count, inx = (i + 1) % v.Count;
                var a = pts[v[ip]]; var b = pts[v[i]]; var c = pts[v[inx]];
                double cr = Cross(a, b, c);
                if (cr <= 0) continue;
                bool inside = false;
                for (int j = 0; j < v.Count && !inside; j++)
                {
                    if (j == ip || j == i || j == inx) continue;
                    int q = v[j];
                    if (q == v[ip] || q == v[i] || q == v[inx]) continue;
                    var p = pts[q];
                    if (p == a || p == b || p == c) continue;
                    inside = InTriangle(p, a, b, c);
                }
                if (inside) continue;
                result.Add((v[ip], v[i], v[inx]));
                v.RemoveAt(i);
                clipped = true;
                break;
            }
            if (clipped) continue;
            // no ear (collinear or touching points): drop a flat corner, or cut the best convex corner anyway
            int flat = Enumerable.Range(0, v.Count).FirstOrDefault(i => Math.Abs(Cross(pts[v[(i + v.Count - 1) % v.Count]], pts[v[i]], pts[v[(i + 1) % v.Count]])) < 1e-24, -1);
            if (flat >= 0) { v.RemoveAt(flat); continue; }
            int best = Enumerable.Range(0, v.Count).MaxBy(i => Cross(pts[v[(i + v.Count - 1) % v.Count]], pts[v[i]], pts[v[(i + 1) % v.Count]]));
            result.Add((v[(best + v.Count - 1) % v.Count], v[best], v[(best + 1) % v.Count]));
            v.RemoveAt(best);
        }
        if (v.Count == 3 && Cross(pts[v[0]], pts[v[1]], pts[v[2]]) > 0) result.Add((v[0], v[1], v[2]));
        return result;
    }

    /// <summary>Whether segments p–q and a–b cross at a point inside both (touching or sharing an end is no crossing).</summary>
    private static bool ProperCross((double X, double Y) p, (double X, double Y) q, (double X, double Y) a, (double X, double Y) b)
    {
        double d1 = Cross(p, q, a), d2 = Cross(p, q, b), d3 = Cross(a, b, p), d4 = Cross(a, b, q);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        // (a point of a–b lying on p–q, between its ends: a bridge through a vertex)
        bool On((double X, double Y) r) => Math.Abs(Cross(p, q, r)) < 1e-30 && (r.X - p.X) * (r.X - q.X) + (r.Y - p.Y) * (r.Y - q.Y) < 0;
        return On(a) || On(b);
    }

    private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static double Dist2((double X, double Y) a, (double X, double Y) b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    private static bool InTriangle((double X, double Y) p, (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        double d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }
}
