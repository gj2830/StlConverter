using static RsDocx.Core.Xt.XtGeometry;

namespace RsDocx.Core.Xt;

/// <summary>
/// Checks B-rep invariants for bodies made of planes and cylinders bounded by lines, circles and ellipses.
/// Rules derived from the XT spec and confirmed on SpaceClaim files (testdata/kubus.rsdocx and
/// testdata/sleutelrek.rsdocx must pass with zero errors).
/// </summary>
public static class XtValidator
{
    /// <param name="strict">When false, faces/edges with geometry types the validator cannot evaluate are skipped
    /// instead of reported (used for SpaceClaim reference files with blends, NURBS, intersections, ...).</param>
    /// <param name="tol">How far points may be off their curves and surfaces: Parasolid's own linear precision (res_linear =
    /// 1e-8 m, as written in every body).</param>
    public static List<string> Validate(XtFile file, double tol = 1e-8, bool strict = true)
    {
        var errors = new List<string>();
        var map = file.ByIndex();
        void Err(string s) { if (errors.Count < 200) errors.Add(s); }
        double[] P(int vertex) => map[map[vertex].Ptr("point")].Vec("pvec");
        var finsEndingAt = file.Nodes.Where(n => n.NodeType == XtNodeTypes.Fin).ToLookup(f => f.Ptr("vertex"), f => f.Index);

        foreach (var body in XtTopology.Bodies(file))
        {
            var b = map[body.Index];
            int totalLoops = 0;

            foreach (var f in body.Faces)
            {
                var face = map[f.Index];
                var surf = map[face.Ptr("surface")];
                bool bsurf = surf.NodeType == XtNodeTypes.BSurface;
                if (surf.NodeType is not (XtNodeTypes.Plane or XtNodeTypes.Cylinder or XtNodeTypes.Cone or XtNodeTypes.Sphere or XtNodeTypes.Torus or XtNodeTypes.BSurface))
                {
                    if (strict) Err($"face {f.Index}: unsupported surface type {surf.NodeType}");
                    totalLoops += XtTopology.Chain(map, face.Ptr("loop"), "next").Count();
                    continue;
                }
                // a surface may be shared: surface.owner is the first face, the others follow via next_on_surface
                if (!XtTopology.Chain(map, surf.Ptr("owner"), "next_on_surface").Any(n => n.Index == face.Index))
                    Err($"surface {surf.Index}: face {face.Index} is not in its owner chain");
                if (!bsurf) CheckSurfaceFrame(surf, Err);
                double faceSign = (face.Byte("sense") == '+' ? 1 : -1) * (surf.Byte("sense") == '+' ? 1 : -1);

                int ccwLoops = 0, loopCount = 0;
                var wrapping = new List<(double turn, double v)>(); // loops going all the way around a cylinder
                var wrappingTube = new List<(double turn, double u)>(); // loops going around the tube of a torus
                foreach (var loop in XtTopology.Chain(map, face.Ptr("loop"), "next"))
                {
                    loopCount++; totalLoops++;
                    if (loop.Ptr("face") != face.Index) Err($"loop {loop.Index}: face mismatch");
                    var samples = new List<double[]>();
                    bool skipOrientation = false;
                    int first = loop.Ptr("fin"), cur = first, guard = 0;
                    do
                    {
                        var fin = map[cur];
                        if (fin.Ptr("loop") != loop.Index) Err($"fin {cur}: loop mismatch");
                        if (map[fin.Ptr("forward")].Ptr("backward") != cur) Err($"fin {cur}: forward/backward mismatch");
                        var other = map[fin.Ptr("other")];
                        if (other.Ptr("other") != cur) Err($"fin {cur}: other/other mismatch");
                        if (other.Ptr("edge") != fin.Ptr("edge")) Err($"fin {cur}: other on different edge");
                        if (other.Byte("sense") == fin.Byte("sense")) Err($"fin {cur}: other has same sense");

                        var edge = map[fin.Ptr("edge")];
                        var curve = map[edge.Ptr("curve")];
                        double[] a, bpt;
                        if (fin.Ptr("vertex") == 0 || other.Ptr("vertex") == 0)
                        {
                            // ring edge without vertices (closed curve, e.g. a full circle around a hole)
                            if (fin.Ptr("vertex") != other.Ptr("vertex")) Err($"fin {cur}: only one end of edge has a vertex");
                            if (curve.NodeType is not (XtNodeTypes.Circle or XtNodeTypes.Ellipse or XtNodeTypes.BCurve))
                            {
                                if (strict) Err($"edge {edge.Index}: ring edge on unsupported curve type {curve.NodeType}");
                                skipOrientation = true;
                                cur = fin.Ptr("forward");
                                continue;
                            }
                            a = bpt = curve.NodeType == XtNodeTypes.BCurve ? BCurveStart(curve, map) : StartOfClosedCurve(curve);
                        }
                        else { a = P(other.Ptr("vertex")); bpt = P(fin.Ptr("vertex")); }
                        bool alongCurve = (curve.Byte("sense") == '+') == (fin.Byte("sense") == '+');
                        if (curve.NodeType == XtNodeTypes.Line)
                        {
                            var d = curve.Vec("direction");
                            var dir = Sub(bpt, a);
                            if (Dot(dir, d) * (alongCurve ? 1 : -1) <= 0) Err($"fin {cur}: direction disagrees with line {curve.Index} and senses");
                        }
                        else if (curve.NodeType is not (XtNodeTypes.Circle or XtNodeTypes.Ellipse or XtNodeTypes.BCurve))
                        {
                            if (strict) Err($"edge {edge.Index}: unsupported curve type {curve.NodeType}");
                            skipOrientation = true;
                            samples.Add(a);
                            cur = fin.Ptr("forward");
                            continue;
                        }

                        var pts = SampleEdge(curve, a, bpt, alongCurve, map: map);
                        foreach (var p in pts)
                        {
                            if (CurveDistance(curve, p, map) > tol) { Err($"edge {edge.Index}: point not on its curve {curve.Index}"); break; }
                            if (SurfaceDistance(surf, p, map) > tol) { Err($"face {face.Index}: edge {edge.Index} leaves surface {surf.Index}"); break; }
                        }
                        samples.AddRange(pts.Take(pts.Count - 1));
                        cur = fin.Ptr("forward");
                    } while (cur != first && ++guard < 100000);

                    // outer loop runs counter-clockwise seen from outside (positive area in surface (u,v) space)
                    // (a B-surface's loops are checked for lying on it, not for their direction: no (u, v) projection yet)
                    if (skipOrientation || bsurf) { ccwLoops = -1000; continue; }
                    var (area, turn, meanV, turnV, meanU) = ParamArea(surf, samples);
                    if (Math.Abs(turn) > Math.PI && Math.Abs(turnV) > Math.PI) Err($"face {face.Index}: loop winds around both directions of the torus");
                    else if (Math.Abs(turnV) > Math.PI) wrappingTube.Add((turnV * faceSign, meanU));
                    else if (Math.Abs(turn) > Math.PI) wrapping.Add((turn * faceSign, meanV));
                    else if (area * faceSign > 0) ccwLoops++;
                }
                if (loopCount == 0)
                {
                    // only a closed surface (full sphere) may be a face without boundary
                    if (surf.NodeType is not (XtNodeTypes.Sphere or XtNodeTypes.Torus)) Err($"face {face.Index}: no loops");
                    ccwLoops = -1000;
                }
                else if (wrapping.Count == 1 && surf.NodeType == XtNodeTypes.Sphere && ccwLoops == 0)
                    ccwLoops = -1000; // sphere face containing one pole of its axis: bounded by one ring around it
                else if (surf.NodeType == XtNodeTypes.Torus && (wrapping.Count > 0 || wrappingTube.Count > 0))
                {
                    // a band on a closed torus: two rings in opposite directions (either side can be the face)
                    var w = wrapping.Count > 0 ? wrapping.Select(x => x.turn).ToList() : wrappingTube.Select(x => x.turn).ToList();
                    if (wrapping.Count > 0 && wrappingTube.Count > 0 || w.Count != 2 || ccwLoops > 0 || !(w[0] * w[1] < 0))
                        Err($"face {face.Index}: torus band needs two opposite rings around one direction and no other outer loop");
                    ccwLoops = -1000;
                }
                else if (wrapping.Count > 0)
                {
                    // full band: lower ring runs +u, upper ring -u (material in between); no outer loop
                    if (wrapping.Count != 2 || ccwLoops > 0) Err($"face {face.Index}: {wrapping.Count} loops around the cylinder, expected 2 and no other outer loop");
                    else
                    {
                        var lo = wrapping.MinBy(w => w.v); var hi = wrapping.MaxBy(w => w.v);
                        if (!(lo.turn > 0 && hi.turn < 0)) Err($"face {face.Index}: cylinder band rings have the wrong direction");
                    }
                    ccwLoops = -1000;
                }
                if (ccwLoops >= 0 && ccwLoops != 1)
                {
                    var curveTypes = XtTopology.Chain(map, face.Ptr("loop"), "next")
                        .Select(l => string.Join(",", Enumerable.Range(0, 1000).Select(i => i).Aggregate((list: new List<int>(), cur: l.Ptr("fin")), (acc, _) =>
                        {
                            if (acc.list.Count > 0 && acc.cur == l.Ptr("fin")) return acc;
                            acc.list.Add(map[map[map[acc.cur].Ptr("edge")].Ptr("curve")].NodeType);
                            return (acc.list, map[acc.cur].Ptr("forward"));
                        }).list));
                    Err($"face {face.Index}: {ccwLoops} of {loopCount} loops counter-clockwise, expected exactly 1 (the outer loop) [surface {surf.NodeType}, loops {string.Join(" | ", curveTypes)}]");
                }
            }

            foreach (var e in body.Edges)
            {
                var edge = map[e.Index];
                var fin = map[edge.Ptr("fin")];
                if (fin.Byte("sense") != '+') Err($"edge {e.Index}: edge->fin is not the positive fin");
                // a curve may be shared: curve.owner is the first edge, the others follow via next_on_curve
                var onCurve = XtTopology.Chain(map, map[edge.Ptr("curve")].Ptr("owner"), "next_on_curve").Select(n => n.Index);
                if (!onCurve.Contains(e.Index)) Err($"edge {e.Index}: not in the owner chain of its curve");
                if (edge.Ptr("owner") != body.Index) Err($"edge {e.Index}: owner is not the body");
            }

            foreach (var v in body.Vertices)
            {
                var vx = map[v.Index];
                var chained = new HashSet<int>();
                for (int f = vx.Ptr("fin"); f != 0 && chained.Add(f); f = map[f].Ptr("next_at_vx")) { }
                if (!finsEndingAt[v.Index].ToHashSet().SetEquals(chained)) Err($"vertex {v.Index}: next_at_vx chain does not hold exactly the fins ending at it");
                if (map[vx.Ptr("point")].Ptr("owner") != v.Index) Err($"vertex {v.Index}: point owner mismatch");
            }

            int maxId = MaxNodeIdInBody(map, b);
            if (b.Int("highest_node_id") < maxId) Err($"body {body.Index}: highest_node_id {b.Int("highest_node_id")} < max node_id {maxId}");
            // Euler-Poincare for one shell: V - E + F - (L - F) = 2 - 2*genus
            // a ring edge without vertex counts like an edge with one vertex
            int ringEdges = body.Edges.Count(e => map[map[e.Index].Ptr("fin")].Ptr("vertex") == 0);
            int chi = body.Vertices.Count + ringEdges - body.Edges.Count + 2 * body.Faces.Count - totalLoops;
            if (chi > 2 || chi % 2 != 0) Err($"body {body.Index}: V-E+2F-L = {chi}, expected 2 - 2*genus");
        }
        return errors;
    }

