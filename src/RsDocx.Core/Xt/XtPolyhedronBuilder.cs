namespace RsDocx.Core.Xt;

/// <summary>A closed polyhedral solid: vertices in metres, faces as outer loops of vertex indices,
/// counter-clockwise when seen from outside (right-hand rule gives the outward normal).
/// Optional holes per face run clockwise seen from outside.</summary>
public sealed class Polyhedron
{
    public required IReadOnlyList<(double X, double Y, double Z)> Vertices { get; init; }
    public required IReadOnlyList<int[]> Faces { get; init; }
    /// <summary>Inner loops per face (same count as Faces), or null when no face has holes.</summary>
    public IReadOnlyList<int[][]>? Holes { get; init; }

    /// <summary>Outer loop followed by the holes of face <paramref name="face"/>.</summary>
    public IEnumerable<int[]> LoopsOf(int face)
    {
        yield return Faces[face];
        if (Holes != null) foreach (var h in Holes[face]) yield return h;
    }

    /// <summary>All loops of all faces as (face index, loop).</summary>
    public IEnumerable<(int Face, int[] Loop)> AllLoops() =>
        Enumerable.Range(0, Faces.Count).SelectMany(f => LoopsOf(f).Select(l => (f, l)));

    /// <summary>Axis-aligned box, coordinates in metres.</summary>
    public static Polyhedron Box(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        if (!(x1 > x0 && y1 > y0 && z1 > z0)) throw new ArgumentException("Box must have positive size");
        var v = new List<(double, double, double)>
        {
            (x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
            (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1),
        };
        var f = new List<int[]>
        {
            new[] { 0, 3, 2, 1 }, // bottom  -z
            new[] { 4, 5, 6, 7 }, // top     +z
            new[] { 0, 1, 5, 4 }, // front   -y
            new[] { 1, 2, 6, 5 }, // right   +x
            new[] { 2, 3, 7, 6 }, // back    +y
            new[] { 3, 0, 4, 7 }, // left    -x
        };
        return new Polyhedron { Vertices = v, Faces = f };
    }
}

/// <summary>Names (SpaceClaim monikers such as "0:12") given to the body, faces and edges.</summary>
public sealed class PolyhedronNames
{
    public required string Body { get; init; }
    public required IReadOnlyList<string> Faces { get; init; }
    /// <summary>Edge names, in the order of <see cref="XtPolyhedronBuilder.Edges"/>.</summary>
    public required IReadOnlyList<string> Edges { get; init; }
}

/// <summary>
/// Builds Parasolid XT for one closed planar polyhedron, mimicking the structure SpaceClaim/DesignSpark
/// writes (observed in testdata/kubus.rsdocx; see NOTES.md):
///  - root PART_XMT_BLOCK -&gt; BODY (body_type 3, two void regions);
///  - inner shell holds all faces as back faces, outer shell as front faces;
///  - every face planar (PLANE, normal = outward), every edge a LINE in edge direction (sense '+');
///  - body, faces and edges carry an SDL/TYSA_NAME attribute with the document.xml moniker.
/// Schema entries are taken from a template XT file (the kubus reference) so the embedded schema matches.
/// </summary>
public sealed class XtPolyhedronBuilder
{
    private readonly Dictionary<int, XtSchemaEntry> _schemas;
    private readonly XtFile _template;

    public XtPolyhedronBuilder(XtFile template)
    {
        _template = template;
        _schemas = new();
        foreach (var n in template.Nodes) _schemas.TryAdd(n.NodeType, n.Schema);
        foreach (int t in new[] { XtNodeTypes.PartXmtBlock, XtNodeTypes.Body, XtNodeTypes.Region, XtNodeTypes.Shell, XtNodeTypes.Face,
                     XtNodeTypes.Loop, XtNodeTypes.Fin, XtNodeTypes.Edge, XtNodeTypes.Vertex, XtNodeTypes.Point, XtNodeTypes.Line,
                     XtNodeTypes.Plane, XtNodeTypes.List, XtNodeTypes.PointerLisBlock, XtNodeTypes.AttribDef, XtNodeTypes.AttDefId,
                     XtNodeTypes.Attribute, XtNodeTypes.CharValues })
            if (!_schemas.ContainsKey(t)) throw new ArgumentException($"Template lacks node type {t}");
    }

