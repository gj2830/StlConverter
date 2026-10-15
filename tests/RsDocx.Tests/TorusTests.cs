using RsDocx.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace RsDocx.Tests;

public class TorusTests
{
    private const double mm = 0.001;
    private static ProfileSegment L(double y, double z) => new((y * mm, z * mm));
    private static ProfileSegment Arc(double y, double z, double cy, double cz, bool ccw) => new((y * mm, z * mm), (cy * mm, cz * mm), ccw);

    /// <summary>Rotates about (1,1,0)/sqrt2 by 0.7 rad and shifts, so the axis is not a coordinate axis.</summary>
    private static Vec3 Move(Vec3 p)
    {
        var k = new Vec3(1, 1, 0).Normalized(); double t = 0.7;
        var r = p * Math.Cos(t) + k.Cross(p) * Math.Sin(t) + k * (k.Dot(p) * (1 - Math.Cos(t)));
        return r + new Vec3(0.012, -0.034, 0.05);
    }

    [Fact]
    public void Fits_a_torus_to_a_patch_of_facets()
    {
        // donut R20 r8 (m), only the facets of a patch: 60 degrees around the axis, half of the tube
        var tris = StlWriter.Revolve((28 * mm, 0), [Arc(28, 0, 20, 0, true)], 72, 7.5)
            .Select(t => new StlTriangle(Move(t.A), Move(t.B), Move(t.C))).ToList();
        var axis = Move(new Vec3(0, 0, 1)) - Move(new Vec3(0, 0, 0));
        var centre = Move(new Vec3(0, 0, 0));
        var patch = tris.Where(t =>
        {
            var c = (t.A + t.B + t.C) * (1.0 / 3) - centre;
            double h = c.Dot(axis);
            var rad = c - axis * h;
            var x = Move(new Vec3(1, 0, 0)) - centre;
            double ang = Math.Atan2(axis.Cross(x).Dot(rad), x.Dot(rad));
            return ang > 0 && ang < Math.PI / 3 && rad.Length > 20 * mm;
        }).ToList();
        Assert.True(patch.Count > 20);
        var pts = patch.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().ToList();
        var normals = patch.Select(t => ((t.A + t.B + t.C) * (1.0 / 3), (t.B - t.A).Cross(t.C - t.A).Normalized())).ToList();
        Assert.True(TorusFit.Fit(pts, normals, out var torus, out double res));
        Assert.True(res < 1e-12, $"residual {res}");
        Assert.Equal(0.020, torus.R, 9);
        Assert.Equal(0.008, torus.r, 9);
        Assert.True(Math.Abs(Math.Abs(torus.Axis.Dot(axis)) - 1) < 1e-9);
        Assert.True((torus.Centre - centre).Length < 1e-9);
    }

    [Fact]
    public void Eigen_decomposition_of_symmetric_matrix()
    {
        var m = new double[,] { { 4, 1, 0 }, { 1, 3, 1 }, { 0, 1, 2 } };
        var (vals, vecs) = TorusFit.EigenSym(m);
        for (int k = 0; k < 3; k++)
            for (int i = 0; i < 3; i++)
            {
                double mv = 0;
                for (int j = 0; j < 3; j++) mv += m[i, j] * vecs[j, k];
                Assert.Equal(vals[k] * vecs[i, k], mv, 12);
            }
    }

    public static IEnumerable<object[]> RevolveShapes() =>
    [
        ["staaf", (0.0, 0.0), new List<ProfileSegment> { L(10, 0), L(10, 37), Arc(7, 40, 7, 37, true), L(0, 40) }, 4],
        ["donut", (28 * mm, 0.0), new List<ProfileSegment> { Arc(28, 0, 20, 0, true) }, 1],
        ["holle afronding", (0.0, 0.0), new List<ProfileSegment> { L(30, 0), L(30, 5), L(12, 5), Arc(8, 9, 12, 9, false), L(8, 30), L(0, 30) }, 6],
        ["bol en groef", (0.0, 0.0), new List<ProfileSegment> { L(12, 0), L(12, 10), Arc(12, 16, 12, 13, false), L(12, 25), Arc(0, 37, 0, 25, true) }, 5],
    ];