    private static double[] BCurveStart(XtNode c, IReadOnlyDictionary<int, XtNode> map)
    {
        var bs = ReadBCurve(c, map);
        var p = bs.At(bs.Start);
        return [p.X, p.Y, p.Z];
    }

    private static double[] StartOfClosedCurve(XtNode c)
    {
        double r = c.NodeType == XtNodeTypes.Circle ? c.Double("radius") : c.Double("major_radius");
        return Add(c.Vec("centre"), Mul(c.Vec("x_axis"), r));
    }

    private static void CheckSurfaceFrame(XtNode s, Action<string> err)
    {
        var (n, x) = s.NodeType == XtNodeTypes.Plane ? (s.Vec("normal"), s.Vec("x_axis")) : (s.Vec("axis"), s.Vec("x_axis"));
        if (Math.Abs(Len(n) - 1) > 1e-12) err($"surface {s.Index}: normal/axis not unit");
        if (Math.Abs(Len(x) - 1) > 1e-12) err($"surface {s.Index}: x_axis not unit");
        if (Math.Abs(Dot(n, x)) > 1e-12) err($"surface {s.Index}: x_axis not perpendicular");
        if (s.NodeType is XtNodeTypes.Cylinder or XtNodeTypes.Sphere && !(s.Double("radius") > 0)) err($"surface {s.Index}: radius not positive");
        if (s.NodeType is XtNodeTypes.Cone)
        {
            double sn = s.Double("sin_half_angle"), cs = s.Double("cos_half_angle");
            if (!(s.Double("radius") >= 0)) err($"surface {s.Index}: cone radius negative");
            if (!(sn > 0 && cs > 0) || Math.Abs(sn * sn + cs * cs - 1) > 1e-12) err($"surface {s.Index}: cone half angle invalid");
        }
        if (s.NodeType is XtNodeTypes.Torus && !(s.Double("minor_radius") > 0 && s.Double("major_radius") > s.Double("minor_radius")))
            err($"surface {s.Index}: torus radii invalid (only ring tori are supported)");
    }