    /// <summary>Undirected edges (a &lt; b) in a deterministic order: first appearance while walking the faces.</summary>
    public static List<(int A, int B)> Edges(Polyhedron p)
    {
        var list = new List<(int, int)>();
        var seen = new HashSet<(int, int)>();
        foreach (var (_, f) in p.AllLoops())
            for (int i = 0; i < f.Length; i++)
            {
                int a = f[i], b = f[(i + 1) % f.Length];
                var key = a < b ? (a, b) : (b, a);
                if (seen.Add(key)) list.Add(key);
            }
        return list;
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
        var s = _schemas[type];
        var values = new object[s.Fields.Count];
        for (int i = 0; i < values.Length; i++)
        {
            var f = s.Fields[i];
            object zero = f.Type switch
            {
                "d" or "p" => 0, "u" or "c" or "l" => (byte)0, "n" or "w" => (short)0, "f" => 0.0,
                "v" or "h" => new double[3], "i" => new double[2], "b" => new double[6],
                _ => throw new NotSupportedException(f.Type),
            };
            if (f.NElts == 0) values[i] = zero;
            else
            {
                int n = f.NElts == 1 ? varLen ?? throw new ArgumentException($"type {type} needs varLen") : f.NElts;
                var arr = new object[n];
                for (int k = 0; k < n; k++) arr[k] = zero is double[] d ? (double[])d.Clone() : zero;
                values[i] = arr;
            }
        }
        var node = new XtNode { NodeType = type, Index = index, Schema = s, Values = values, VarLength = s.IsVariable ? varLen : null };
        a.Nodes.Add(node);
        return node;
    }

    public XtFile Build(Polyhedron poly, PolyhedronNames names) => Build([(poly, names)]);

    /// <summary>Builds one part holding several bodies (like kubus.rsdocx: one PART_XMT_BLOCK, shared ATTRIB_DEF).</summary>
    public XtFile Build(IReadOnlyList<(Polyhedron Poly, PolyhedronNames Names)> bodies)
    {
        if (bodies.Count == 0) throw new ArgumentException("At least one body required");
        var a = new Alloc();
        int iRoot = a.Take();
        int[] iBodies = bodies.Select(_ => a.Take()).ToArray();
        int iAdef = a.Take(), iAdid = a.Take();

        var root = New(a, XtNodeTypes.PartXmtBlock, iRoot, iBodies.Length);
        root["n_entries"] = iBodies.Length;
        for (int k = 0; k < iBodies.Length; k++) root.Array("entries")[k] = iBodies[k];

        // ---- attribute definition SDL/TYSA_NAME (copied from template), shared by all bodies
        var tAdef = _template.Nodes.First(n => n.NodeType == XtNodeTypes.AttribDef &&
            XtTopology.CharString(_template.ByIndex()[n.Ptr("identifier")], "string") == XtTopology.NameAttribute);
        var adef = New(a, XtNodeTypes.AttribDef, iAdef, tAdef.VarLength);
        for (int i = 0; i < adef.Values.Length; i++) adef.Values[i] = CloneValue(tAdef.Values[i]);
        adef["next"] = 0;
        adef["identifier"] = iAdid;
        var nameBytes = System.Text.Encoding.Latin1.GetBytes(XtTopology.NameAttribute);
        var adid = New(a, XtNodeTypes.AttDefId, iAdid, nameBytes.Length);
        for (int k = 0; k < nameBytes.Length; k++) adid.Array("string")[k] = nameBytes[k];

        for (int k = 0; k < bodies.Count; k++)
        {
            // as observed: body k has next = body k-1, previous = body k+1
            int next = k > 0 ? iBodies[k - 1] : 0, prev = k + 1 < iBodies.Length ? iBodies[k + 1] : 0;
            BuildBody(a, bodies[k].Poly, bodies[k].Names, iBodies[k], iAdef, next, prev);
        }

        return new XtFile
        {
            Flag = _template.Flag, ModellerVersion = _template.ModellerVersion, SchemaVersion = _template.SchemaVersion,
            MaxNodeTypes = _template.MaxNodeTypes, UserFieldSize = 0,
            Nodes = a.Nodes.OrderBy(n => n.Index).ToList(),
        };
    }

