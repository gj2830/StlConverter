using System.Globalization;
using RsDocx.Core.Package;
using RsDocx.Core.SpaceClaim;
using RsDocx.Core.Xt;

// rsgen ronde1 <template.rsdocx> <outdir>
// Generates the files for DesignSpark test round 1 (see NOTES.md).

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 2 && args[0] == "teststl")
{
    // test meshes for recognition experiments
    Directory.CreateDirectory(args[1]);
    File.WriteAllBytes(Path.Combine(args[1], "bol_r10.stl"), RsDocx.Core.Stl.StlWriter.WriteBinary(RsDocx.Core.Stl.StlWriter.UvSphere(new(5, -3, 20), 10, 36, 18), "bol r10"));
    File.WriteAllBytes(Path.Combine(args[1], "koepel_r10.stl"), RsDocx.Core.Stl.StlWriter.WriteBinary(RsDocx.Core.Stl.StlWriter.UvSphere(new(0, 0, 0), 10, 48, 16, cutZ: 3), "koepel r10"));
    return 0;
}
if (args.Length != 3 || args[0] is not ("ronde1" or "ronde2" or "ronde3" or "ronde5" or "ronde7" or "ronde10"))
{
    Console.Error.WriteLine("usage: rsgen ronde1|ronde2|ronde3|ronde5|ronde7 <template.rsdocx> <outdir>");
    return 2;
}
string template = args[1], outDir = args[2];
Directory.CreateDirectory(outDir);

if (args[0] == "ronde10")
{
    // the rounded rod of round 7 with every ring edge written as a B-spline (B_CURVE) instead of a circle:
    // does DesignSpark accept our B-spline edges?
    var (_, start, segs) = RevolveShapes().First();
    var body = RsDocx.Core.Brep.BrepShapes.Revolve(start, segs);
    var edges = body.Edges.Select(e =>
    {
        var c = (RsDocx.Core.Brep.BrepCircle)e.Curve;
        var y = c.Normal.Cross(c.XAxis);
        var pts = Enumerable.Range(0, 241).Select(i => c.Centre + c.XAxis * (c.Radius * Math.Cos(2 * Math.PI * i / 240)) + y * (c.Radius * Math.Sin(2 * Math.PI * i / 240))).ToList();
        return e with { Curve = RsDocx.Core.Brep.BSpline.Interpolate(pts, closed: true) };
    }).ToList();
    var bsBody = new RsDocx.Core.Brep.BrepBody { Vertices = body.Vertices, Edges = edges, Faces = body.Faces };
    RsDocx.Core.RsDocxComposer.Compose([bsBody], RsDocxPackage.Open(template)).Save(Path.Combine(outDir, "W_staaf_bspline_randen.rsdocx"));
    Console.WriteLine($"W_staaf_bspline_randen: F{bsBody.Faces.Count} E{bsBody.Edges.Count} (all B-spline)");
    return 0;
}

if (args[0] == "ronde7")
{
    // bodies of revolution with tori (analytic), plus the same shapes tessellated as STL for recognition tests
    foreach (var (name, start, segs) in RevolveShapes())
    {
        var body = RsDocx.Core.Brep.BrepShapes.Revolve(start, segs);
        RsDocx.Core.RsDocxComposer.Compose([body], RsDocxPackage.Open(template)).Save(Path.Combine(outDir, name + ".rsdocx"));
        File.WriteAllBytes(Path.Combine(outDir, name + ".stl"),
            RsDocx.Core.Stl.StlWriter.WriteBinary(RsDocx.Core.Stl.StlWriter.Revolve(start, segs, 72, 7.5).Select(t => new RsDocx.Core.Stl.StlTriangle(t.A * 1000, t.B * 1000, t.C * 1000)).ToList(), name + "<stl unit=MM>"));
        // the same mesh tilted and shifted (axis not along a coordinate axis, as in real models)
        File.WriteAllBytes(Path.Combine(outDir, name + "_scheef.stl"),
            RsDocx.Core.Stl.StlWriter.WriteBinary(RsDocx.Core.Stl.StlWriter.Revolve(start, segs, 72, 7.5).Select(t => new RsDocx.Core.Stl.StlTriangle(Tilt(t.A) * 1000, Tilt(t.B) * 1000, Tilt(t.C) * 1000)).ToList(), name + " scheef<stl unit=MM>"));
        Console.WriteLine($"{name}: F{body.Faces.Count} E{body.Edges.Count} V{body.Vertices.Count}");
    }
    return 0;
}

