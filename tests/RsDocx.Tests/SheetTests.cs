using RsDocx.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Package;
using RsDocx.Core.SpaceClaim;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace RsDocx.Tests;

/// <summary>
/// Faces without thickness (DesignSpark's "Oppervlak"): testdata/oppervlak.rsdocx holds, beside four cubes, one such
/// body of a rectangle and two discs drawn in the air (z = 50 mm). Ours must be built the same way.
/// </summary>
public class SheetTests
{
    private const double Mm = 0.001;
    private static RsDocxPackage Reference() => RsDocxPackage.Open(KubusXtTests.TestData("oppervlak.rsdocx"));
    private static XtFile ReferenceXt() => XtReader.Read(Reference().GetPart(RsDocxComposer.GeometryPart));

    private static readonly Vec3 Up = new(0, 0, 1), X = new(1, 0, 0);

    /// <summary>The faces of the reference body: rectangle x -130..-71, y 11..41 and discs r 53.6 at (62, -96) and
    /// r 15.5 at (-172, 49) mm, all at z 50 mm facing up.</summary>
    internal static BrepBody LikeReference()
    {
        var plane = new BrepPlane(new Vec3(0.015, -0.017, 0.05), Up, X);
        Vec3 P(double x, double y) => new(x * Mm, y * Mm, 0.05);
        BrepEdge Ring(double x, double y, double r) => new(-1, -1, new BrepCircle(P(x, y), Up, X, r * Mm));
        return new BrepBody
        {
            Vertices = [P(-130, 11), P(-71, 11), P(-71, 41), P(-130, 41)],
            Edges = [Ring(62, -96, 53.600373133029594), Ring(-172, 49, 15.524174696260011),
                new(0, 1, new BrepLine()), new(1, 2, new BrepLine()), new(2, 3, new BrepLine()), new(3, 0, new BrepLine())],
            Faces = [new(plane, false, [[new(0, true)]]), new(plane, false, [[new(1, true)]]),
                new(plane, false, [[new(2, true), new(3, true), new(4, true), new(5, true)]])],
            Sheet = true,
        };
    }

    internal static BrepNames Names(BrepBody b, string body = "0:1") => new()
    {
        Body = body, Faces = b.Faces.Select((_, i) => $"0:{100 + i}").ToList(), Edges = b.Edges.Select((_, i) => $"0:{200 + i}").ToList(),
    };

    /// <summary>How many nodes of each kind of topology and geometry a body holds (planes left out: DesignSpark lets the
    /// faces of one plane share it); <paramref name="more"/>: other kinds of node to count too.</summary>
    internal static Dictionary<int, int> Mix(XtFile file, int bodyIndex, params int[] more)
    {
        var map = file.ByIndex();
        int[] kinds = [XtNodeTypes.Region, XtNodeTypes.Shell, XtNodeTypes.Face, XtNodeTypes.Loop, XtNodeTypes.Fin, XtNodeTypes.Edge,
            XtNodeTypes.Vertex, XtNodeTypes.Point, XtNodeTypes.Line, XtNodeTypes.Circle, .. more];
        var seen = new HashSet<int>(); var todo = new Stack<int>([bodyIndex]);
        while (todo.Count > 0)
        {
            int i = todo.Pop();
            if (i == 0 || !seen.Add(i)) continue;
            var n = map[i];
            for (int k = 0; k < n.Values.Length; k++)
                if (n.Schema.Fields[k].IsPointer && n.Values[k] is int p && p != 0 && map.TryGetValue(p, out var q) && kinds.Contains(q.NodeType)) todo.Push(p);
        }
        return seen.Select(i => map[i]).Where(n => n.NodeType != XtNodeTypes.Body).GroupBy(n => n.NodeType).ToDictionary(g => g.Key, g => g.Count());
    }

    [Fact]
    public void The_reference_sheet_is_one_region_with_a_shell_per_piece()
    {
        var xt = ReferenceXt(); var map = xt.ByIndex();
        var body = Assert.Single(XtTopology.Bodies(xt), b => b.Name == "3:24");
        var region = Assert.Single(XtTopology.Chain(map, map[body.Index].Ptr("region"), "next"));
        var shells = XtTopology.Chain(map, region.Ptr("shell"), "next").ToList();
        Assert.Equal(3, shells.Count);
        Assert.All(shells, s => Assert.Equal(s.Ptr("face"), s.Ptr("front_face")));
        // every edge: one fin in a loop, the other in none
        foreach (var e in body.Edges)
        {
            var fin = map[map[e.Index].Ptr("fin")]; var other = map[fin.Ptr("other")];
            Assert.True((fin.Ptr("loop") == 0) != (other.Ptr("loop") == 0));
        }
        Assert.Empty(XtValidator.Validate(xt));
    }

