using System.Text;
using RsDocx.Core;
using RsDocx.Core.Package;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace RsDocx.Tests;

public class StlTests
{
    private static StlFile Kubus() => StlReader.Read(KubusXtTests.TestData("kubus.stl"));

    [Fact]
    public void Reads_binary_kubus_stl()
    {
        var stl = Kubus();
        Assert.True(stl.Binary);
        Assert.Equal(48, stl.Triangles.Count);
        Assert.Contains("unit=MM", stl.Header);
    }

    [Theory]
    [InlineData(false, 12)]
    [InlineData(true, 6)]
    public void Kubus_stl_gives_four_valid_bodies(bool merge, int facesPerBody)
    {
        var bodies = MeshConverter.Convert(Kubus(), new MeshOptions { MergeCoplanar = merge }, out var stats);
        Assert.Equal(4, bodies.Count);
        Assert.All(bodies, b => Assert.Equal(8, b.Vertices.Count));
        Assert.All(bodies, b => Assert.Equal(facesPerBody, b.Faces.Count));
        Assert.Equal(0, stats.MergedFallbacks);

        // same 32 corner points as the reference rsdocx
        var reference = XtTopology.Bodies(XtReader.Read(RsDocxPackage.Open(KubusXtTests.TestData("kubus.rsdocx"))
                .GetPart(RsDocxComposer.GeometryPart)))
            .SelectMany(b => b.Vertices).Select(v => (Math.Round(v.X, 9), Math.Round(v.Y, 9), Math.Round(v.Z, 9))).ToHashSet();
        var ours = bodies.SelectMany(b => b.Vertices).Select(v => (Math.Round(v.X, 9), Math.Round(v.Y, 9), Math.Round(v.Z, 9))).ToHashSet();
        Assert.True(reference.SetEquals(ours));

        var pkg = RsDocxComposer.Compose(bodies);
        var xt = XtReader.Read(pkg.GetPart(RsDocxComposer.GeometryPart), out int used);
        Assert.Equal(pkg.GetPart(RsDocxComposer.GeometryPart).Length, used);
        Assert.Empty(XtValidator.Validate(xt));
        Assert.Equal(4, XtTopology.Bodies(xt).Count);
        Assert.False(pkg.Contains("SpaceClaim/Graphics/facets.bin"));
        Assert.False(pkg.Contains("SpaceClaim/checksums.bin"));
        Assert.DoesNotContain("EncryptedData", Encoding.UTF8.GetString(pkg.GetPart(RsDocxComposer.DocumentPart)));
    }

    private static StlFile Ascii(string body) => StlReader.Read(Encoding.ASCII.GetBytes(body));

    private const string Tetra = """
        solid t
        facet normal 0 0 0
         outer loop
          vertex 0 0 0
          vertex 0 10 0
          vertex 10 0 0
         endloop
        endfacet
        facet normal 0 0 0
         outer loop
          vertex 0 0 0
          vertex 10 0 0
          vertex 0 0 10
         endloop
        endfacet
        facet normal 0 0 0
         outer loop
          vertex 0 0 0
          vertex 0 0 10
          vertex 0 10 0
         endloop
        endfacet
        facet normal 0 0 0
         outer loop
          vertex 10 0 0
          vertex 0 10 0
          vertex 0 0 10
         endloop
        endfacet
        endsolid t
        """;

    [Fact]
    public void Ascii_tetrahedron_converts()
    {
        var stl = Ascii(Tetra);
        Assert.False(stl.Binary);
        var bodies = MeshConverter.Convert(stl, new MeshOptions { MergeCoplanar = true }, out _);
        var b = Assert.Single(bodies);
        Assert.Equal(4, b.Faces.Count);
        Assert.Empty(XtValidator.Validate(XtReader.Read(RsDocxComposer.Compose(bodies).GetPart(RsDocxComposer.GeometryPart))));
    }

