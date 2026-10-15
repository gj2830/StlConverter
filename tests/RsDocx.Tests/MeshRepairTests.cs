using RsDocx.Core;
using RsDocx.Core.Stl;
using Xunit;

namespace RsDocx.Tests;

public class MeshRepairTests
{
    // unit cube (mm), outward, two triangles per side; top side z = 1 split along (0,0,1)-(1,1,1)
    private static readonly Vec3[] P =
    [
        new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
        new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1),
    ];

    private static List<StlTriangle> Box(double size = 1, Vec3 at = default)
    {
        int[][] f = [[0, 2, 1], [0, 3, 2], [4, 5, 6], [4, 6, 7], [0, 1, 5], [0, 5, 4], [1, 2, 6], [1, 6, 5], [2, 3, 7], [2, 7, 6], [3, 0, 4], [3, 4, 7]];
        return f.Select(t => new StlTriangle(at + P[t[0]] * size, at + P[t[1]] * size, at + P[t[2]] * size)).ToList();
    }

    private static StlFile File(List<StlTriangle> tris) => new() { Header = "", Binary = true, Triangles = tris };

    private static int Faces(StlFile stl) => MeshConverter.ConvertToBrep(stl, new MeshOptions { MergeCoplanar = true }, out _).Sum(b => b.Faces.Count);

    [Fact]
    public void A_hole_is_filled_and_the_box_converts_to_six_faces()
    {
        var tris = Box(); tris.RemoveAt(2);
        Assert.Throws<MeshException>(() => Faces(File(tris)));
        var fixedStl = MeshRepair.Repair(File(tris), new MeshRepairOptions(), out var s);
        Assert.Equal(1, s.HolesFilled);
        Assert.Equal(0, s.OpenEdges);
        Assert.Equal(6, Faces(fixedStl));
    }

    [Fact]
    public void Duplicate_and_fin_triangles_are_removed()
    {
        var tris = Box();
        tris.Add(tris[0]);                                                        // duplicate
        tris.Add(new StlTriangle(P[0], P[1], new Vec3(0.5, -1, 0.5)));            // fin on the bottom edge (0)-(1)
        var fixedStl = MeshRepair.Repair(File(tris), new MeshRepairOptions(), out var s);
        Assert.Equal(1, s.DuplicatesRemoved);
        Assert.Equal(1, s.NonManifoldRemoved + s.PiecesRemoved);
        Assert.Equal(0, s.OpenEdges);
        Assert.Equal(6, Faces(fixedStl));
    }

    [Fact]
    public void A_small_loose_piece_is_removed()
    {
        var tris = Box(); tris.AddRange(Box(0.01, new Vec3(5, 5, 5)));
        var fixedStl = MeshRepair.Repair(File(tris), new MeshRepairOptions(), out var s);
        Assert.Equal(1, s.PiecesRemoved);
        Assert.Equal(12, fixedStl.Triangles.Count);
    }

    [Fact]
    public void A_T_junction_crack_is_closed_with_proper_triangles()
    {
        // one top triangle split at the middle of the diagonal, the other not: a crack the converter refuses
        var tris = Box();
        var m = (P[4] + P[6]) * 0.5;
        tris[2] = new StlTriangle(P[4], P[5], m);
        tris.Add(new StlTriangle(m, P[5], P[6]));
        Assert.Throws<MeshException>(() => Faces(File(tris)));
        var fixedStl = MeshRepair.Repair(File(tris), new MeshRepairOptions(), out var s);
        Assert.Equal(0, s.OpenEdges);
        Assert.Equal(0, s.FlatLeft);
        Assert.Equal(6, Faces(fixedStl));
    }

    [Fact]
    public void A_clean_mesh_is_left_as_it_is()
    {
        var stl = StlReader.Read(KubusXtTests.TestData("sleutelrek.stl"));
        var fixedStl = MeshRepair.Repair(stl, new MeshRepairOptions(), out var s);
        Assert.Equal(stl.Triangles.Count, fixedStl.Triangles.Count);
        Assert.Equal(0, s.HolesFilled + s.DuplicatesRemoved + s.NonManifoldRemoved + s.PiecesRemoved + s.FlatFixed);
        var key = (Vec3 p) => (Math.Round(p.X, 5), Math.Round(p.Y, 5), Math.Round(p.Z, 5));
        var before = stl.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Select(key).ToHashSet();
        Assert.All(fixedStl.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }), p => Assert.Contains(key(p), before));
    }

    /// <summary>The screw ball: 2 µm steps at the start of its thread; holes filled there were opened again by the
    /// later steps (folds, flat triangles) and 28 open edges were left. A last fill closes them.</summary>
    [Fact]
    public void Holes_opened_again_after_the_fill_are_closed_in_a_last_round()
    {
        var stl = StlReader.Read(KubusXtTests.TestData("schroefbol.stl"));
        MeshRepair.Repair(stl, new MeshRepairOptions(), out var s);
        Assert.Equal(0, s.OpenEdges);
    }

    /// <summary>Parts touching in one point within a body are counted (screw ball: 5, the conversion refuses them);
    /// separate bodies touching in a point are not (bigbox: 6 bodies, converts fine).</summary>
    [Fact]
    public void Pinched_vertices_are_counted_per_body()
    {
        var opt = new MeshOptions { Scale = 0.001 };
        Assert.Equal(0, MeshConverter.CountPinchedVertices(StlReader.Read(KubusXtTests.TestData("bigbox.stl")), opt));
        Assert.Equal(5, MeshConverter.CountPinchedVertices(StlReader.Read(KubusXtTests.TestData("schroefbol.stl")), opt));
    }

    [Fact]
    public void Simplifying_keeps_the_mesh_closed_and_only_original_vertices()
    {
        var stl = StlReader.Read(KubusXtTests.TestData("RondeVorm.stl"));
        var fixedStl = MeshRepair.Repair(stl, new MeshRepairOptions { TargetTriangles = 4000 }, out var s);
        Assert.True(fixedStl.Triangles.Count <= 4000);
        Assert.Equal(0, s.OpenEdges);
        var key = (Vec3 p) => (Math.Round(p.X, 5), Math.Round(p.Y, 5), Math.Round(p.Z, 5));
        var before = stl.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Select(key).ToHashSet();
        Assert.All(fixedStl.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }), p => Assert.Contains(key(p), before));
        // still two spheres
        var bodies = MeshConverter.ConvertToBrep(fixedStl, new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, RecognizeSpheres = true, RecognizeTori = true }, out var ms);
        Assert.Equal(2, ms.Spheres);
    }
}

