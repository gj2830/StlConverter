using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace RsDocx.Core.Brep;

/// <summary>One segment of a 2D profile in the (y,z) plane, ending at <see cref="End"/>.
/// Arc: centre (y,z) given, Ccw = counter-clockwise seen from +x.</summary>
public sealed record ProfileSegment((double Y, double Z) End, (double Y, double Z)? ArcCentre = null, bool Ccw = true);

public static class BrepShapes
{
    /// <summary>Planar polyhedron (planes + lines) as a B-rep body. Edge order = <see cref="XtPolyhedronBuilder.Edges"/>.</summary>
    public static BrepBody FromPolyhedron(Polyhedron p)
    {
        var undirected = XtPolyhedronBuilder.Edges(p);
        var index = new Dictionary<(int, int), int>();
        for (int i = 0; i < undirected.Count; i++) index[undirected[i]] = i;
        var verts = p.Vertices.Select(v => new Vec3(v.X, v.Y, v.Z)).ToList();
        var edges = undirected.Select(e => new BrepEdge(e.A, e.B, new BrepLine())).ToList();
        IReadOnlyList<BrepCoedge> Loop(int[] l) => l.Select((v, k) =>
        {
            int w = l[(k + 1) % l.Length];
            return v < w ? new BrepCoedge(index[(v, w)], true) : new BrepCoedge(index[(w, v)], false);
        }).ToList();
        var faces = new List<BrepFace>();
        for (int f = 0; f < p.Faces.Count; f++)
        {
            var outer = p.Faces[f];
            var n = Newell(verts, outer);
            var x = (verts[outer[1]] - verts[outer[0]]);
            x = (x - n * x.Dot(n)).Normalized();
            faces.Add(new BrepFace(new BrepPlane(verts[outer[0]], n, x), false, p.LoopsOf(f).Select(Loop).ToList()));
        }
        return new BrepBody { Vertices = verts, Edges = edges, Faces = faces };
    }

    /// <summary>
    /// Extrudes a closed profile in the (y,z) plane (counter-clockwise seen from +x, segments are lines or arcs)
    /// from x0 to x1. Line segments give planar side faces, arcs give cylindrical faces (convex for ccw arcs,
    /// hollow for clockwise arcs). Coordinates in metres.
    /// </summary>
    public static BrepBody ExtrudeProfile((double Y, double Z) start, IReadOnlyList<ProfileSegment> segs, double x0, double x1)
    {
        int n = segs.Count;
        var pts = new List<(double Y, double Z)> { start };
        for (int i = 0; i < n - 1; i++) pts.Add(segs[i].End);
        if (Math.Abs(segs[^1].End.Y - start.Y) > 1e-12 || Math.Abs(segs[^1].End.Z - start.Z) > 1e-12)
            throw new ArgumentException("Profile must end at its start point");
        var X = new Vec3(1, 0, 0);
        Vec3 P(int i, double x) => new(x, pts[i % n].Y, pts[i % n].Z);

        var verts = new List<Vec3>();
        for (int i = 0; i < n; i++) verts.Add(P(i, x0));
        for (int i = 0; i < n; i++) verts.Add(P(i, x1));
        int V0(int i) => i % n;
        int V1(int i) => n + i % n;

        var edges = new List<BrepEdge>();
        // profile edges at x0 (index i) and x1 (index n + i), lateral lines (index 2n + i), all oriented i -> i+1 / x0 -> x1
        for (int side = 0; side < 2; side++)
        {
            double x = side == 0 ? x0 : x1;
            for (int i = 0; i < n; i++)
            {
                var s = segs[i];
                BrepCurve curve = new BrepLine();
                if (s.ArcCentre is { } c)
                {
                    var centre = new Vec3(x, c.Y, c.Z);
                    var from = P(i, x) - centre;
                    curve = new BrepCircle(centre, s.Ccw ? X : X * -1, from.Normalized(), from.Length);
                }
                edges.Add(new BrepEdge(side == 0 ? V0(i) : V1(i), side == 0 ? V0(i + 1) : V1(i + 1), curve));
            }
        }
        for (int i = 0; i < n; i++) edges.Add(new BrepEdge(V0(i), V1(i), new BrepLine()));

        var faces = new List<BrepFace>
        {
            // cap at x1 (normal +x): profile order; cap at x0 (normal -x): reversed
            new(new BrepPlane(verts[V1(0)], X, new Vec3(0, 1, 0)), false,
                [Enumerable.Range(0, n).Select(i => new BrepCoedge(n + i, true)).ToList()]),
            new(new BrepPlane(verts[V0(0)], X * -1, new Vec3(0, 1, 0)), false,
                [Enumerable.Range(0, n).Reverse().Select(i => new BrepCoedge(i, false)).ToList()]),
        };
        for (int i = 0; i < n; i++)
        {
            // P_i@x0 -> P_i+1@x0 -> P_i+1@x1 -> P_i@x1 (counter-clockwise seen from outside)
            IReadOnlyList<BrepCoedge> loop = [new(i, true), new(2 * n + (i + 1) % n, true), new(n + i, false), new(2 * n + i, false)];
            var s = segs[i];
            BrepSurface surf;
            bool reversed = false;
            if (s.ArcCentre is { } c)
            {
                var axisPt = new Vec3(x0, c.Y, c.Z);
                var radial = P(i, x0) - axisPt;
                surf = new BrepCylinder(axisPt, X, radial.Normalized(), radial.Length);
                reversed = !s.Ccw; // clockwise arc: material on the outside of the circle -> hollow face
            }
            else
            {
                var a = P(i, x0); var b = P(i + 1, x0);
                var d = (b - a).Normalized();
                var normal = new Vec3(0, d.Z, -d.Y); // right of travel direction in (y,z) = outward for a ccw profile
                surf = new BrepPlane(a, normal, d);
            }
            faces.Add(new BrepFace(surf, reversed, [loop]));
        }
        return new BrepBody { Vertices = verts, Edges = edges, Faces = faces };
    }