    [Fact]
    public void Inside_out_mesh_is_reoriented()
    {
        // swap two vertices in every facet -> all normals inward
        var stl = Ascii(Tetra);
        var inv = new StlFile { Header = "", Binary = false, Triangles = stl.Triangles.Select(t => new StlTriangle(t.A, t.C, t.B)).ToList() };
        var bodies = MeshConverter.Convert(inv, new MeshOptions(), out var stats);
        Assert.Equal(4, stats.FlippedTriangles);
        Assert.Empty(XtValidator.Validate(XtReader.Read(RsDocxComposer.Compose(bodies).GetPart(RsDocxComposer.GeometryPart))));
    }

    [Fact]
    public void Open_mesh_is_rejected_with_clear_message()
    {
        var stl = Ascii(Tetra);
        var open = new StlFile { Header = "", Binary = false, Triangles = stl.Triangles.Take(3).ToList() };
        var ex = Assert.Throws<MeshException>(() => MeshConverter.Convert(open, new MeshOptions(), out _));
        Assert.Contains("not watertight", ex.Message);
        Assert.Contains("3 open edges", ex.Message);
    }

    [Fact]
    public void Collinear_vertex_on_merged_edge_is_removed()
    {
        // cube 10 mm whose front face (y=0) and bottom face (z=0) share an edge split at (5,0,0)
        var v = new Vec3[] { new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0), new(0, 0, 10), new(10, 0, 10), new(10, 10, 10), new(0, 10, 10), new(5, 0, 0) };
        int[][] quads = [[0, 3, 2, 1], [4, 5, 6, 7], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        var tris = new List<StlTriangle>();
        foreach (var q in quads)
        {
            tris.Add(new(v[q[0]], v[q[1]], v[q[2]]));
            tris.Add(new(v[q[0]], v[q[2]], v[q[3]]));
        }
        // bottom (z=0) must also use vertex 8: replace its two triangles by a fan through 8
        tris.RemoveRange(0, 2);
        tris.AddRange([new(v[0], v[3], v[8]), new(v[3], v[2], v[8]), new(v[2], v[1], v[8])]);
        // front (y=0): 0,1,5,4 with 8 on edge 0-1
        tris.AddRange([new(v[0], v[8], v[4]), new(v[8], v[5], v[4]), new(v[8], v[1], v[5])]);

        var bodies = MeshConverter.Convert(new StlFile { Header = "", Binary = false, Triangles = tris }, new MeshOptions { MergeCoplanar = true }, out var stats);
        var b = Assert.Single(bodies);
        Assert.Equal(6, b.Faces.Count);
        Assert.Equal(8, b.Vertices.Count);
        Assert.Equal(1, stats.CollinearRemoved);
        Assert.Empty(XtValidator.Validate(XtReader.Read(RsDocxComposer.Compose(bodies).GetPart(RsDocxComposer.GeometryPart))));
    }
}

public class HoleTests
{
    [Fact]
    public void Frame_with_hole_merges_into_faces_with_inner_loops()
    {
        var tris = StlWriter.Frame(30, 10, 5);
        // roundtrip through the binary writer/reader as well
        var stl = StlReader.Read(StlWriter.WriteBinary(tris, "frame"));
        Assert.Equal(32, stl.Triangles.Count);

        var bodies = MeshConverter.Convert(stl, new MeshOptions { MergeCoplanar = true }, out var stats);
        var b = Assert.Single(bodies);
        Assert.Equal(0, stats.MergedFallbacks);
        Assert.Equal(10, b.Faces.Count);                       // top, bottom, 4 outer walls, 4 hole walls
        Assert.Equal(16, b.Vertices.Count);
        Assert.NotNull(b.Holes);
        Assert.Equal(2, b.Holes!.Count(h => h.Length == 1));   // top and bottom have one hole each
        Assert.All(b.Holes!.Where(h => h.Length == 1), h => Assert.Equal(4, h[0].Length));

        var xt = XtReader.Read(RsDocxComposer.Compose(bodies).GetPart(RsDocxComposer.GeometryPart));
        Assert.Empty(XtValidator.Validate(xt));
        var info = Assert.Single(XtTopology.Bodies(xt));
        Assert.Equal(24, info.Edges.Count);
        Assert.Equal(2, info.Faces.Count(f => f.LoopCount == 2));
    }