// rotation about (1,1,0) by 0.7 rad plus a shift (same as TorusTests.Move)
static RsDocx.Core.Stl.Vec3 Tilt(RsDocx.Core.Stl.Vec3 p)
{
    var k = new RsDocx.Core.Stl.Vec3(1, 1, 0).Normalized(); double t = 0.7;
    var r = p * Math.Cos(t) + k.Cross(p) * Math.Sin(t) + k * (k.Dot(p) * (1 - Math.Cos(t)));
    return r + new RsDocx.Core.Stl.Vec3(0.012, -0.034, 0.05);
}

static IEnumerable<(string, (double, double), List<RsDocx.Core.Brep.ProfileSegment>)> RevolveShapes()
{
    const double mm = 0.001;
    RsDocx.Core.Brep.ProfileSegment L(double y, double z) => new((y * mm, z * mm));
    RsDocx.Core.Brep.ProfileSegment Arc(double y, double z, double cy, double cz, bool ccw) => new((y * mm, z * mm), (cy * mm, cz * mm), ccw);
    // rod R10 x 40 with a R3 round on the top edge
    yield return ("S_staaf_afgerond", (0, 0), [L(10, 0), L(10, 37), Arc(7, 40, 7, 37, true), L(0, 40)]);
    // full torus (donut) R20 / r8: one face without edges
    yield return ("T_donut", (28 * mm, 0), [Arc(28, 0, 20, 0, true)]);
    // plate R30 x 5 with a shaft R8, concave R4 fillet between them
    yield return ("U_holle_afronding", (0, 0), [L(30, 0), L(30, 5), L(12, 5), Arc(8, 9, 12, 9, false), L(8, 30), L(0, 30)]);
    // cylinder R12 with a hemisphere on top and a ring groove (concave torus) half way
    yield return ("V_bol_en_groef", (0, 0), [L(12, 0), L(12, 10), Arc(12, 16, 12, 13, false), L(12, 25), Arc(0, 37, 0, 25, true)]);
}

if (args[0] == "ronde5")
{
    // analytic cylinders and circular arcs (same shapes as the unit tests)
    const double mm = 0.001;
    RsDocx.Core.Brep.ProfileSegment L(double y, double z) => new((y * mm, z * mm));
    RsDocx.Core.Brep.ProfileSegment Arc(double y, double z, double cy, double cz, bool ccw) => new((y * mm, z * mm), (cy * mm, cz * mm), ccw);
    var shapes = new (string name, RsDocx.Core.Brep.BrepBody body)[]
    {
        ("P_afronding_bol", RsDocx.Core.Brep.BrepShapes.ExtrudeProfile((0, 0), [L(30, 0), L(30, 25), Arc(25, 30, 25, 25, true), L(0, 30), L(0, 0)], 0, 30 * mm)),
        ("Q_afronding_hol", RsDocx.Core.Brep.BrepShapes.ExtrudeProfile((0, 0), [L(30, 0), L(30, 10), L(15, 10), Arc(10, 15, 15, 15, false), L(10, 30), L(0, 30), L(0, 0)], 0, 20 * mm)),
        ("R_cilinder", RsDocx.Core.Brep.BrepShapes.ExtrudeProfile((10 * mm, 0), [Arc(-10, 0, 0, 0, true), Arc(10, 0, 0, 0, true)], 0, 30 * mm)),
    };
    foreach (var (name, body) in shapes)
    {
        RsDocx.Core.RsDocxComposer.Compose([body], RsDocxPackage.Open(template)).Save(Path.Combine(outDir, name + ".rsdocx"));
        Console.WriteLine($"{name}: F{body.Faces.Count} E{body.Edges.Count} V{body.Vertices.Count}");
    }
    return 0;
}