    [Fact]
    public void A_sheet_is_built_as_DesignSpark_builds_it()
    {
        var b = LikeReference();
        Assert.Null(XtBrepBuilder.TopologyError(b));
        var xt = new XtBrepBuilder().Build([(b, Names(b))]);
        Assert.Empty(XtValidator.Validate(xt));
        var bytes = XtWriter.Write(xt);
        var again = XtReader.Read(bytes, out int used);
        Assert.Equal(bytes.Length, used);
        Assert.Empty(XtValidator.Validate(again));

        var reference = ReferenceXt();
        var refBody = XtTopology.Bodies(reference).Single(x => x.Name == "3:24");
        var ours = Assert.Single(XtTopology.Bodies(again));
        Assert.Equal(Mix(reference, refBody.Index), Mix(again, ours.Index));
        Assert.Equal(refBody.BodyType, ours.BodyType);
        var map = again.ByIndex();
        Assert.All(XtTopology.Chain(map, map[ours.Index].Ptr("region"), "next").SelectMany(r => XtTopology.Chain(map, r.Ptr("shell"), "next")),
            s => Assert.Equal(s.Ptr("face"), s.Ptr("front_face")));
        Assert.All(ours.Faces, f => Assert.Equal(map[f.Index].Ptr("shell"), map[f.Index].Ptr("front_shell")));
    }

    [Fact]
    public void A_sheet_of_two_faces_sharing_an_edge_is_one_piece()
    {
        // a 20 x 10 mm rectangle divided in two by the line x = 10: that line is used by both faces
        Vec3 P(double x, double y) => new(x * Mm, y * Mm, 0);
        var plane = new BrepPlane(new Vec3(0, 0, 0), Up, X);
        var b = new BrepBody
        {
            Vertices = [P(0, 0), P(10, 0), P(20, 0), P(20, 10), P(10, 10), P(0, 10)],
            Edges = [new(0, 1, new BrepLine()), new(1, 2, new BrepLine()), new(2, 3, new BrepLine()), new(3, 4, new BrepLine()),
                new(4, 5, new BrepLine()), new(5, 0, new BrepLine()), new(1, 4, new BrepLine())],
            Faces = [new(plane, false, [[new(0, true), new(6, true), new(4, true), new(5, true)]]),
                new(plane, false, [[new(1, true), new(2, true), new(3, true), new(6, false)]])],
            Sheet = true,
        };
        var xt = new XtBrepBuilder().Build([(b, Names(b))]);
        Assert.Empty(XtValidator.Validate(xt));
        var map = xt.ByIndex();
        var body = Assert.Single(XtTopology.Bodies(xt));
        var region = Assert.Single(XtTopology.Chain(map, map[body.Index].Ptr("region"), "next"));
        Assert.Single(XtTopology.Chain(map, region.Ptr("shell"), "next"));
        Assert.Equal(2, body.Faces.Count);
        // 7 edges: the shared one has two fins in loops, the six on the border one in a loop and one in none
        Assert.Equal(14, xt.Nodes.Count(n => n.NodeType == XtNodeTypes.Fin));
        Assert.Equal(6, xt.Nodes.Count(n => n.NodeType == XtNodeTypes.Fin && n.Ptr("loop") == 0));
    }

    [Fact]
    public void Topology_rules_for_a_sheet_and_a_solid()
    {
        var sheet = LikeReference();
        // the same faces as a solid: their border edges are used once only
        var solid = new BrepBody { Vertices = sheet.Vertices, Edges = sheet.Edges, Faces = sheet.Faces };
        Assert.NotNull(XtBrepBuilder.TopologyError(solid));
        // a sheet with an edge no face uses
        var loose = new BrepBody { Vertices = sheet.Vertices, Edges = [.. sheet.Edges, new BrepEdge(0, 2, new BrepLine())], Faces = sheet.Faces, Sheet = true };
        Assert.NotNull(XtBrepBuilder.TopologyError(loose));
    }

    [Fact]
    public void A_saved_sheet_is_a_transparent_layout_body_called_Oppervlak()
    {
        var pkg = RsDocxComposer.Compose([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 0.01, 0.01, 0.01)), LikeReference()]);
        var nom = DocumentBuilder.Nom; var pres = DocumentBuilder.Presentation;
        var doc = pkg.GetXml(RsDocxComposer.DocumentPart);
        var bodies = doc.Descendants(nom + "NominalBodyDef").ToList();
        Assert.Equal(["Solid", "Layout"], bodies.Select(b => b.Element(nom + "type")!.Value));
        Assert.Equal(["Opaque", "Transparent"], bodies.Select(b => b.Element(nom + "fillStyle")!.Value));
        var names = bodies.Select(b => doc.Descendants(pres + "CaptionDef").Single(c => c.Element(pres + "subjectId")!.Value == (string)b.Attribute("Id")!).Element(pres + "name")!.Value);
        Assert.Equal(["Vaste vorm", "Oppervlak"], names);
        // the render list as DesignSpark wrote it for its sheet (the layer colour at 40 %)
        var refSheet = Reference().GetXml(RsDocxComposer.RenderListPart).Descendants("Body").First(e => (string)e.Attribute("Id")! == "3:24");
        var rl = pkg.GetXml(RsDocxComposer.RenderListPart);
        Assert.All(rl.Root!.Elements("View"), v =>
        {
            var b = v.Element("Item")!.Elements("Body").ToList();
            Assert.Equal("0", (string)b[0].Attribute("BodyType")!);
            foreach (var at in new[] { "Color", "FillStyle", "BodyType", "RenderingStyle", "FinishStyle" })
                Assert.Equal((string)refSheet.Attribute(at)!, (string)b[1].Attribute(at)!);
        });
        Assert.Empty(XtValidator.Validate(XtReader.Read(pkg.GetPart(RsDocxComposer.GeometryPart))));
    }
}
