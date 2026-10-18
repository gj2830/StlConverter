using System.Globalization;
using System.Text;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace RsDocx.Core.Step;

/// <summary>
/// Writes bodies as STEP (ISO 10303-21, AP214 "automotive design", as nearly every CAD program reads it): exact shapes,
/// one entity per line, lengths in millimetres without needless digits, names on the bodies, so that the file can be read
/// by people too. A solid is a MANIFOLD_SOLID_BREP (a CLOSED_SHELL of ADVANCED_FACEs), faces without thickness a
/// SHELL_BASED_SURFACE_MODEL (an OPEN_SHELL per connected piece); loose lines a GEOMETRIC_CURVE_SET of TRIMMED_CURVEs;
/// face colours STYLED_ITEMs. Surfaces: plane, cylinder, cone, sphere, torus, B-spline (rational ones as the complex
/// entity); curves: line, circle, ellipse, B-spline. A ring edge (a whole circle) gets a vertex where the curve starts.
/// </summary>
public static class StepWriter
{
    private const double Mm = 1000;

    /// <summary>The file as text; <paramref name="fileName"/> and <paramref name="time"/> go into its header.</summary>
    public static string Write(StepModel model, string fileName = "model.step", DateTime? time = null)
    {
        var w = new Writer();
        var ctx = w.Context();
        var origin = w.Axis2(new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0));
        var solids = new List<int>(); var sheets = new List<int>(); var styled = new List<int>();
        foreach (var b in model.Bodies)
        {
            var faces = w.Body(b.Body);
            if (b.Body.Sheet)
            {
                var shells = Pieces(b.Body).Select(p => w.Add($"OPEN_SHELL('',({Refs(p.Select(f => faces[f]))}))")).ToList();
                sheets.Add(w.Add($"SHELL_BASED_SURFACE_MODEL({Text(b.Name)},({Refs(shells)}))"));
            }
            else solids.Add(w.Add($"MANIFOLD_SOLID_BREP({Text(b.Name)},{Ref(w.Add($"CLOSED_SHELL('',({Refs(faces)}))"))})"));
            foreach (var (f, argb) in b.FaceColors ?? new Dictionary<int, uint>())
                if (f < faces.Count) styled.Add(w.Add($"STYLED_ITEM('color',({Ref(w.Style(argb))}),{Ref(faces[f])})"));
        }
        var curves = model.Curves.Select(w.Trimmed).ToList();

        // the representations: of the solids, the faces without thickness and the loose lines; the first one found is
        // the product's shape, the others are tied to it
        var reps = new List<int>();
        if (solids.Count > 0) reps.Add(w.Add($"ADVANCED_BREP_SHAPE_REPRESENTATION('',({Refs(solids.Prepend(origin))}),{Ref(ctx)})"));
        if (sheets.Count > 0) reps.Add(w.Add($"MANIFOLD_SURFACE_SHAPE_REPRESENTATION('',({Refs(sheets.Prepend(origin))}),{Ref(ctx)})"));
        if (curves.Count > 0)
        {
            int set = w.Add($"GEOMETRIC_CURVE_SET('Schets',({Refs(curves)}))");
            reps.Add(w.Add($"GEOMETRICALLY_BOUNDED_WIREFRAME_SHAPE_REPRESENTATION('',({Ref(origin)},{Ref(set)}),{Ref(ctx)})"));
        }
        if (reps.Count == 0) reps.Add(w.Add($"SHAPE_REPRESENTATION('',({Ref(origin)}),{Ref(ctx)})"));
        int app = w.Add("APPLICATION_CONTEXT('automotive design')");
        w.Add($"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,{Ref(app)})");
        string name = Text(Path.GetFileNameWithoutExtension(fileName));
        int product = w.Add($"PRODUCT({name},{name},'',({Ref(w.Add($"PRODUCT_CONTEXT('',{Ref(app)},'mechanical')"))}))");
        w.Add($"PRODUCT_RELATED_PRODUCT_CATEGORY('part',$,({Ref(product)}))");
        int formation = w.Add($"PRODUCT_DEFINITION_FORMATION('','',{Ref(product)})");
        int definition = w.Add($"PRODUCT_DEFINITION('design','',{Ref(formation)},{Ref(w.Add($"PRODUCT_DEFINITION_CONTEXT('part definition',{Ref(app)},'design')"))})");
        int shape = w.Add($"PRODUCT_DEFINITION_SHAPE('','',{Ref(definition)})");
        w.Add($"SHAPE_DEFINITION_REPRESENTATION({Ref(shape)},{Ref(reps[0])})");
        foreach (int r in reps.Skip(1)) w.Add($"SHAPE_REPRESENTATION_RELATIONSHIP('','',{Ref(reps[0])},{Ref(r)})");
        if (styled.Count > 0) w.Add($"MECHANICAL_DESIGN_GEOMETRIC_PRESENTATION_REPRESENTATION('',({Refs(styled)}),{Ref(ctx)})");

