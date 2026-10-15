using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace RsDocx.Core.Brep;

/// <summary>Names (SpaceClaim monikers such as "0:12") of a body, its faces and its edges (same order as the body).</summary>
public sealed class BrepNames
{
    public required string Body { get; init; }
    public required IReadOnlyList<string> Faces { get; init; }
    public required IReadOnlyList<string> Edges { get; init; }
}

/// <summary>
/// Writes <see cref="BrepBody"/>s as Parasolid XT, following the structure SpaceClaim/DesignSpark uses
/// (see NOTES.md: kubus.rsdocx for topology, sleutelrek.rsdocx for cylinders and circles):
///  - root PART_XMT_BLOCK, per body BODY (body_type 3) with two void regions; inner shell has all faces as back faces,
///    outer shell as front faces;
///  - curves and surfaces get sense '+', faces get sense '-' when reversed;
///  - body, faces and edges carry an SDL/TYSA_NAME attribute with the document.xml moniker.
/// The header, embedded schema and name attribute are DesignSpark's own (<see cref="XtDesignSpark"/>).
/// </summary>
public sealed class XtBrepBuilder
{
    private readonly Dictionary<int, XtSchemaEntry> _schemas = XtDesignSpark.Schemas.ToDictionary(s => s.NodeType);

    /// <summary>Schema entry for a node type: from the template files, or built from the base schema (0xFF).</summary>
    private XtSchemaEntry Schema(int type)
    {
        if (_schemas.TryGetValue(type, out var s)) return s;
        if (!XtBaseSchema.Types.TryGetValue(type, out var def))
            throw new InvalidOperationException($"No schema for node type {type}");
        // Only node types that SpaceClaim itself writes as "same as base" may be emitted this way (see NOTES.md).
        if (type is not (XtNodeTypes.Circle or XtNodeTypes.Ellipse or XtNodeTypes.Cylinder or XtNodeTypes.Cone or XtNodeTypes.Sphere or XtNodeTypes.Torus
            or XtNodeTypes.BCurve or XtNodeTypes.NurbsCurve or XtNodeTypes.CurveData or XtNodeTypes.BsplineVertices
            or XtNodeTypes.KnotMult or XtNodeTypes.KnotSet
            // as in testdata/helix.rsdocx (SpaceClaim): B-surfaces and helix forms, all "same as base"
            or XtNodeTypes.BSurface or XtNodeTypes.SurfaceData or XtNodeTypes.NurbsSurf or XtNodeTypes.HelixSuForm or XtNodeTypes.HelixCuForm))
            throw new InvalidOperationException($"Node type {type} has not been observed as 'same as base'; refusing to guess");
        s = new XtSchemaEntry { NodeType = type, Kind = XtSchemaKind.SameAsBase, Fields = def.Fields };
        _schemas[type] = s;
        return s;
    }

    private sealed class Alloc
    {
        public readonly List<XtNode> Nodes = new();
        public int NextIndex = 1;
        public int NextNodeId = 1;
        public int Take() => NextIndex++;
        public int Id() => NextNodeId++;
    }

    private XtNode New(Alloc a, int type, int index, int? varLen = null)
    {
        var s = Schema(type);
        var values = new object[s.Fields.Count];
        for (int i = 0; i < values.Length; i++)
        {
            var f = s.Fields[i];
            object Zero() => f.Type switch
            {
                "d" or "p" => 0, "u" or "c" or "l" => (byte)0, "n" or "w" => (short)0, "f" => 0.0,
                "v" or "h" => new double[3], "i" => new double[2], "b" => new double[6],
                _ => throw new NotSupportedException(f.Type),
            };
            if (f.NElts == 0) values[i] = Zero();
            else
            {
                int n = f.NElts == 1 ? varLen ?? throw new ArgumentException($"type {type} needs varLen") : f.NElts;
                values[i] = Enumerable.Range(0, n).Select(_ => Zero()).ToArray();
            }
        }
        var node = new XtNode { NodeType = type, Index = index, Schema = s, Values = values, VarLength = s.IsVariable ? varLen : null };
        a.Nodes.Add(node);
        return node;
    }