if (args[0] == "ronde3")
{
    // STL test shapes, written as .stl (for the user to inspect) and converted with --merge-coplanar
    var shapes = new (string name, List<RsDocx.Core.Stl.StlTriangle> tris)[]
    {
        ("K_frame_met_gat", RsDocx.Core.Stl.StlWriter.Frame(30, 10, 5)),
        ("L_l_profiel", LProfile()),
    };
    foreach (var (name, tris) in shapes)
    {
        var stlBytes = RsDocx.Core.Stl.StlWriter.WriteBinary(tris, name);
        File.WriteAllBytes(Path.Combine(outDir, name + ".stl"), stlBytes);
        var bodies = RsDocx.Core.Stl.MeshConverter.Convert(RsDocx.Core.Stl.StlReader.Read(stlBytes),
            new RsDocx.Core.Stl.MeshOptions { MergeCoplanar = true }, out var stats);
        RsDocx.Core.RsDocxComposer.Compose(bodies, RsDocxPackage.Open(template)).Save(Path.Combine(outDir, name + ".rsdocx"));
        Console.WriteLine($"{name}: {stats}");
    }
    return 0;
}

// L-shaped profile (outer legs 30 mm, leg width 10 mm), extruded 5 mm. The profile is split into two rectangles,
// so the top/bottom need merging into one non-convex face and the x=0 wall has an extra vertex at y=10.
static List<RsDocx.Core.Stl.StlTriangle> LProfile()
{
    var p = new (double x, double y)[] { (0, 0), (30, 0), (30, 10), (10, 10), (10, 30), (0, 30), (0, 10) };
    RsDocx.Core.Stl.Vec3 V(int i, double z) => new(p[i].x, p[i].y, z);
    var t = new List<RsDocx.Core.Stl.StlTriangle>();
    void Quad(RsDocx.Core.Stl.Vec3 a, RsDocx.Core.Stl.Vec3 b, RsDocx.Core.Stl.Vec3 c, RsDocx.Core.Stl.Vec3 d) { t.Add(new(a, b, c)); t.Add(new(a, c, d)); }
    const double h = 5;
    // top (+z): pentagon 0-1-2-3-6 (vertex 3 lies on 2-6, so it must be a corner here: no T-junction) + rectangle 6-3-4-5
    int[][] top = [[0, 1, 2], [0, 2, 3], [0, 3, 6], [6, 3, 4], [6, 4, 5]];
    foreach (var tr in top)
    {
        t.Add(new(V(tr[0], h), V(tr[1], h), V(tr[2], h)));
        t.Add(new(V(tr[0], 0), V(tr[2], 0), V(tr[1], 0))); // bottom (-z): reversed
    }
    // walls along the CCW profile 0..6
    for (int i = 0; i < p.Length; i++)
    {
        int n = (i + 1) % p.Length;
        Quad(V(i, 0), V(n, 0), V(n, h), V(i, h));
    }
    return t;
}

if (args[0] == "ronde2")
{
    // G: a real cube, 30 mm, x 0..30, y 0..30, z 0..30
    void SaveComposed(IReadOnlyList<Polyhedron> bodies, string name)
    {
        var pkg = RsDocx.Core.RsDocxComposer.Compose(bodies, RsDocxPackage.Open(template));
        string path = Path.Combine(outDir, name);
        pkg.Save(path);
        var xt = XtReader.Read(RsDocxPackage.Open(path).GetPart(RsDocx.Core.RsDocxComposer.GeometryPart));
        Console.WriteLine($"{name}: " + string.Join("; ", XtTopology.Bodies(xt).Select(b => $"{b.Name} F{b.Faces.Count} E{b.Edges.Count} V{b.Vertices.Count}")));
    }
    SaveComposed([Polyhedron.Box(0, 0, 0, 0.030, 0.030, 0.030)], "G_kubus_30mm.rsdocx");
    // H: tetrahedron with 20 mm legs at the origin: corners (0,0,0) (20,0,0) (0,20,0) (0,0,20)
    SaveComposed([new Polyhedron
    {
        Vertices = [(0, 0, 0), (0.020, 0, 0), (0, 0.020, 0), (0, 0, 0.020)],
        Faces = [[0, 2, 1], [0, 1, 3], [0, 3, 2], [1, 2, 3]],
    }], "H_tetraeder_20mm.rsdocx");
    return 0;
}

const string Geometry = "SpaceClaim/Geometry/part1bodies.x_b";
const string Doc = "SpaceClaim/document.xml";
const string Facets = "SpaceClaim/Graphics/facets.bin";
const string RenderList = "SpaceClaim/Graphics/renderlist.xml";
const string Thumbnail = "docProps/thumbnail.png";

RsDocxPackage Load() => RsDocxPackage.Open(template);

void StripChecksums(RsDocxPackage p)
{
    p.RemovePart("SpaceClaim/checksums.bin");
    p.RemovePart("SpaceClaim/_rels/checksums.bin.rels");
}

