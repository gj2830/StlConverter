using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using static RsDocx.Core.Step.StepFile;

namespace RsDocx.Core.Step;

/// <summary>
/// Reads a STEP file (ISO 10303-21; AP203, AP214, AP242 alike) into bodies: solids (MANIFOLD_SOLID_BREP, BREP_WITH_VOIDS),
/// faces without thickness (SHELL_BASED_SURFACE_MODEL), their names and face colours (STYLED_ITEM on a face or a whole
/// body), and loose lines (TRIMMED_CURVEs of lines and circles in a GEOMETRIC_CURVE_SET). Surfaces: plane, cylinder,
/// cone, sphere, torus, B-spline; curves: line, circle, ellipse, B-spline (also wrapped in SURFACE_CURVE, SEAM_CURVE or
/// INTERSECTION_CURVE). Lengths in any unit (millimetres, metres, inches) come out in metres. What it cannot read it
/// says, by name.
/// </summary>
public static class StepReader
{
    public static StepModel Read(string path) => Parse(File.ReadAllText(path));

    public static StepModel Parse(string text)
    {
        var file = StepFile.Parse(text);
        var r = new Reader(file);
        var bodies = new List<StepBody>();
        foreach (var e in file.Entities.Values.OrderBy(x => x.Id))
        {
            if (e.Is("MANIFOLD_SOLID_BREP") || e.Is("BREP_WITH_VOIDS")) bodies.Add(r.Solid(e));
            else if (e.Is("SHELL_BASED_SURFACE_MODEL")) bodies.Add(r.Sheet(e));
            else if (e.Is("FACETED_BREP")) throw new StepException("Dit STEP-bestand heeft een lichaam van alleen driehoekjes (FACETED_BREP); open het als STL. / This STEP file has a body of triangles only (FACETED_BREP); open it as STL.");
        }
        var colours = r.Colours(bodies);
        bodies = bodies.Select((b, i) => b with { FaceColors = colours[i].Count > 0 ? colours[i] : null }).ToList();
        // each body where it stands in the assembly (as often as its part is used there), named after its part when it
        // has no name of its own
        // the assemblies between a part and the top become groups (one per place in the tree: an assembly used twice is
        // two groups, the same part placed twice), each before the groups in it
        var placed = new List<StepBody>();
        var groups = new List<StepGroup>();
        var groupAt = new Dictionary<string, int>();
        var groupRep = new List<int>();
        int? GroupFor(List<(int Rep, int Rel, Move? Up)> path, int k)
        {
            // (path: the assemblies from the part up; the last is the top, the file itself: no group. Which use of an
            // assembly: the uses above it)
            if (k >= path.Count - 1) return null;
            string key = path[k].Rep + "@" + string.Join("/", path.Skip(k + 1).Select(p => p.Rel));
            if (groupAt.TryGetValue(key, out int g)) return g;
            int? parent = GroupFor(path, k + 1);
            groups.Add(new StepGroup(r.PartName(path[k].Rep) ?? "", parent, Place: Place(path[k].Up)));
            groupRep.Add(path[k].Rep);
            return groupAt[key] = groups.Count - 1;
        }
        for (int i = 0; i < bodies.Count; i++)
        {
            var uses = r.Placements(r.BodyIds[i]);
            for (int k = 0; k < uses.Count; k++)
            {
                var (move, part, path) = uses[k];
                string name = bodies[i].Name.Length > 0 ? bodies[i].Name : part ?? "";
                if (uses.Count > 1 && name.Length > 0) name += $" ({k + 1})";
                placed.Add(bodies[i] with { Body = move is { } m ? Moved(bodies[i].Body, m) : bodies[i].Body, Name = name, Group = GroupFor(path, 0) });
            }
        }
        // an assembly placed more than once: a part shared by those groups
        var shared = groupRep.GroupBy(x => x).Where(x => x.Count() > 1).Select((x, i) => (x.Key, i)).ToDictionary(x => x.Key, x => x.i);
        groups = groups.Select((g, i) => shared.TryGetValue(groupRep[i], out int s) ? g with { Shared = s } : g with { Place = null }).ToList();
        return new StepModel(placed, r.Curves(), groups);
    }

    /// <summary>A move as a place (none: where it is).</summary>
    private static StepPlace Place(Move? m) => m is null ? StepPlace.Identity
        : new StepPlace(m.Point(new Vec3(0, 0, 0)), m.Dir(new Vec3(1, 0, 0)), m.Dir(new Vec3(0, 1, 0)), m.Dir(new Vec3(0, 0, 1)));