    public XtFile Build(IReadOnlyList<(BrepBody Body, BrepNames Names)> bodies)
    {
        if (bodies.Count == 0) throw new ArgumentException("At least one body required");
        var a = new Alloc();
        int iRoot = a.Take();
        int[] iBodies = bodies.Select(_ => a.Take()).ToArray();
        int iAdef = a.Take(), iAdid = a.Take();

        var root = New(a, XtNodeTypes.PartXmtBlock, iRoot, iBodies.Length);
        root["n_entries"] = iBodies.Length;
        for (int k = 0; k < iBodies.Length; k++) root.Array("entries")[k] = iBodies[k];

        // attribute definition SDL/TYSA_NAME, shared by all bodies
        var adef = New(a, XtNodeTypes.AttribDef, iAdef, XtDesignSpark.NameAttribDefLength);
        var values = XtDesignSpark.NameAttribDefValues();
        for (int i = 0; i < adef.Values.Length; i++) adef.Values[i] = values[i];
        adef["next"] = 0;
        adef["identifier"] = iAdid;
        var nameBytes = System.Text.Encoding.Latin1.GetBytes(XtTopology.NameAttribute);
        var adid = New(a, XtNodeTypes.AttDefId, iAdid, nameBytes.Length);
        for (int k = 0; k < nameBytes.Length; k++) adid.Array("string")[k] = nameBytes[k];

        for (int k = 0; k < bodies.Count; k++)
        {
            // as observed: body k has next = body k-1, previous = body k+1
            int next = k > 0 ? iBodies[k - 1] : 0, prev = k + 1 < iBodies.Length ? iBodies[k + 1] : 0;
            BuildBody(a, bodies[k].Body, bodies[k].Names, iBodies[k], iAdef, next, prev);
        }

        return new XtFile
        {
            Flag = XtDesignSpark.Flag, ModellerVersion = XtDesignSpark.ModellerVersion, SchemaVersion = XtDesignSpark.SchemaVersion,
            MaxNodeTypes = XtDesignSpark.MaxNodeTypes, UserFieldSize = 0,
            Nodes = a.Nodes.OrderBy(n => n.Index).ToList(),
        };
    }