void StripPart(RsDocxPackage p, string part)
{
    p.RemovePart(part);
    p.RemoveRelationshipsTo(part);
}

void Save(RsDocxPackage p, string name)
{
    string path = Path.Combine(outDir, name);
    p.Save(path);
    // sanity: reopen and parse geometry
    var check = RsDocxPackage.Open(path);
    var xt = XtReader.Read(check.GetPart(Geometry), out int used);
    var bodies = XtTopology.Bodies(xt);
    Console.WriteLine($"{name}: {new FileInfo(path).Length} bytes, {check.Entries.Count} parts, XT {used} bytes, " +
                      string.Join("; ", bodies.Select(b => $"{b.Name} F{b.Faces.Count} E{b.Edges.Count} V{b.Vertices.Count}")));
}

// A: unchanged content, re-zipped by our writer
Save(Load(), "A_herverpakt.rsdocx");

// B: without checksums.bin / checksums.bin.rels
{
    var p = Load();
    StripChecksums(p);
    Save(p, "B_zonder_checksums.rsdocx");
}

// C: without display caches (facets.bin, renderlist.xml, thumbnail.png)
{
    var p = Load();
    foreach (var part in new[] { Facets, RenderList, Thumbnail }) StripPart(p, part);
    Save(p, "C_zonder_caches.rsdocx");
}

// E: only the x_b changed: the 10 mm cube (1:4589, x -153..-143) becomes 20 x 10 x 25 mm (x -153..-133, z 0..25).
{
    var p = Load();
    var xt = XtReader.Read(p.GetPart(Geometry));
    var map = xt.ByIndex();
    var body = XtTopology.Bodies(xt).Single(b => b.Name == "1:4589");
    double[] Map(double[] v) => [-0.153 + (v[0] + 0.153) * 2.0, v[1], v[2] * 2.5];
    var bodyNode = map[body.Index];
    foreach (var pt in XtTopology.Chain(map, bodyNode.Ptr("boundary_point"), "next")) pt["pvec"] = Map(pt.Vec("pvec"));
    foreach (var ln in XtTopology.Chain(map, bodyNode.Ptr("boundary_curve"), "next")) ln["pvec"] = Map(ln.Vec("pvec"));
    foreach (var pl in XtTopology.Chain(map, bodyNode.Ptr("boundary_surface"), "next")) pl["pvec"] = Map(pl.Vec("pvec"));
    p.SetPart(Geometry, XtWriter.Write(xt));
    Save(p, "E_kubus10_uitgerekt.rsdocx");
}

// F: brand-new box 25 x 15 x 40 mm at x 10..35, y 20..35, z 5..45, generated from scratch, new document.xml.
foreach (bool noEncrypted in new[] { false, true })
{
    var p = Load();
    var templateXt = XtReader.Read(p.GetPart(Geometry));
    var box = Polyhedron.Box(0.010, 0.020, 0.005, 0.035, 0.035, 0.045);
    var edges = XtPolyhedronBuilder.Edges(box);
    var ids = new MonikerAllocator(0, 5000);
    var bodyIds = ids.ForBody(box.Faces.Count, edges.Count);
    var xt = new XtPolyhedronBuilder(templateXt).Build(box, new PolyhedronNames { Body = bodyIds.Body, Faces = bodyIds.Faces, Edges = bodyIds.Edges });
    var bytes = XtWriter.Write(xt);
    XtReader.Read(bytes, out int used);
    if (used != bytes.Length) throw new InvalidOperationException("generated XT does not reparse exactly");
    p.SetPart(Geometry, bytes);

    var doc = p.GetXml(Doc);
    DocumentBuilder.ReplaceBodies(doc, [bodyIds], ids.Next, removeEncryptedData: noEncrypted);
    p.SetXml(Doc, doc);

    var rl = p.GetXml(RenderList);
    DocumentBuilder.UpdateRenderList(rl, [bodyIds.Body], (0.010, 0.020, 0.005, 0.035, 0.035, 0.045));
    p.SetXml(RenderList, rl);

    StripChecksums(p);
    StripPart(p, Facets);
    StripPart(p, Thumbnail);
    Save(p, noEncrypted ? "F2_nieuwe_kubus_zonder_encrypted.rsdocx" : "F1_nieuwe_kubus.rsdocx");
}
return 0;