public class ForcedGroupTests
{
    /// <summary>A 10 mm block whose top is a 4 x 4 grid with its 9 inner points raised by <paramref name="bump"/> mm (a hump
    /// that leaving out a few triangles does not cure).</summary>
    private static List<StlTriangle> BumpyBlock(double bump, out List<int> top)
    {
        const int n = 4; const double s = 10, h = 10;
        Vec3 P(int i, int j) => new(i * s / n, j * s / n, h + (i > 0 && i < n && j > 0 && j < n ? bump : 0));
        var tris = new List<StlTriangle>();
        top = new List<int>();
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                top.Add(tris.Count); tris.Add(new StlTriangle(P(i, j), P(i + 1, j), P(i + 1, j + 1)));
                top.Add(tris.Count); tris.Add(new StlTriangle(P(i, j), P(i + 1, j + 1), P(i, j + 1)));
            }
        tris.Add(new StlTriangle(new Vec3(0, 0, 0), new Vec3(s, s, 0), new Vec3(s, 0, 0)));
        tris.Add(new StlTriangle(new Vec3(0, 0, 0), new Vec3(0, s, 0), new Vec3(s, s, 0)));
        // the sides, walking around the top counter-clockwise: a fan from the first bottom corner
        (int, int)[] corners = [(0, 0), (n, 0), (n, n), (0, n)];
        for (int e = 0; e < 4; e++)
        {
            var (ia, ja) = corners[e]; var (ib, jb) = corners[(e + 1) % 4];
            var edge = Enumerable.Range(0, n + 1).Select(k => P(ia + (ib - ia) * k / n, ja + (jb - ja) * k / n)).ToList();
            var b0 = edge[0] with { Z = 0 }; var b1 = edge[^1] with { Z = 0 };
            tris.Add(new StlTriangle(b0, b1, edge[^1]));
            for (int k = n - 1; k >= 0; k--) tris.Add(new StlTriangle(b0, edge[k + 1], edge[k]));
        }
        return tris;
    }

    [Fact]
    public void A_bumpy_face_can_be_forced_flat()
    {
        var stl = new StlFile { Header = "", Binary = true, Triangles = BumpyBlock(0.3, out var top) };
        var plain = new MeshOptions { MergeCoplanar = true };
        // not forced: the best plane is 0.19 mm off the hump, more than a group may be
        MeshConverter.ConvertToBrep(stl, plain with { Hints = [new ShapeHint(top, FaceKind.Plane)] }, out var st0);
        Assert.Single(st0.HintFailures);
        // forced: one flat top, the block is six faces again, valid
        var bodies = MeshConverter.ConvertToBrep(stl, plain with { Hints = [new ShapeHint(top, FaceKind.Plane, Force: true)] }, out var st, out var labels);
        Assert.Empty(st.HintFailures);
        Assert.Equal(6, Assert.Single(bodies).Faces.Count);
        var group = labels.Faces.SelectMany(f => f).Single(f => f.Group == 0);
        Assert.Equal(FaceKind.Plane, group.Kind);
        Assert.True(group.Deviation > 0.1e-3 && group.Deviation < 0.3e-3, $"deviation {group.Deviation}");
        RsDocxComposer.Compose(bodies); // strict validation inside
    }
}