    [Fact]
    public void Frame_without_merge_is_valid_too()
    {
        var stl = StlReader.Read(StlWriter.WriteBinary(StlWriter.Frame(30, 10, 5)));
        var bodies = MeshConverter.Convert(stl, new MeshOptions(), out _);
        Assert.Equal(32, Assert.Single(bodies).Faces.Count);
        Assert.Empty(XtValidator.Validate(XtReader.Read(RsDocxComposer.Compose(bodies).GetPart(RsDocxComposer.GeometryPart))));
    }
}

public class RealStlTests
{
    [Theory]
    [InlineData("sleutelrek.stl", false)]
    [InlineData("sleutelrek.stl", true)]
    [InlineData("Spoelhouder.stl", false)]
    [InlineData("Spoelhouder.stl", true)]
    [InlineData("boltest.stl", false)]
    [InlineData("boltest.stl", true)]
    public void Real_stl_converts_to_valid_geometry(string file, bool merge)
    {
        var stl = StlReader.Read(KubusXtTests.TestData(file));
        var opt = new MeshOptions { MergeCoplanar = merge };
        var bodies = MeshConverter.Convert(stl, opt, out var stats);
        Assert.True(stats.MaxSnap <= opt.SnapTolerance);
        var pkg = RsDocxComposer.Compose(bodies); // throws if XtValidator finds errors
        var bytes = pkg.GetPart(RsDocxComposer.GeometryPart);
        XtReader.Read(bytes, out int used);
        Assert.Equal(bytes.Length, used);
    }
}

public class CylinderRecognitionTests
{
    [Fact]
    public void Sleutelrek_matches_spaceclaim_topology()
    {
        // the reference sleutelrek.rsdocx body has F=61 (31 planes, 30 cylinders), E=152, V=93
        var stl = StlReader.Read(KubusXtTests.TestData("sleutelrek.stl"));
        var bodies = MeshConverter.ConvertToBrep(stl, new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true }, out var stats);
        var b = Assert.Single(bodies);
        Assert.Equal(0, stats.RecognitionFallbacks);
        Assert.Equal(61, b.Faces.Count);
        Assert.Equal(30, b.Faces.Count(f => f.Surface is RsDocx.Core.Brep.BrepCylinder));
        Assert.Equal(152, b.Edges.Count);
        Assert.Equal(93, b.Vertices.Count);
        var radii = b.Faces.Select(f => f.Surface).OfType<RsDocx.Core.Brep.BrepCylinder>().Select(c => Math.Round(c.Radius * 1000, 4)).Distinct().Order();
        Assert.Equal(new[] { 2.0, 5.0 }, radii);
    }

    [Theory]
    [InlineData("kubus.stl")]
    [InlineData("Spoelhouder.stl")]
    public void Parallel_conversion_gives_the_same_result_as_one_shell_at_a_time(string file)
    {
        var stl = StlReader.Read(KubusXtTests.TestData(file));
        var opt = new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, RecognizeSpheres = true, RecognizeTori = true };
        static string Summary(List<RsDocx.Core.Brep.BrepBody> bodies) => string.Join(" | ", bodies.Select(b =>
            $"{b.Faces.Count} faces, {b.Edges.Count} edges: " + string.Join(",", b.Faces.Select(f => f.Surface.GetType().Name).GroupBy(x => x).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"))));
        var parallel = MeshConverter.ConvertToBrep(stl, opt, out var ps, out var pl);
        var serial = MeshConverter.ConvertToBrep(stl, opt with { MaxParallelism = 1 }, out var ss, out var sl);
        Assert.Equal(Summary(serial), Summary(parallel));
        Assert.Equal(sl.Triangles.Select(t => (t.Source, t.Body, t.Face)), pl.Triangles.Select(t => (t.Source, t.Body, t.Face)));
    }

    [Fact]
    public void RondeVorm_becomes_two_sphere_faces_like_the_original()
    {
        // the reference RondeVorm.rsdocx: two SPHERE faces meeting in one circle edge, the sphere axes through that circle
        var stl = StlReader.Read(KubusXtTests.TestData("RondeVorm.stl"));
        var bodies = MeshConverter.ConvertToBrep(stl, new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, RecognizeSpheres = true, RecognizeTori = true }, out _);
        var b = Assert.Single(bodies);
        Assert.Equal(2, b.Faces.Count);
        Assert.All(b.Faces, f => Assert.IsType<RsDocx.Core.Brep.BrepSphere>(f.Surface));
        Assert.Equal(new[] { 7.2801, 10.2234 }, b.Faces.Select(f => Math.Round(((RsDocx.Core.Brep.BrepSphere)f.Surface).Radius * 1000, 4)).Order());
        Assert.IsType<RsDocx.Core.Brep.BrepCircle>(Assert.Single(b.Edges).Curve);
        RsDocxComposer.Compose(bodies); // strict validation inside
    }

    [Theory]
    [InlineData("Spoelhouder.stl")]
    [InlineData("yubikey.stl")]
    [InlineData("boltest.stl")]
    public void Recognition_gives_valid_geometry(string file)
    {
        var stl = StlReader.Read(KubusXtTests.TestData(file));
        var bodies = MeshConverter.ConvertToBrep(stl, new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true }, out var stats);
        Assert.Equal(0, stats.RecognitionFallbacks);
        Assert.True(stats.Cylinders > 0);
        var pkg = RsDocxComposer.Compose(bodies); // strict validation inside
        var bytes = pkg.GetPart(RsDocxComposer.GeometryPart);
        XtReader.Read(bytes, out int used);
        Assert.Equal(bytes.Length, used);
    }
}

