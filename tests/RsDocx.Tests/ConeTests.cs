using RsDocx.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Package;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;
using Xunit;

namespace RsDocx.Tests;

public class ConeTests
{
    private const double mm = 0.001;
    private static ProfileSegment L(double y, double z) => new((y * mm, z * mm));

    /// <summary>The bowl the user drew in DesignSpark (testdata/schaaltje.rsdocx): outer cone r26→31 over 5 mm, top ring,
    /// inner cone r30→25 down to the floor at 2 mm.</summary>
    internal static BrepBody Schaaltje() => BrepShapes.Revolve((0, 0), [L(26, 0), L(31, 5), L(30, 5), L(25, 2), L(0, 2)]);

    [Fact]
    public void The_spaceclaim_bowl_with_cones_passes_strict_validation()
    {
        var pkg = RsDocxPackage.Open(File.OpenRead(KubusXtTests.TestData("schaaltje.rsdocx")));
        Assert.Empty(XtValidator.Validate(XtReader.Read(pkg.GetPart(RsDocxComposer.GeometryPart))));
    }

    [Fact]
    public void A_revolved_bowl_is_written_with_the_same_cones_as_spaceclaim()
    {
        var pkg = RsDocxComposer.Compose([Schaaltje()]); // strict validation inside
        var ours = XtReader.Read(pkg.GetPart(RsDocxComposer.GeometryPart));
        var reference = XtReader.Read(RsDocxPackage.Open(File.OpenRead(KubusXtTests.TestData("schaaltje.rsdocx"))).GetPart(RsDocxComposer.GeometryPart));
        static string Cones(XtFile f) => string.Join(" | ", f.Nodes.Where(n => n.NodeType == XtNodeTypes.Cone)
            .Select(n => FormattableString.Invariant($"pvec z {n.Vec("pvec")[2] * 1000:0.###} axis z {n.Vec("axis")[2]:0} r {n.Double("radius") * 1000:0.###} sin {n.Double("sin_half_angle"):0.######} cos {n.Double("cos_half_angle"):0.######} sense {(char)n.Byte("sense")}"))
            .Order());
        Assert.Equal(Cones(reference), Cones(ours));
        static string Counts(XtFile f) => string.Join(",", f.Nodes.GroupBy(n => n.NodeType).Where(g => g.Key is XtNodeTypes.Face or XtNodeTypes.Edge or XtNodeTypes.Loop
            or XtNodeTypes.Fin or XtNodeTypes.Circle or XtNodeTypes.Plane or XtNodeTypes.Cone).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"));
        Assert.Equal(Counts(reference), Counts(ours));
        // the face senses of the cones: SpaceClaim reverses the hollow cone's surface, not its face (both faces '+')
        static string Senses(XtFile f)
        {
            var map = f.ByIndex();
            return string.Join(",", f.Nodes.Where(n => n.NodeType == XtNodeTypes.Face && map[n.Ptr("surface")].NodeType == XtNodeTypes.Cone)
                .Select(n => $"{(char)n.Byte("sense")}").Order());
        }
        Assert.Equal(Senses(reference), Senses(ours));
    }

    [Fact]
    public void The_bowl_exported_from_spaceclaim_is_recognised_as_two_cones()
    {
        // testdata/schaaltje.stl is the STL export of schaaltje.rsdocx: 720 facets, 90 strips per cone
        var stl = StlReader.Read(KubusXtTests.TestData("schaaltje.stl"));
        var bodies = MeshConverter.ConvertToBrep(stl, new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, RecognizeSpheres = true, RecognizeTori = true, RecognizeCones = true }, out var st);
        var b = Assert.Single(bodies);
        Assert.Equal(5, b.Faces.Count);
        Assert.Equal(2, st.Cones);
        var cones = b.Faces.Select(f => f.Surface).OfType<BrepCone>().OrderBy(c => c.HalfAngle).ToList();
        Assert.Equal(45.0, cones[0].HalfAngle * 180 / Math.PI, 3);
        Assert.Equal(Math.Atan(5.0 / 3) * 180 / Math.PI, cones[1].HalfAngle * 180 / Math.PI, 3);
        Assert.Equal(26.0, cones[0].Radius * 1000, 3); // at the narrow end, z = 0
        Assert.Equal(25.0, cones[1].Radius * 1000, 3); // at the floor, z = 2
        RsDocxComposer.Compose(bodies); // strict validation inside
        // without cone recognition the walls stay strips
        var plain = MeshConverter.ConvertToBrep(stl, new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true }, out _);
        Assert.True(plain.Sum(x => x.Faces.Count) > 100);
    }
}