    /// <summary>A placement: a point and three directions (x, y, z).</summary>
    private readonly record struct Frame(Vec3 O, Vec3 X, Vec3 Y, Vec3 Z)
    {
        /// <summary>A point given in this frame, in the frame it is placed in.</summary>
        public Vec3 Out(Vec3 p) => O + X * p.X + Y * p.Y + Z * p.Z;
        /// <summary>A point given where this frame is placed, in this frame.</summary>
        public Vec3 In(Vec3 p) { var d = p - O; return new Vec3(d.Dot(X), d.Dot(Y), d.Dot(Z)); }
    }

    /// <summary>A rigid move: a point, and a direction.</summary>
    private sealed record Move(Func<Vec3, Vec3> Point, Func<Vec3, Vec3> Dir);

    /// <summary>A body moved (turned and shifted; its shapes all along).</summary>
    private static BrepBody Moved(BrepBody b, Move m)
    {
        BrepCurve C(BrepCurve c) => c switch
        {
            BrepLine => c,
            BrepCircle ci => ci with { Centre = m.Point(ci.Centre), Normal = m.Dir(ci.Normal), XAxis = m.Dir(ci.XAxis) },
            BrepEllipse el => el with { Centre = m.Point(el.Centre), Normal = m.Dir(el.Normal), XAxis = m.Dir(el.XAxis) },
            BrepBSpline bs => bs with { ControlPoints = bs.ControlPoints.Select(m.Point).ToList() },
            _ => c,
        };
        BrepSurface S(BrepSurface s) => s switch
        {
            BrepPlane p => p with { Origin = m.Point(p.Origin), Normal = m.Dir(p.Normal), XAxis = m.Dir(p.XAxis) },
            BrepCylinder c => c with { Origin = m.Point(c.Origin), Axis = m.Dir(c.Axis), XAxis = m.Dir(c.XAxis) },
            BrepCone k => k with { Origin = m.Point(k.Origin), Axis = m.Dir(k.Axis), XAxis = m.Dir(k.XAxis) },
            BrepSphere sp => sp with { Centre = m.Point(sp.Centre), Axis = m.Dir(sp.Axis), XAxis = m.Dir(sp.XAxis) },
            BrepTorus t => t with { Centre = m.Point(t.Centre), Axis = m.Dir(t.Axis), XAxis = m.Dir(t.XAxis) },
            BrepNurbsSurface n => n with { Points = n.Points.Select(m.Point).ToList() },
            _ => s,
        };
        return new BrepBody
        {
            Vertices = b.Vertices.Select(m.Point).ToList(),
            Edges = b.Edges.Select(e => e with { Curve = C(e.Curve) }).ToList(),
            Faces = b.Faces.Select(f => f with { Surface = S(f.Surface) }).ToList(),
            Sheet = b.Sheet,
        };
    }

    private sealed class Reader(StepFile f)
    {
        private readonly double _len = LengthScale(f), _angle = AngleScale(f);
        // per body being read: its vertices, edges and faces
        private List<Vec3> _verts = new();
        private List<BrepEdge> _edges = new();
        private Dictionary<int, int> _vIndex = new(), _eIndex = new();
        private Dictionary<int, bool> _eFlipped = new();
        /// <summary>The STEP id of each vertex and edge read (to keep them in the file's order).</summary>
        private List<int> _vEnt = new(), _eEnt = new();
        /// <summary>Per body read, the STEP id of each face (for the colours).</summary>
        public readonly List<List<int>> FaceIds = new();
        public readonly List<int> BodyIds = new();

        private Entity E(object? o) => o is StepRef r ? f[r] : throw new StepException("Een verwijzing ontbreekt in het STEP-bestand. / A reference is missing in the STEP file.");
        private static string Name(Entity e) => e.Args.Count > 0 && e.Args[0] is string s ? s : "";
        private static double D(object? o) => o switch
        {
            double d => d,
            Typed t when t.Args.Count == 1 && t.Args[0] is double d => d,
            _ => throw new StepException("Een getal ontbreekt in het STEP-bestand. / A number is missing in the STEP file."),
        };
        private static bool B(object? o) => o is StepFile.Enum { Value: "T" };
        private static IReadOnlyList<object?> L(object? o) => o as IReadOnlyList<object?> ?? [];