        var sb = new StringBuilder();
        sb.Append("ISO-10303-21;\nHEADER;\n");
        sb.Append("FILE_DESCRIPTION(('PushPullMe model'),'2;1');\n");
        sb.Append($"FILE_NAME({Text(Path.GetFileName(fileName))},'{(time ?? DateTime.Now).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}',(''),(''),'PushPullMe','PushPullMe','');\n");
        sb.Append("FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\nENDSEC;\nDATA;\n");
        foreach (var line in w.Lines) sb.Append(line).Append('\n');
        sb.Append("ENDSEC;\nEND-ISO-10303-21;\n");
        return sb.ToString();
    }

    /// <summary>The faces of faces without thickness in connected pieces (sharing an edge or a corner).</summary>
    private static List<List<int>> Pieces(BrepBody body)
    {
        int n = body.Faces.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Root(int f) { while (parent[f] != f) f = parent[f] = parent[parent[f]]; return f; }
        var seen = new Dictionary<(bool, int), int>();
        for (int f = 0; f < n; f++)
            foreach (var c in body.Faces[f].Loops.SelectMany(l => l))
            {
                var e = body.Edges[c.Edge];
                foreach (var key in new[] { (true, c.Edge), (false, e.Start), (false, e.End) })
                {
                    if (!key.Item1 && key.Item2 < 0) continue;
                    if (seen.TryGetValue(key, out int g)) parent[Root(g)] = Root(f); else seen[key] = f;
                }
            }
        return Enumerable.Range(0, n).GroupBy(Root).OrderBy(g => g.Min()).Select(g => g.ToList()).ToList();
    }

    internal static string Ref(int id) => "#" + id.ToString(CultureInfo.InvariantCulture);
    internal static string Refs(IEnumerable<int> ids) => string.Join(",", ids.Select(Ref));

    /// <summary>A real as STEP writes it: always a decimal point, no needless digits (12 decimals: a nanometre of a
    /// millimetre; very small or large ones with an exponent).</summary>
    internal static string Num(double v)
    {
        if (v == 0 || !double.IsFinite(v)) return "0.";
        double a = Math.Abs(v);
        string s = a >= 1e-6 && a < 1e12 ? v.ToString("0.############", CultureInfo.InvariantCulture) : v.ToString("0.############E+0", CultureInfo.InvariantCulture);
        if (s == "-0") s = "0";
        if (!s.Contains('.'))
        {
            int e = s.IndexOf('E');
            s = e < 0 ? s + "." : s[..e] + "." + s[e..];
        }
        return s;
    }

    /// <summary>A string in quotes, an apostrophe doubled, anything outside ASCII as \X2\…\X0\ (as Part 21 asks).</summary>
    internal static string Text(string s)
    {
        var sb = new StringBuilder("'");
        foreach (char ch in s)
        {
            if (ch == '\'') sb.Append("''");
            else if (ch == '\\') sb.Append("\\\\");
            else if (ch >= 32 && ch < 127) sb.Append(ch);
            else sb.Append("\\X2\\").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)).Append("\\X0\\");
        }
        return sb.Append('\'').ToString();
    }

    private sealed class Writer
    {
        public readonly List<string> Lines = new();
        private readonly Dictionary<uint, int> _styles = new();

        public int Add(string entity)
        {
            int id = Lines.Count + 1;
            Lines.Add($"{Ref(id)}={entity};");
            return id;
        }

        /// <summary>The geometric context: millimetres, radians, an accuracy of 1e-5 mm.</summary>
        public int Context()
        {
            int mm = Add("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))");
            int rad = Add("(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.))");
            int sr = Add("(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT())");
            int acc = Add($"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-05),{Ref(mm)},'distance_accuracy_value','confusion accuracy')");
            return Add($"(GEOMETRIC_REPRESENTATION_CONTEXT(3)GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT(({Ref(acc)}))GLOBAL_UNIT_ASSIGNED_CONTEXT(({Ref(mm)},{Ref(rad)},{Ref(sr)}))REPRESENTATION_CONTEXT('Context #1','3D Context with UNIT and UNCERTAINTY'))");
        }

        public int Point(Vec3 p) => Add($"CARTESIAN_POINT('',({Num(p.X * Mm)},{Num(p.Y * Mm)},{Num(p.Z * Mm)}))");

        public int Direction(Vec3 d)
        {
            d = d.Normalized();
            return Add($"DIRECTION('',({Num(d.X)},{Num(d.Y)},{Num(d.Z)}))");
        }

        /// <summary>A placement: at <paramref name="o"/>, its z along <paramref name="z"/>, its x as near <paramref name="x"/>
        /// as square to z allows.</summary>
        public int Axis2(Vec3 o, Vec3 z, Vec3 x)
        {
            z = z.Normalized();
            x -= z * x.Dot(z);
            if (x.Length < 1e-9) x = (Math.Abs(z.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0)).Cross(z);
            return Add($"AXIS2_PLACEMENT_3D('',{Ref(Point(o))},{Ref(Direction(z))},{Ref(Direction(x))})");
        }

        /// <summary>A body's topology; the ADVANCED_FACEs, by face index.</summary>
        public List<int> Body(BrepBody b)
        {
            var vertices = b.Vertices.Select(v => Add($"VERTEX_POINT('',{Ref(Point(v))})")).ToList();
            var edges = new List<int>();
            foreach (var e in b.Edges)
            {
                int start, end;
                if (e.Start < 0)
                {
                    // a ring: one vertex where its curve starts
                    start = end = Add($"VERTEX_POINT('',{Ref(Point(CurveStart(e.Curve)))})");
                }
                else { start = vertices[e.Start]; end = vertices[e.End]; }
                var from = e.Start >= 0 ? b.Vertices[e.Start] : CurveStart(e.Curve);
                var to = e.End >= 0 ? b.Vertices[e.End] : from;
                edges.Add(Add($"EDGE_CURVE('',{Ref(start)},{Ref(end)},{Ref(Curve(e.Curve, from, to))},.T.)"));
            }
            var faces = new List<int>();
            foreach (var f in b.Faces)
            {
                var bounds = new List<int>();
                for (int k = 0; k < f.Loops.Count; k++)
                {
                    var co = f.Loops[k].Select(c => Add($"ORIENTED_EDGE('',*,*,{Ref(edges[c.Edge])},{(c.Forward ? ".T." : ".F.")})")).ToList();
                    int loop = Add($"EDGE_LOOP('',({Refs(co)}))");
                    // (the first loop of a flat face is its outer one; a round face's loops are just its bounds)
                    bool outer = k == 0 && f.Surface is BrepPlane;
                    bounds.Add(Add($"{(outer ? "FACE_OUTER_BOUND" : "FACE_BOUND")}('',{Ref(loop)},.T.)"));
                }
                foreach (int v in f.Points) bounds.Add(Add($"FACE_BOUND('',{Ref(Add($"VERTEX_LOOP('',{Ref(vertices[v])})"))},.T.)"));
                faces.Add(Add($"ADVANCED_FACE('',({Refs(bounds)}),{Ref(Surface(f.Surface))},{(f.Reversed ? ".F." : ".T.")})"));
            }
            return faces;
        }

        private static Vec3 CurveStart(BrepCurve c) => c switch
        {
            BrepCircle ci => ci.Centre + ci.XAxis.Normalized() * ci.Radius,
            BrepEllipse el => el.Centre + el.XAxis.Normalized() * el.MajorRadius,
            BrepBSpline bs => bs.At(bs.Start),
            _ => throw new ArgumentException($"a ring edge on a {c.GetType().Name}"),
        };

        private int Curve(BrepCurve c, Vec3 from, Vec3 to)
        {
            switch (c)
            {
                case BrepLine:
                {
                    var d = to - from;
                    if (d.Length < 1e-15) throw new ArgumentException("a line edge of no length");
                    return Add($"LINE('',{Ref(Point(from))},{Ref(Add($"VECTOR('',{Ref(Direction(d))},{Num(d.Length * Mm)})"))})");
                }
                case BrepCircle ci: return Add($"CIRCLE('',{Ref(Axis2(ci.Centre, ci.Normal, ci.XAxis))},{Num(ci.Radius * Mm)})");
                case BrepEllipse el: return Add($"ELLIPSE('',{Ref(Axis2(el.Centre, el.Normal, el.XAxis))},{Num(el.MajorRadius * Mm)},{Num(el.MinorRadius * Mm)})");
                case BrepBSpline bs:
                {
                    string pts = Refs(bs.ControlPoints.Select(Point));
                    string mults = string.Join(",", bs.Mults.Select(m => m.ToString(CultureInfo.InvariantCulture)));
                    string knots = string.Join(",", bs.Knots.Select(Num));
                    string closed = bs.Closed ? ".T." : ".F.";
                    if (bs.Weights is not { } wts || wts.All(x => x == 1))
                        return Add($"B_SPLINE_CURVE_WITH_KNOTS('',{bs.Degree},({pts}),.UNSPECIFIED.,{closed},.F.,({mults}),({knots}),.UNSPECIFIED.)");
                    return Add($"(BOUNDED_CURVE()B_SPLINE_CURVE({bs.Degree},({pts}),.UNSPECIFIED.,{closed},.F.)B_SPLINE_CURVE_WITH_KNOTS(({mults}),({knots}),.UNSPECIFIED.)CURVE()GEOMETRIC_REPRESENTATION_ITEM()RATIONAL_B_SPLINE_CURVE(({string.Join(",", wts.Select(Num))}))REPRESENTATION_ITEM(''))");
                }
                default: throw new NotSupportedException($"STEP: curve {c.GetType().Name}");
            }
        }

        private int Surface(BrepSurface s)
        {
            switch (s)
            {
                case BrepPlane p: return Add($"PLANE('',{Ref(Axis2(p.Origin, p.Normal, p.XAxis))})");
                case BrepCylinder c: return Add($"CYLINDRICAL_SURFACE('',{Ref(Axis2(c.Origin, c.Axis, c.XAxis))},{Num(c.Radius * Mm)})");
                case BrepCone k: return Add($"CONICAL_SURFACE('',{Ref(Axis2(k.Origin, k.Axis, k.XAxis))},{Num(k.Radius * Mm)},{Num(k.HalfAngle)})");
                case BrepSphere sp: return Add($"SPHERICAL_SURFACE('',{Ref(Axis2(sp.Centre, sp.Axis, sp.XAxis))},{Num(sp.Radius * Mm)})");
                case BrepTorus t: return Add($"TOROIDAL_SURFACE('',{Ref(Axis2(t.Centre, t.Axis, t.XAxis))},{Num(t.MajorRadius * Mm)},{Num(t.MinorRadius * Mm)})");
                case BrepNurbsSurface ns:
                {
                    // the points row by row: for each u the points along v (as the surface keeps them)
                    var rows = Enumerable.Range(0, ns.NU).Select(i => "(" + Refs(Enumerable.Range(0, ns.NV).Select(j => Point(ns.Points[i * ns.NV + j]))) + ")");
                    string pts = string.Join(",", rows);
                    string M(IReadOnlyList<int> m) => string.Join(",", m.Select(x => x.ToString(CultureInfo.InvariantCulture)));
                    string K(IReadOnlyList<double> k) => string.Join(",", k.Select(Num));
                    var fm = ns.Format;
                    string uc = fm is { UClosed: true } ? ".T." : ".F.", vc = fm is { VClosed: true } ? ".T." : ".F.";
                    string knots = $"({M(ns.UMults)}),({M(ns.VMults)}),({K(ns.UKnots)}),({K(ns.VKnots)}),.UNSPECIFIED.";
                    if (ns.Weights.All(x => x == 1))
                        return Add($"B_SPLINE_SURFACE_WITH_KNOTS('',{ns.UDegree},{ns.VDegree},({pts}),.UNSPECIFIED.,{uc},{vc},.F.,{knots})");
                    var w = string.Join(",", Enumerable.Range(0, ns.NU).Select(i => "(" + string.Join(",", Enumerable.Range(0, ns.NV).Select(j => Num(ns.Weights[i * ns.NV + j]))) + ")"));
                    return Add($"(BOUNDED_SURFACE()B_SPLINE_SURFACE({ns.UDegree},{ns.VDegree},({pts}),.UNSPECIFIED.,{uc},{vc},.F.)B_SPLINE_SURFACE_WITH_KNOTS({knots})GEOMETRIC_REPRESENTATION_ITEM()RATIONAL_B_SPLINE_SURFACE(({w}))REPRESENTATION_ITEM('')SURFACE())");
                }
                default: throw new NotSupportedException($"STEP: surface {s.GetType().Name}");
            }
        }

        /// <summary>A loose line or arc: its curve trimmed at its two ends.</summary>
        public int Trimmed(StepCurve c)
        {
            int basis = c.Arc is { } a ? Curve(a, c.Start, c.End) : Curve(new BrepLine(), c.Start, c.End);
            return Add($"TRIMMED_CURVE('',{Ref(basis)},({Ref(Point(c.Start))}),({Ref(Point(c.End))}),.T.,.CARTESIAN.)");
        }

        /// <summary>The presentation style of a colour (ARGB), made once per colour.</summary>
        public int Style(uint argb)
        {
            if (_styles.TryGetValue(argb, out int had)) return had;
            string C(int shift) => Num(((argb >> shift) & 0xFF) / 255.0);
            int colour = Add($"COLOUR_RGB('',{C(16)},{C(8)},{C(0)})");
            int fill = Add($"FILL_AREA_STYLE('',({Ref(Add($"FILL_AREA_STYLE_COLOUR('',{Ref(colour)})"))}))");
            int side = Add($"SURFACE_SIDE_STYLE('',({Ref(Add($"SURFACE_STYLE_FILL_AREA({Ref(fill)})"))}))");
            int psa = Add($"PRESENTATION_STYLE_ASSIGNMENT(({Ref(Add($"SURFACE_STYLE_USAGE(.BOTH.,{Ref(side)})"))}))");
            _styles[argb] = psa;
            return psa;
        }
    }
}
