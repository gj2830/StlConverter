using System.Globalization;
using RsDocx.Core;
using RsDocx.Core.Stl;
using Xunit;
using Xunit.Abstractions;

namespace RsDocx.Tests;

public class ThreadFitTests(ITestOutputHelper output)
{
    /// <summary>
    /// The surface of an ISO metric thread (basic profile: 60° flanks, crest flat p/8, root flat p/4, depth 5/8·H with
    /// H = 0.866·p) as a grid around an axis through <paramref name="at"/> along <paramref name="axis"/>, in metres. Wound
    /// outward for a bolt; <paramref name="inner"/> turns it over (the same surface as the wall of a nut's hole).
    /// </summary>
    private static List<(Vec3, Vec3, Vec3)> Thread(double dMm, double pMm, double turns, int hand = 1, bool inner = false,
        Vec3 at = default, Vec3? axis = null)
    {
        double p = pMm / 1000, R = dMm / 2000, H = 0.866025 * p, depth = 5.0 / 8 * H;
        double Radius(double s) // s = position in the pitch, 0..p
        {
            s = ((s % p) + p) % p;
            double crest = p / 8, root = p / 4, flank = (p - crest - root) / 2;
            if (s < crest) return R;
            if (s < crest + flank) return R - depth * (s - crest) / flank;
            if (s < crest + flank + root) return R - depth;
            return R - depth + depth * (s - crest - flank - root) / flank;
        }
        var a = (axis ?? new Vec3(0, 0, 1)).Normalized();
        var x = (Math.Abs(a.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0)); x = (x - a * x.Dot(a)).Normalized();
        var y = a.Cross(x);
        int nTh = 96, nZ = (int)(turns * 32);
        double len = turns * p;
        Vec3 P(int i, int j)
        {
            double th = 2 * Math.PI * i / nTh, z = len * j / nZ;
            double r = Radius(z - hand * p * th / (2 * Math.PI));
            return at + a * z + x * (r * Math.Cos(th)) + y * (r * Math.Sin(th));
        }
        var tris = new List<(Vec3, Vec3, Vec3)>();
        for (int i = 0; i < nTh; i++)
            for (int j = 0; j < nZ; j++)
            {
                Vec3 p00 = P(i, j), p10 = P(i + 1, j), p11 = P(i + 1, j + 1), p01 = P(i, j + 1);
                // outward for a bolt: θ grows counter-clockwise around a, z along a
                if (!inner) { tris.Add((p00, p10, p11)); tris.Add((p00, p11, p01)); }
                else { tris.Add((p00, p11, p10)); tris.Add((p00, p01, p11)); }
            }
        return tris;
    }

    private void Show(ThreadMeasure? m)
    {
        output.WriteLine(m == null ? "null" : m.Describe(CultureInfo.GetCultureInfo("nl-NL")) + $" fit {m.Fit:0.###}, axis {m.Axis}");
    }

    [Fact]
    public void An_M10_bolt_is_measured()
    {
        var m = ThreadFit.Measure(Thread(10, 1.5, 6));
        Show(m);
        Assert.NotNull(m);
        Assert.False(m.Internal);
        Assert.True(m.RightHanded);
        Assert.Equal(1.5e-3, m.Pitch, 1.5e-5);
        Assert.Equal(10e-3, m.MajorDiameter, 5e-5);
        Assert.Equal(10e-3 - 2 * 5.0 / 8 * 0.866025 * 1.5e-3, m.MinorDiameter, 5e-5);
        Assert.InRange(m.ProfileAngleDeg, 58, 62);
        Assert.Equal("M10", m.Standard);
    }

    [Fact]
    public void A_tilted_left_handed_fine_thread_off_the_origin_is_measured()
    {
        var m = ThreadFit.Measure(Thread(12, 1.25, 5, hand: -1, at: new Vec3(1.7, -0.2, 0.03), axis: new Vec3(0.3, -0.5, 0.8)));
        Show(m);
        Assert.NotNull(m);
        Assert.False(m.RightHanded);
        Assert.Equal(1.25e-3, m.Pitch, 1.5e-5);
        Assert.True(Math.Abs(m.Axis.Dot(new Vec3(0.3, -0.5, 0.8).Normalized())) > 0.9999);
        Assert.Equal("M12×1,25", m.Standard);
    }