        public StepBody Solid(Entity e)
        {
            Start(e);
            var faces = new List<BrepFace>(); var ids = new List<int>();
            var shells = e.Is("BREP_WITH_VOIDS") ? L(e.Part("BREP_WITH_VOIDS")![2]).Prepend(e.Args[1]) : [e.Args[1]];
            foreach (var s in shells) Shell(E(s), faces, ids, false);
            FaceIds.Add(ids); BodyIds.Add(e.Id);
            return new StepBody(Compact(faces, false), Name(e));
        }

        public StepBody Sheet(Entity e)
        {
            Start(e);
            var faces = new List<BrepFace>(); var ids = new List<int>();
            foreach (var s in L(e.Args[1])) Shell(E(s), faces, ids, false);
            FaceIds.Add(ids); BodyIds.Add(e.Id);
            return new StepBody(Compact(faces, true), Name(e));
        }

        /// <summary>The body, its vertices and edges in the order the file has them, without the vertices nothing uses (the
        /// one a STEP ring edge has, which a ring here has not).</summary>
        private BrepBody Compact(List<BrepFace> faces, bool sheet)
        {
            // a closed edge whose vertex nothing else uses: a ring without vertices, as the rest keeps rings (one that
            // meets a seam, as a hole OpenCASCADE writes, keeps its vertex)
            var uses = new Dictionary<int, int>();
            foreach (var x in _edges) if (x.Start >= 0) { uses[x.Start] = uses.GetValueOrDefault(x.Start) + 1; if (x.End != x.Start) uses[x.End] = uses.GetValueOrDefault(x.End) + 1; }
            foreach (var p in faces.SelectMany(x => x.Points)) uses[p] = uses.GetValueOrDefault(p) + 1;
            for (int i = 0; i < _edges.Count; i++)
                if (_edges[i] is { Curve: not BrepLine } x && x.Start >= 0 && x.Start == x.End && uses[x.Start] == 1)
                    _edges[i] = x with { Start = -1, End = -1 };
            var used = _edges.SelectMany(x => new[] { x.Start, x.End }).Concat(faces.SelectMany(x => x.Points)).Where(v => v >= 0).Distinct().OrderBy(v => _vEnt[v]).ToList();
            var map = used.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
            int M(int v) => v < 0 ? v : map[v];
            var edgeOrder = Enumerable.Range(0, _edges.Count).OrderBy(e => _eEnt[e]).ToList();
            var emap = edgeOrder.Select((e, i) => (e, i)).ToDictionary(x => x.e, x => x.i);
            return new BrepBody
            {
                Vertices = used.Select(v => _verts[v]).ToList(),
                Edges = edgeOrder.Select(e => _edges[e] with { Start = M(_edges[e].Start), End = M(_edges[e].End) }).ToList(),
                Faces = faces.Select(x => x with
                {
                    Loops = x.Loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Select(c => new BrepCoedge(emap[c.Edge], c.Forward)).ToList()).ToList(),
                    Points = x.Points.Select(M).ToList(),
                }).ToList(),
                Sheet = sheet,
            };
        }

        private void Start(Entity e) { _verts = new(); _edges = new(); _vIndex = new(); _eIndex = new(); _eFlipped = new(); _vEnt = new(); _eEnt = new(); }

        private void Shell(Entity s, List<BrepFace> faces, List<int> ids, bool flip)
        {
            if (s.Is("ORIENTED_CLOSED_SHELL") || s.Is("ORIENTED_OPEN_SHELL")) { Shell(E(s.Args[2]), faces, ids, flip != !B(s.Args[3])); return; }
            if (!(s.Is("CLOSED_SHELL") || s.Is("OPEN_SHELL"))) throw Unknown(s);
            foreach (var fr in L(s.Args[1])) { var fe = E(fr); faces.Add(Face(fe, flip)); ids.Add(fe.Id); }
        }