public class KeepFacetsTests
{
    private static int SourceKindCount(MeshLabels labels, int source, FaceKind kind) =>
        labels.Triangles.Count(t => t.Source == source && labels.FaceOf(t).Kind == kind);

    [Fact]
    public void A_detached_triangle_is_not_merged_into_its_plane()
    {
        var stl = StlReader.Read(KubusXtTests.TestData("sleutelrek.stl"));
        var opt = new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, RecognizeSpheres = true, RecognizeTori = true };
        var plain = MeshConverter.ConvertToBrep(stl, opt, out _, out var l0);
        // a triangle of the largest plane
        var big = l0.Triangles.Where(t => l0.FaceOf(t).Kind == FaceKind.Plane).GroupBy(t => (t.Body, t.Face)).MaxBy(g => g.Count())!;
        int tri = big.First().Source;
        var bodies = MeshConverter.ConvertToBrep(stl, opt with { KeepFacets = [tri] }, out _, out var labels);
        Assert.Equal(1, SourceKindCount(labels, tri, FaceKind.Facet));
        // (one more face or a few: in a CAD mesh every triangle of a plane touches its rim, so the plane is pinched there
        // and gives up the triangles at that corner too)
        Assert.InRange(bodies.Sum(b => b.Faces.Count), plain.Sum(b => b.Faces.Count) + 1, plain.Sum(b => b.Faces.Count) + 4);
        RsDocxComposer.Compose(bodies);
    }

    [Fact]
    public void A_triangle_detached_from_a_cylinder_stays_loose_and_the_model_stays_valid()
    {
        var stl = StlReader.Read(KubusXtTests.TestData("sleutelrek.stl"));
        var opt = new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, RecognizeSpheres = true, RecognizeTori = true };
        MeshConverter.ConvertToBrep(stl, opt, out _, out var l0);
        var cyl = l0.Triangles.Where(t => l0.FaceOf(t).Kind == FaceKind.Cylinder).GroupBy(t => (t.Body, t.Face)).MaxBy(g => g.Count())!;
        int tri = cyl.ElementAt(cyl.Count() / 2).Source;
        var bodies = MeshConverter.ConvertToBrep(stl, opt with { KeepFacets = [tri] }, out var st, out var labels);
        Assert.Equal(1, SourceKindCount(labels, tri, FaceKind.Facet));
        Assert.True(st.Cylinders > 0);
        RsDocxComposer.Compose(bodies);
    }
}