    [Fact]
    public void A_nut_thread_is_internal()
    {
        var m = ThreadFit.Measure(Thread(20, 2.5, 4, inner: true));
        Show(m);
        Assert.NotNull(m);
        Assert.True(m.Internal);
        Assert.True(m.RightHanded);
        Assert.Equal(2.5e-3, m.Pitch, 2.5e-5);
        Assert.Equal("M20", m.Standard);
    }

    [Fact]
    public void A_plain_cylinder_is_not_a_thread()
    {
        var tris = new List<(Vec3, Vec3, Vec3)>();
        for (int i = 0; i < 64; i++)
            for (int j = 0; j < 10; j++)
            {
                Vec3 P(int a, int b) => new(0.005 * Math.Cos(2 * Math.PI * a / 64), 0.005 * Math.Sin(2 * Math.PI * a / 64), 0.001 * b);
                tris.Add((P(i, j), P(i + 1, j), P(i + 1, j + 1))); tris.Add((P(i, j), P(i + 1, j + 1), P(i, j + 1)));
            }
        Assert.Null(ThreadFit.Measure(tris));
    }
}

public class ThreadFitModelTests(ITestOutputHelper output)
{
    /// <summary>The screw ball: the outside of its neck (axis x 1719.6, y -178.6; r 4.94..5.75, z 20.5..28.8 mm) is a thread.</summary>
    [Fact]
    public void The_thread_on_the_screw_ball_is_measured()
    {
        var stl = StlReader.Read(KubusXtTests.TestData("schroefbol.stl"));
        var ax = new Vec3(1719.602, -178.633, 0);
        var tris = stl.Triangles.Where(t => new[] { t.A, t.B, t.C }.All(p =>
        {
            double r = Math.Sqrt((p.X - ax.X) * (p.X - ax.X) + (p.Y - ax.Y) * (p.Y - ax.Y));
            return r > 4.8 && r < 5.9 && p.Z > 20.5 && p.Z < 28.8;
        }) && Math.Abs((t.B - t.A).Cross(t.C - t.A).Normalized().Z) < 0.95)
            .Select(t => (t.A * 0.001, t.B * 0.001, t.C * 0.001)).ToList();
        var m = ThreadFit.Measure(tris);
        output.WriteLine($"{tris.Count} triangles: " + (m == null ? "null" : m.Describe(CultureInfo.GetCultureInfo("nl-NL")) + $" fit {m.Fit:0.###} axis {m.Axis} at {m.AxisPoint * 1000}"));
        Assert.NotNull(m);
        Assert.False(m.Internal);
        Assert.True(m.RightHanded);
        Assert.Equal(1.5e-3, m.Pitch, 2e-5);
        Assert.InRange(m.ProfileAngleDeg, 57, 63);
        Assert.Equal("M12×1,5", m.Standard); // a printed thread: outer 11.5 mm
        Assert.True(Math.Abs(m.Axis.Z) > 0.9999);
        Assert.InRange(m.MinorDiameter * 1000, 9.7, 10.0);

        // a wider selection, as the user made it: the ends of the neck (chamfers, the bore's rim) taken along; the core
        // diameter used to come out at 8.5 mm
        var wide = stl.Triangles.Where(t => new[] { t.A, t.B, t.C }.All(p =>
        {
            double r = Math.Sqrt((p.X - ax.X) * (p.X - ax.X) + (p.Y - ax.Y) * (p.Y - ax.Y));
            return r > 3.5 && r < 6 && p.Z > 19.9;
        })).Select(t => (t.A * 0.001, t.B * 0.001, t.C * 0.001)).ToList();
        var w = ThreadFit.Measure(wide);
        output.WriteLine($"{wide.Count} triangles: " + (w == null ? "null" : w.Describe(CultureInfo.GetCultureInfo("nl-NL")) + $" fit {w.Fit:0.###} axis {w.Axis} at {w.AxisPoint * 1000}"));
        Assert.NotNull(w);
        Assert.Equal("M12×1,5", w.Standard);
        Assert.InRange(w.MinorDiameter * 1000, 9.7, 10.0);
        Assert.InRange(w.MajorDiameter * 1000, 11.3, 11.7);
    }
}

