using System.Xml.Linq;
using RsDocx.Core.Package;
using RsDocx.Core.Xt;

namespace RsDocx.Tests;

public class KubusXtTests
{
    private const string GeometryPart = "SpaceClaim/Geometry/part1bodies.x_b";

    internal static string TestData(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "testdata", name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException($"testdata/{name} not found");
    }

    private static readonly Lazy<RsDocxPackage> Pkg = new(() => RsDocxPackage.Open(TestData("kubus.rsdocx")));
    private static byte[] XtBytes => Pkg.Value.GetPart(GeometryPart);

    [Fact]
    public void Package_contains_expected_parts()
    {
        var expected = new[]
        {
            "[Content_Types].xml", "_rels/.rels", "docProps/app.xml", "docProps/custom.xml", "docProps/thumbnail.png",
            "SpaceClaim/document.xml", "SpaceClaim/versions.xml", "SpaceClaim/_rels/document.xml.rels",
            "SpaceClaim/_rels/checksums.bin.rels", "SpaceClaim/checksums.bin", "SpaceClaim/Schemas/package.xsd",
            "SpaceClaim/UI/windows.xml", "SpaceClaim/Graphics/facets.bin", "SpaceClaim/Graphics/renderlist.xml", GeometryPart,
        };
        var actual = Pkg.Value.Entries.Select(e => e.Path).ToList();
        foreach (var p in expected) Assert.Contains(p, actual);
        Assert.Single(actual, p => p.StartsWith("package/services/metadata/core-properties/") && p.EndsWith(".psmdcp"));
        Assert.Equal(expected.Length + 1, actual.Count);
        Assert.Empty(Pkg.Value.GetPart("SpaceClaim/checksums.bin"));
        Assert.Equal(17570, XtBytes.Length);
    }

    [Fact]
    public void Parser_consumes_file_exactly_up_to_terminator()
    {
        var file = XtReader.Read(XtBytes, out int consumed);
        Assert.Equal(XtBytes.Length, consumed);
        Assert.Equal(": TRANSMIT FILE created by modeller version 3401212", file.ModellerVersion);
        Assert.Equal("SCH_3401212_34101_13006", file.SchemaVersion);
        Assert.Equal((short)231, file.MaxNodeTypes);
        Assert.Equal(0, file.UserFieldSize);
        Assert.Equal(XtNodeTypes.PartXmtBlock, file.Nodes[0].NodeType);
        Assert.Equal(1, file.Nodes[0].Index);
    }

    [Fact]
    public void Node_counts_match_four_cubes()
    {
        var file = XtReader.Read(XtBytes);
        int Count(int t) => file.Nodes.Count(n => n.NodeType == t);
        Assert.Equal(4, Count(XtNodeTypes.Body));
        Assert.Equal(24, Count(XtNodeTypes.Face));
        Assert.Equal(48, Count(XtNodeTypes.Edge));
        Assert.Equal(32, Count(XtNodeTypes.Vertex));
        Assert.Equal(96, Count(XtNodeTypes.Fin));
        Assert.Equal(24, Count(XtNodeTypes.Plane));
        Assert.Equal(48, Count(XtNodeTypes.Line));
        Assert.Equal(32, Count(XtNodeTypes.Point));
    }

    [Fact]
    public void Topology_per_body_is_a_cube()
    {
        var bodies = XtTopology.Bodies(XtReader.Read(XtBytes));
        Assert.Equal(4, bodies.Count);
        foreach (var b in bodies)
        {
            Assert.Equal(6, b.Faces.Count);
            Assert.Equal(12, b.Edges.Count);
            Assert.Equal(8, b.Vertices.Count);
            Assert.Equal(2, b.EulerCharacteristic);
            Assert.All(b.Faces, f => Assert.Equal(4, f.FinCount));
            Assert.All(b.Faces, f => Assert.Equal(XtNodeTypes.Plane, f.SurfaceType));
        }
        // 24 faces x 4 fins = 96 fins = 2 per edge
        Assert.Equal(96, bodies.Sum(b => b.Faces.Sum(f => f.FinCount)));
    }