    private void BuildBody(Alloc a, BrepBody body, BrepNames names, int iBody, int iAdef, int nextBody, int prevBody)
    {
        int nv = body.Vertices.Count, ne = body.Edges.Count, nf = body.Faces.Count;
        if (names.Faces.Count != nf || names.Edges.Count != ne) throw new ArgumentException("One name per face and per edge required");
        var loops = body.Faces.SelectMany((f, fi) => f.Loops.Select(l => (Face: fi, Coedges: l))).ToList();
        CheckTopology(body, loops.Select(l => l.Coedges).ToList());
        a.NextNodeId = 1; // node ids are unique per body (reference bodies restart their numbering)

        int firstNode = a.Nodes.Count; // this body's nodes start here (for the owner lookup below)
        int iList = a.Take(), iPlb = a.Take();
        int iRegOut = a.Take(), iRegIn = a.Take(), iShIn = a.Take(), iShOut = a.Take();
        int[] iVert = Enumerable.Range(0, nv).Select(_ => a.Take()).ToArray();
        int[] iPoint = Enumerable.Range(0, nv).Select(_ => a.Take()).ToArray();
        int[] iEdge = Enumerable.Range(0, ne).Select(_ => a.Take()).ToArray();
        int[] iCurve = Enumerable.Range(0, ne).Select(_ => a.Take()).ToArray();
        int[] iFace = Enumerable.Range(0, nf).Select(_ => a.Take()).ToArray();
        int[] iSurf = Enumerable.Range(0, nf).Select(_ => a.Take()).ToArray();
        int nl = loops.Count;
        int[] iLoop = Enumerable.Range(0, nl).Select(_ => a.Take()).ToArray();
        int[][] iFin = loops.Select(l => l.Coedges.Select(_ => a.Take()).ToArray()).ToArray();

        var b = New(a, XtNodeTypes.Body, iBody);
        b["res_size"] = 1000.0000000000001; // as observed
        b["res_linear"] = 1e-8;
        b["next"] = nextBody;
        b["previous"] = prevBody;
        b["state"] = (byte)1;
        b["body_type"] = (byte)3;           // as observed (see NOTES.md)
        b["nom_geom_state"] = (byte)1;
        b["attribute_chains"] = iList;
        b["shell"] = iShIn;
        b["region"] = iRegOut;
        b["boundary_surface"] = iSurf[0];
        b["boundary_curve"] = ne > 0 ? iCurve[0] : 0;
        b["boundary_point"] = nv > 0 ? iPoint[0] : 0;
        b["edge"] = ne > 0 ? iEdge[0] : 0;
        b["vertex"] = nv > 0 ? iVert[0] : 0;

        var regOut = New(a, XtNodeTypes.Region, iRegOut);
        regOut["body"] = iBody; regOut["next"] = iRegIn; regOut["shell"] = iShOut; regOut["type"] = (byte)'V';
        var regIn = New(a, XtNodeTypes.Region, iRegIn);
        regIn["body"] = iBody; regIn["previous"] = iRegOut; regIn["shell"] = iShIn; regIn["type"] = (byte)'V';
        var shIn = New(a, XtNodeTypes.Shell, iShIn);
        shIn["body"] = iBody; shIn["face"] = iFace[0]; shIn["region"] = iRegIn;
        var shOut = New(a, XtNodeTypes.Shell, iShOut);
        shOut["region"] = iRegOut; shOut["front_face"] = iFace[0];

        // ---- fins
        var finOfCoedge = new Dictionary<(int edge, bool fwd), int>();
        for (int li = 0; li < nl; li++)
            for (int k = 0; k < loops[li].Coedges.Count; k++)
                finOfCoedge[(loops[li].Coedges[k].Edge, loops[li].Coedges[k].Forward)] = iFin[li][k];
        var finsAtVx = Enumerable.Range(0, nv).Select(_ => new List<int>()).ToArray();
        var finNodes = new Dictionary<int, XtNode>();
        for (int li = 0; li < nl; li++)
        {
            var ce = loops[li].Coedges;
            int m = ce.Count;
            for (int k = 0; k < m; k++)
            {
                var (e, fwd) = (ce[k].Edge, ce[k].Forward);
                var edge = body.Edges[e];
                int end = fwd ? edge.End : edge.Start; // fin.vertex = forward (end) vertex of the fin
                var fin = New(a, XtNodeTypes.Fin, iFin[li][k]);
                fin["loop"] = iLoop[li];
                fin["forward"] = iFin[li][(k + 1) % m];
                fin["backward"] = iFin[li][(k + m - 1) % m];
                fin["vertex"] = end >= 0 ? iVert[end] : 0;
                fin["other"] = finOfCoedge[(e, !fwd)];
                fin["edge"] = iEdge[e];
                fin["sense"] = (byte)(fwd ? '+' : '-');
                if (end >= 0) finsAtVx[end].Add(iFin[li][k]);
                finNodes[iFin[li][k]] = fin;
            }
        }
        for (int v = 0; v < nv; v++)
            for (int k = 0; k + 1 < finsAtVx[v].Count; k++)
                finNodes[finsAtVx[v][k]]["next_at_vx"] = finsAtVx[v][k + 1];

        // ---- vertices and points
        for (int v = 0; v < nv; v++)
        {
            var vx = New(a, XtNodeTypes.Vertex, iVert[v]);
            vx["node_id"] = a.Id();
            vx["fin"] = finsAtVx[v].Count > 0 ? finsAtVx[v][0] : 0;
            vx["previous"] = v > 0 ? iVert[v - 1] : 0;
            vx["next"] = v + 1 < nv ? iVert[v + 1] : 0;
            vx["point"] = iPoint[v];
            vx["tolerance"] = XtNodeTypes.NullDouble;
            vx["owner"] = iBody;
            var pt = New(a, XtNodeTypes.Point, iPoint[v]);
            pt["node_id"] = a.Id();
            pt["owner"] = iVert[v];
            pt["previous"] = v > 0 ? iPoint[v - 1] : 0;
            pt["next"] = v + 1 < nv ? iPoint[v + 1] : 0;
            pt["pvec"] = A(body.Vertices[v]);
        }

        // ---- edges and curves
        var attrs = new List<(int owner, string name)> { (iBody, names.Body) };
        for (int ei = 0; ei < ne; ei++)
        {
            var edge = body.Edges[ei];
            var e = New(a, XtNodeTypes.Edge, iEdge[ei]);
            e["node_id"] = a.Id();
            e["tolerance"] = XtNodeTypes.NullDouble;
            e["fin"] = finOfCoedge[(ei, true)]; // positive fin
            e["previous"] = ei > 0 ? iEdge[ei - 1] : 0;
            e["next"] = ei + 1 < ne ? iEdge[ei + 1] : 0;
            e["curve"] = iCurve[ei];
            e["owner"] = iBody;
            XtNode c;
            switch (edge.Curve)
            {
                case BrepLine:
                    if (edge.Start < 0) throw new ArgumentException($"edge {ei}: a line cannot be a ring edge");
                    c = New(a, XtNodeTypes.Line, iCurve[ei]);
                    c["pvec"] = A(body.Vertices[edge.Start]);
                    c["direction"] = Unit(body.Vertices[edge.End] - body.Vertices[edge.Start]);
                    break;
                case BrepCircle circle:
                    c = New(a, XtNodeTypes.Circle, iCurve[ei]);
                    c["centre"] = A(circle.Centre);
                    c["normal"] = Unit(circle.Normal);
                    c["x_axis"] = Unit(circle.XAxis);
                    c["radius"] = circle.Radius;
                    break;
                case BrepEllipse el:
                    c = New(a, XtNodeTypes.Ellipse, iCurve[ei]);
                    c["centre"] = A(el.Centre);
                    c["normal"] = Unit(el.Normal);
                    c["x_axis"] = Unit(el.XAxis);
                    c["major_radius"] = el.MajorRadius;
                    c["minor_radius"] = el.MinorRadius;
                    break;
                case BrepBSpline { Weights: not null } rb:
                {
                    // a rational curve (so far: helices) as in testdata/helix.rsdocx: vertex_dim 4 with homogeneous
                    // vertices (w·x, w·y, w·z, w), knot_type 1, curve_form 8, CURVE_DATA self_int 2 and the HELIX_CU_FORM
                    c = New(a, XtNodeTypes.BCurve, iCurve[ei]);
                    int iN = a.Take(), iD = a.Take(), iV = a.Take(), iM = a.Take(), iK = a.Take();
                    c["nurbs"] = iN; c["data"] = iD;
                    var nurbs = New(a, XtNodeTypes.NurbsCurve, iN);
                    nurbs["degree"] = (short)rb.Degree;
                    nurbs["n_vertices"] = rb.ControlPoints.Count;
                    nurbs["vertex_dim"] = (short)4;
                    nurbs["n_knots"] = rb.Knots.Count;
                    nurbs["knot_type"] = (byte)1;
                    nurbs["periodic"] = (byte)0;
                    nurbs["closed"] = (byte)(rb.Closed ? 1 : 0);
                    nurbs["rational"] = (byte)1;
                    nurbs["curve_form"] = (byte)8;
                    nurbs["bspline_vertices"] = iV; nurbs["knot_mult"] = iM; nurbs["knots"] = iK;
                    var data = New(a, XtNodeTypes.CurveData, iD);
                    data["self_int"] = (byte)2;
                    if (rb.Helix is { } hx) data["analytic_form"] = HelixCurveForm(a, hx);
                    var verts = New(a, XtNodeTypes.BsplineVertices, iV, rb.ControlPoints.Count * 4);
                    verts["vertices"] = Homogeneous(rb.ControlPoints, rb.Weights);
                    var mult = New(a, XtNodeTypes.KnotMult, iM, rb.Knots.Count);
                    mult["mult"] = rb.Mults.Select(m => (object)(short)m).ToArray();
                    var knots = New(a, XtNodeTypes.KnotSet, iK, rb.Knots.Count);
                    knots["knots"] = rb.Knots.Select(k => (object)k).ToArray();
                    break;
                }
                case BrepBSpline bs:
                {
                    // as in SpaceClaim's own files (yubikey.rsdocx): B_CURVE -> NURBS_CURVE (+ vertices, multiplicities, knots)
                    // and CURVE_DATA; non-rational, clamped ends (knot_type 6), curve_form unset (1)
                    c = New(a, XtNodeTypes.BCurve, iCurve[ei]);
                    int iN = a.Take(), iD = a.Take(), iV = a.Take(), iM = a.Take(), iK = a.Take();
                    c["nurbs"] = iN; c["data"] = iD;
                    var nurbs = New(a, XtNodeTypes.NurbsCurve, iN);
                    nurbs["degree"] = (short)bs.Degree;
                    nurbs["n_vertices"] = bs.ControlPoints.Count;
                    nurbs["vertex_dim"] = (short)3;
                    nurbs["n_knots"] = bs.Knots.Count;
                    nurbs["knot_type"] = (byte)6;
                    nurbs["periodic"] = (byte)0;
                    nurbs["closed"] = (byte)(bs.Closed ? 1 : 0);
                    nurbs["rational"] = (byte)0;
                    nurbs["curve_form"] = (byte)1;
                    nurbs["bspline_vertices"] = iV; nurbs["knot_mult"] = iM; nurbs["knots"] = iK;
                    var data = New(a, XtNodeTypes.CurveData, iD);
                    data["self_int"] = (byte)1;
                    var verts = New(a, XtNodeTypes.BsplineVertices, iV, bs.ControlPoints.Count * 3);
                    verts["vertices"] = bs.ControlPoints.SelectMany(q => new object[] { q.X, q.Y, q.Z }).ToArray();
                    var mult = New(a, XtNodeTypes.KnotMult, iM, bs.Knots.Count);
                    mult["mult"] = bs.Mults.Select(m => (object)(short)m).ToArray();
                    var knots = New(a, XtNodeTypes.KnotSet, iK, bs.Knots.Count);
                    knots["knots"] = bs.Knots.Select(k => (object)k).ToArray();
                    break;
                }
                default: throw new NotSupportedException(edge.Curve.GetType().Name);
            }
            c["node_id"] = a.Id();
            c["owner"] = iEdge[ei];
            c["previous"] = ei > 0 ? iCurve[ei - 1] : 0;
            c["next"] = ei + 1 < ne ? iCurve[ei + 1] : 0;
            c["sense"] = (byte)'+';
        }

        // ---- faces, surfaces, loops
        var loopsOfFace = Enumerable.Range(0, nf).Select(_ => new List<int>(1)).ToArray(); // (in loop order, as before)
        for (int li = 0; li < nl; li++) loopsOfFace[loops[li].Face].Add(li);
        for (int fi = 0; fi < nf; fi++)
        {
            var face = body.Faces[fi];
            var faceLoops = loopsOfFace[fi];
            var fc = New(a, XtNodeTypes.Face, iFace[fi]);
            fc["node_id"] = a.Id();
            fc["tolerance"] = XtNodeTypes.NullDouble;
            fc["next"] = fi + 1 < nf ? iFace[fi + 1] : 0;
            fc["previous"] = fi > 0 ? iFace[fi - 1] : 0;
            fc["loop"] = faceLoops.Count > 0 ? iLoop[faceLoops[0]] : 0; // a full sphere has no loops
            fc["shell"] = iShIn;
            fc["surface"] = iSurf[fi];
            // a hollow cone: SpaceClaim reverses the surface, not the face (schaaltje.rsdocx: CONE sense '-', FACE '+');
            // other surfaces: the face is reversed
            bool flipSurface = face.Reversed && face.Surface is BrepCone;
            fc["sense"] = (byte)(face.Reversed && !flipSurface ? '-' : '+');
            fc["next_front"] = fi + 1 < nf ? iFace[fi + 1] : 0;
            fc["previous_front"] = fi > 0 ? iFace[fi - 1] : 0;
            fc["front_shell"] = iShOut;
            for (int k = 0; k < faceLoops.Count; k++)
            {
                int li = faceLoops[k];
                var lp = New(a, XtNodeTypes.Loop, iLoop[li]);
                lp["node_id"] = a.Id();
                lp["fin"] = iFin[li][0];
                lp["face"] = iFace[fi];
                lp["next"] = k + 1 < faceLoops.Count ? iLoop[faceLoops[k + 1]] : 0;
            }
            XtNode s;
            switch (face.Surface)
            {
                case BrepPlane pl:
                    s = New(a, XtNodeTypes.Plane, iSurf[fi]);
                    s["pvec"] = A(pl.Origin);
                    s["normal"] = Unit(pl.Normal);
                    s["x_axis"] = Unit(pl.XAxis);
                    break;
                case BrepSphere sp:
                    s = New(a, XtNodeTypes.Sphere, iSurf[fi]);
                    s["centre"] = A(sp.Centre);
                    s["radius"] = sp.Radius;
                    s["axis"] = Unit(sp.Axis);
                    s["x_axis"] = Unit(sp.XAxis);
                    break;
                case BrepCylinder cy:
                    s = New(a, XtNodeTypes.Cylinder, iSurf[fi]);
                    s["pvec"] = A(cy.Origin);
                    s["axis"] = Unit(cy.Axis);
                    s["radius"] = cy.Radius;
                    s["x_axis"] = Unit(cy.XAxis);
                    break;
                case BrepCone co:
                    // fields as in schaaltje.rsdocx (SpaceClaim): radius at pvec, half angle as sine and cosine
                    s = New(a, XtNodeTypes.Cone, iSurf[fi]);
                    s["pvec"] = A(co.Origin);
                    s["axis"] = Unit(co.Axis);
                    s["radius"] = co.Radius;
                    s["sin_half_angle"] = Math.Sin(co.HalfAngle);
                    s["cos_half_angle"] = Math.Cos(co.HalfAngle);
                    s["x_axis"] = Unit(co.XAxis);
                    break;
                case BrepTorus to:
                    s = New(a, XtNodeTypes.Torus, iSurf[fi]);
                    s["centre"] = A(to.Centre);
                    s["axis"] = Unit(to.Axis);
                    s["major_radius"] = to.MajorRadius;
                    s["minor_radius"] = to.MinorRadius;
                    s["x_axis"] = Unit(to.XAxis);
                    break;
                case BrepNurbsSurface ns:
                {
                    // as in testdata/helix.rsdocx: B_SURFACE -> NURBS_SURF (rational, vertex_dim 4, knot types 1, surface_form
                    // 12; vertices row by row, v fastest) and SURFACE_DATA (parameter box, self_int 2, forms 'B'/'?' and
                    // analytic form 'H' -> HELIX_SU_FORM)
                    s = New(a, XtNodeTypes.BSurface, iSurf[fi]);
                    int iN = a.Take(), iD = a.Take(), iV = a.Take(), iUM = a.Take(), iVM = a.Take(), iUK = a.Take(), iVK = a.Take();
                    s["nurbs"] = iN; s["data"] = iD;
                    var nurbs = New(a, XtNodeTypes.NurbsSurf, iN);
                    nurbs["u_periodic"] = (byte)0; nurbs["v_periodic"] = (byte)0;
                    nurbs["u_degree"] = (short)ns.UDegree; nurbs["v_degree"] = (short)ns.VDegree;
                    nurbs["n_u_vertices"] = ns.NU; nurbs["n_v_vertices"] = ns.NV;
                    nurbs["u_knot_type"] = (byte)1; nurbs["v_knot_type"] = (byte)1;
                    nurbs["n_u_knots"] = ns.UKnots.Count; nurbs["n_v_knots"] = ns.VKnots.Count;
                    nurbs["rational"] = (byte)1; nurbs["u_closed"] = (byte)0; nurbs["v_closed"] = (byte)0;
                    nurbs["surface_form"] = (byte)12; nurbs["vertex_dim"] = (short)4;
                    nurbs["bspline_vertices"] = iV; nurbs["u_knot_mult"] = iUM; nurbs["v_knot_mult"] = iVM; nurbs["u_knots"] = iUK; nurbs["v_knots"] = iVK;
                    var data = New(a, XtNodeTypes.SurfaceData, iD);
                    double[] ub = [ns.UKnots[0], ns.UKnots[^1]], vb = [ns.VKnots[0], ns.VKnots[^1]];
                    data["original_uint"] = ub; data["original_vint"] = vb;
                    data["extended_uint"] = (double[])ub.Clone(); data["extended_vint"] = (double[])vb.Clone();
                    data["self_int"] = (byte)2;
                    foreach (var f in new[] { "original_u_start", "original_u_end", "original_v_start", "original_v_end",
                                              "extended_u_start", "extended_u_end", "extended_v_start", "extended_v_end" })
                        data[f] = (byte)'B';
                    data["analytic_form_type"] = (byte)(ns.Helix != null ? 'H' : '?');
                    data["swept_form_type"] = (byte)'?'; data["spun_form_type"] = (byte)'?'; data["blend_form_type"] = (byte)'?';
                    if (ns.Helix is { } hx)
                    {
                        int iH = a.Take();
                        data["analytic_form"] = iH;
                        var h = New(a, XtNodeTypes.HelixSuForm, iH);
                        h["axis_pt"] = A(hx.AxisPoint); h["axis_dir"] = Unit(hx.AxisDir);
                        h["hand"] = (byte)(hx.RightHanded ? '+' : '-');
                        h["turns"] = new[] { hx.Turns0, hx.Turns1 };
                        h["pitch"] = hx.Pitch; h["gap"] = XtNodeTypes.NullDouble; h["tol"] = hx.Tol;
                    }
                    var verts = New(a, XtNodeTypes.BsplineVertices, iV, ns.Points.Count * 4);
                    verts["vertices"] = Homogeneous(ns.Points, ns.Weights);
                    var um = New(a, XtNodeTypes.KnotMult, iUM, ns.UKnots.Count); um["mult"] = ns.UMults.Select(m => (object)(short)m).ToArray();
                    var vm = New(a, XtNodeTypes.KnotMult, iVM, ns.VKnots.Count); vm["mult"] = ns.VMults.Select(m => (object)(short)m).ToArray();
                    var uk = New(a, XtNodeTypes.KnotSet, iUK, ns.UKnots.Count); uk["knots"] = ns.UKnots.Select(k => (object)k).ToArray();
                    var vk = New(a, XtNodeTypes.KnotSet, iVK, ns.VKnots.Count); vk["knots"] = ns.VKnots.Select(k => (object)k).ToArray();
                    break;
                }
                default: throw new NotSupportedException(face.Surface.GetType().Name);
            }
            s["node_id"] = a.Id();
            s["owner"] = iFace[fi];
            s["previous"] = fi > 0 ? iSurf[fi - 1] : 0;
            s["next"] = fi + 1 < nf ? iSurf[fi + 1] : 0;
            s["sense"] = (byte)(flipSurface ? '-' : '+');
            attrs.Add((iFace[fi], names.Faces[fi]));
        }
        for (int ei = 0; ei < ne; ei++) attrs.Add((iEdge[ei], names.Edges[ei]));

        // ---- name attributes, chained by next_of_type; list header points at the first one
        var byIndex = new Dictionary<int, XtNode>(a.Nodes.Count - firstNode);
        for (int k = firstNode; k < a.Nodes.Count; k++) byIndex[a.Nodes[k].Index] = a.Nodes[k];
        var attrIdx = attrs.Select(_ => a.Take()).ToArray();
        for (int k = 0; k < attrs.Count; k++)
        {
            var bytes = System.Text.Encoding.Latin1.GetBytes(attrs[k].name);
            int iChar = a.Take();
            var at = New(a, XtNodeTypes.Attribute, attrIdx[k], 1);
            at["node_id"] = a.Id();
            at["definition"] = iAdef;
            at["owner"] = attrs[k].owner;
            at["next_of_type"] = k + 1 < attrs.Count ? attrIdx[k + 1] : 0;
            at["previous_of_type"] = k > 0 ? attrIdx[k - 1] : 0;
            at.Array("fields")[0] = iChar;
            var cv = New(a, XtNodeTypes.CharValues, iChar, bytes.Length);
            for (int i = 0; i < bytes.Length; i++) cv.Array("values")[i] = bytes[i];
            byIndex[attrs[k].owner]["attributes_groups"] = attrIdx[k];
        }
        var list = New(a, XtNodeTypes.List, iList);
        list["list_type"] = (byte)4; list["notransmit"] = (byte)1; list["owner"] = iBody;
        list["list_length"] = 1; list["block_length"] = 20; list["finger_index"] = 1;
        list["finger_block"] = iPlb; list["list_block"] = iPlb;
        var plb = New(a, XtNodeTypes.PointerLisBlock, iPlb, 20);
        plb["n_entries"] = 1;
        plb.Array("entries")[0] = attrIdx[0];

        foreach (var n in new[] { regOut, regIn, shIn, shOut }) n["node_id"] = a.Id();
        b["highest_node_id"] = a.NextNodeId - 1;
    }