    /// <summary>
    /// Signed area of a closed polyline in the (u,v) parameter space of the surface, with du x dv along the surface normal.
    /// Plane: u,v along x_axis and normal x x_axis. Cylinder: u = arc length around the axis (unwrapped), v = axial position.
    /// </summary>
    private static (double area, double turn, double meanV, double turnV, double meanU) ParamArea(XtNode s, List<double[]> pts)
    {
        if (pts.Count < 2) return (0, 0, 0, 0, 0);
        double turn = 0, turnV = 0;
        var uv = new List<(double u, double v)>();
        if (s.NodeType == XtNodeTypes.Plane)
        {
            var o = s.Vec("pvec"); var x = s.Vec("x_axis"); var y = Cross(s.Vec("normal"), x);
            foreach (var p in pts) { var d = Sub(p, o); uv.Add((Dot(d, x), Dot(d, y))); }
        }
        else if (s.NodeType == XtNodeTypes.Torus)
        {
            // torus: u = angle around the axis * R, v = angle around the tube * r (du x dv outward)
            var o = s.Vec("centre"); var a = s.Vec("axis"); var x = s.Vec("x_axis"); var y = Cross(a, x);
            double R = s.Double("major_radius"), r = s.Double("minor_radius");
            var us = Unwrap(pts.Select(p => { var d = Sub(p, o); return Math.Atan2(Dot(d, y), Dot(d, x)); }).ToList(), out turn);
            var vs = Unwrap(pts.Select(p =>
            {
                var d = Sub(p, o); double h = Dot(d, a);
                return Math.Atan2(h, Len(Sub(d, Mul(a, h))) - R);
            }).ToList(), out turnV);
            for (int i = 0; i < pts.Count; i++) uv.Add((us[i] * R, vs[i] * r));
        }
        else
        {
            // cylinder and cone: u = angle * r, v = axial position; sphere: u = longitude, v = latitude (du x dv along the
            // natural normal for all three)
            bool sphere = s.NodeType == XtNodeTypes.Sphere;
            var o = sphere ? s.Vec("centre") : s.Vec("pvec"); var a = s.Vec("axis"); var x = s.Vec("x_axis"); var y = Cross(a, x);
            double prev = double.NaN, acc = 0;
            foreach (var p in pts)
            {
                var d = Sub(p, o);
                double ang = Math.Atan2(Dot(d, y), Dot(d, x));
                if (double.IsNaN(prev)) acc = ang;
                else
                {
                    double step = ang - prev;
                    while (step > Math.PI) step -= 2 * Math.PI;
                    while (step < -Math.PI) step += 2 * Math.PI;
                    acc += step;
                }
                prev = ang;
                uv.Add(sphere ? (acc, Math.Asin(Math.Clamp(Dot(d, a) / Len(d), -1, 1))) : (acc * s.Double("radius"), Dot(d, a)));
            }
            // close the loop to measure the net turn around the axis
            var d0 = Sub(pts[0], o);
            double step0 = Math.Atan2(Dot(d0, y), Dot(d0, x)) - prev;
            while (step0 > Math.PI) step0 -= 2 * Math.PI;
            while (step0 < -Math.PI) step0 += 2 * Math.PI;
            turn = acc + step0 - Math.Atan2(Dot(d0, y), Dot(d0, x));
        }
        double area = 0;
        for (int i = 0; i < uv.Count; i++)
        {
            var (u0, v0) = uv[i]; var (u1, v1) = uv[(i + 1) % uv.Count];
            area += u0 * v1 - u1 * v0;
        }
        return (area / 2, turn, uv.Average(p => p.v), turnV, uv.Average(p => p.u));
    }

