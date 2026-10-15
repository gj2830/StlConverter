using RsDocx.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;
using RsDocx.Core.Package;
using Xunit;
using Xunit.Abstractions;

namespace RsDocx.Tests;

public class ThreadShapeTests(ITestOutputHelper output)
{
    /// <summary>SpaceClaim's own thread (testdata/helix.rsdocx: B-surfaces with HELIX_SU_FORM, rational helices with
    /// HELIX_CU_FORM) passes our checks: the readers and the B-surface distance agree with it.</summary>
    [Fact]
    public void SpaceClaims_helix_passes_the_validator()
    {
        var pkg = RsDocxPackage.Open(KubusXtTests.TestData("helix.rsdocx"));
        var xt = XtReader.Read(pkg.GetPart(RsDocxComposer.GeometryPart));
        var errors = XtValidator.Validate(xt, tol: 2e-8);
        foreach (var e in errors.Take(20)) output.WriteLine(e);
        Assert.Empty(errors);
    }

    [Fact]
    public void A_helix_is_a_circle_seen_along_its_axis_and_climbs_one_pitch_per_turn()
    {
        var h = ThreadShapes.Helix(new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0), 0.006, 0.001, 0.0015, true, 3.5);
        for (int i = 0; i <= 400; i++)
        {
            var p = h.At(h.Start + (h.End - h.Start) * i / 400);
            Assert.Equal(0.006, Math.Sqrt(p.X * p.X + p.Y * p.Y), 1e-12);
            // the climb follows the angle within the 1e-5 m the helix form allows
            double phi = Math.Atan2(p.Y, p.X); if (phi < 0) phi += 2 * Math.PI;
            double turnsDone = 3.5 * i / 400, expectedPhi = 2 * Math.PI * (turnsDone - Math.Floor(turnsDone));
            double z = 0.001 + 0.0015 * (Math.Floor(turnsDone) + phi / (2 * Math.PI));
            if (Math.Abs(phi - expectedPhi) < 0.5) Assert.Equal(z, p.Z, 1e-5);
        }
        Assert.Equal(0.001 + 3.5 * 0.0015, h.At(h.End).Z, 1e-12);
    }

    /// <summary>The same coil as SpaceClaim's (r 8.15 .. 8.9 mm, pitch 1.5, 14 turns), written by us: valid, and saved
    /// for a test in DesignSpark.</summary>
    [Fact]
    public void A_thread_coil_like_SpaceClaims_is_valid()
    {
        double p = 0.0015;
        var body = ThreadShapes.Coil(new Vec3(0.0025, -0.002, 0.012), new Vec3(0, 0, 1), new Vec3(1, 0, 0), 0.00815, 0.0089,
            0.0011547, 0.0002887, p, true, 14);
        var pkg = RsDocxComposer.Compose([body]); // strict validation inside
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../output/ronde22"));
        Directory.CreateDirectory(dir);
        pkg.Save(Path.Combine(dir, "helix_eigen.rsdocx"));
    }

    [Fact]
    public void An_M12x1_5_coil_left_and_right_is_valid()
    {
        var (depth, wb, wc) = ThreadShapes.MetricProfile(0.0015);
        double rMinor = 0.006 - depth;
        foreach (bool right in new[] { true, false })
        {
            var body = ThreadShapes.Coil(new Vec3(0, 0, 0), new Vec3(0.3, -0.2, 0.9), new Vec3(1, 0, 0), rMinor, 0.006, wb, wc, 0.0015, right, 5.5);
            var pkg = RsDocxComposer.Compose([body]);
            string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../output/ronde22"));
            Directory.CreateDirectory(dir);
            pkg.Save(Path.Combine(dir, right ? "M12x1_5_rechts.rsdocx" : "M12x1_5_links.rsdocx"));
        }
    }
}