public class ThreadReplaceTests(ITestOutputHelper output)
{
    /// <summary>A closed bolt in mm: the thread surface of ThreadFitTests (M10×1.5, 6 turns) with flat caps.</summary>
    private static (StlFile stl, List<int> thread) Bolt()
    {
        const double p = 1.5, R = 5, H = 0.866025 * p, depth = 5.0 / 8 * H;
        double Radius(double s)
        {
            s = ((s % p) + p) % p;
            double crest = p / 8, root = p / 4, flank = (p - crest - root) / 2;
            if (s < crest) return R;
            if (s < crest + flank) return R - depth * (s - crest) / flank;
            if (s < crest + flank + root) return R - depth;
            return R - depth + depth * (s - crest - flank - root) / flank;
        }
        int nTh = 96, nZ = 6 * 32; double len = 6 * p;
        Vec3 P(int i, int j)
        {
            double th = 2 * Math.PI * i / nTh, z = len * j / nZ, r = Radius(z - p * th / (2 * Math.PI));
            return new Vec3(r * Math.Cos(th), r * Math.Sin(th), z);
        }
        var tris = new List<StlTriangle>();
        var thread = new List<int>();
        for (int i = 0; i < nTh; i++)
            for (int j = 0; j < nZ; j++)
            {
                Vec3 p00 = P(i, j), p10 = P(i + 1, j), p11 = P(i + 1, j + 1), p01 = P(i, j + 1);
                // the whole thread up to the caps is selected (as the user does on the screw ball: up to its
                // shoulder and top)
                bool sel = true;
                if (sel) thread.Add(tris.Count); tris.Add(new StlTriangle(p00, p10, p11));
                if (sel) thread.Add(tris.Count); tris.Add(new StlTriangle(p00, p11, p01));
            }
        for (int i = 0; i < nTh; i++)
        {
            tris.Add(new StlTriangle(new Vec3(0, 0, 0), P(i + 1, 0), P(i, 0)));
            tris.Add(new StlTriangle(new Vec3(0, 0, len), P(i, nZ), P(i + 1, nZ)));
        }
        return (new StlFile { Header = "", Binary = true, Triangles = tris }, thread);
    }

    [Fact]
    public void A_bolts_thread_becomes_a_smooth_core_and_a_coil()
    {
        var (stl, thread) = Bolt();
        var opt = new MeshOptions { MergeCoplanar = true, Scale = 0.001 };
        Assert.Equal((0, 0), MeshConverter.CountBadEdges(stl, opt));
        var m = ThreadFit.Measure(thread.Select(i => stl.Triangles[i]).Select(t => (t.A * 0.001, t.B * 0.001, t.C * 0.001)).ToList())!;
        output.WriteLine(m.Describe(CultureInfo.GetCultureInfo("nl-NL")) + $" base phase {m.BasePhase:0.###}, crests {m.CrestFrom * 1000:0.##}..{m.CrestTo * 1000:0.##} mm");
        var r = ThreadReplace.Replace(stl, thread, m, 0.001);
        output.WriteLine($"removed {r.Removed}, band {r.Band.Count}, mesh {r.Mesh.Triangles.Count}");
        Assert.Equal((0, 0), MeshConverter.CountBadEdges(r.Mesh, opt));
        Assert.Equal(0, MeshConverter.CountPinchedVertices(r.Mesh, opt));
        var bodies = MeshConverter.ConvertToBrep(r.Mesh, opt with { RecognizeCylinders = true, Hints = [new ShapeHint(r.Band, FaceKind.Cylinder)] }, out var st, out var labels);
        Assert.Empty(st.HintFailures);
        var core = labels.Faces.SelectMany(f => f).Single(f => f.Group == 0);
        output.WriteLine($"core: {core.Kind} r {core.Radius * 1000:0.####} mm");
        Assert.Equal(FaceKind.Cylinder, core.Kind);
        Assert.Equal(m.MinorDiameter / 2, core.Radius, 2e-6);
        // the coil's crests where the bolt's were: the middle of a crest at phase p/16 (the crest flat is 0..p/8 of
        // z − p·θ/2π in Bolt), the coil's crest midway between its two outer corners
        var cr = (r.Coil.Vertices[2] + r.Coil.Vertices[3]) * 0.5 * 1000;
        double th = Math.Atan2(cr.Y, cr.X), sCrest = ((cr.Z - 1.5 * th / (2 * Math.PI)) / 1.5) % 1;
        if (sCrest < 0) sCrest += 1;
        output.WriteLine($"coil crest phase {sCrest:0.####} (bolt: 0.0625)");
        Assert.InRange(sCrest, 0.0625 - 0.02, 0.0625 + 0.02);
        var pkg = RsDocxComposer.Compose([.. bodies, r.Coil]); // both valid
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../output/ronde22"));
        Directory.CreateDirectory(dir);
        pkg.Save(Path.Combine(dir, "bout_M10_kern_en_draad.rsdocx"));
    }