    [Theory]
    [MemberData(nameof(RevolveShapes))]
    public void Tessellated_revolved_bodies_are_recognised_exactly(string name, (double, double) start, List<ProfileSegment> segs, int faces)
    {
        // same as rsgen ronde7: 72 steps around, arcs in 7.5 degree steps, written in mm
        var tris = StlWriter.Revolve(start, segs, 72, 7.5).Select(t => new StlTriangle(Move(t.A) * 1000, Move(t.B) * 1000, Move(t.C) * 1000)).ToList();
        var stl = StlReader.Read(StlWriter.WriteBinary(tris));
        var bodies = MeshConverter.ConvertToBrep(stl, new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, RecognizeSpheres = true, RecognizeTori = true }, out var stats);
        var b = Assert.Single(bodies);
        Assert.Equal(0, stats.RecognitionFallbacks);
        Assert.True(faces == b.Faces.Count, $"{name}: {b.Faces.Count} faces");
        Assert.Equal(1, stats.Tori);
        RsDocxComposer.Compose(bodies);
    }

    [Theory]
    [MemberData(nameof(RevolveShapes))]
    public void Revolved_bodies_with_tori_pass_strict_validation(string name, (double, double) start, List<ProfileSegment> segs, int faces)
    {
        var body = BrepShapes.Revolve(start, segs);
        Assert.Equal(faces, body.Faces.Count);
        RsDocxComposer.Compose([body]); // throws when the strict validator finds a problem
        Assert.NotNull(name);
    }
}

public class LabelTests
{
    [Fact]
    public void Every_input_triangle_is_labelled_with_its_face()
    {
        var stl = StlReader.Read(KubusXtTests.TestData("sleutelrek.stl"));
        var bodies = MeshConverter.ConvertToBrep(stl, new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true }, out var stats, out var labels);
        Assert.Equal(stl.Triangles.Count - stats.DegenerateTriangles, labels.Triangles.Count);
        Assert.Equal(labels.Triangles.Count, labels.Triangles.Select(t => t.Source).Distinct().Count());
        for (int b = 0; b < bodies.Count; b++)
        {
            Assert.Equal(bodies[b].Faces.Count, labels.Faces[b].Count);
            // every face has triangles, and the face kinds match the B-rep surfaces
            var used = labels.Triangles.Where(t => t.Body == b).Select(t => t.Face).ToHashSet();
            Assert.Equal(bodies[b].Faces.Count, used.Count);
            for (int f = 0; f < bodies[b].Faces.Count; f++)
                Assert.Equal(bodies[b].Faces[f].Surface is RsDocx.Core.Brep.BrepCylinder, labels.Faces[b][f].Kind == FaceKind.Cylinder);
        }
        Assert.Equal(30, labels.Faces.SelectMany(f => f).Count(f => f.Kind == FaceKind.Cylinder));
    }
}

public class HintTests
{
    private const double mm = 0.001;

