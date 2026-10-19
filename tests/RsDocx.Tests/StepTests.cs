using RsDocx.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Step;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace RsDocx.Tests;

/// <summary>STEP written and read back: the same bodies (vertices, edges, faces, surfaces), still sound for Parasolid
/// (a .rsdocx made of what was read passes the validator), with names, face colours and loose lines.</summary>
public class StepTests
{
    private const double Mm = 0.001;

    public static TheoryData<string> Shapes() => new(["blok", "afgerond blok", "holle afronding", "cilinder", "staaf", "donut", "holle afronding (gedraaid)", "bol en groef", "schaaltje", "los vlak", "tolvorm"]);

    private static BrepBody Shape(string name)
    {
        var revolve = TorusTests.RevolveShapes().ToDictionary(r => (string)r[0], r => BrepShapes.Revolve(((double, double))r[1], (List<ProfileSegment>)r[2]));
        return name switch
        {
            "blok" => BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 40 * Mm, 30 * Mm, 20 * Mm)),
            "afgerond blok" => CurvedShapeTests.RoundedBox(),
            "holle afronding" => CurvedShapeTests.HollowFillet(),
            "cilinder" => CurvedShapeTests.Cylinder(),
            "holle afronding (gedraaid)" => revolve["holle afronding"],
            "schaaltje" => ConeTests.Schaaltje(),
            "los vlak" => SheetTests.LikeReference(),
            "tolvorm" => FreeFormTests.Revolved(FreeFormTests.Reference()).Body,
            _ => revolve[name],
        };
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_body_goes_through_STEP_unchanged(string name)
    {
        var body = Shape(name);
        string text = StepWriter.Write(new StepModel([new StepBody(body, "Vaste vorm 1")], []), "proef.step");
        var back = StepReader.Parse(text);
        var b = Assert.Single(back.Bodies);
        Assert.Equal("Vaste vorm 1", b.Name);
        Assert.Equal(body.Sheet, b.Body.Sheet);
        Assert.Equal(body.Vertices.Count, b.Body.Vertices.Count);
        Assert.Equal(body.Edges.Count, b.Body.Edges.Count);
        Assert.Equal(body.Faces.Count, b.Body.Faces.Count);
        for (int f = 0; f < body.Faces.Count; f++)
        {
            Assert.Equal(body.Faces[f].Surface.GetType(), b.Body.Faces[f].Surface.GetType());
            Assert.Equal(body.Faces[f].Reversed, b.Body.Faces[f].Reversed);
            Assert.Equal(body.Faces[f].Loops.Select(l => l.Count), b.Body.Faces[f].Loops.Select(l => l.Count));
            Assert.Equal(body.Faces[f].Points.Count, b.Body.Faces[f].Points.Count);
        }
        for (int v = 0; v < body.Vertices.Count; v++) Assert.True((body.Vertices[v] - b.Body.Vertices[v]).Length < 1e-12);
        // still sound: as a DesignSpark file through the strict validator
        Assert.Null(XtBrepBuilder.TopologyError(b.Body));
        RsDocxComposer.Compose([b.Body]);
        // written again: the same text (nothing lost or changed on the way)
        Assert.Equal(text, StepWriter.Write(back with { }, "proef.step", ParseTime(text)));
    }

    private static DateTime ParseTime(string text)
    {
        int i = text.IndexOf("'20", StringComparison.Ordinal);
        return DateTime.ParseExact(text.Substring(i + 1, 19), "yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Colours_loose_lines_and_several_bodies_go_through_STEP()
    {
        var block = Shape("blok"); var sheet = Shape("los vlak");
        var colours = new Dictionary<int, uint> { [0] = 0xFF2563EB, [3] = 0xFFFACC15 };
        var arc = new BrepCircle(new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0), 10 * Mm);
        var model = new StepModel([new StepBody(block, "Blok 'a'", colours), new StepBody(sheet, "Oppervlak é")],
            [new StepCurve(new Vec3(0, 0, 0), new Vec3(0.05, 0, 0)), new StepCurve(new Vec3(10 * Mm, 0, 0), new Vec3(0, 10 * Mm, 0), arc)]);
        string text = StepWriter.Write(model, "proef.step");
        // an assembly named after the file, each body a part in it named after the body (what FreeCAD shows)
        Assert.Contains("PRODUCT('proef','proef'", text);
        Assert.Contains("PRODUCT('Blok ''a''','Blok ''a'''", text);
        Assert.Equal(2, text.Split("NEXT_ASSEMBLY_USAGE_OCCURRENCE(").Length - 1);
        var back = StepReader.Parse(text);
        Assert.Equal(["Blok 'a'", "Oppervlak é"], back.Bodies.Select(b => b.Name));
        // each once, where it stood
        for (int v = 0; v < block.Vertices.Count; v++) Assert.True((block.Vertices[v] - back.Bodies[0].Body.Vertices[v]).Length < 1e-12);
        // written again: the same
        Assert.Equal(text, StepWriter.Write(back, "proef.step", ParseTime(text)));
        Assert.Equal(colours, back.Bodies[0].FaceColors);
        Assert.Null(back.Bodies[1].FaceColors);
        Assert.Equal(2, back.Curves.Count);
        Assert.Null(back.Curves[0].Arc);
        Assert.True((back.Curves[0].End - new Vec3(0.05, 0, 0)).Length < 1e-12);
        var a = Assert.IsType<BrepCircle>(back.Curves[1].Arc);
        Assert.Equal(10 * Mm, a.Radius, 12);
        Assert.True(a.Normal.Dot(new Vec3(0, 0, 1)) > 0.999);
        Assert.True((back.Curves[1].End - new Vec3(0, 10 * Mm, 0)).Length < 1e-12);
    }

    [Fact]
    public void Groups_go_through_STEP_as_assemblies()
    {
        BrepBody Block(double x) => BrepShapes.FromPolyhedron(Polyhedron.Box(x * Mm, 0, 0, (x + 10) * Mm, 10 * Mm, 10 * Mm));
        // "Paar" (two blocks) inside "Alles" with a third block; a fourth block on its own
        var model = new StepModel(
            [new StepBody(Block(0), "Links", Group: 1), new StepBody(Block(20), "Rechts", Group: 1), new StepBody(Block(40), "Derde", Group: 0), new StepBody(Block(60), "Los")],
            [], [new StepGroup("Alles"), new StepGroup("Paar", 0)]);
        string text = StepWriter.Write(model, "proef.step");
        Assert.Contains("PRODUCT('Alles','Alles'", text);
        Assert.Contains("PRODUCT('Paar','Paar'", text);
        Assert.Equal(6, text.Split("NEXT_ASSEMBLY_USAGE_OCCURRENCE(").Length - 1);
        var back = StepReader.Parse(text);
        Assert.Equal(["Alles", "Paar"], back.Groups.Select(g => g.Name));
        Assert.Equal([null, (int?)0], back.Groups.Select(g => g.Parent));
        var byName = back.Bodies.ToDictionary(b => b.Name);
        Assert.Equal(4, byName.Count);
        Assert.Equal(1, byName["Links"].Group);
        Assert.Equal(1, byName["Rechts"].Group);
        Assert.Equal(0, byName["Derde"].Group);
        Assert.Null(byName["Los"].Group);
        // each where it stood
        Assert.True((byName["Rechts"].Body.Vertices[0] - model.Bodies[1].Body.Vertices[0]).Length < 1e-12);
        // FreeCAD's file: parts in one product, no groups
        Assert.Empty(StepReader.Read(KubusXtTests.TestData("freecad_proef.step")).Groups);
    }

    [Fact]
    public void A_part_placed_twice_is_written_once_and_read_as_two_groups_sharing_it()
    {
        BrepBody Block(double x) => BrepShapes.FromPolyhedron(Polyhedron.Box(x * Mm, 0, 0, (x + 10) * Mm, 20 * Mm, 5 * Mm));
        // the second a quarter turn round z and 100 mm along x
        var turned = new StepPlace(new Vec3(100 * Mm, 0, 0), new Vec3(0, 1, 0), new Vec3(-1, 0, 0), new Vec3(0, 0, 1));
        Vec3 Dir(Vec3 v) => turned.X * v.X + turned.Y * v.Y + turned.Z * v.Z;
        Vec3 Out(Vec3 p) => turned.Origin + Dir(p);
        var block = Block(0);
        var there = new BrepBody
        {
            Vertices = block.Vertices.Select(Out).ToList(), Edges = block.Edges,
            Faces = block.Faces.Select(f => f with { Surface = f.Surface is BrepPlane p ? p with { Origin = Out(p.Origin), Normal = Dir(p.Normal), XAxis = Dir(p.XAxis) } : f.Surface }).ToList(),
        };
        var model = new StepModel(
            [new StepBody(block, "Blok", Group: 0), new StepBody(there, "Blok", Group: 1), new StepBody(Block(40), "Los")],
            [], [new StepGroup("Poot", Shared: 0, Place: StepPlace.Identity), new StepGroup("Poot", Shared: 0, Place: turned)]);
        string text = StepWriter.Write(model, "proef.step");
        // the block once (and the loose one), the part used twice
        Assert.Equal(2, text.Split("MANIFOLD_SOLID_BREP(").Length - 1);
        Assert.Equal(1, text.Split("PRODUCT('Poot','Poot'").Length - 1);
        var back = StepReader.Parse(text);
        Assert.Equal(3, back.Bodies.Count);
        Assert.Equal(2, back.Groups.Count);
        Assert.Equal(0, back.Groups[0].Shared);
        Assert.Equal(0, back.Groups[1].Shared);
        var second = back.Bodies.Single(b => b.Group == 1).Body;
        foreach (var v in there.Vertices) Assert.Contains(second.Vertices, w => (w - v).Length < 1e-9);
        // its place: the turn, relative to the first
        var p0 = back.Groups[0].Place!.Value; var p1 = back.Groups[1].Place!.Value;
        Assert.True(p0.Origin.Length < 1e-12 && (p0.X - new Vec3(1, 0, 0)).Length < 1e-12);
        Assert.True((p1.Origin - turned.Origin).Length < 1e-12 && (p1.X - turned.X).Length < 1e-12 && (p1.Z - turned.Z).Length < 1e-12);
        Assert.Null(back.Bodies.Single(b => b.Name == "Los").Group);
        Assert.Null(XtBrepBuilder.TopologyError(second));
    }

    [Fact]
    public void A_file_in_inches_and_degrees_comes_out_in_metres()
    {
        // a cone (half angle 30°) on a block, as a program working in inches might write it
        string text = StepWriter.Write(new StepModel([new StepBody(ConeTests.Schaaltje(), "")], []));
        string inch = text.Replace("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))", "(CONVERSION_BASED_UNIT('INCH',#9999)LENGTH_UNIT()NAMED_UNIT(#9998))")
            .Replace("DATA;\n", "DATA;\n#9997=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));\n#9998=DIMENSIONAL_EXPONENTS(1.,0.,0.,0.,0.,0.,0.);\n#9999=LENGTH_MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4),#9997);\n");
        var mm = StepReader.Parse(text).Bodies[0].Body; var inches = StepReader.Parse(inch).Bodies[0].Body;
        for (int v = 0; v < mm.Vertices.Count; v++) Assert.True((mm.Vertices[v] * 25.4 - inches.Vertices[v]).Length < 1e-9);
    }

    [Fact]
    public void What_cannot_be_read_is_said_by_name()
    {
        var e = Assert.Throws<StepException>(() => StepReader.Parse("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n#1=CARTESIAN_POINT('',(0.,0.,0.));\n#2=AXIS2_PLACEMENT_3D('',#1,$,$);\n#3=OFFSET_SURFACE('',#4,1.,.F.);\n#4=PLANE('',#2);\n#5=ADVANCED_FACE('',(),#3,.T.);\n#6=CLOSED_SHELL('',(#5));\n#7=MANIFOLD_SOLID_BREP('',#6);\nENDSEC;\nEND-ISO-10303-21;\n"));
        Assert.Contains("OFFSET_SURFACE", e.Message);
        Assert.Throws<StepException>(() => StepReader.Parse("solid x\nendsolid x\n"));
    }

    [Fact]
    public void A_STEP_file_from_FreeCAD_is_read()
    {
        // testdata/freecad_proef.step, written by FreeCAD (OpenCASCADE) as an assembly: a rounded block with a hole
        // (blue), a ball, a cone, a ring (yellow) and a loft (free form), each a part of its own; volumes as FreeCAD gave
        var m = StepReader.Read(KubusXtTests.TestData("freecad_proef.step"));
        Assert.Equal(["Blok", "Bol", "Kegel", "Torus", "Loft"], m.Bodies.Select(b => b.Name));
        Assert.Equal(0xFF2663EBu, m.Bodies[0].FaceColors![0]);
        Assert.All(m.Bodies[0].Body.Faces.Select((_, i) => i), i => Assert.Equal(m.Bodies[0].FaceColors![0], m.Bodies[0].FaceColors![i]));
        Assert.Contains(m.Bodies[4].Body.Faces, f => f.Surface is BrepNurbsSurface);
        foreach (var b in m.Bodies)
        {
            Assert.Null(XtBrepBuilder.TopologyError(b.Body));
            RsDocxComposer.Compose([b.Body]);
        }
        // the ball where FreeCAD put it: round (70, 15, 10) mm, r 10
        var ball = (BrepSphere)m.Bodies[1].Body.Faces[0].Surface;
        Assert.True((ball.Centre - new Vec3(70 * Mm, 15 * Mm, 10 * Mm)).Length < 1e-9);
        Assert.Equal(10 * Mm, ball.Radius, 12);
    }
}