public class GroupMergeTests
{
    /// <summary>
    /// bigbox: a tube of r 13 mm recognised as several cylinder pieces with strips and facets between them. A cylinder
    /// group over the strips and facets (as the user made it, testdata/bigbox_buis.groepen.txt) used to make a 604-facet
    /// piece that touched it in one corner fall apart into strips; now it is taken into the group.
    /// </summary>
    [Fact]
    public void A_cylinder_group_takes_in_the_tube_pieces_on_its_surface()
    {
        var stl = StlReader.Read(KubusXtTests.TestData("bigbox.stl"));
        var line = File.ReadAllLines(KubusXtTests.TestData("bigbox_buis.groepen.txt")).Single(l => l.StartsWith("group "));
        var tris = line[(line.IndexOf(':') + 1)..].Split(',').Select(int.Parse).ToList();
        var opt = new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, RecognizeSpheres = true, RecognizeTori = true, RecognizeCones = true, Scale = 0.001, Hints = [new ShapeHint(tris, FaceKind.Cylinder)] };
        var bodies = MeshConverter.ConvertToBrep(stl, opt, out var st, out var labels);
        Assert.Empty(st.HintFailures);
        Assert.True(bodies.Sum(b => b.Faces.Count) < 800, $"{bodies.Sum(b => b.Faces.Count)} faces");
        // every cylinder facet of r 13 in the group's body is in the group, none in another cylinder
        int body = labels.Triangles.Where(t => labels.FaceOf(t).Group >= 0).GroupBy(t => t.Body).MaxBy(g => g.Count())!.Key;
        // (below z = 100 mm: a 39-facet piece at the top, r 13.013, does not touch the group and stays a face of its own)
        var wall = labels.Triangles.Where(t => t.Body == body && labels.Vertices[t.A].Z < 0.1
            && labels.FaceOf(t) is { Kind: FaceKind.Cylinder } f && Math.Abs(f.Radius * 1000 - 13) < 0.1).ToList();
        Assert.True(wall.Count > 2000, $"{wall.Count} tube facets");
        Assert.All(wall.Where(t => Math.Abs(labels.FaceOf(t).Radius * 1000 - 13) < 0.01 && labels.FaceOf(t).Group < 0), t => Assert.Fail($"tube facet {t.Source} not in the group"));
        RsDocx.Core.RsDocxComposer.Compose(bodies);
    }

    /// <summary>A small group of 29 tube facets (the user's, 18447-18475): its own fit is only good near them; it takes
    /// over the surface of the larger tube piece it lies on instead of dissolving it.</summary>
    [Fact]
    public void A_small_cylinder_group_does_not_dissolve_the_tube_piece_it_lies_on()
    {
        var stl = StlReader.Read(KubusXtTests.TestData("bigbox.stl"));
        var opt = new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, RecognizeSpheres = true, RecognizeTori = true, RecognizeCones = true, Scale = 0.001 };
        int Tube(MeshLabels l) => l.Triangles.Count(t => l.FaceOf(t) is { Kind: FaceKind.Cylinder } f && Math.Abs(f.Radius * 1000 - 13) < 0.05);
        MeshConverter.ConvertToBrep(stl, opt, out _, out var l0);
        var bodies = MeshConverter.ConvertToBrep(stl, opt with { Hints = [new ShapeHint(Enumerable.Range(18447, 29).ToList(), FaceKind.Cylinder)] }, out var st, out var labels);
        Assert.Empty(st.HintFailures);
        Assert.True(Tube(labels) >= Tube(l0), $"tube facets {Tube(l0)} -> {Tube(labels)}");
        RsDocx.Core.RsDocxComposer.Compose(bodies);
    }
}