    /// <summary>The rounded rod of rsgen ronde7, tilted: with torus recognition switched off, the user marks the round as
    /// one torus (and in a second case lets the converter choose).</summary>
    [Theory]
    [InlineData(FaceKind.Torus)]
    [InlineData(FaceKind.Facet)] // automatic
    public void A_group_becomes_one_face_of_the_requested_kind(FaceKind kind)
    {
        var segs = new List<ProfileSegment> { new((10 * mm, 0)), new((10 * mm, 37 * mm)), new((7 * mm, 40 * mm), (7 * mm, 37 * mm), true), new((0, 40 * mm)) };
        var raw = StlWriter.Revolve((0, 0), segs, 72, 7.5);
        // the round: triangles with all corners above z = 37 mm and away from the axis (not the top disc)
        var group = raw.Select((t, i) => (t, i)).Where(x => new[] { x.t.A, x.t.B, x.t.C }.All(p => p.Z > 0.037 - 1e-9)
            && new[] { x.t.A, x.t.B, x.t.C }.Any(p => p.Z < 0.04 - 1e-9)).Select(x => x.i).ToList();
        var stl = StlReader.Read(StlWriter.WriteBinary(raw.Select(t => new StlTriangle(t.A * 1000, t.B * 1000, t.C * 1000)).ToList()));
        var opt = new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, Hints = [new ShapeHint(group, kind)] };
        var bodies = MeshConverter.ConvertToBrep(stl, opt, out var stats, out var labels);
        var b = Assert.Single(bodies);
        Assert.Equal(0, stats.RecognitionFallbacks);
        Assert.Equal(4, b.Faces.Count);
        var face = Assert.Single(labels.Faces[0], f => f.Group == 0);
        Assert.Equal(FaceKind.Torus, face.Kind);
        Assert.Equal(0.007, face.Radius, 6);
        Assert.Equal(0.003, face.MinorRadius, 6);
        Assert.True(face.Deviation < 1e-8);
        RsDocxComposer.Compose(bodies);
    }

    /// <summary>"Only my groups": the marked round becomes one torus, every other triangle stays a facet.</summary>
    [Fact]
    public void Only_the_groups_are_converted_the_rest_stays_mesh()
    {
        var segs = new List<ProfileSegment> { new((10 * mm, 0)), new((10 * mm, 37 * mm)), new((7 * mm, 40 * mm), (7 * mm, 37 * mm), true), new((0, 40 * mm)) };
        var raw = StlWriter.Revolve((0, 0), segs, 72, 7.5);
        var group = raw.Select((t, i) => (t, i)).Where(x => new[] { x.t.A, x.t.B, x.t.C }.All(p => p.Z > 0.037 - 1e-9)
            && new[] { x.t.A, x.t.B, x.t.C }.Any(p => p.Z < 0.04 - 1e-9)).Select(x => x.i).ToList();
        var stl = StlReader.Read(StlWriter.WriteBinary(raw.Select(t => new StlTriangle(t.A * 1000, t.B * 1000, t.C * 1000)).ToList()));
        var opt = new MeshOptions { OnlyHints = true, Hints = [new ShapeHint(group, FaceKind.Torus)] };
        var bodies = MeshConverter.ConvertToBrep(stl, opt, out var stats, out var labels);
        var b = Assert.Single(bodies);
        Assert.Empty(stats.HintFailures);
        Assert.Equal(raw.Count - group.Count + 1, b.Faces.Count);
        var face = Assert.Single(labels.Faces[0], f => f.Group == 0);
        Assert.Equal(FaceKind.Torus, face.Kind);
        Assert.Equal(0.003, face.MinorRadius, 6);
        Assert.Equal(raw.Count - group.Count, labels.Faces[0].Count(f => f.Kind == FaceKind.Facet));
        RsDocxComposer.Compose(bodies);
    }

    /// <summary>
    /// Redo only around a group: first everything without tori (the round becomes facets), then the round as a group with
    /// every other face kept (soft, exact surface) and only the faces next to it free: the rod's cylinder and caps stay.
    /// </summary>
    [Fact]
    public void A_group_after_recognition_only_changes_its_surroundings()
    {
        var segs = new List<ProfileSegment> { new((10 * mm, 0)), new((10 * mm, 37 * mm)), new((7 * mm, 40 * mm), (7 * mm, 37 * mm), true), new((0, 40 * mm)) };
        var raw = StlWriter.Revolve((0, 0), segs, 72, 7.5);
        var group = raw.Select((t, i) => (t, i)).Where(x => new[] { x.t.A, x.t.B, x.t.C }.All(p => p.Z > 0.037 - 1e-9)
            && new[] { x.t.A, x.t.B, x.t.C }.Any(p => p.Z < 0.04 - 1e-9)).Select(x => x.i).ToList();
        var stl = StlReader.Read(StlWriter.WriteBinary(raw.Select(t => new StlTriangle(t.A * 1000, t.B * 1000, t.C * 1000)).ToList()));
        var first = new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true };
        MeshConverter.ConvertToBrep(stl, first, out _, out var labels0);
        var inGroup = new HashSet<int>(group);
        var faceOf = labels0.Triangles.ToDictionary(t => t.Source, t => (t.Body, t.Face));
        var touched = new HashSet<(int, int)>(group.Select(i => faceOf[i]));
        var free = new HashSet<int>(group);
        var hints = new List<ShapeHint>();
        foreach (var face in labels0.Triangles.GroupBy(t => (t.Body, t.Face)))
        {
            var info = labels0.Faces[face.Key.Body][face.Key.Face];
            var tris = face.Select(t => t.Source).Where(i => !inGroup.Contains(i)).ToList();
            if (touched.Contains(face.Key) || info.Kind == FaceKind.Facet) free.UnionWith(tris);
            else if (tris.Count > 0) hints.Add(new ShapeHint(tris, info.Kind, Soft: true, Surface: info));
        }
        hints.Add(new ShapeHint(group, FaceKind.Torus));
        var opt = first with { Hints = hints, FreeTriangles = free };
        var bodies = MeshConverter.ConvertToBrep(stl, opt, out var stats, out var labels);
        Assert.Empty(stats.HintFailures);
        var b = Assert.Single(bodies);
        Assert.Equal(4, b.Faces.Count);
        Assert.Single(labels.Faces[0], f => f.Group == hints.Count - 1 && f.Kind == FaceKind.Torus);
        RsDocxComposer.Compose(bodies);
    }
}

