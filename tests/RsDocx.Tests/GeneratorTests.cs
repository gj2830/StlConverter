using System.Xml.Linq;
using RsDocx.Core.Package;
using RsDocx.Core.SpaceClaim;
using RsDocx.Core.Xt;

namespace RsDocx.Tests;

public class GeneratorTests
{
    private static RsDocxPackage Pkg() => RsDocxPackage.Open(KubusXtTests.TestData("kubus.rsdocx"));
    private static XtFile Template() => XtReader.Read(Pkg().GetPart("SpaceClaim/Geometry/part1bodies.x_b"));

    [Fact]
    public void Validator_accepts_reference()
    {
        Assert.Empty(XtValidator.Validate(Template()));
    }

    private static (XtFile xt, BodyIds ids) BuildBox(Polyhedron box)
    {
        var ids = new MonikerAllocator(0, 5000).ForBody(box.Faces.Count, XtPolyhedronBuilder.Edges(box).Count);
        var xt = new XtPolyhedronBuilder(Template()).Build(box, new PolyhedronNames { Body = ids.Body, Faces = ids.Faces, Edges = ids.Edges });
        return (xt, ids);
    }

    [Fact]
    public void Generated_box_is_valid_and_reparses_exactly()
    {
        var (xt, ids) = BuildBox(Polyhedron.Box(0.010, 0.020, 0.005, 0.035, 0.035, 0.045));
        Assert.Empty(XtValidator.Validate(xt));

        var bytes = XtWriter.Write(xt);
        var again = XtReader.Read(bytes, out int used);
        Assert.Equal(bytes.Length, used);
        Assert.Equal(bytes, XtWriter.Write(again));
        Assert.Empty(XtValidator.Validate(again));

        var body = Assert.Single(XtTopology.Bodies(again));
        Assert.Equal(ids.Body, body.Name);
        Assert.Equal(ids.Faces.Order(), body.Faces.Select(f => f.Name!).Order());
        Assert.Equal(ids.Edges.Order(), body.Edges.Select(e => e.Name!).Order());
        Assert.Equal(2, body.EulerCharacteristic);
    }

    [Fact]
    public void Generated_box_has_same_node_type_mix_as_reference_body()
    {
        var (xt, _) = BuildBox(Polyhedron.Box(0, 0, 0, 0.01, 0.01, 0.01));
        var reference = Template().Nodes.GroupBy(n => n.NodeType).ToDictionary(g => g.Key, g => g.Count());
        var generated = xt.Nodes.GroupBy(n => n.NodeType).ToDictionary(g => g.Key, g => g.Count());
        // reference holds 4 bodies sharing one ATTRIB_DEF/ATT_DEF_ID and one root
        foreach (var (type, count) in reference)
        {
            int expected = type is XtNodeTypes.AttribDef or XtNodeTypes.AttDefId or XtNodeTypes.PartXmtBlock ? count : count / 4;
            Assert.True(generated.TryGetValue(type, out int g) && g == expected, $"node type {type}: expected {expected}, got {g}");
        }
        Assert.Equal(reference.Keys.Order(), generated.Keys.Order());
    }

    [Fact]
    public void Document_builder_replaces_bodies()
    {
        // (into the built-in template, an empty document: the bodies and captions are made from nothing)
        var pkg = RsDocx.Core.RsDocxComposer.DefaultTemplate();
        var doc = pkg.GetXml("SpaceClaim/document.xml");
        var ids = new MonikerAllocator(0, 5000).ForBody(6, 12);
        DocumentBuilder.ReplaceBodies(doc, [ids], 5100);
        var nom = DocumentBuilder.Nom;
        var body = Assert.Single(doc.Descendants(nom + "NominalBodyDef"));
        Assert.Equal(ids.Body, (string)body.Attribute("Id")!);
        Assert.Equal("Solid", body.Element(nom + "type")!.Value);
        Assert.Equal(6, body.Elements(nom + "NominalFaceDef").Count());
        Assert.Equal(12, body.Elements(nom + "NominalEdgeDef").Count());
        Assert.Equal("5100", doc.Root!.Element(DocumentBuilder.Core + "nextId")!.Value);
        Assert.Empty(doc.Root!.Element(DocumentBuilder.Core + "originalToReplacements_Keys")!.Elements());
        var pres = DocumentBuilder.Presentation;
        var caption = Assert.Single(doc.Descendants(pres + "CaptionDef"));
        Assert.Equal(ids.Body, caption.Element(pres + "subjectId")!.Value);
        Assert.Equal(ids.Caption, (string)caption.Attribute("Id")!);
        Assert.Single(doc.Root!.Elements(), e => e.Name.LocalName == "EncryptedData");
    }