        private BrepFace Face(Entity fe, bool flip)
        {
            if (!(fe.Is("ADVANCED_FACE") || fe.Is("FACE_SURFACE"))) throw Unknown(fe);
            var surface = Surface(E(fe.Args[2]));
            bool reversed = !B(fe.Args[3]) != flip;
            var loops = new List<(bool Outer, List<BrepCoedge> Co)>(); var points = new List<int>();
            foreach (var br in L(fe.Args[1]))
            {
                var bound = E(br);
                var loop = E(bound.Args[1]);
                bool along = B(bound.Args[2]) != flip;
                if (loop.Is("VERTEX_LOOP")) { points.Add(Vertex(E(loop.Args[1]))); continue; }
                if (!loop.Is("EDGE_LOOP")) throw Unknown(loop);
                var co = L(loop.Args[1]).Select(o => Coedge(E(o))).ToList();
                if (!along) co = co.AsEnumerable().Reverse().Select(c => new BrepCoedge(c.Edge, !c.Forward)).ToList();
                loops.Add((bound.Is("FACE_OUTER_BOUND"), co));
            }
            // (the outer loop first, as the rest expects)
            var ordered = loops.Where(l => l.Outer).Concat(loops.Where(l => !l.Outer)).Select(l => (IReadOnlyList<BrepCoedge>)l.Co).ToList();
            return new BrepFace(surface, reversed, ordered) { Points = points };
        }

        private BrepCoedge Coedge(Entity oe)
        {
            if (!oe.Is("ORIENTED_EDGE")) throw Unknown(oe);
            var ec = E(oe.Args[3]);
            int e = Edge(ec);
            // (an edge kept against its curve's direction: the use of it the other way round)
            return new BrepCoedge(e, B(oe.Args[4]));
        }

        private int Vertex(Entity v)
        {
            if (_vIndex.TryGetValue(v.Id, out int i)) return i;
            if (!v.Is("VERTEX_POINT")) throw Unknown(v);
            _vIndex[v.Id] = _verts.Count;
            _vEnt.Add(v.Id);
            _verts.Add(Point(E(v.Args[1])));
            return _verts.Count - 1;
        }

        private int Edge(Entity ec)
        {
            if (_eIndex.TryGetValue(ec.Id, out int i)) return i;
            if (!ec.Is("EDGE_CURVE")) throw Unknown(ec);
            var v1 = E(ec.Args[1]); var v2 = E(ec.Args[2]);
            bool sense = B(ec.Args[4]);
            int a = Vertex(v1), b = Vertex(v2);
            Vec3 pa = _verts[a], pb = _verts[b];
            var curve = Curve(E(ec.Args[3]), pa, pb, sense, v1.Id == v2.Id);
            // (a closed curve from a vertex back to it: kept with its vertex for now; a ring at the end if nothing else
            // uses that vertex)
            _eIndex[ec.Id] = _edges.Count;
            _eEnt.Add(ec.Id);
            _edges.Add(new BrepEdge(a, b, curve));
            return _edges.Count - 1;
        }

        public Vec3 Point(Entity p)
        {
            if (!p.Is("CARTESIAN_POINT")) throw Unknown(p);
            var c = L(p.Args[1]).Select(D).ToList();
            return new Vec3(c[0], c.Count > 1 ? c[1] : 0, c.Count > 2 ? c[2] : 0) * _len;
        }

        private Vec3 Dir(object? o, Vec3 fallback)
        {
            if (o is not StepRef) return fallback;
            var d = E(o);
            var c = L(d.Args[1]).Select(D).ToList();
            var v = new Vec3(c[0], c.Count > 1 ? c[1] : 0, c.Count > 2 ? c[2] : 0);
            return v.Length < 1e-15 ? fallback : v.Normalized();
        }

        /// <summary>A placement: its place, its z and its x (square to z).</summary>
        private (Vec3 O, Vec3 Z, Vec3 X) Axis2(Entity a)
        {
            if (!a.Is("AXIS2_PLACEMENT_3D")) throw Unknown(a);
            var o = Point(E(a.Args[1]));
            var z = Dir(a.Args[2], new Vec3(0, 0, 1));
            var x = Dir(a.Args.Count > 3 ? a.Args[3] : null, new Vec3(1, 0, 0));
            x -= z * x.Dot(z);
            if (x.Length < 1e-9) x = (Math.Abs(z.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0)).Cross(z);
            return (o, z, x.Normalized());
        }

        private BrepSurface Surface(Entity s)
        {
            if (s.Is("PLANE")) { var (o, z, x) = Axis2(E(s.Args[1])); return new BrepPlane(o, z, x); }
            if (s.Is("CYLINDRICAL_SURFACE")) { var (o, z, x) = Axis2(E(s.Args[1])); return new BrepCylinder(o, z, x, D(s.Args[2]) * _len); }
            if (s.Is("CONICAL_SURFACE")) { var (o, z, x) = Axis2(E(s.Args[1])); return new BrepCone(o, z, x, D(s.Args[2]) * _len, D(s.Args[3]) * _angle); }
            if (s.Is("SPHERICAL_SURFACE")) { var (o, z, x) = Axis2(E(s.Args[1])); return new BrepSphere(o, D(s.Args[2]) * _len, z, x); }
            if (s.Is("TOROIDAL_SURFACE")) { var (o, z, x) = Axis2(E(s.Args[1])); return new BrepTorus(o, z, x, D(s.Args[2]) * _len, D(s.Args[3]) * _len); }
            if (s.Is("B_SPLINE_SURFACE_WITH_KNOTS")) return Nurbs(s);
            throw Unknown(s);
        }