    /// <summary>The screw ball (cleaned): its M12×1.5 thread, selected from the shoulder to the top, replaced by a
    /// smooth core and a coil.</summary>
    [Fact]
    public void The_screw_balls_thread_is_replaced()
    {
        var stl = MeshRepair.Repair(StlReader.Read(KubusXtTests.TestData("schroefbol.stl")), new MeshRepairOptions(), out _);
        var opt = new MeshOptions { MergeCoplanar = true, Scale = 0.001 };
        var ax = new Vec3(1719.602, -178.633, 0);
        var sel = Enumerable.Range(0, stl.Triangles.Count).Where(i =>
        {
            var t = stl.Triangles[i];
            bool inZone = new[] { t.A, t.B, t.C }.All(p =>
            {
                double r = Math.Sqrt((p.X - ax.X) * (p.X - ax.X) + (p.Y - ax.Y) * (p.Y - ax.Y));
                return r > 4.8 && r < 5.9 && p.Z > 19.99;
            });
            return inZone && Math.Abs((t.B - t.A).Cross(t.C - t.A).Normalized().Z) < 0.95;
        }).ToList();
        var m = ThreadFit.Measure(sel.Select(i => stl.Triangles[i]).Select(t => (t.A * 0.001, t.B * 0.001, t.C * 0.001)).ToList())!;
        output.WriteLine($"{sel.Count} selected: " + m.Describe(CultureInfo.GetCultureInfo("nl-NL")));
        var r = ThreadReplace.Replace(stl, sel, m, 0.001);
        output.WriteLine($"removed {r.Removed}, band {r.Band.Count}, mesh {r.Mesh.Triangles.Count}");
        Assert.Equal((0, 0), MeshConverter.CountBadEdges(r.Mesh, opt));
        Assert.Equal(0, MeshConverter.CountPinchedVertices(r.Mesh, opt));
        var bodies = MeshConverter.ConvertToBrep(r.Mesh, opt with { OnlyHints = true, Hints = [new ShapeHint(r.Band, FaceKind.Cylinder)] }, out var st, out var labels);
        Assert.Empty(st.HintFailures);
        var pkg = RsDocxComposer.Compose([.. bodies, r.Coil]);
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../output/ronde22"));
        Directory.CreateDirectory(dir);
        pkg.Save(Path.Combine(dir, "knop_kern_en_draad.rsdocx"));
    }
}

public class ThreadReplaceUserSelectionTests(ITestOutputHelper output)
{
    /// <summary>The user's own selection on the raw (uncleaned) screw ball that gave "3 randen": with pieces on the far side of the model.</summary>
    [Fact]
    public void The_users_second_selection_on_the_raw_knob_is_replaced()
    {
        var stl = StlReader.Read(KubusXtTests.TestData("schroefbol.stl"));
        var sel = File.ReadAllText(KubusXtTests.TestData("schroefbol.selectie.txt")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
        // (it holds pieces elsewhere too, the sphere's bottom and the bore, selected through the model)
        var m = ThreadFit.Measure(sel.Select(i => stl.Triangles[i]).Select(t => (t.A * 0.001, t.B * 0.001, t.C * 0.001)).ToList());
        output.WriteLine($"{stl.Triangles.Count} triangles, {sel.Count} selected: " + m?.Describe(CultureInfo.GetCultureInfo("nl-NL")) + $" axis {m?.Axis}");
        Assert.NotNull(m);
        // (the axis straight: the run-out at the shoulder tilted it 1°)
        Assert.True(Math.Abs(m.Axis.Normalized().Z) > Math.Cos(0.2 * Math.PI / 180), $"axis {m.Axis}");
        var r = ThreadReplace.Replace(stl, sel, m, 0.001);
        var opt = new MeshOptions { MergeCoplanar = true, Scale = 0.001 };
        Assert.Equal("M12×1,5", m.Standard);
        // as the app measured it then (outer 11.5, core 9.88 mm)
        var r2 = ThreadReplace.Replace(stl, sel, m with { MajorDiameter = 0.0115, MinorDiameter = 0.00988 }, 0.001);
        Assert.Equal((0, 0), MeshConverter.CountBadEdges(r2.Mesh, opt));
        Assert.Equal((0, 0), MeshConverter.CountBadEdges(r.Mesh, opt));
    }
}