    /// <summary>
    /// Revolves a profile in the (rho, z) half-plane (Y = rho, Z = z of <see cref="ProfileSegment"/>) around the z axis.
    /// Either open, from the axis back to the axis, or closed (ending at its start). Traversed so that the material lies on
    /// the left (e.g. from the bottom of the axis outwards and up). Vertical lines give cylinders, horizontal lines planes,
    /// arcs spheres (centre on the axis) or tori, slanted lines cones; consecutive segments meet in ring edges.
    /// </summary>
    public static BrepBody Revolve((double Y, double Z) start, IReadOnlyList<ProfileSegment> segs)
    {
        const double eps = 1e-12;
        int n = segs.Count;
        var pts = new List<(double Y, double Z)> { start };
        pts.AddRange(segs.Select(s => s.End));
        bool closed = Math.Abs(pts[^1].Y - start.Y) < eps && Math.Abs(pts[^1].Z - start.Z) < eps;
        if (!closed && (Math.Abs(start.Y) > eps || Math.Abs(pts[^1].Y) > eps))
            throw new ArgumentException("Open profile must start and end on the axis");
        var Z = new Vec3(0, 0, 1); var X = new Vec3(1, 0, 0);

        // ring edge at each profile point between two segments
        var ringAt = new Dictionary<int, int>();
        var edges = new List<BrepEdge>();
        if (!(closed && n == 1))
            for (int k = closed ? 0 : 1; k < n; k++)
            {
                if (pts[k].Y <= eps) throw new ArgumentException("Inner profile point on the axis");
                ringAt[k] = edges.Count;
                edges.Add(new BrepEdge(-1, -1, new BrepCircle(new Vec3(0, 0, pts[k].Z), Z, X, pts[k].Y)));
            }

        var faces = new List<BrepFace>();
        for (int i = 0; i < n; i++)
        {
            var (a, b) = (pts[i], pts[i + 1]);
            BrepSurface surf; bool reversed = false;
            if (segs[i].ArcCentre is { } c)
            {
                double r = Math.Sqrt((a.Y - c.Y) * (a.Y - c.Y) + (a.Z - c.Z) * (a.Z - c.Z));
                surf = Math.Abs(c.Y) < eps ? new BrepSphere(new Vec3(0, 0, c.Z), r, Z, X)
                    : new BrepTorus(new Vec3(0, 0, c.Z), Z, X, c.Y, r);
                reversed = !segs[i].Ccw; // clockwise: material outside the arc's circle (concave)
            }
            else if (Math.Abs(b.Y - a.Y) < eps)
            {
                surf = new BrepCylinder(new Vec3(0, 0, 0), Z, X, a.Y);
                reversed = b.Z < a.Z; // going down: outward normal points to the axis
            }
            else if (Math.Abs(b.Z - a.Z) < eps) surf = new BrepPlane(new Vec3(0, 0, a.Z), b.Y > a.Y ? Z * -1 : Z, X);
            else
            {
                // slanted line: a cone, as SpaceClaim writes it (schaaltje.rsdocx): pvec at the narrow end, the axis
                // pointing to the wide end; going up the outward normal points away from the axis, going down to it
                if (Math.Min(a.Y, b.Y) <= eps) throw new NotSupportedException("Cone with its apex on the profile");
                var (lo, hi) = a.Y < b.Y ? (a, b) : (b, a);
                var axis = hi.Z > lo.Z ? Z : Z * -1;
                surf = new BrepCone(new Vec3(0, 0, lo.Z), axis, X, lo.Y, Math.Atan((hi.Y - lo.Y) / Math.Abs(hi.Z - lo.Z)));
                reversed = b.Z < a.Z;
            }
            // start ring runs around +z, end ring around -z (outward normal = around x along-profile)
            var loops = new List<IReadOnlyList<BrepCoedge>>();
            if (ringAt.TryGetValue(i, out int s0)) loops.Add([new BrepCoedge(s0, true)]);
            if (ringAt.TryGetValue((i + 1) % n, out int s1) && (closed || i + 1 < n)) loops.Add([new BrepCoedge(s1, false)]);
            faces.Add(new BrepFace(surf, reversed, loops));
        }
        return new BrepBody { Vertices = [], Edges = edges, Faces = faces };
    }

    private static Vec3 Newell(List<Vec3> v, int[] loop)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < loop.Length; i++)
        {
            var c = v[loop[i]]; var n = v[loop[(i + 1) % loop.Length]];
            x += (c.Y - n.Y) * (c.Z + n.Z);
            y += (c.Z - n.Z) * (c.X + n.X);
            z += (c.X - n.X) * (c.Y + n.Y);
        }
        return new Vec3(x, y, z).Normalized();
    }
}