    /// <summary>Why the body cannot be written (an edge not used once in each direction, a loop not connected), or null.</summary>
    public static string? TopologyError(BrepBody body)
    {
        try { CheckTopology(body, body.Faces.SelectMany(f => f.Loops).ToList()); return null; }
        catch (ArgumentException e) { return e.Message; }
    }

    /// <summary>Every edge must be used exactly once forward and once backward; loops must be connected.</summary>
    private static void CheckTopology(BrepBody body, List<IReadOnlyList<BrepCoedge>> loops)
    {
        var uses = new Dictionary<(int, bool), int>();
        foreach (var l in loops)
        {
            if (l.Count == 0) throw new ArgumentException("Empty loop");
            for (int k = 0; k < l.Count; k++)
            {
                uses[(l[k].Edge, l[k].Forward)] = uses.GetValueOrDefault((l[k].Edge, l[k].Forward)) + 1;
                var e = body.Edges[l[k].Edge]; var n = body.Edges[l[(k + 1) % l.Count].Edge];
                int end = l[k].Forward ? e.End : e.Start;
                int nextStart = l[(k + 1) % l.Count].Forward ? n.Start : n.End;
                if (end != nextStart) throw new ArgumentException($"Loop not connected at edge {l[k].Edge}");
            }
        }
        for (int e = 0; e < body.Edges.Count; e++)
            if (uses.GetValueOrDefault((e, true)) != 1 || uses.GetValueOrDefault((e, false)) != 1)
                throw new ArgumentException($"Edge {e} is not used exactly once in each direction");
    }

