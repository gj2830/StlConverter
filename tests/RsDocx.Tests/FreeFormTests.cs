using RsDocx.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Package;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace RsDocx.Tests;

/// <summary>
/// Free-form faces as DesignSpark writes them: testdata/kubus_getrokken.rsdocx holds, beside the cubes, a spline turned
/// round an axis (body 4:9412): one NURBS face, no edges, closed at both ends in a point on the axis (a loop that is one
/// point). Read, written back the same way, found again.
/// </summary>
public class FreeFormTests
{
    internal static XtFile Reference() => XtReader.Read(RsDocxPackage.Open(KubusXtTests.TestData("kubus_getrokken.rsdocx")).GetPart(RsDocxComposer.GeometryPart));

    private static readonly int[] Surface = [XtNodeTypes.BSurface, XtNodeTypes.SurfaceData, XtNodeTypes.NurbsSurf, XtNodeTypes.KnotMult,
        XtNodeTypes.KnotSet, XtNodeTypes.BsplineVertices];

    /// <summary>DesignSpark's free-form body as a <see cref="BrepBody"/>: its surface, its two points, the face reversed.</summary>
    internal static (BrepBody Body, XtBodyInfo Info) Revolved(XtFile xt)
    {
        var map = xt.ByIndex();
        var info = XtTopology.Bodies(xt).Single(b => b.Name == "4:9412");
        var face = map[info.Faces.Single().Index];
        var ns = XtGeometry.ReadBSurface(map[face.Ptr("surface")], map);
        var body = new BrepBody
        {
            Vertices = info.Vertices.Select(v => new Vec3(v.X, v.Y, v.Z)).ToList(),
            Edges = [],
            Faces = [new BrepFace(ns, face.Byte("sense") == '-', []) { Points = [0, 1] }],
        };
        return (body, info);
    }

    [Fact]
    public void DesignSparks_revolved_spline_is_read_with_how_it_is_stored()
    {
        var (body, _) = Revolved(Reference());
        var ns = (BrepNurbsSurface)body.Faces[0].Surface;
        Assert.Equal((3, 3, 7, 7), (ns.UDegree, ns.VDegree, ns.NU, ns.NV));
        var f = Assert.IsType<NurbsFormat>(ns.Format);
        Assert.True(f.VPeriodic && f.VClosed && !f.UPeriodic && !f.UClosed && f.Rational);
        Assert.Equal((6, 5, 8), ((int)f.UKnotType, (int)f.VKnotType, (int)f.SurfaceForm));
        Assert.Equal("DDPPDDPP", f.Ends);
        // both ends of the profile on the axis: the surface's first and last row of points are one point each
        for (int k = 0; k < ns.NV; k++)
        {
            Assert.True((ns.Points[k] - body.Vertices[0]).Length < 1e-9 || (ns.Points[k] - body.Vertices[1]).Length < 1e-9);
            Assert.True((ns.Points[(ns.NU - 1) * ns.NV + k] - ns.Points[(ns.NU - 1) * ns.NV]).Length < 1e-9);
        }
    }

    [Fact]
    public void A_revolved_spline_is_written_back_as_DesignSpark_wrote_it()
    {
        var reference = Reference();
        var (body, info) = Revolved(reference);
        Assert.Null(XtBrepBuilder.TopologyError(body));
        var xt = new XtBrepBuilder().Build([(body, SheetTests.Names(body))]);
        Assert.Empty(XtValidator.Validate(xt));
        var bytes = XtWriter.Write(xt);
        var again = XtReader.Read(bytes, out int used);
        Assert.Equal(bytes.Length, used);
        Assert.Empty(XtValidator.Validate(again));
        var ours = Assert.Single(XtTopology.Bodies(again));
        Assert.Equal(SheetTests.Mix(reference, info.Index, Surface), SheetTests.Mix(again, ours.Index, Surface));
        // the surface the same, stored the same
        var map = again.ByIndex();
        var face = map[ours.Faces.Single().Index];
        Assert.Equal(info.Faces.Single().Sense, face.Byte("sense"));
        var back = XtGeometry.ReadBSurface(map[face.Ptr("surface")], map);
        var ns = (BrepNurbsSurface)body.Faces[0].Surface;
        Assert.Equal(ns.Format, back.Format);
        Assert.Equal(ns.UKnots, back.UKnots); Assert.Equal(ns.VMults, back.VMults); Assert.Equal(ns.Weights, back.Weights);
        Assert.All(ns.Points.Zip(back.Points), q => Assert.True((q.First - q.Second).Length < 1e-15));
        // two loops, each one point with a fin to itself
        var loops = XtTopology.Chain(map, face.Ptr("loop"), "next").ToList();
        Assert.Equal(2, loops.Count);
        Assert.All(loops, l => { var fin = map[l.Ptr("fin")]; Assert.Equal((0, 0, fin.Index), (fin.Ptr("edge"), fin.Ptr("other"), fin.Ptr("forward"))); });
    }

    [Fact]
    public void The_parameters_of_a_point_on_a_free_form_surface_are_found_again()
    {
        var ns = (BrepNurbsSurface)Revolved(Reference()).Body.Faces[0].Surface;
        foreach (var (u, v) in new[] { (0.2, 0.1), (0.5, 0.5), (0.7, 0.9), (0.33, 0.27) })
        {
            var p = ns.At(u, v);
            var (pu, pv) = ns.Parameters(p);
            Assert.True((ns.At(pu, pv) - p).Length < 1e-9, $"({u}, {v}) → ({pu}, {pv})");
            Assert.Equal(u, pu, 6); Assert.Equal(v, pv, 6);
        }
    }
}