public class HintSafetyTests
{
    private const double mm = 0.001;

    [Fact]
    public void A_group_that_does_not_fit_is_skipped_and_the_rest_is_kept()
    {
        var segs = new List<ProfileSegment> { new((10 * mm, 0)), new((10 * mm, 37 * mm)), new((7 * mm, 40 * mm), (7 * mm, 37 * mm), true), new((0, 40 * mm)) };
        var raw = StlWriter.Revolve((0, 0), segs, 72, 7.5);
        // nonsense group: the whole upper half of the rod as one plane
        var group = raw.Select((t, i) => (t, i)).Where(x => x.t.A.Z > 0.02 && x.t.B.Z > 0.02 && x.t.C.Z > 0.02).Select(x => x.i).ToList();
        var stl = StlReader.Read(StlWriter.WriteBinary(raw.Select(t => new StlTriangle(t.A * 1000, t.B * 1000, t.C * 1000)).ToList()));
        var opt = new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, RecognizeTori = true, Hints = [new ShapeHint(group, FaceKind.Plane)] };
        var bodies = MeshConverter.ConvertToBrep(stl, opt, out var stats, out _);
        var f = Assert.Single(stats.HintFailures);
        Assert.Equal(0, f.group);
        Assert.Contains("deviates", f.reason);
        Assert.Equal(0, stats.RecognitionFallbacks);
        Assert.Equal(4, Assert.Single(bodies).Faces.Count); // recognised as without the group
    }
}

public class BSplineTests
{
    private const double mm = 0.001;

    [Fact]
    public void Interpolation_passes_through_its_points_and_stays_close_to_a_circle()
    {
        var pts = Enumerable.Range(0, 161).Select(i => new Vec3(0.01 * Math.Cos(2 * Math.PI * i / 160), 0.01 * Math.Sin(2 * Math.PI * i / 160), 0)).ToList();
        var bs = BSpline.Interpolate(pts, closed: true);
        Assert.True(bs.Closed);
        Assert.Equal(4, bs.Mults[0]);
        foreach (var p in pts) Assert.True((bs.At(BSpline.ClosestParam(bs, p)) - p).Length < 1e-12);
        for (int i = 0; i < 1000; i++)
        {
            var q = bs.At(bs.Start + (bs.End - bs.Start) * i / 1000);
            Assert.True(Math.Abs(Math.Sqrt(q.X * q.X + q.Y * q.Y) - 0.01) < 1e-9, $"off circle at {i}"); // worst near the clamped ends (0.67 nm)
        }
    }