    private static object CloneValue(object v) => v switch
    {
        double[] d => d.Clone(),
        object[] arr => arr.Select(CloneValue).ToArray(),
        _ => v,
    };

    private static double[] A(Vec3 v) => [v.X, v.Y, v.Z];

    /// <summary>Rational control points as Parasolid stores them: (w·x, w·y, w·z, w).</summary>
    private static object[] Homogeneous(IReadOnlyList<Vec3> pts, IReadOnlyList<double> w) =>
        pts.SelectMany((q, i) => new object[] { q.X * w[i], q.Y * w[i], q.Z * w[i], w[i] }).ToArray();

    /// <summary>A HELIX_CU_FORM node for a curve's helix; returns its index.</summary>
    private int HelixCurveForm(Alloc a, HelixForm hx)
    {
        int iH = a.Take();
        var h = New(a, XtNodeTypes.HelixCuForm, iH);
        h["axis_pt"] = A(hx.AxisPoint); h["axis_dir"] = Unit(hx.AxisDir); h["point"] = A(hx.Point);
        h["hand"] = (byte)(hx.RightHanded ? '+' : '-');
        h["turns"] = new[] { hx.Turns0, hx.Turns1 };
        h["pitch"] = hx.Pitch; h["tol"] = hx.Tol;
        return iH;
    }

    private static double[] Unit(Vec3 v)
    {
        double l = v.Length;
        if (l == 0) throw new ArgumentException("Degenerate vector");
        double S(double c) { c /= l; return Math.Abs(c) < 1e-15 ? 0.0 : c; }
        return [S(v.X), S(v.Y), S(v.Z)];
    }
}