        private BrepNurbsSurface Nurbs(Entity s)
        {
            // simple: ('', ud, vd, pts, form, uclosed, vclosed, selfint, um, vm, uk, vk, spec); complex: split over its parts
            IReadOnlyList<object?> bs, kn;
            if (s.Parts.Count == 1) { bs = s.Args.Skip(1).Take(7).ToList(); kn = s.Args.Skip(8).ToList(); }
            else { bs = s.Part("B_SPLINE_SURFACE")!; kn = s.Part("B_SPLINE_SURFACE_WITH_KNOTS")!; }
            int ud = (int)D(bs[0]), vd = (int)D(bs[1]);
            var rows = L(bs[2]).Select(r => L(r).Select(p => Point(E(p))).ToList()).ToList();
            int nu = rows.Count, nv = rows[0].Count;
            var pts = rows.SelectMany(r => r).ToList();
            var w = s.Part("RATIONAL_B_SPLINE_SURFACE") is { } rw ? L(rw[0]).SelectMany(r => L(r).Select(D)).ToList() : Enumerable.Repeat(1.0, pts.Count).ToList();
            var um = L(kn[0]).Select(x => (int)D(x)).ToList(); var vm = L(kn[1]).Select(x => (int)D(x)).ToList();
            var uk = L(kn[2]).Select(D).ToList(); var vk = L(kn[3]).Select(D).ToList();
            bool uc = B(bs[4]), vc = B(bs[5]);
            var format = uc || vc ? NurbsFormat.Default with { UClosed = uc, VClosed = vc } : null;
            return new BrepNurbsSurface(ud, vd, nu, nv, pts, w, uk, um, vk, vm, null, format);
        }

        /// <summary>An edge's curve, running from its first vertex <paramref name="from"/> to <paramref name="to"/> (as the
        /// rest keeps edges): one against its curve (<paramref name="sense"/> false) turned round; a B-spline of which
        /// the edge is only a part, cut to that part.</summary>
        private BrepCurve Curve(Entity c, Vec3 from, Vec3 to, bool sense, bool closed)
        {
            if (c.Is("SURFACE_CURVE") || c.Is("SEAM_CURVE") || c.Is("INTERSECTION_CURVE")) return Curve(E(c.Args[1]), from, to, sense, closed);
            if (c.Is("TRIMMED_CURVE")) return Curve(E(c.Args[1]), from, to, sense, closed);
            if (c.Is("LINE")) return new BrepLine();
            if (c.Is("CIRCLE") || c.Is("ELLIPSE"))
            {
                var (o, z, x) = Axis2(E(c.Args[1]));
                // (a ring starts where its vertex is)
                if (closed) { var r0 = from - o; r0 -= z * r0.Dot(z); if (r0.Length > 1e-12) x = r0.Normalized(); }
                if (!sense) z = z * -1;
                if (c.Is("CIRCLE")) return new BrepCircle(o, z, x, D(c.Args[2]) * _len);
                double a = D(c.Args[2]) * _len, b = D(c.Args[3]) * _len;
                var ex = Dir(L(E(c.Args[1]).Args).Count > 3 ? E(c.Args[1]).Args[3] : null, x);
                ex -= (z) * ex.Dot(z);
                return new BrepEllipse(o, z, ex.Normalized(), a, b);
            }
            if (c.Is("B_SPLINE_CURVE_WITH_KNOTS"))
            {
                IReadOnlyList<object?> bs, kn;
                if (c.Parts.Count == 1) { bs = c.Args.Skip(1).Take(5).ToList(); kn = c.Args.Skip(6).ToList(); }
                else { bs = c.Part("B_SPLINE_CURVE")!; kn = c.Part("B_SPLINE_CURVE_WITH_KNOTS")!; }
                int deg = (int)D(bs[0]);
                var pts = L(bs[1]).Select(p => Point(E(p))).ToList();
                var mults = L(kn[0]).Select(x => (int)D(x)).ToList();
                var knots = L(kn[1]).Select(D).ToList();
                var w = c.Part("RATIONAL_B_SPLINE_CURVE") is { } rw ? L(rw[0]).Select(D).ToList() : null;
                var curve = new BrepBSpline(deg, pts, knots, mults, B(bs[3]) || closed, w);
                if (!sense) curve = Reverse(curve);
                // (the edge only a part of it: the part between its vertices, followed closely)
                if (!closed && ((curve.At(curve.Start) - from).Length > 1e-7 || (curve.At(curve.End) - to).Length > 1e-7))
                    curve = Part(curve, from, to);
                return curve;
            }
            throw Unknown(c);
        }