    /// <summary>Unwraps a sequence of angles; turn = net change around the closed loop.</summary>
    private static List<double> Unwrap(List<double> ang, out double turn)
    {
        var res = new List<double> { ang[0] };
        static double Step(double d) { while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; return d; }
        for (int i = 1; i < ang.Count; i++) res.Add(res[^1] + Step(ang[i] - ang[i - 1]));
        turn = res[^1] + Step(ang[0] - ang[^1]) - res[0];
        return res;
    }

    private static int MaxNodeIdInBody(Dictionary<int, XtNode> map, XtNode body)
    {
        // all nodes reachable from the body without leaving it via body next/previous
        var seen = new HashSet<int>();
        var stack = new Stack<int>([body.Index]);
        int max = 0;
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (i == 0 || !map.TryGetValue(i, out var n) || !seen.Add(i)) continue;
            if (n.NodeType == XtNodeTypes.Body && n != body) continue;
            if (n.Has("node_id")) max = Math.Max(max, n.Int("node_id"));
            for (int k = 0; k < n.Values.Length; k++)
            {
                if (!n.Schema.Fields[k].IsPointer) continue;
                if (n.Values[k] is int p) stack.Push(p);
                else if (n.Values[k] is object[] arr) foreach (var o in arr) stack.Push((int)o);
            }
        }
        return max;
    }
}