    private void BuildBody(Alloc a, Polyhedron poly, PolyhedronNames names, int iBody, int iAdef, int nextBody, int prevBody)
    {
        var edges = Edges(poly);
        if (names.Faces.Count != poly.Faces.Count) throw new ArgumentException("One name per face required");
        if (names.Edges.Count != edges.Count) throw new ArgumentException("One name per edge required");
        if (poly.Holes != null && poly.Holes.Count != poly.Faces.Count) throw new ArgumentException("Holes must have one entry per face");
        ValidateClosed(poly, edges);
        a.NextNodeId = 1; // node ids are unique per body (all four reference bodies restart their numbering)

        int iList = a.Take(), iPlb = a.Take();
        int iRegOut = a.Take(), iRegIn = a.Take(), iShIn = a.Take(), iShOut = a.Take();
        int nv = poly.Vertices.Count, ne = edges.Count, nf = poly.Faces.Count;
        int[] iVert = Enumerable.Range(0, nv).Select(_ => a.Take()).ToArray();
        int[] iPoint = Enumerable.Range(0, nv).Select(_ => a.Take()).ToArray();
        int[] iEdge = Enumerable.Range(0, ne).Select(_ => a.Take()).ToArray();
        int[] iLine = Enumerable.Range(0, ne).Select(_ => a.Take()).ToArray();
        int[] iFace = Enumerable.Range(0, nf).Select(_ => a.Take()).ToArray();
        var loops = poly.AllLoops().ToList(); // (face, vertex loop); a face's outer loop comes first
        int nl = loops.Count;
        int[] iLoop = Enumerable.Range(0, nl).Select(_ => a.Take()).ToArray();
        int[] iPlane = Enumerable.Range(0, nf).Select(_ => a.Take()).ToArray();
        int[][] iFin = loops.Select(l => l.Loop.Select(_ => a.Take()).ToArray()).ToArray();

        var body = New(a, XtNodeTypes.Body, iBody);
        body["res_size"] = 1000.0000000000001; // as observed
        body["res_linear"] = 1e-8;
        body["next"] = nextBody;
        body["previous"] = prevBody;
        body["state"] = (byte)1;
        body["body_type"] = (byte)3;           // as observed in kubus.rsdocx (see NOTES.md)
        body["nom_geom_state"] = (byte)1;
        body["attribute_chains"] = iList;
        body["shell"] = iShIn;
        body["region"] = iRegOut;
        body["boundary_surface"] = iPlane[0];
        body["boundary_curve"] = iLine[0];
        body["boundary_point"] = iPoint[0];
        body["edge"] = iEdge[0];
        body["vertex"] = iVert[0];

        // ---- regions and shells
        var regOut = New(a, XtNodeTypes.Region, iRegOut);
        regOut["body"] = iBody; regOut["next"] = iRegIn; regOut["shell"] = iShOut; regOut["type"] = (byte)'V';
        var regIn = New(a, XtNodeTypes.Region, iRegIn);
        regIn["body"] = iBody; regIn["previous"] = iRegOut; regIn["shell"] = iShIn; regIn["type"] = (byte)'V';
        var shIn = New(a, XtNodeTypes.Shell, iShIn);
        shIn["body"] = iBody; shIn["face"] = iFace[0]; shIn["region"] = iRegIn;
        var shOut = New(a, XtNodeTypes.Shell, iShOut);
        shOut["region"] = iRegOut; shOut["front_face"] = iFace[0];

        // ---- fins per face, map directed edge -> fin
        var finOf = new Dictionary<(int, int), int>();
        for (int li = 0; li < nl; li++)
        {
            var f = loops[li].Loop;
            for (int k = 0; k < f.Length; k++) finOf[(f[k], f[(k + 1) % f.Length])] = iFin[li][k];
        }
        var edgeOfFin = new Dictionary<int, int>();
        for (int ei = 0; ei < ne; ei++)
        {
            edgeOfFin[finOf[(edges[ei].A, edges[ei].B)]] = ei;
            edgeOfFin[finOf[(edges[ei].B, edges[ei].A)]] = ei;
        }
        // fins ending at each vertex (fin.vertex = forward vertex)
        var finsAtVx = Enumerable.Range(0, nv).Select(_ => new List<int>()).ToArray();

        for (int li = 0; li < nl; li++)
        {
            var f = loops[li].Loop;
            int m = f.Length;
            for (int k = 0; k < m; k++)
            {
                int from = f[k], to = f[(k + 1) % m];
                int ei = edgeOfFin[iFin[li][k]];
                bool positive = edges[ei].A == from; // edge direction is A -> B
                var fin = New(a, XtNodeTypes.Fin, iFin[li][k]);
                fin["loop"] = iLoop[li];
                fin["forward"] = iFin[li][(k + 1) % m];
                fin["backward"] = iFin[li][(k + m - 1) % m];
                fin["vertex"] = iVert[to];
                fin["other"] = finOf[(to, from)];
                fin["edge"] = iEdge[ei];
                fin["sense"] = (byte)(positive ? '+' : '-');
                finsAtVx[to].Add(iFin[li][k]);
            }
        }
        var finNodes = a.Nodes.Where(n => n.NodeType == XtNodeTypes.Fin).ToDictionary(n => n.Index);
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
            var p = poly.Vertices[v];
            pt["pvec"] = new[] { p.X, p.Y, p.Z };
        }