        /// <summary>A B-spline run the other way.</summary>
        private static BrepBSpline Reverse(BrepBSpline c)
        {
            double a = c.Knots[0], b = c.Knots[^1];
            return c with
            {
                ControlPoints = c.ControlPoints.Reverse().ToList(), Weights = c.Weights?.Reverse().ToList(),
                Knots = c.Knots.Reverse().Select(k => a + b - k).ToList(), Mults = c.Mults.Reverse().ToList(),
            };
        }

        /// <summary>The part of a curve from near <paramref name="from"/> to near <paramref name="to"/>: points along it (a
        /// point every quarter millimetre, at least 97) and a curve through them, ending exactly on the vertices.</summary>
        private static BrepBSpline Part(BrepBSpline c, Vec3 from, Vec3 to)
        {
            double t0 = BSpline.ClosestParam(c, from), t1 = BSpline.ClosestParam(c, to);
            if (t1 < t0) (t0, t1) = (t1, t0);
            double len = 0; var prev = c.At(t0);
            for (int i = 1; i <= 64; i++) { var q = c.At(t0 + (t1 - t0) * i / 64); len += (q - prev).Length; prev = q; }
            int n = Math.Clamp((int)Math.Ceiling(len / 2.5e-4), 96, 4000);
            var pts = new List<Vec3> { from };
            bool forward = (c.At(t0) - from).Length <= (c.At(t1) - from).Length;
            for (int k = 1; k < n; k++) pts.Add(c.At(forward ? t0 + (t1 - t0) * k / n : t1 - (t1 - t0) * k / n));
            pts.Add(to);
            return BSpline.Interpolate(pts, false);
        }

        /// <summary>
        /// Where a body stands: per use of it in the assembly, the move from its own representation up to the top (each
        /// step an ITEM_DEFINED_TRANSFORMATION from a placement in the part to one in the assembly; null: none), and the
        /// name of its part (PRODUCT).
        /// </summary>
        public List<(Move? Move, string? Part, List<(int Rep, int Rel, Move? Up)> Path)> Placements(int body)
        {
            var reps = f.Entities.Values.Where(e => e.Type.EndsWith("REPRESENTATION", StringComparison.Ordinal) && e.Args.Count > 1 && L(e.Args[1]).Any(x => x is StepRef r && r.Id == body)).Select(e => e.Id).ToList();
            var result = new List<(Move?, string?, List<(int, int, Move?)>)>();
            foreach (int rep in reps.Take(1))
                foreach (var (move, path) in Chains(rep, new HashSet<int>()))
                    result.Add((move, PartName(rep), path));
            return result.Count > 0 ? result : [(null, null, [])];
        }

        /// <summary>Every way up from a representation to the top: the moves composed (null: none on the way), and the
        /// assemblies it goes through (the nearest first): the shape of each, the relationship that places the one below
        /// in it, and the move from it up to the top.</summary>
        private List<(Move? Move, List<(int Rep, int Rel, Move? Up)> Path)> Chains(int rep, HashSet<int> seen)
        {
            if (!seen.Add(rep)) return [];
            var ups = new List<(Move?, List<(int, int, Move?)>)>();
            foreach (var rr in f.Entities.Values)
            {
                if (rr.Part("REPRESENTATION_RELATIONSHIP") is not { } rel || rel.Count < 4) continue;
                if (rel[2] is not StepRef r1 || rel[3] is not StepRef r2) continue;
                if (rr.Part("REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION") is { } wt)
                {
                    // the part (rep_1) placed in the assembly (rep_2)
                    if (r1.Id != rep || wt[0] is not StepRef tr || !f.Entities.TryGetValue(tr.Id, out var t) || !t.Is("ITEM_DEFINED_TRANSFORMATION")) continue;
                    var from = Frame3(E(t.Args[2])); var to = Frame3(E(t.Args[3]));
                    var step = new Move(p => to.Out(from.In(p)), d => to.Out(from.In(d + from.O)) - to.O);
                    foreach (var (up, path) in Chains(r2.Id, new HashSet<int>(seen)))
                        ups.Add((up is null ? step : new Move(p => up.Point(step.Point(p)), d => up.Dir(step.Dir(d))), path.Prepend((r2.Id, rr.Id, up)).ToList()));
                }
                else if (r1.Id == rep || r2.Id == rep)
                {
                    // the same shape in another representation, unmoved: only the moves found on from there count (a link
                    // between a file's own representations, as ours has, is no second use)
                    foreach (var up in Chains(r1.Id == rep ? r2.Id : r1.Id, seen)) if (up.Move != null) ups.Add(up);
                }
            }
            return ups.Count > 0 ? ups : [(null, [])];
        }

