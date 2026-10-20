using System.Text.RegularExpressions;
using RsDocx.Core.Brep;
using RsDocx.Core.Revit;
using RsDocx.Core.Step;
using RsDocx.Core.Xt;

namespace RsDocx.Tests;

/// <summary>
/// Decisions the user took that must not change unnoticed (guarded: an assistant may not edit this file without asking).
/// Each test names the decision; when one fails, the code changed a decision: ask the user, do not adjust the test.
/// </summary>
public class IntentTests
{
    private const double Mm = 0.001;
    private static BrepBody Block(double x0) => BrepShapes.FromPolyhedron(Polyhedron.Box(x0 * Mm, 0, 0, (x0 + 40) * Mm, 30 * Mm, 20 * Mm));

    [Fact]
    public void STEP_is_written_readable_in_millimetres_one_entity_per_line()
    {
        // (user, 18 Oct 2026: STEP the main format, "and the file readable")
        string text = StepWriter.Write(new StepModel([new StepBody(Block(0), "Blok")], []), "proef.step");
        Assert.Contains("AUTOMOTIVE_DESIGN", text);
        Assert.Contains("SI_UNIT(.MILLI.,.METRE.)", text);
        Assert.Matches(new Regex(@"CARTESIAN_POINT\('[^']*',\(40\.?0*,"), text);
        var data = text[(text.IndexOf("DATA;", StringComparison.Ordinal) + 5)..text.LastIndexOf("ENDSEC;", StringComparison.Ordinal)];
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Assert.Matches(new Regex(@"^#\d+=.*;$"), line);
    }

    [Fact]
    public void Several_bodies_are_an_assembly_with_a_named_product_per_body()
    {
        // (18 Oct 2026: as FreeCAD writes it, so that FreeCAD shows our names)
        string text = StepWriter.Write(new StepModel([new StepBody(Block(0), "Links"), new StepBody(Block(60), "Rechts")], []), "twee.step");
        Assert.Contains("PRODUCT('Links'", text);
        Assert.Contains("PRODUCT('Rechts'", text);
        Assert.Contains("NEXT_ASSEMBLY_USAGE_OCCURRENCE", text);
        Assert.Equal(["Links", "Rechts"], StepReader.Parse(text).Bodies.Select(b => b.Name));
    }

    [Fact]
    public void What_STEP_cannot_read_is_said_by_name_never_skipped()
    {
        string text = StepWriter.Write(new StepModel([new StepBody(Block(0), "Blok")], []), "proef.step")
            .Replace("PLANE(", "OFFSET_SURFACE(");
        var e = Assert.ThrowsAny<Exception>(() => StepReader.Parse(text));
        Assert.Contains("OFFSET_SURFACE", e.Message);
    }

    [Fact]
    public void Revit_light_sources_and_voids_are_left_out_and_counted()
    {
        // (19 Oct 2026: a lamp's cone of light and the voids of in-place families are not building parts; counted in
        // the report so nothing goes silently). Needs testdata/test.rvt, which is never committed: skipped without it.
        string path;
        try { path = KubusXtTests.TestData("test.rvt"); }
        catch (FileNotFoundException) { return; }
        var report = new RevitGeometryReport();
        var elements = RevitGeometry.Read(RevitFile.Open(path), report);
        Assert.True(report.LightSources > 0);
        Assert.True(report.Voids > 0);
        Assert.DoesNotContain(elements, e => e.Class == "ImposterLight");
    }
}