        // ---- edges and lines
        var attrs = new List<(int owner, string name)> { (iBody, names.Body) };
        for (int ei = 0; ei < ne; ei++)
        {
            var (va, vb) = edges[ei];
            var e = New(a, XtNodeTypes.Edge, iEdge[ei]);
            e["node_id"] = a.Id();
            e["tolerance"] = XtNodeTypes.NullDouble;
            e["fin"] = finOf[(va, vb)]; // positive fin
            e["previous"] = ei > 0 ? iEdge[ei - 1] : 0;
            e["next"] = ei + 1 < ne ? iEdge[ei + 1] : 0;
            e["curve"] = iLine[ei];
            e["owner"] = iBody;
            var ln = New(a, XtNodeTypes.Line, iLine[ei]);
            ln["node_id"] = a.Id();
            ln["owner"] = iEdge[ei];
            ln["previous"] = ei > 0 ? iLine[ei - 1] : 0;
            ln["next"] = ei + 1 < ne ? iLine[ei + 1] : 0;
            ln["sense"] = (byte)'+';
            var pa = poly.Vertices[va];
            ln["pvec"] = new[] { pa.X, pa.Y, pa.Z };
            ln["direction"] = Normalize(Sub(poly.Vertices[vb], pa));
        }

        // ---- faces, loops, planes
        for (int fi = 0; fi < nf; fi++)
        {
            var f = poly.Faces[fi];
            var normal = FaceNormal(poly, f);
            var fc = New(a, XtNodeTypes.Face, iFace[fi]);
            fc["node_id"] = a.Id();
            fc["tolerance"] = XtNodeTypes.NullDouble;
            fc["next"] = fi + 1 < nf ? iFace[fi + 1] : 0;
            fc["previous"] = fi > 0 ? iFace[fi - 1] : 0;
            int firstLoop = loops.FindIndex(l => l.Face == fi);
            fc["loop"] = iLoop[firstLoop];
            fc["shell"] = iShIn;
            fc["surface"] = iPlane[fi];
            fc["sense"] = (byte)'+';
            fc["next_front"] = fi + 1 < nf ? iFace[fi + 1] : 0;
            fc["previous_front"] = fi > 0 ? iFace[fi - 1] : 0;
            fc["front_shell"] = iShOut;
            var faceLoops = Enumerable.Range(0, nl).Where(li => loops[li].Face == fi).ToList();
            for (int k = 0; k < faceLoops.Count; k++)
            {
                var lp = New(a, XtNodeTypes.Loop, iLoop[faceLoops[k]]);
                lp["node_id"] = a.Id();
                lp["fin"] = iFin[faceLoops[k]][0];
                lp["face"] = iFace[fi];
                lp["next"] = k + 1 < faceLoops.Count ? iLoop[faceLoops[k + 1]] : 0;
            }
            var pl = New(a, XtNodeTypes.Plane, iPlane[fi]);
            pl["node_id"] = a.Id();
            pl["owner"] = iFace[fi];
            pl["previous"] = fi > 0 ? iPlane[fi - 1] : 0;
            pl["next"] = fi + 1 < nf ? iPlane[fi + 1] : 0;
            pl["sense"] = (byte)'+';
            var p0 = poly.Vertices[f[0]];
            pl["pvec"] = new[] { p0.X, p0.Y, p0.Z };
            pl["normal"] = normal;
            // x_axis: first edge direction, made exactly perpendicular to the normal
            var x = Sub(poly.Vertices[f[1]], p0);
            double d = Dot(x, normal);
            pl["x_axis"] = Normalize((x[0] - d * normal[0], x[1] - d * normal[1], x[2] - d * normal[2]));
            attrs.Add((iFace[fi], names.Faces[fi]));
        }
        for (int ei = 0; ei < ne; ei++) attrs.Add((iEdge[ei], names.Edges[ei]));