        private Frame Frame3(Entity a)
        {
            var (o, z, x) = Axis2(a);
            return new Frame(o, x, z.Cross(x), z);
        }

        /// <summary>The name of the part a representation is the shape of (SHAPE_DEFINITION_REPRESENTATION → … → PRODUCT).</summary>
        public string? PartName(int rep)
        {
            foreach (var sdr in f.OfType("SHAPE_DEFINITION_REPRESENTATION"))
            {
                if (sdr.Args[1] is not StepRef r || r.Id != rep) continue;
                try
                {
                    var pd = E(E(sdr.Args[0]).Args[2]);
                    var product = E(E(pd.Args[2]).Args[2]);
                    if (product.Is("PRODUCT") && product.Args[1] is string n) return n;
                }
                catch (StepException) { }
            }
            return null;
        }

        /// <summary>The face colours per body read: colours on faces, and on a whole body for its faces without one.</summary>
        public List<Dictionary<int, uint>> Colours(List<StepBody> bodies)
        {
            var result = bodies.Select(_ => new Dictionary<int, uint>()).ToList();
            var faceAt = new Dictionary<int, (int Body, int Face)>();
            for (int b = 0; b < FaceIds.Count; b++) for (int i = 0; i < FaceIds[b].Count; i++) faceAt[FaceIds[b][i]] = (b, i);
            var bodyAt = BodyIds.Select((id, b) => (id, b)).ToDictionary(x => x.id, x => x.b);
            var whole = new Dictionary<int, uint>();
            foreach (var si in f.OfType("STYLED_ITEM").Concat(f.OfType("OVER_RIDING_STYLED_ITEM")).Distinct())
            {
                if (si.Args.Count < 3 || si.Args[2] is not StepRef item || Colour(si) is not uint argb) continue;
                if (faceAt.TryGetValue(item.Id, out var fb)) result[fb.Body][fb.Face] = argb;
                else if (bodyAt.TryGetValue(item.Id, out int b)) whole[b] = argb;
            }
            foreach (var (b, argb) in whole)
                for (int i = 0; i < FaceIds[b].Count; i++) result[b].TryAdd(i, argb);
            return result;
        }

        /// <summary>The fill colour of a styled item, ARGB (opaque), or null.</summary>
        private uint? Colour(Entity styled)
        {
            var seen = new HashSet<int>(); var todo = new Stack<object?>(L(styled.Args[1]));
            while (todo.Count > 0)
            {
                var o = todo.Pop();
                if (o is IReadOnlyList<object?> list) { foreach (var x in list) todo.Push(x); continue; }
                if (o is not StepRef r || !seen.Add(r.Id) || !f.Entities.TryGetValue(r.Id, out var e)) continue;
                if (e.Is("COLOUR_RGB"))
                {
                    uint C(object? v) => (uint)Math.Clamp(Math.Round(D(v) * 255), 0, 255);
                    return 0xFF000000 | (C(e.Args[1]) << 16) | (C(e.Args[2]) << 8) | C(e.Args[3]);
                }
                if (e.Is("DRAUGHTING_PRE_DEFINED_COLOUR") && Name(e) is var n && Predefined.TryGetValue(n, out uint pc)) return pc;
                // (a curve's or a text's style is no fill: only surface styles are followed)
                if (e.Is("CURVE_STYLE")) continue;
                foreach (var a in e.Args) todo.Push(a);
            }
            return null;
        }

