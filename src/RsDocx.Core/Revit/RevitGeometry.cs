using RsDocx.Core.Brep;
using RsDocx.Core.Geometry;
using RsDocx.Core.Stl;

namespace RsDocx.Core.Revit;

/// <summary>A face of a Revit body (metres): its triangles, wound outward as saved; its rings, the outer one first, then
/// its holes (the points the faces next to it share), when it has them; and its exact surface where it is curved
/// (a cylinder, sphere, torus, a plane square to an axis, or a NURBS surface of revolution; null for a flat face, which
/// its rings give, or a surface not given exactly). The surface may face either way: its triangles say which is out.</summary>
public sealed record RevitFace(IReadOnlyList<StlTriangle> Triangles, IReadOnlyList<IReadOnlyList<Vec3>>? Rings, BrepSurface? Surface = null);

/// <summary>A body of a Revit element (one saved Geometry, or a mesh): its faces.</summary>
public sealed record RevitBody(IReadOnlyList<RevitFace> Faces);

/// <summary>An element of a Revit model as Revit last drew it in 3D: its id, its class ("ArcWall", "FamilyInstance") and
/// its bodies.</summary>
public sealed record RevitElementMesh(ulong Id, string Class, IReadOnlyList<RevitBody> Bodies)
{
    /// <summary>All its triangles.</summary>
    public IEnumerable<StlTriangle> Triangles => Bodies.SelectMany(b => b.Faces).SelectMany(f => f.Triangles);
}

/// <summary>What reading the saved graphics left out, counted: faces of a kind not read yet (by surface class), filters
/// that hid a branch, branches it could not judge, and graphs that could not be decoded.</summary>
public sealed class RevitGeometryReport
{
    public Dictionary<string, int> SkippedSurfaces { get; } = new();
    public Dictionary<string, int> Unjudged { get; } = new();
    public int HiddenBranches, RejectedFilters, UndecodedGraphs, Elements, Faces;
    /// <summary>How many light sources' shapes (a lamp's cone of light, drawn in Revit only when asked for) were left out.</summary>
    public int LightSources;
    /// <summary>How many voids (forms that cut others away, not drawn themselves) were left out.</summary>
    public int Voids;
    /// <summary>Per kind of surface: how many faces and triangles it gave.</summary>
    public Dictionary<string, (int Faces, long Triangles)> BySurface { get; } = new();
}

/// <summary>
/// The geometry Revit saved with a model (from Revit 2023 on): per element its graphics (channel 103 of the partitions, a
/// GElement graph, see <see cref="RevitGraph"/>), walked as rvt-rs does (native_graphics_traversal and native_saved_mesh,
/// see THIRD-PARTY-NOTICES.md) for the default 3D view at detail level 3: through GGroup and GFilter (their conditions on
/// view direction, view type and detail level), GInstance (a family's symbol, by its own graphics or the symbol's, with its
/// transform), down to Geometry (faces) and GPolyMesh (triangles). Faces are triangulated from their trimming loops in the
/// surface's own coordinates, by the points Revit saved along their edges (which the faces on either side share, so they
/// meet without gaps); on cylinders, surfaces of revolution and ruled surfaces the inner edges are split until they
/// follow the curvature. Other surfaces are left out (counted). Lengths are saved in feet.
/// </summary>
public static class RevitGeometry
{
    private const double Foot = 0.3048;
    private static readonly double[,] Identity = { { 1, 0, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, 0 }, { 0, 0, 0, 1 } };

    public static List<RevitElementMesh> Read(RevitFile file, RevitGeometryReport? report = null)
    {
        var result = new List<RevitElementMesh>();
        Read(file, result.Add, report);
        return result;
    }

    /// <summary>As <see cref="Read(RevitFile, RevitGeometryReport?)"/>, handing each element over as soon as it is read
    /// (so a large model need not be held whole): only the graphics of family symbols, which instances place, are kept
    /// meanwhile.</summary>
    public static void Read(RevitFile file, Action<RevitElementMesh> each, RevitGeometryReport? report = null)
    {
        report ??= new RevitGeometryReport();
        var schema = file.Schema();
        int version = file.Version;
        if (version < 2023) throw new FormatException($"Saved graphics are read from Revit 2023 on; this file is from {(version > 0 ? version.ToString() : "an unknown release")}.");
        // per element its class (channel 102) and its graphics record (103)
        var records = file.Records(102);
        var classes = records.ToDictionary(kv => kv.Key, kv => kv.Value.Class);
        // (voids: forms that cut (an extrusion, blend, sweep … whose m_cutting is set), not drawn themselves; their
        // graphics are the shape they cut away, inside out)
        var cutters = classes.Values.Distinct().Where(c => schema.ByName(c) is { } k && schema.AllFields(k).Any(f => f.Name == "m_cutting")).ToHashSet();
        var voids = new HashSet<ulong>();
        foreach (var (id, (cls, body)) in records)
        {
            if (!cutters.Contains(cls)) continue;
            try { if (RevitGraph.Decode(body, schema) is { Objects.Count: > 0 } r && r.Objects[0].Fields.GetValueOrDefault("m_cutting") is true) voids.Add(id); }
            catch (Exception e) when (e is FormatException or InvalidCastException) { }
        }
        report.Voids = voids.Count;
        var graphics = file.Records(103).Where(kv => kv.Value.Body.Length >= 2).ToDictionary(kv => kv.Key, kv => kv.Value.Body);
        RevitGraph? Decode(ulong id)
        {
            if (!graphics.TryGetValue(id, out var body)) return null;
            try { return RevitGraph.Decode(body, schema); }
            catch (FormatException) { return null; }
        }
        // the symbols instances refer to are drawn where they are placed, not on their own; only by the instances the
        // walk takes (a group's instances of its members, drawn in the view (m_resolveSymInView), it does not: those
        // members, a balcony's floor and railing, are drawn as themselves)
        var symbols = new HashSet<ulong>();
        foreach (var id in graphics.Keys)
        {
            if (Decode(id) is not { } g) { report.UndecodedGraphs++; continue; }
            foreach (var instance in g.OfClass("GInstance"))
                if (Walker.Taken(instance) && g.Follow(instance.Fields.GetValueOrDefault("m_instanceInfo")) is { ClassName: "InstanceInfo" } info
                    && Identifier(info.Fields.GetValueOrDefault("m_symbolId")) is { } s and > 0)
                    symbols.Add((ulong)s);
        }
        // (a symbol's graphics read once, kept for every instance of it)
        var symbolGraphs = new Dictionary<ulong, RevitGraph?>();
        RevitGraph? Symbol(ulong id) => symbolGraphs.TryGetValue(id, out var g) ? g : symbolGraphs[id] = Decode(id);
        foreach (var id in graphics.Keys.Order())
        {
            if (symbols.Contains(id) || voids.Contains(id) || !InModel(classes.GetValueOrDefault(id, "?")) || Decode(id) is not { } g || g.Objects.Count == 0 || g.Objects[0].ClassName != "GElement") continue;
            var bodies = new List<RevitBody>();
            new Walker(Symbol, report, bodies).Visit(g, 0, Identity, 0);
            if (bodies.Count == 0) continue;
            report.Elements++;
            each(new RevitElementMesh(id, classes.GetValueOrDefault(id, "?"), bodies));
        }
    }