    [Fact]
    public void A_saved_file_holds_only_its_bodies()
    {
        // nothing of the template (sketch lines, annotation planes, other bodies) goes along; the geometry part is linked
        // from document.xml with the document's guid, its content type declared
        var pkg = RsDocx.Core.RsDocxComposer.Compose([Polyhedron.Box(0, 0, 0, 0.01, 0.01, 0.01), Polyhedron.Box(0.02, 0, 0, 0.03, 0.01, 0.01)]);
        var doc = pkg.GetXml("SpaceClaim/document.xml");
        var bodies = doc.Descendants(DocumentBuilder.Nom + "NominalBodyDef").Select(b => (string)b.Attribute("Id")!).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName is "SketchCurveDef" or "DatumPlaneDef" or "NoteDef");
        var captions = doc.Descendants(DocumentBuilder.Presentation + "CaptionDef").ToList();
        Assert.Equal(bodies, captions.Select(c => c.Element(DocumentBuilder.Presentation + "subjectId")!.Value));
        Assert.All(captions, c => Assert.Equal("Vaste vorm", c.Element(DocumentBuilder.Presentation + "name")!.Value));
        Assert.All(captions, c => Assert.Equal((string)c.Attribute("Id")!, c.Element(DocumentBuilder.Presentation + "updateState")!.Value));
        string guid = pkg.GetXml("SpaceClaim/versions.xml").Descendants().First(e => e.Name.LocalName == "guid").Value.Trim();
        Assert.Contains(pkg.Relationships(), r => r.Source == "SpaceClaim/document.xml" && r.Target == "/SpaceClaim/Geometry/part1bodies.x_b"
            && r.Type == $"http://www.spaceclaim.com/relationships/internal/partBodyGeometry#{guid}:2");
        Assert.Contains("Extension=\"x_b\"", System.Text.Encoding.UTF8.GetString(pkg.GetPart("[Content_Types].xml")));
        var xt = XtReader.Read(pkg.GetPart("SpaceClaim/Geometry/part1bodies.x_b"));
        Assert.Empty(XtValidator.Validate(xt));
        var rl = pkg.GetXml("SpaceClaim/Graphics/renderlist.xml");
        Assert.All(rl.Root!.Elements("View"), v => Assert.Equal(bodies, v.Element("Item")!.Elements("Body").Select(b => (string)b.Attribute("Id")!)));
    }

    [Fact]
    public void Package_repack_preserves_all_parts()
    {
        var pkg = Pkg();
        using var ms = new MemoryStream();
        pkg.Save(ms);
        ms.Position = 0;
        var again = RsDocxPackage.Open(ms);
        Assert.Equal(pkg.Entries.Select(e => e.Path), again.Entries.Select(e => e.Path));
        foreach (var e in pkg.Entries) Assert.Equal(pkg.GetPart(e.Path), again.GetPart(e.Path));
    }
}

public class CurvedShapeTests
{
    private const double Mm = 0.001;
    private static RsDocx.Core.Brep.ProfileSegment L(double y, double z) => new((y * Mm, z * Mm));
    private static RsDocx.Core.Brep.ProfileSegment Arc(double y, double z, double cy, double cz, bool ccw) => new((y * Mm, z * Mm), (cy * Mm, cz * Mm), ccw);

    public static RsDocx.Core.Brep.BrepBody RoundedBox() =>
        // 30 mm cube, edge at y=30,z=30 rounded R5 (convex)
        RsDocx.Core.Brep.BrepShapes.ExtrudeProfile((0, 0), [L(30, 0), L(30, 25), Arc(25, 30, 25, 25, true), L(0, 30), L(0, 0)], 0, 30 * Mm);

    public static RsDocx.Core.Brep.BrepBody HollowFillet() =>
        // L-profile 30 x 30, legs 10 mm, inner corner rounded R5 (hollow), extruded 20 mm
        RsDocx.Core.Brep.BrepShapes.ExtrudeProfile((0, 0), [L(30, 0), L(30, 10), L(15, 10), Arc(10, 15, 15, 15, false), L(10, 30), L(0, 30), L(0, 0)], 0, 20 * Mm);

    public static RsDocx.Core.Brep.BrepBody Cylinder() =>
        // cylinder D20 along x, length 30: profile = two half circles
        RsDocx.Core.Brep.BrepShapes.ExtrudeProfile((10 * Mm, 0), [Arc(-10, 0, 0, 0, true), Arc(10, 0, 0, 0, true)], 0, 30 * Mm);

    [Fact]
    public void Curved_shapes_validate_and_reparse()
    {
        foreach (var body in new[] { RoundedBox(), HollowFillet(), Cylinder() })
        {
            var pkg = RsDocx.Core.RsDocxComposer.Compose([body]); // throws on validation errors
            var bytes = pkg.GetPart(RsDocx.Core.RsDocxComposer.GeometryPart);
            var xt = XtReader.Read(bytes, out int used);
            Assert.Equal(bytes.Length, used);
            Assert.Empty(XtValidator.Validate(xt));
        }
    }

    [Fact]
    public void Wrong_fillet_orientation_is_caught_by_validator()
    {
        // same rounded box but the fillet face flagged as hollow: the validator must object
        var b = RoundedBox();
        var faces = b.Faces.Select(f => f.Surface is RsDocx.Core.Brep.BrepCylinder ? f with { Reversed = true } : f).ToList();
        var bad = new RsDocx.Core.Brep.BrepBody { Vertices = b.Vertices, Edges = b.Edges, Faces = faces };
        Assert.Throws<InvalidOperationException>(() => RsDocx.Core.RsDocxComposer.Compose([bad]));
    }
}