        private static readonly Dictionary<string, uint> Predefined = new(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = 0xFFFF0000, ["green"] = 0xFF00FF00, ["blue"] = 0xFF0000FF, ["yellow"] = 0xFFFFFF00,
            ["magenta"] = 0xFFFF00FF, ["cyan"] = 0xFF00FFFF, ["black"] = 0xFF000000, ["white"] = 0xFFFFFFFF,
        };

        /// <summary>The loose lines and arcs: the trimmed lines and circles in curve sets.</summary>
        public List<StepCurve> Curves()
        {
            var r = new List<StepCurve>();
            foreach (var set in f.OfType("GEOMETRIC_CURVE_SET").Concat(f.OfType("GEOMETRIC_SET")).Distinct())
                foreach (var o in L(set.Args[1]))
                {
                    var c = E(o);
                    if (!c.Is("TRIMMED_CURVE")) continue;
                    var basis = E(c.Args[1]);
                    if (Trim(basis, L(c.Args[2])) is not { } a || Trim(basis, L(c.Args[3])) is not { } b) continue;
                    bool sense = B(c.Args[4]);
                    if (basis.Is("LINE")) r.Add(new StepCurve(a, b));
                    else if (basis.Is("CIRCLE"))
                    {
                        var (ctr, z, x) = Axis2(E(basis.Args[1]));
                        var arc = new BrepCircle(ctr, sense ? z : z * -1, (a - ctr).Normalized(), D(basis.Args[2]) * _len);
                        r.Add(new StepCurve(a, b, arc));
                    }
                }
            return r;
        }

        /// <summary>A trimming point: given as a point, or as a parameter on a line or circle.</summary>
        private Vec3? Trim(Entity basis, IReadOnlyList<object?> select)
        {
            foreach (var s in select) if (s is StepRef) { var e = E(s); if (e.Is("CARTESIAN_POINT")) return Point(e); }
            foreach (var s in select)
                if (s is Typed { Type: "PARAMETER_VALUE" } t && t.Args[0] is double u)
                {
                    if (basis.Is("LINE"))
                    {
                        var p = Point(E(basis.Args[1])); var v = E(basis.Args[2]);
                        return p + Dir(v.Args[1], new Vec3(1, 0, 0)) * (u * D(v.Args[2]) * _len);
                    }
                    if (basis.Is("CIRCLE"))
                    {
                        var (o, z, x) = Axis2(E(basis.Args[1])); var y = z.Cross(x); double r = D(basis.Args[2]) * _len, ang = u * _angle;
                        return o + (x * Math.Cos(ang) + y * Math.Sin(ang)) * r;
                    }
                }
            return null;
        }

        private static StepException Unknown(Entity e) => new(
            $"Dit STEP-bestand heeft {e.Type} (#{e.Id}); dat kan nog niet gelezen worden. / This STEP file has {e.Type} (#{e.Id}); that cannot be read yet.");

        /// <summary>Metres per length unit of the file: millimetres, centimetres, metres, inches, ….</summary>
        private static double LengthScale(StepFile f)
        {
            foreach (var u in f.OfType("LENGTH_UNIT"))
            {
                if (u.Part("SI_UNIT") is { } si) return Prefix(si[0]);
                if (u.Part("CONVERSION_BASED_UNIT") is { } cb && cb[1] is StepRef m && f.Entities.TryGetValue(m.Id, out var mu))
                {
                    // (the measure: so many of another unit, e.g. 25.4 mm for an inch)
                    double v = D(mu.Args[0]);
                    double inner = mu.Args[1] is StepRef ur && f.Entities.TryGetValue(ur.Id, out var ue) && ue.Part("SI_UNIT") is { } isi ? Prefix(isi[0]) : 0.001;
                    return v * inner;
                }
            }
            return 0.001;
        }

        private static double Prefix(object? p) => p is StepFile.Enum e ? e.Value switch
        {
            "MILLI" => 1e-3, "CENTI" => 1e-2, "DECI" => 1e-1, "KILO" => 1e3, "MICRO" => 1e-6, _ => 1,
        } : 1;

        /// <summary>Radians per angle unit of the file (radians, or degrees).</summary>
        private static double AngleScale(StepFile f)
        {
            foreach (var u in f.OfType("PLANE_ANGLE_UNIT"))
            {
                if (u.Part("SI_UNIT") is not null) return 1;
                if (u.Part("CONVERSION_BASED_UNIT") is { } cb && cb[0] is string name && name.Contains("DEG", StringComparison.OrdinalIgnoreCase)) return Math.PI / 180;
            }
            return 1;
        }
    }
}