    [Fact]
    public void Vertices_match_cube_boxes_in_mm()
    {
        var boxes = new (double x0, double x1, double y0, double y1, double z0, double z1)[]
        {
            (-153, -143, -10, 0, 0, 10),
            (-113, -93, -17, 3, 0, 20),
            (-80, -50, -23, 7, 0, 30),
            (-10, 40, -42, 8, 0, 50),
        };
        var expected = new HashSet<(double, double, double)>();
        foreach (var b in boxes)
            foreach (var x in new[] { b.x0, b.x1 })
                foreach (var y in new[] { b.y0, b.y1 })
                    foreach (var z in new[] { b.z0, b.z1 })
                        expected.Add((x, y, z));

        var bodies = XtTopology.Bodies(XtReader.Read(XtBytes));
        var actual = bodies.SelectMany(b => b.Vertices)
            .Select(v => (Math.Round(v.X * 1000, 9), Math.Round(v.Y * 1000, 9), Math.Round(v.Z * 1000, 9)))
            .ToHashSet();
        Assert.Equal(32, actual.Count);
        Assert.True(expected.SetEquals(actual));

        // Each body's 8 vertices form exactly one of the boxes.
        foreach (var body in bodies)
        {
            var xs = body.Vertices.Select(v => Math.Round(v.X * 1000, 9)).Distinct().Order().ToArray();
            var zs = body.Vertices.Select(v => Math.Round(v.Z * 1000, 9)).Distinct().Order().ToArray();
            Assert.Equal(2, xs.Length);
            Assert.Contains(boxes, b => b.x0 == xs[0] && b.x1 == xs[1] && b.z0 == zs[0] && b.z1 == zs[1]);
        }
    }

    [Fact]
    public void Plane_normals_are_axis_aligned_unit_vectors()
    {
        var file = XtReader.Read(XtBytes);
        foreach (var plane in file.Nodes.Where(n => n.NodeType == XtNodeTypes.Plane))
        {
            var n = plane.Vec("normal");
            Assert.Equal(1.0, n.Sum(Math.Abs), 12);
            Assert.Single(n, c => Math.Abs(c) == 1.0);
        }
    }

    [Fact]
    public void Roundtrip_is_byte_identical()
    {
        var file = XtReader.Read(XtBytes);
        var written = XtWriter.Write(file);
        Assert.Equal(XtBytes, written);

        var again = XtReader.Read(written, out int consumed);
        Assert.Equal(written.Length, consumed);
        Assert.Equal(file.Nodes.Count, again.Nodes.Count);
    }

    [Fact]
    public void Xt_names_match_document_xml_ids()
    {
        var bodies = XtTopology.Bodies(XtReader.Read(XtBytes));
        XNamespace nom = "urn:nom";
        var doc = Pkg.Value.GetXml("SpaceClaim/document.xml");
        var docBodies = doc.Descendants(nom + "NominalBodyDef").ToList();
        Assert.Equal(4, docBodies.Count);

        foreach (var db in docBodies)
        {
            string id = (string)db.Attribute("Id")!;
            var xb = Assert.Single(bodies, b => b.Name == id);
            var faceIds = db.Elements(nom + "NominalFaceDef").Select(e => (string)e.Attribute("Id")!).Order().ToList();
            var edgeIds = db.Elements(nom + "NominalEdgeDef").Select(e => (string)e.Attribute("Id")!).Order().ToList();
            Assert.Equal(faceIds, xb.Faces.Select(f => f.Name!).Order().ToList());
            Assert.Equal(edgeIds, xb.Edges.Select(e => e.Name!).Order().ToList());
        }
    }

    [Fact]
    public void Geometry_relationship_points_at_x_b_with_version_guid_and_partdef_id()
    {
        var rel = Assert.Single(Pkg.Value.Relationships(), r => r.Type.Contains("/partBodyGeometry#"));
        Assert.Equal("/" + GeometryPart, rel.Target);
        string suffix = rel.Type[(rel.Type.IndexOf('#') + 1)..]; // "<guid>:<partdef id>"
        var parts = suffix.Split(':');
        var versions = Pkg.Value.GetXml("SpaceClaim/versions.xml").Descendants("guid").Select(g => g.Value).ToList();
        Assert.Contains(parts[0], versions);
        XNamespace nom = "urn:nom";
        var partDef = Pkg.Value.GetXml("SpaceClaim/document.xml").Descendants(nom + "PartDef").Single();
        Assert.Equal("0:" + parts[1], (string)partDef.Attribute("Id")!);
    }
}