    /// <summary>Whether elements of a class are part of the model in 3D: not a type (a family's symbol, drawn where an
    /// instance places it; "…Symbol", "…Sym"), a filled region or legend component (2D, in a view), a room's volume or a
    /// light's source (its cone of light, drawn only when asked for).</summary>
    private static bool InModel(string cls) =>
        !cls.EndsWith("Symbol", StringComparison.Ordinal) && !cls.EndsWith("Sym", StringComparison.Ordinal) && cls is not ("FilledRegion" or "RoomElem" or "LegendComponent" or "ImposterLight");

    /// <summary>A saved ElementId (a number, or an object with m_id / m_id64 inside), or null.</summary>
    public static long? Identifier(object? value) => value switch
    {
        long l => l,
        int i => i,
        uint u => u,
        IReadOnlyDictionary<string, object?> d => d.TryGetValue("m_id", out var a) ? Identifier(a) : d.TryGetValue("m_id64", out var b) ? Identifier(b) : null,
        _ => null,
    };

    private static double[] Numbers(object? v, int n) =>
        v is List<object?> l && l.Count == n ? l.Select(x => x switch { double d => d, float f => f, _ => throw new FormatException("not a number") }).ToArray()
            : throw new FormatException($"not {n} numbers");

    private sealed class Walker(Func<ulong, RevitGraph?> graph, RevitGeometryReport report, List<RevitBody> bodies)
    {
        /// <summary>The faces of the body being read.</summary>
        private List<RevitFace> _faces = new();

        /// <summary>A body read: its faces (none: no body).</summary>
        private void Body(Action read)
        {
            _faces = new List<RevitFace>();
            read();
            if (_faces.Count > 0) bodies.Add(new RevitBody(_faces));
        }

        private void Unjudged(string why) => report.Unjudged[why] = report.Unjudged.GetValueOrDefault(why) + 1;

        private static object? F(RevitObject o, string name) => o.Fields.GetValueOrDefault(name);

        private static List<int> Children(RevitGraph g, RevitObject o) =>
            F(o, "m_subNodes") is List<object?> l ? l.Select(g.IndexOf).OfType<int>().ToList() : [];

        private static readonly HashSet<string> Curves = ["GLine", "GArc", "GEllipse", "GCurve", "GNurbSpline", "GPolyLine", "GPolyline", "GPoint", "GHermiteSpline"];

        private static bool OnlyCurves(RevitGraph g, RevitObject o, int depth) => depth < 128 && (Curves.Contains(o.ClassName)
            || o.ClassName is "GElement" or "GGroup" or "GFilter" && Children(g, o).All(c => OnlyCurves(g, g.Objects[c], depth + 1)));

        public void Visit(RevitGraph g, int index, double[,] m, int depth)
        {
            if (depth > 128) { Unjudged("too deep"); return; }
            var o = g.Objects[index];
            // (visibility in the graphics flags: 1 and 3 are drawn only in a special mode)
            if (F(o, "m_GInfo") is Dictionary<string, object?> info && info.GetValueOrDefault("m_flags") is uint flags && Visibility(flags) is 1 or 3)
            {
                report.HiddenBranches++;
                return;
            }
            switch (o.ClassName)
            {
                case "Geometry":
                    if (F(o, "m_pFaces") is List<object?> faces)
                        Body(() =>
                        {
                            foreach (var p in faces)
                                if (g.Follow(p) is { ClassName: "Face" } face) Face(g, face, m);
                        });
                    break;
                case "GPolyMesh":
                    Body(() => PolyMesh(g, o, m));
                    break;
                case "GElement" or "GGroup" or "GFilter":
                {
                    var children = Children(g, o);
                    if (o.ClassName == "GFilter")
                    {
                        if (children.Count == 0 || children.All(c => OnlyCurves(g, g.Objects[c], 0))) return;
                        if (F(o, "m_bIsNestedDetailFamily") is true) { Unjudged("nested detail family"); return; }
                        if (F(o, "m_oConditions") is List<object?> conditions)
                            foreach (var cp in conditions)
                            {
                                if (g.Follow(cp) is not { } c) { Unjudged("condition missing"); return; }
                                bool? ok = c.ClassName is "GConditionDir" or "GConditionCut" ? Direction(c, m) : Condition(c);
                                if (ok == null) { Unjudged("condition " + c.ClassName); return; }
                                if (ok == false) { report.RejectedFilters++; return; }
                            }
                    }
                    // (beside a light (GImposter, its rendering asset) the shape of its light, in a category of its own,
                    // Revit's yellow "Light Source", drawn only when asked for: left out; the lamp itself has none)
                    bool light = children.Any(c => g.Objects[c].ClassName == "GImposter");
                    foreach (int c in children)
                    {
                        if (light && g.Objects[c] is { ClassName: "Geometry" or "GPolyMesh" } shape && OwnCategory(shape))
                        {
                            report.LightSources++;
                            continue;
                        }
                        Visit(g, c, m, depth + 1);
                    }
                    break;
                }
                case "GInstance":
                {
                    if (!Taken(o)) { Unjudged("instance mode"); return; }
                    if (g.Follow(F(o, "m_instanceInfo")) is not { ClassName: "InstanceInfo" } ii) { Unjudged("instance info"); return; }
                    var t = Transform(F(ii, "m_Trf"));
                    if (t == null) { Unjudged("instance transform"); return; }
                    var placed = Multiply(m, t);
                    if (F(o, "m_oEmbeddedSymbolGRep") is RevitPointer { Token: 0 })
                    {
                        if (Identifier(F(ii, "m_symbolId")) is not { } sid || sid <= 0 || graph((ulong)sid) is not { } sg || sg.Objects.Count == 0) { Unjudged("symbol graphics"); return; }
                        Visit(sg, 0, placed, depth + 1);
                    }
                    else if (g.IndexOf(F(o, "m_oEmbeddedSymbolGRep")) is { } embedded) Visit(g, embedded, placed, depth + 1);
                    else Unjudged("embedded symbol");
                    break;
                }
                case "GComponentRef":
                    Unjudged("component reference");
                    break;
                default:
                    if (!Curves.Contains(o.ClassName)) Unjudged("class " + o.ClassName);
                    break;
            }
        }

        /// <summary>Whether the walk takes an instance (one drawn without a scale, not resolved in the view).</summary>
        public static bool Taken(RevitObject instance) => F(instance, "m_bHasScale") is false && F(instance, "m_resolveSymInView") is false;

        /// <summary>Whether a node has a category of its own (not the one it is in: -1).</summary>
        private static bool OwnCategory(RevitObject o) =>
            F(o, "m_GInfo") is Dictionary<string, object?> info && Identifier(info.GetValueOrDefault("m_categoryId")) is > 0;