    [Fact]
    public void Ring_edges_as_bsplines_pass_strict_validation_and_round_trip()
    {
        var segs = new List<ProfileSegment> { new((10 * mm, 0)), new((10 * mm, 37 * mm)), new((7 * mm, 40 * mm), (7 * mm, 37 * mm), true), new((0, 40 * mm)) };
        var body = BrepShapes.Revolve((0, 0), segs);
        var edges = body.Edges.Select(e =>
        {
            var c = (BrepCircle)e.Curve;
            var y = c.Normal.Cross(c.XAxis);
            var pts = Enumerable.Range(0, 201).Select(i => c.Centre + c.XAxis * (c.Radius * Math.Cos(2 * Math.PI * i / 200)) + y * (c.Radius * Math.Sin(2 * Math.PI * i / 200))).ToList();
            return e with { Curve = BSpline.Interpolate(pts, closed: true) };
        }).ToList();
        var bsBody = new BrepBody { Vertices = body.Vertices, Edges = edges, Faces = body.Faces };
        var pkg = RsDocxComposer.Compose([bsBody]); // strict validation inside
        var xt = RsDocx.Core.Xt.XtReader.Read(pkg.GetPart(RsDocxComposer.GeometryPart));
        Assert.Equal(3, xt.Nodes.Count(n => n.NodeType == RsDocx.Core.Xt.XtNodeTypes.BCurve));
        Assert.Equal(pkg.GetPart(RsDocxComposer.GeometryPart), RsDocx.Core.Xt.XtWriter.Write(xt));
    }
}

public class KeepPlanesTests
{
    private const double mm = 0.001;

    [Fact]
    public void Kept_planes_stay_and_a_new_group_is_added()
    {
        var segs = new List<ProfileSegment> { new((10 * mm, 0)), new((10 * mm, 37 * mm)), new((7 * mm, 40 * mm), (7 * mm, 37 * mm), true), new((0, 40 * mm)) };
        var raw = StlWriter.Revolve((0, 0), segs, 72, 7.5);
        var stl = StlReader.Read(StlWriter.WriteBinary(raw.Select(t => new StlTriangle(t.A * 1000, t.B * 1000, t.C * 1000)).ToList()));
        // first pass without tori: the round stays facets
        var first = new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true };
        MeshConverter.ConvertToBrep(stl, first, out _, out var labels);
        var group = raw.Select((t, i) => (t, i)).Where(x => new[] { x.t.A, x.t.B, x.t.C }.All(p => p.Z > 0.037 - 1e-9)
            && new[] { x.t.A, x.t.B, x.t.C }.Any(p => p.Z < 0.04 - 1e-9)).Select(x => x.i).ToHashSet();
        var soft = labels.Triangles.GroupBy(t => (t.Body, t.Face))
            .Where(g => labels.Faces[g.Key.Body][g.Key.Face].Kind == FaceKind.Plane)
            .Select(g => new ShapeHint(g.Select(t => t.Source).Where(i => !group.Contains(i)).ToList(), FaceKind.Plane, Soft: true, Surface: labels.Faces[g.Key.Body][g.Key.Face]))
            .Where(h => h.SourceTriangles.Count >= 2).ToList();
        Assert.Equal(2, soft.Count); // bottom and top disc
        var again = first with { Hints = [.. soft, new ShapeHint(group.ToList(), FaceKind.Torus)] };
        var bodies = MeshConverter.ConvertToBrep(stl, again, out var stats, out var l2);
        Assert.Empty(stats.HintFailures);
        Assert.Equal(4, Assert.Single(bodies).Faces.Count);
        Assert.Single(l2.Faces[0], f => f.Kind == FaceKind.Torus && f.Group == 2);
    }
}