        // ---- name attributes, chained by next_of_type; list header points at the first one
        var byIndex = a.Nodes.ToDictionary(n => n.Index);
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

        // regions/shells get node ids too; body records the highest
        foreach (var n in new[] { regOut, regIn, shIn, shOut }) n["node_id"] = a.Id();
        body["highest_node_id"] = a.NextNodeId - 1;
    }

    private static void ValidateClosed(Polyhedron p, List<(int A, int B)> edges)
    {
        var directed = new Dictionary<(int, int), int>();
        foreach (var (_, f) in p.AllLoops())
        {
            if (f.Length < 3) throw new ArgumentException("Face with fewer than 3 vertices");
            for (int k = 0; k < f.Length; k++)
            {
                var key = (f[k], f[(k + 1) % f.Length]);
                directed[key] = directed.GetValueOrDefault(key) + 1;
            }
        }
        foreach (var (a, b) in edges)
            if (directed.GetValueOrDefault((a, b)) != 1 || directed.GetValueOrDefault((b, a)) != 1)
                throw new ArgumentException($"Edge {a}-{b} is not shared by exactly two consistently oriented faces");
    }

    private static object CloneValue(object v) => v switch
    {
        double[] d => d.Clone(),
        object[] arr => arr.Select(CloneValue).ToArray(),
        _ => v,
    };

    private static double[] Sub((double X, double Y, double Z) a, (double X, double Y, double Z) b) => [a.X - b.X, a.Y - b.Y, a.Z - b.Z];
    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static double[] Normalize(double[] v) => Normalize((v[0], v[1], v[2]));
    private static double[] Normalize((double X, double Y, double Z) v)
    {
        double l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (l == 0) throw new ArgumentException("Degenerate vector");
        // Avoid -0 noise: snap tiny components to 0 so axis-aligned planes get exact ±1 normals.
        double S(double c) { c /= l; return Math.Abs(c) < 1e-15 ? 0.0 : c; }
        return [S(v.X), S(v.Y), S(v.Z)];
    }

    /// <summary>Newell normal of a polygon (outward for a CCW loop seen from outside).</summary>
    private static double[] FaceNormal(Polyhedron p, int[] f)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int k = 0; k < f.Length; k++)
        {
            var c = p.Vertices[f[k]]; var n = p.Vertices[f[(k + 1) % f.Length]];
            nx += (c.Y - n.Y) * (c.Z + n.Z);
            ny += (c.Z - n.Z) * (c.X + n.X);
            nz += (c.X - n.X) * (c.Y + n.Y);
        }
        return Normalize((nx, ny, nz));
    }
}