        private static int Visibility(uint flags)
        {
            uint bits = flags & 0x1e0;
            if (bits == 0) return 0;
            if (bits == 0xe0) return 3;
            if (bits < 0xe0) return (flags & 0x160) == 0x20 ? 1 : 2;
            return (flags & 0x1a0) == 0x100 ? 1 : 2;
        }

        // (the default 3D view: view type 4; direction conditions 6..8 are false before negation; 10 and 11 compare
        // the angle with the view direction to a right angle)
        private static bool? Direction(RevitObject c, double[,] m)
        {
            if (F(c, "m_negateDirCondition") is not bool negate || F(c, "m_comp") is not int comp) return null;
            if (comp is >= 6 and <= 8) return negate;
            double[] local;
            try { local = Numbers(F(c, "m_dir"), 3); } catch (FormatException) { return null; }
            var w = new double[3];
            for (int i = 0; i < 3; i++) w[i] = m[i, 0] * local[0] + m[i, 1] * local[1] + m[i, 2] * local[2];
            double len = Math.Sqrt(w[0] * w[0] + w[1] * w[1] + w[2] * w[2]);
            if (len < 1e-12) return null;
            double[] view = [-0.577350269189625, -0.577350269189627, 0.577350269189626];
            bool answer;
            if (comp is 10 or 11)
            {
                double angle = Math.Acos(Math.Clamp((w[0] * view[0] + w[1] * view[1] + w[2] * view[2]) / len, -1, 1));
                answer = comp == 10 ? angle < 1.5707963266948965 : angle > 1.5707963268948966;
            }
            else if (comp is -1 or 0 or 3)
            {
                double d = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (w[i] - view[i]) * (w[i] - view[i])));
                if (d is >= 1e-14 and <= 1e-6) return null;
                bool equal = d < 1e-14;
                answer = comp == 0 ? !equal : equal;
            }
            else return null;
            return answer != negate;
        }

        private static bool? Condition(RevitObject c)
        {
            if (c.ClassName is not ("GConditionInt" or "GConditionViewType" or "GConditionViewDetailLevel" or "GConditionVModeType")) return null;
            if (F(c, "m_param") is not int kind || F(c, "m_value") is not int saved || F(c, "m_comp") is not int comp) return null;
            if (kind == 7 && saved == 2 && comp == 0) return true;
            int actual = kind switch { 1 => 4, 3 => 3, 6 => 0, _ => int.MinValue };
            if (actual == int.MinValue) return null;
            return comp switch
            {
                -1 or 3 => saved == actual,
                0 => saved != actual,
                1 => saved > actual,
                2 => saved >= actual,
                4 => saved <= actual,
                5 => saved < actual,
                _ => null,
            };
        }

        private static double[,]? Transform(object? trf)
        {
            if (trf is not Dictionary<string, object?> d || d.GetValueOrDefault("m_3x3") is not List<object?> rows || rows.Count != 3) return null;
            try
            {
                var or = Numbers(d.GetValueOrDefault("m_or"), 3);
                var m = (double[,])Identity.Clone();
                for (int i = 0; i < 3; i++)
                {
                    var row = Numbers(rows[i], 3);
                    for (int j = 0; j < 3; j++) m[i, j] = row[j];
                    m[i, 3] = or[i];
                }
                return m;
            }
            catch (FormatException) { return null; }
        }

        private static double[,] Multiply(double[,] a, double[,] b)
        {
            var r = new double[4, 4];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    for (int k = 0; k < 4; k++) r[i, j] += a[i, k] * b[k, j];
            return r;
        }

        private static Vec3 Apply(double[,] m, double x, double y, double z) =>
            new Vec3(m[0, 0] * x + m[0, 1] * y + m[0, 2] * z + m[0, 3], m[1, 0] * x + m[1, 1] * y + m[1, 2] * z + m[1, 3], m[2, 0] * x + m[2, 1] * y + m[2, 2] * z + m[2, 3]) * Foot;

        /// <summary>A surface (in its own feet) put where the transform says, in metres; null when the transform is not a
        /// turn, move or mirror with one scale.</summary>
        private static BrepSurface? Placed(BrepSurface? e, double[,] m)
        {
            if (e == null) return null;
            Vec3 Dir(Vec3 d) => new Vec3(m[0, 0] * d.X + m[0, 1] * d.Y + m[0, 2] * d.Z, m[1, 0] * d.X + m[1, 1] * d.Y + m[1, 2] * d.Z, m[2, 0] * d.X + m[2, 1] * d.Y + m[2, 2] * d.Z);
            Vec3 P(Vec3 p) => Apply(m, p.X, p.Y, p.Z);
            var ex = Dir(new Vec3(1, 0, 0)); var ey = Dir(new Vec3(0, 1, 0)); var ez = Dir(new Vec3(0, 0, 1));
            double k = ex.Length;
            if (k < 1e-12 || Math.Abs(ey.Length - k) > 1e-9 * k || Math.Abs(ez.Length - k) > 1e-9 * k || Math.Abs(ex.Dot(ey)) > 1e-9 * k * k || Math.Abs(ex.Dot(ez)) > 1e-9 * k * k) return null;
            Vec3 U(Vec3 d) => Dir(d).Normalized();
            double R(double r) => r * k * Foot;
            return e switch
            {
                BrepPlane pl => new BrepPlane(P(pl.Origin), U(pl.Normal), U(pl.XAxis)),
                BrepCylinder c => new BrepCylinder(P(c.Origin), U(c.Axis), U(c.XAxis), R(c.Radius)),
                BrepSphere sp => new BrepSphere(P(sp.Centre), R(sp.Radius), U(sp.Axis), U(sp.XAxis)),
                BrepTorus t => new BrepTorus(P(t.Centre), U(t.Axis), U(t.XAxis), R(t.MajorRadius), R(t.MinorRadius)),
                BrepNurbsSurface n => n with { Points = n.Points.Select(P).ToList() },
                _ => null,
            };
        }

        /// <summary>
        /// A profile (a rational B-spline in the surface's own frame: degree, points, weights, knots and their counts)
        /// turned round that frame's z from angle <paramref name="a0"/> to <paramref name="a1"/>: the surface of
        /// revolution exactly, as a NURBS surface (Piegl and Tiller's MakeRevolvedSurf): along u the rational circle
        /// through every profile point, in arcs of at most a quarter turn, each with its middle point out by 1/cos of half
        /// the arc and weighted cos of it; along v the profile. In the frame through <paramref name="c"/> with
        /// <paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/> (feet).
        /// </summary>
        private static BrepNurbsSurface Revolve(NurbsCurve profile, double a0, double a1, double[] c, double[] x, double[] y, double[] z)
        {
            int arcs = Math.Max(1, (int)Math.Ceiling((a1 - a0) / (Math.PI / 2) - 1e-9));
            double da = (a1 - a0) / arcs, half = Math.Cos(da / 2);
            int nu = 2 * arcs + 1, nv = profile.Points.Count;
            var pts = new List<Vec3>(nu * nv); var ws = new List<double>(nu * nv);
            for (int i = 0; i < nu; i++)
            {
                double a = a0 + i * da / 2, f = i % 2 == 1 ? 1 / half : 1, wu = i % 2 == 1 ? half : 1;
                for (int j = 0; j < nv; j++)
                {
                    var q = profile.Points[j];
                    double l0 = (q.X * Math.Cos(a) - q.Y * Math.Sin(a)) * f, l1 = (q.X * Math.Sin(a) + q.Y * Math.Cos(a)) * f, l2 = q.Z;
                    pts.Add(new Vec3(c[0] + x[0] * l0 + y[0] * l1 + z[0] * l2, c[1] + x[1] * l0 + y[1] * l1 + z[1] * l2, c[2] + x[2] * l0 + y[2] * l1 + z[2] * l2));
                    ws.Add(wu * profile.Weights[j]);
                }
            }
            var uk = Enumerable.Range(0, arcs + 1).Select(k => (double)k).ToList();
            var um = Enumerable.Range(0, arcs + 1).Select(k => k == 0 || k == arcs ? 3 : 2).ToList();
            return new BrepNurbsSurface(2, profile.Degree, nu, nv, pts, ws, uk, um, profile.Knots, profile.Mults);
        }

        /// <summary>A rational B-spline curve: degree, points, weights, knots (distinct) and how often each.</summary>
        private sealed record NurbsCurve(int Degree, List<Vec3> Points, List<double> Weights, List<double> Knots, List<int> Mults);

        /// <summary>An arc of the ellipse <paramref name="o"/> + cos t <paramref name="a"/> + sin t <paramref name="b"/>
        /// from t0 to t1 as a rational quadratic B-spline (a circle's arc when a and b are as long and square), in arcs of
        /// at most a quarter turn.</summary>
        private static NurbsCurve EllipseArc(Vec3 o, Vec3 a, Vec3 b, double t0, double t1)
        {
            int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(t1 - t0) / (Math.PI / 2) - 1e-9));
            double dt = (t1 - t0) / n, w = Math.Cos(dt / 2);
            var pts = new List<Vec3>(); var ws = new List<double>();
            for (int i = 0; i <= 2 * n; i++)
            {
                double t = t0 + i * dt / 2;
                bool mid = i % 2 == 1;
                pts.Add(o + (a * Math.Cos(t) + b * Math.Sin(t)) * (mid ? 1 / w : 1));
                ws.Add(mid ? w : 1);
            }
            return new NurbsCurve(2, pts, ws, Enumerable.Range(0, n + 1).Select(k => (double)k).ToList(), Enumerable.Range(0, n + 1).Select(k => k == 0 || k == n ? 3 : 2).ToList());
        }

        private static double Det(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

        private static StlTriangle Tri(Vec3 a, Vec3 b, Vec3 c, bool flip) => flip ? new StlTriangle(a, c, b) : new StlTriangle(a, b, c);

        private void PolyMesh(RevitGraph g, RevitObject o, double[,] m)
        {
            if (g.Follow(F(o, "m_pFacetedTopology")) is not { ClassName: "FacetedTopology0" } t
                || F(t, "m_pointsArr") is not List<object?> points || F(t, "m_facetsArr") is not List<object?> facets) { Unjudged("poly mesh"); return; }
            try
            {
                var pts = points.Select(p => Numbers(p, 3)).Select(p => Apply(m, p[0], p[1], p[2])).ToList();
                bool flip = Det(m) < 0;
                var tris = new List<StlTriangle>();
                foreach (var f in facets)
                {
                    if (f is not List<object?> { Count: 3 } ix) throw new FormatException("not a triangle");
                    int[] k = ix.Select(x => Convert.ToInt32(x)).ToArray();
                    tris.Add(Tri(pts[k[0]], pts[k[1]], pts[k[2]], flip));
                }
                _faces.Add(new RevitFace(tris, null));
            }
            catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or InvalidCastException) { Unjudged("poly mesh"); }
        }

        /// <summary>
        /// A surface in its own coordinates (u, v; feet): where a point lies, how fine the grid must be along u and along v
        /// to follow its curvature (null: it is straight that way), and the way back from a point on it to its
        /// coordinates (given the coordinates it should be near; null: not known).
        /// </summary>
        private sealed record Surface(Func<double, double, (double X, double Y, double Z)> At, double? StepU, double? StepV, double[]? Envelope,
            Func<(double X, double Y, double Z), (double U, double V), (double U, double V)>? Back = null, BrepSurface? Shape = null)
        {
            /// <summary>How many grid steps the line from a to b in its coordinates spans (0: straight there).</summary>
            public double Steps((double U, double V) a, (double U, double V) b) =>
                Math.Max(StepU is { } su ? Math.Abs(a.U - b.U) / su : 0, StepV is { } sv ? Math.Abs(a.V - b.V) / sv : 0);
        }

        /// <summary>A point of a trimming loop: in the face's surface coordinates, and in space (feet, before the
        /// transform), the same for the faces on both sides of its edge.</summary>
        private readonly record struct LoopPoint((double X, double Y) Uv, (double X, double Y, double Z) P);

        /// <summary>An edge's points, the same for the faces on both sides: in space, and in each side's surface
        /// coordinates (null for a side without a face read).</summary>
        private sealed record EdgeSample(List<(double X, double Y, double Z)> P, List<(double X, double Y)>?[] Uv);

        private readonly Dictionary<RevitObject, Surface?> _surfaces = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<RevitObject, EdgeSample> _edges = new(ReferenceEqualityComparer.Instance);

        /// <summary>The surface of a face (read once), or null.</summary>
        private Surface? SurfaceOf(RevitGraph g, RevitObject? face)
        {
            if (face == null) return null;
            if (_surfaces.TryGetValue(face, out var known)) return known;
            Surface? s = null;
            try { if (g.Follow(F(face, "m_pSurf")) is { } surf) s = Read(g, surf); }
            catch (Exception e) when (e is FormatException or InvalidCastException) { }
            return _surfaces[face] = s;
        }

        /// <summary>
        /// An edge's points (read once for both its faces): those Revit saved, each placed by the first side's surface,
        /// and between two of them, where a side's surface curves more than its grid step, more points, placed on the
        /// surface that curves most there; the other side takes their coordinates by its way back (a plane, a cylinder),
        /// or else halfway between its own. So both faces have the very same points along it.
        /// </summary>
        private EdgeSample Sample(RevitGraph g, RevitObject edge)
        {
            if (_edges.TryGetValue(edge, out var known)) return known;
            var faces = F(edge, "m_pFace") as List<object?> ?? throw new FormatException("edge without faces");
            var ends = F(edge, "m_firstAndLastEdgePnts") as List<object?> ?? throw new FormatException("edge ends");
            var raw = new List<object?> { ends[0] };
            if (F(edge, "m_interiorEdgePnts") is List<object?> inner) raw.AddRange(inner);
            raw.Add(ends[1]);
            var surfs = new Surface?[2];
            var uv = new List<(double X, double Y)>?[2];
            for (int side = 0; side < 2; side++)
            {
                surfs[side] = side < faces.Count ? SurfaceOf(g, g.Follow(faces[side])) : null;
                if (raw.All(p => p is Dictionary<string, object?> d && d.GetValueOrDefault("uv") is List<object?> l && l.Count > side))
                    uv[side] = raw.Select(p => { var a = Numbers(((List<object?>)((Dictionary<string, object?>)p!)["uv"]!)[side], 2); return (a[0], a[1]); }).ToList();
            }
            int first = surfs[0] != null && uv[0] != null ? 0 : 1;
            if (surfs[first] == null || uv[first] == null) throw new FormatException("edge without a surface");
            var pts = new List<(double X, double Y, double Z)>();
            var outUv = new List<(double X, double Y)>?[] { uv[0] != null ? new() : null, uv[1] != null ? new() : null };
            static (double X, double Y) Lerp((double X, double Y) a, (double X, double Y) b, double t) => (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            for (int k = 0; k < raw.Count; k++)
            {
                pts.Add(surfs[first]!.At(uv[first]![k].X, uv[first]![k].Y));
                for (int side = 0; side < 2; side++) outUv[side]?.Add(uv[side]![k]);
                if (k + 1 == raw.Count) break;
                int driver = -1; double most = 1;
                for (int side = 0; side < 2; side++)
                    if (surfs[side] is { } su && uv[side] != null && su.Steps(uv[side]![k], uv[side]![k + 1]) is var st && st > most) { most = st; driver = side; }
                if (driver < 0) continue;
                int n = Math.Min(64, (int)Math.Ceiling(most));
                for (int j = 1; j < n; j++)
                {
                    double t = (double)j / n;
                    var d = Lerp(uv[driver]![k], uv[driver]![k + 1], t);
                    var q = surfs[driver]!.At(d.X, d.Y);
                    pts.Add(q);
                    for (int side = 0; side < 2; side++)
                    {
                        if (outUv[side] == null) continue;
                        if (side == driver) { outUv[side]!.Add(d); continue; }
                        var guess = Lerp(uv[side]![k], uv[side]![k + 1], t);
                        outUv[side]!.Add(surfs[side]?.Back is { } back ? back(q, guess) : guess);
                    }
                }
            }
            return _edges[edge] = new EdgeSample(pts, outUv);
        }

        /// <summary>An angle step that keeps the chord of an arc of radius <paramref name="r"/> (feet) within a tenth of
        /// an inch (2.5 mm) of it: at most 30° (a thin rod twelve-sided), at least 1°.</summary>
        private static double AngleStep(double r) =>
            Math.Clamp(r > 0 ? 2 * Math.Acos(Math.Max(-1, 1 - 0.1 / 12 / r)) : Math.PI, Math.PI / 180, Math.PI / 6);

        /// <summary>A profile curve of a surface (GLine, GArc or GEllipse), as a point at a parameter, with the angle step an
        /// arc or ellipse needs.</summary>
        private static (Func<double, double[]> At, double? Step, double[] Range, double[]? ArcCentre, double ArcRadius, NurbsCurve Nurbs) Profile(RevitObject? c)
        {
            if (c?.ClassName == "GLine")
            {
                var o = Numbers(F(c, "m_origin"), 3); var d = Numbers(F(c, "m_dirVec"), 3); var r = Numbers(F(c, "m_endParams"), 2);
                var line = new NurbsCurve(1, [new Vec3(o[0] + d[0] * r[0], o[1] + d[1] * r[0], o[2] + d[2] * r[0]), new Vec3(o[0] + d[0] * r[1], o[1] + d[1] * r[1], o[2] + d[2] * r[1])], [1, 1], [0, 1], [2, 2]);
                return (t => [o[0] + d[0] * t, o[1] + d[1] * t, o[2] + d[2] * t], null, r, null, 0, line);
            }
            if (c?.ClassName == "GArc")
            {
                var o = Numbers(F(c, "m_center"), 3); var x = Numbers(F(c, "m_xVec"), 3); var y = Numbers(F(c, "m_yVec"), 3);
                double rad = Convert.ToDouble(F(c, "m_radius")); var r = Numbers(F(c, "m_endParams"), 2);
                var arc = EllipseArc(new Vec3(o[0], o[1], o[2]), new Vec3(x[0], x[1], x[2]) * rad, new Vec3(y[0], y[1], y[2]) * rad, r[0], r[1]);
                return (t => [o[0] + rad * (Math.Cos(t) * x[0] + Math.Sin(t) * y[0]), o[1] + rad * (Math.Cos(t) * x[1] + Math.Sin(t) * y[1]), o[2] + rad * (Math.Cos(t) * x[2] + Math.Sin(t) * y[2])], AngleStep(rad), r, o, rad, arc);
            }
            if (c?.ClassName == "GEllipse")
            {
                var o = Numbers(F(c, "m_center"), 3); var x = Numbers(F(c, "m_xVec"), 3); var y = Numbers(F(c, "m_yVec"), 3);
                double a = Convert.ToDouble(F(c, "m_xLen")), b = Convert.ToDouble(F(c, "m_yLen")); var r = Numbers(F(c, "m_endParams"), 2);
                var ell = EllipseArc(new Vec3(o[0], o[1], o[2]), new Vec3(x[0], x[1], x[2]) * a, new Vec3(y[0], y[1], y[2]) * b, r[0], r[1]);
                return (t => [o[0] + a * Math.Cos(t) * x[0] + b * Math.Sin(t) * y[0], o[1] + a * Math.Cos(t) * x[1] + b * Math.Sin(t) * y[1], o[2] + a * Math.Cos(t) * x[2] + b * Math.Sin(t) * y[2]], AngleStep(Math.Max(a, b)), r, null, 0, ell);
            }
            throw new FormatException("profile curve " + (c?.ClassName ?? "missing"));
        }

        /// <summary>The surfaces read (as rvt-rs's native_parametric_surface evaluates them), or null for another kind.</summary>
        private static Surface? Read(RevitGraph g, RevitObject s)
        {
            double[]? Envelope() => F(s, "m_Envelope") is Dictionary<string, object?> e && e.GetValueOrDefault("m_corners") is List<object?> { Count: 2 } c
                ? [Numbers(c[0], 2)[0], Numbers(c[0], 2)[1], Numbers(c[1], 2)[0], Numbers(c[1], 2)[1]] : null;
            switch (s.ClassName)
            {
                case "Plane":
                {
                    var o = Numbers(F(s, "m_origin"), 3); var x = Numbers(F(s, "m_xVec"), 3); var y = Numbers(F(s, "m_yVec"), 3);
                    double xx = x[0] * x[0] + x[1] * x[1] + x[2] * x[2], xy = x[0] * y[0] + x[1] * y[1] + x[2] * y[2], yy = y[0] * y[0] + y[1] * y[1] + y[2] * y[2], det = xx * yy - xy * xy;
                    return new Surface((u, v) => (o[0] + u * x[0] + v * y[0], o[1] + u * x[1] + v * y[1], o[2] + u * x[2] + v * y[2]), null, null, null, (p, _) =>
                    {
                        // (the point projected on the plane, in its coordinates: x and y need not be square or of unit length)
                        double dx = p.X - o[0], dy = p.Y - o[1], dz = p.Z - o[2];
                        double px = dx * x[0] + dy * x[1] + dz * x[2], py = dx * y[0] + dy * y[1] + dz * y[2];
                        return ((px * yy - py * xy) / det, (py * xx - px * xy) / det);
                    });
                }
                case "CylSurf":
                {
                    var c = Numbers(F(s, "m_center"), 3); var x = Numbers(F(s, "m_xVec"), 3); var y = Numbers(F(s, "m_yVec"), 3); var z = Numbers(F(s, "m_zVec"), 3);
                    double r = Convert.ToDouble(F(s, "m_radius"));
                    return new Surface((u, v) =>
                    {
                        double cu = Math.Cos(u), su = Math.Sin(u);
                        return (c[0] + r * (cu * x[0] + su * y[0]) + v * z[0], c[1] + r * (cu * x[1] + su * y[1]) + v * z[1], c[2] + r * (cu * x[2] + su * y[2]) + v * z[2]);
                    }, AngleStep(r), null, Envelope(), (p, near) =>
                    {
                        // (the angle round the axis, the turn nearest to where it should be; the height along it)
                        double dx = p.X - c[0], dy = p.Y - c[1], dz = p.Z - c[2];
                        double u = Math.Atan2(dx * y[0] + dy * y[1] + dz * y[2], dx * x[0] + dy * x[1] + dz * x[2]);
                        u += 2 * Math.PI * Math.Round((near.U - u) / (2 * Math.PI));
                        return (u, dx * z[0] + dy * z[1] + dz * z[2]);
                    }, new BrepCylinder(new Vec3(c[0], c[1], c[2]), new Vec3(z[0], z[1], z[2]), new Vec3(x[0], x[1], x[2]), r));
                }
                case "SurfRev":
                {
                    var c = Numbers(F(s, "m_center"), 3); var x = Numbers(F(s, "m_xVec"), 3); var y = Numbers(F(s, "m_yVec"), 3); var z = Numbers(F(s, "m_zVec"), 3);
                    var p = Profile(g.Follow(F(s, "m_pProfileCurve")));
                    // (the profile in the surface's own frame, turned round its z by u)
                    double reach = Enumerable.Range(0, 9).Select(i => p.At(p.Range[0] + (p.Range[1] - p.Range[0]) * i / 8)).Max(q => Math.Sqrt(q[0] * q[0] + q[1] * q[1]));
                    // (exactly: a straight profile parallel to the axis a cylinder, square to it a plane; an arc round a point
                    // on the axis a sphere, round one beside it in the same half plane a torus; the profile in the plane of
                    // local x and z)
                    BrepSurface? exact = null;
                    var vz = new Vec3(z[0], z[1], z[2]); var vx = new Vec3(x[0], x[1], x[2]);
                    Vec3 V3(double[] a) => new Vec3(a[0], a[1], a[2]);
                    double[] At3(double[] l) => [c[0] + x[0] * l[0] + y[0] * l[1] + z[0] * l[2], c[1] + x[1] * l[0] + y[1] * l[1] + z[1] * l[2], c[2] + x[2] * l[0] + y[2] * l[1] + z[2] * l[2]];
                    var q0 = p.At(p.Range[0]); var q1 = p.At(p.Range[1]);
                    const double tol = 1e-9;
                    if (p.ArcCentre is { } ac)
                    {
                        if (Math.Abs(ac[1]) < tol && Math.Abs(q0[1]) < tol && Math.Abs(q1[1]) < tol)
                            exact = Math.Abs(ac[0]) < tol
                                ? new BrepSphere(V3(At3([0, 0, ac[2]])), p.ArcRadius, vz, vx)
                                : ac[0] > p.ArcRadius - tol ? new BrepTorus(V3(At3([0, 0, ac[2]])), vz, vx, ac[0], p.ArcRadius) : null;
                    }
                    else if (p.Step == null && Math.Abs(q0[1]) < tol && Math.Abs(q1[1]) < tol)
                    {
                        if (Math.Abs(q0[0] - q1[0]) < tol && q0[0] > tol) exact = new BrepCylinder(V3(c), vz, vx, q0[0]);
                        else if (Math.Abs(q0[2] - q1[2]) < tol) exact = new BrepPlane(V3(At3([0, 0, q0[2]])), vz, vx);
                    }
                    // (any other profile: the surface of revolution exactly as a NURBS surface, over the turn its envelope spans)
                    if (exact == null && Envelope() is { } env && env[2] > env[0] && env[2] - env[0] <= 2 * Math.PI + 1e-9)
                        exact = Revolve(p.Nurbs, env[0], env[2], c, x, y, z);
                    return new Surface((u, v) =>
                    {
                        var q = p.At(v);
                        double l0 = q[0] * Math.Cos(u) - q[1] * Math.Sin(u), l1 = q[0] * Math.Sin(u) + q[1] * Math.Cos(u), l2 = q[2];
                        return (c[0] + x[0] * l0 + y[0] * l1 + z[0] * l2, c[1] + x[1] * l0 + y[1] * l1 + z[1] * l2, c[2] + x[2] * l0 + y[2] * l1 + z[2] * l2);
                    }, AngleStep(reach), p.Step, Envelope(), null, exact);
                }
                case "RuledSurf":
                {
                    var a = Profile(g.Follow(F(s, "m_pProfileCurve1")));
                    var b = Profile(g.Follow(F(s, "m_pProfileCurve2")));
                    // (u from 0 to 1 along both curves, v from the first to the second)
                    double? step = a.Step is null && b.Step is null ? null
                        : Math.Min(a.Step is { } sa ? sa / Math.Max(1e-9, Math.Abs(a.Range[1] - a.Range[0])) : 1, b.Step is { } sb ? sb / Math.Max(1e-9, Math.Abs(b.Range[1] - b.Range[0])) : 1);
                    return new Surface((u, v) =>
                    {
                        var p = a.At(a.Range[0] + u * (a.Range[1] - a.Range[0])); var q = b.At(b.Range[0] + u * (b.Range[1] - b.Range[0]));
                        return (p[0] * (1 - v) + q[0] * v, p[1] * (1 - v) + q[1] * v, p[2] * (1 - v) + q[2] * v);
                    }, step, null, Envelope());
                }
                default:
                    return null;
            }
        }

        private void Face(RevitGraph g, RevitObject face, double[,] m)
        {
            report.Faces++;
            if (g.Follow(F(face, "m_pSurf")) is not { } surf) { Unjudged("face without surface"); return; }
            if (g.Follow(F(face, "m_pFirstLoop")) is not { } first) return; // (no trimming loop: an auxiliary surface)
            try
            {
                var s = Read(g, surf);
                if (s == null)
                {
                    report.SkippedSurfaces[surf.ClassName] = report.SkippedSurfaces.GetValueOrDefault(surf.ClassName) + 1;
                    return;
                }
                bool orient = (F(surf, "m_orientFlag") is true) ^ ((Convert.ToUInt32(F(face, "m_faceFlags_v9")) & 2) != 0);
                var loops = new List<List<LoopPoint>>();
                var seen = new HashSet<RevitObject>();
                for (RevitObject? loop = first; loop != null; loop = g.Follow(F(loop, "m_nextLoop")))
                {
                    if (!seen.Add(loop)) throw new FormatException("loop cycle");
                    loops.Add(Ring(g, face, loop));
                }
                var uvs = loops.Select(l => l.Select(p => p.Uv).ToList()).ToList();
                // (rings inside an even number of others are outer rings; those one deeper inside them, their holes)
                int Depth(int i) => uvs.Where((r, j) => j != i && Inside(uvs[i][0], r)).Count();
                var depths = loops.Select((_, i) => Depth(i)).ToList();
                // (counter-clockwise in u, v faces along ∂u × ∂v: the saved orientation says whether that is outward)
                bool flip = !orient ^ (Det(m) < 0);
                for (int i = 0; i < loops.Count; i++)
                {
                    if (depths[i] % 2 != 0) continue;
                    var rings = new List<List<LoopPoint>> { loops[i] };
                    for (int j = 0; j < loops.Count; j++)
                        if (depths[j] == depths[i] + 1 && Inside(uvs[j][0], uvs[i])) rings.Add(loops[j]);
                    var region = rings.Select(r => r.Select(p => p.Uv).ToList()).ToList();
                    // (the boundary's points where its edges put them, the same as the faces beside it; the points inside on the surface)
                    var onEdges = rings.SelectMany(r => r).Select(p => Apply(m, p.P.X, p.P.Y, p.P.Z)).ToList();
                    Vec3 At(int k, (double X, double Y) uv) { if (k < onEdges.Count) return onEdges[k]; var p = s.At(uv.X, uv.Y); return Apply(m, p.X, p.Y, p.Z); }
                    var (pts, tris) = Mesh(region, s);
                    var at = pts.Select((uv, k) => At(k, uv)).ToList();
                    var triangles = new List<StlTriangle>();
                    foreach (var (a, b, c) in tris)
                        // (none where the surface folds to a point, at a pole)
                        if ((at[b] - at[a]).Cross(at[c] - at[a]).Length > 1e-12) triangles.Add(Tri(at[a], at[b], at[c], flip));
                    if (triangles.Count == 0) continue;
                    var seen0 = report.BySurface.GetValueOrDefault(surf.ClassName);
                    report.BySurface[surf.ClassName] = (seen0.Faces + 1, seen0.Triangles + triangles.Count);
                    // (the rings in space; for a curved face only with its exact surface, else it stays its triangles)
                    bool planar = s.StepU == null && s.StepV == null;
                    var exact = planar ? null : Placed(s.Shape, m);
                    IReadOnlyList<IReadOnlyList<Vec3>>? ringsInSpace = null;
                    if (planar || exact != null)
                    {
                        int k0 = 0;
                        var list = new List<IReadOnlyList<Vec3>>();
                        foreach (var r in rings) { list.Add(onEdges.GetRange(k0, r.Count)); k0 += r.Count; }
                        ringsInSpace = list;
                    }
                    _faces.Add(new RevitFace(triangles, ringsInSpace, exact));
                }
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or ArgumentException or InvalidOperationException) { Unjudged(surf.ClassName + " face: " + e.Message); }
        }

        /// <summary>
        /// A region (an outer ring and its holes, in u, v) as triangles: triangulated by its own points; on a curved
        /// surface then made as near to Delaunay as its boundary allows (diagonals turned, measured as lengths on the
        /// surface, so a cylinder's side becomes a strip from rim to rim), and every inner edge longer than the surface's
        /// grid step that is the longest of its triangles split in its middle, until none is. Its boundary keeps exactly
        /// the points it has, the points the faces next to it share (see <see cref="Sample"/>), so the faces meet without
        /// gaps.
        /// </summary>
        private static (List<(double X, double Y)> Points, List<(int A, int B, int C)> Triangles) Mesh(List<List<(double X, double Y)>> region, Surface s)
        {
            var pts = region.SelectMany(r => r).ToList();
            var tris = Triangulator.Triangulate(region.Cast<IReadOnlyList<(double X, double Y)>>().ToList()).ToList();
            if (s.StepU == null && s.StepV == null) return (pts, tris);
            // (how long a step in u and in v is on the surface, near the middle of the region)
            double cu = (pts.Min(p => p.X) + pts.Max(p => p.X)) / 2, cv = (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2;
            double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
            const double h = 1e-4;
            double su = Math.Max(1e-9, Dist(s.At(cu + h, cv), s.At(cu - h, cv)) / (2 * h)), sv = Math.Max(1e-9, Dist(s.At(cu, cv + h), s.At(cu, cv - h)) / (2 * h));
            (double X, double Y) M(int i) => (pts[i].X * su, pts[i].Y * sv);
            static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
            var boundary = new HashSet<(int, int)>();
            int first = 0;
            foreach (var r in region)
            {
                for (int i = 0; i < r.Count; i++) boundary.Add(Key(first + i, first + (i + 1) % r.Count));
                first += r.Count;
            }
            var edges = new Dictionary<(int, int), List<int>>();
            void Link(int t)
            {
                var (a, b, c) = tris[t];
                foreach (var e in new[] { Key(a, b), Key(b, c), Key(c, a) })
                {
                    if (!edges.TryGetValue(e, out var l)) edges[e] = l = new List<int>(2);
                    l.Add(t);
                }
            }
            void Unlink(int t)
            {
                var (a, b, c) = tris[t];
                foreach (var e in new[] { Key(a, b), Key(b, c), Key(c, a) })
                    if (edges.TryGetValue(e, out var l)) { l.Remove(t); if (l.Count == 0) edges.Remove(e); }
            }
            for (int t = 0; t < tris.Count; t++) Link(t);
            static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            // (whether d lies inside the circle through a, b, c, counter-clockwise; in the surface's lengths)
            bool InCircle(int a, int b, int c, int d)
            {
                var (pa, pb, pc, pd) = (M(a), M(b), M(c), M(d));
                double ax = pa.X - pd.X, ay = pa.Y - pd.Y, bx = pb.X - pd.X, by = pb.Y - pd.Y, cx = pc.X - pd.X, cy = pc.Y - pd.Y;
                double det = (ax * ax + ay * ay) * (bx * cy - cx * by) - (bx * bx + by * by) * (ax * cy - cx * ay) + (cx * cx + cy * cy) * (ax * by - bx * ay);
                return det > 1e-12 * Math.Max(1, Math.Abs(ax * by - bx * ay));
            }
            // (turn diagonals until no triangle's circle holds the point across from it; the boundary's edges stay)
            void Flip(IEnumerable<(int, int)> start)
            {
                var todo = new Stack<(int, int)>(start);
                var queued = new HashSet<(int, int)>(todo);
                int guard = 50 * tris.Count + 1000;
                while (todo.Count > 0 && guard-- > 0)
                {
                    var e = todo.Pop(); queued.Remove(e);
                    if (boundary.Contains(e) || !edges.TryGetValue(e, out var adj) || adj.Count != 2) continue;
                    int t1 = adj[0], t2 = adj[1];
                    // (t1 turned to a b c with the edge a → b; t2 holds b a d)
                    var (a, b, c) = Turn(tris[t1], e);
                    int d = Third(tris[t2], a, b);
                    if (d < 0 || !InCircle(a, b, c, d)) continue;
                    // (only where the four points make a convex shape: then c d crosses a b)
                    if (Cross(M(c), M(d), M(a)) * Cross(M(c), M(d), M(b)) >= 0 || Cross(M(a), M(b), M(c)) * Cross(M(a), M(b), M(d)) >= 0) continue;
                    Unlink(t1); Unlink(t2);
                    tris[t1] = (a, d, c); tris[t2] = (d, b, c);
                    Link(t1); Link(t2);
                    foreach (var n in new[] { Key(a, d), Key(d, b), Key(b, c), Key(c, a) })
                        if (queued.Add(n)) todo.Push(n);
                }
            }
            static (int, int, int) Turn((int A, int B, int C) t, (int, int) e) =>
                Key(t.A, t.B) == e ? (t.A, t.B, t.C) : Key(t.B, t.C) == e ? (t.B, t.C, t.A) : (t.C, t.A, t.B);
            int Third((int A, int B, int C) t, int a, int b) => t.A != a && t.A != b ? t.A : t.B != a && t.B != b ? t.B : t.C != a && t.C != b ? t.C : -1;
            Flip(edges.Keys.ToList());
            // (then split what is still too long for the curve: an edge only where it is the longest of the triangles on
            // either side, so every split makes them smaller)
            double Need((int A, int B) e) => s.Steps(pts[e.A], pts[e.B]);
            bool Longest((int, int) e, int t)
            {
                var (a, b, c) = tris[t];
                double n = Need(e);
                return Need(Key(a, b)) <= n && Need(Key(b, c)) <= n && Need(Key(c, a)) <= n;
            }
            for (int round = 0; round < 12; round++)
            {
                var split = edges.Where(kv => !boundary.Contains(kv.Key) && Need(kv.Key) > 1 && kv.Value.All(t => Longest(kv.Key, t)))
                    .Select(kv => kv.Key).OrderByDescending(Need).ToList();
                if (split.Count == 0 || tris.Count > 200_000) break;
                var changed = new List<(int, int)>();
                foreach (var e in split)
                {
                    if (!edges.TryGetValue(e, out var adjacent) || !adjacent.All(t => Longest(e, t))) continue;
                    int m = pts.Count;
                    pts.Add(((pts[e.Item1].X + pts[e.Item2].X) / 2, (pts[e.Item1].Y + pts[e.Item2].Y) / 2));
                    foreach (int t in adjacent.ToList())
                    {
                        // (the triangle turned so the edge runs x → y in its own order; it becomes x m z and m y z)
                        var (x, y, z) = Turn(tris[t], e);
                        Unlink(t);
                        tris[t] = (x, m, z); Link(t);
                        tris.Add((m, y, z)); Link(tris.Count - 1);
                        changed.Add(Key(x, z)); changed.Add(Key(y, z)); changed.Add(Key(m, z));
                    }
                }
                Flip(changed);
            }
            return (pts, tris);
        }

        /// <summary>A trimming loop's points (see <see cref="Sample"/>): its edges in order, each on this face's side of it.
        /// An edge that ends where the next begins in space but not in the surface's coordinates (across a pole, where a
        /// line of them is one point) joins it with that line.</summary>
        private List<LoopPoint> Ring(RevitGraph g, RevitObject face, RevitObject loop)
        {
            if (loop.ClassName is not ("EdgeLoop" or "EdgeLoopWithChainEnvelopes")) throw new FormatException("unknown loop " + loop.ClassName);
            if (F(loop, "m_open") is true) throw new FormatException("open loop");
            var segments = new List<List<LoopPoint>>();
            var seen = new HashSet<RevitObject>();
            for (var cur = g.Follow(F(loop, "m_next")); cur != null && !ReferenceEquals(cur, loop); )
            {
                if (!seen.Add(cur) || cur.ClassName != "Edge") throw new FormatException("broken edge chain");
                var sidesFaces = F(cur, "m_pFace") as List<object?> ?? throw new FormatException("edge without faces");
                int side = -1;
                for (int k = 0; k < sidesFaces.Count; k++) if (ReferenceEquals(g.Follow(sidesFaces[k]), face)) { if (side >= 0) throw new FormatException("edge on both sides"); side = k; }
                if (side < 0) throw new FormatException("edge not on the face");
                var sample = Sample(g, cur);
                var uv = sample.Uv[side] ?? throw new FormatException("edge point");
                segments.Add(uv.Select((p, k) => new LoopPoint(p, sample.P[k])).ToList());
                cur = F(cur, "m_next") is List<object?> nx && nx.Count > side ? g.Follow(nx[side]) : null;
            }
            if (segments.Count < 2) throw new FormatException("too few edges");
            static bool Close(LoopPoint a, LoopPoint b) => Math.Abs(a.Uv.X - b.Uv.X) + Math.Abs(a.Uv.Y - b.Uv.Y) < 1e-7;
            static bool Meets(LoopPoint a, LoopPoint b) => Close(a, b) || Math.Abs(a.P.X - b.P.X) + Math.Abs(a.P.Y - b.P.Y) + Math.Abs(a.P.Z - b.P.Z) < 1e-7;
            foreach (bool reverse in new[] { false, true })
            {
                var output = new List<LoopPoint>(segments[0]);
                if (reverse) output.Reverse();
                bool ok = true;
                foreach (var seg in segments.Skip(1))
                {
                    if (Close(output[^1], seg[0])) output.AddRange(seg.Skip(1));
                    else if (Close(output[^1], seg[^1])) output.AddRange(Enumerable.Reverse(seg).Skip(1));
                    else if (Meets(output[^1], seg[0])) output.AddRange(seg);
                    else if (Meets(output[^1], seg[^1])) output.AddRange(Enumerable.Reverse(seg));
                    else { ok = false; break; }
                }
                if (ok && Meets(output[0], output[^1]))
                {
                    if (Close(output[0], output[^1])) output.RemoveAt(output.Count - 1);
                    var clean = new List<LoopPoint>();
                    foreach (var p in output) if (clean.Count == 0 || !Close(clean[^1], p)) clean.Add(p);
                    if (clean.Count >= 3) return clean;
                }
            }
            throw new FormatException("loop does not close");
        }

        private static bool Inside((double X, double Y) p, List<(double X, double Y)> ring)
        {
            bool hit = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y) && p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X) hit = !hit;
            return hit;
        }
    }
}