public class SphereRecognitionTests
{
    private static List<RsDocx.Core.Brep.BrepBody> Convert(List<StlTriangle> tris, out MeshStats stats)
    {
        var stl = StlReader.Read(StlWriter.WriteBinary(tris));
        return MeshConverter.ConvertToBrep(stl, new MeshOptions { MergeCoplanar = true, RecognizeSpheres = true, RecognizeCylinders = true }, out stats);
    }

    [Fact]
    public void Full_uv_sphere_becomes_one_face()
    {
        var bodies = Convert(StlWriter.UvSphere(new Vec3(5, -3, 20), 10, 36, 18), out var stats);
        var b = Assert.Single(bodies);
        Assert.Equal(0, stats.RecognitionFallbacks);
        var f = Assert.Single(b.Faces);
        var sph = Assert.IsType<RsDocx.Core.Brep.BrepSphere>(f.Surface);
        Assert.Equal(0.010, sph.Radius, 7);
        Assert.Empty(f.Loops);
        Assert.Empty(b.Edges);
        RsDocxComposer.Compose(bodies); // strict validation
    }

    [Fact]
    public void Dome_becomes_sphere_and_disc_with_circular_edge()
    {
        var bodies = Convert(StlWriter.UvSphere(new Vec3(0, 0, 0), 10, 48, 16, cutZ: 3), out var stats);
        var b = Assert.Single(bodies);
        Assert.Equal(0, stats.RecognitionFallbacks);
        Assert.Equal(2, b.Faces.Count);
        Assert.Single(b.Faces, f => f.Surface is RsDocx.Core.Brep.BrepSphere);
        var e = Assert.Single(b.Edges);
        var c = Assert.IsType<RsDocx.Core.Brep.BrepCircle>(e.Curve);
        Assert.Equal(Math.Sqrt(100 - 9) / 1000, c.Radius, 7);
        RsDocxComposer.Compose(bodies);
    }
}
