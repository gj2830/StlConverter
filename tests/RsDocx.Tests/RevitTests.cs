using RsDocx.Core.Revit;
using Xunit.Abstractions;

namespace RsDocx.Tests;

/// <summary>Revit files read without Revit: the container, the file's own description, its preview and its schema.
/// testdata/test.rvt is Autodesk's "Snowdon Towers Sample Architectural" saved by Revit 2024; it is not in the repository
/// (Autodesk's), so the tests that need it pass without it.</summary>
public class RevitTests(ITestOutputHelper output)
{
    private static string? Sample()
    {
        string path = KubusXtTests.TestData("test.rvt");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void Something_else_is_not_taken_for_a_compound_file()
    {
        Assert.Throws<CompoundFileException>(() => new CompoundFile(new byte[1024]));
        Assert.Throws<CompoundFileException>(() => RevitFile.Open(File.ReadAllBytes(KubusXtTests.TestData("kubus.rsdocx"))));
    }

    [Fact]
    public void The_container_and_the_files_own_description_are_read()
    {
        if (Sample() is not { } path) return;
        var rvt = RevitFile.Open(path);
        foreach (var s in new[] { "BasicFileInfo", "Formats/Latest", "Global/ElemTable", "Global/Latest", "Partitions/68", "RevitPreview4.0" })
            Assert.Contains(s, rvt.Container.Streams);
        Assert.Equal(90_635_652, rvt.Container.Size("Partitions/68"));
        var info = rvt.Info();
        Assert.Equal("2024", info.Format);
        Assert.StartsWith("20230405_1134", info.Build);
        Assert.EndsWith("Snowdon Towers Sample Architectural.rvt", info.LastSavePath);
        foreach (var (k, v) in info.Fields) output.WriteLine($"{k}: {v}");
        Assert.Equal("Not enabled", info.Fields["Worksharing"]);
        var png = rvt.Preview()!;
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
        Assert.Equal("IEND"u8.ToArray(), png[^8..^4]);
    }

    [Fact]
    public void The_partitions_split_into_groups_and_records()
    {
        if (Sample() is not { } path) return;
        var rvt = RevitFile.Open(path);
        var schema = rvt.Schema();
        foreach (var name in rvt.Container.Streams.Where(s => s.StartsWith("Partitions/")).Order())
        {
            var prepared = RevitStreams.StripPages(rvt.Container.Stream(name));
            var groups = RevitPartitions.Groups(prepared, schema).ToList();
            var byChannel = new SortedDictionary<ulong, (int Groups, int Records, Dictionary<string, int> Classes)>();
            foreach (var (g, data) in groups)
            {
                var records = RevitPartitions.Records(g, data, 2024);
                if (!byChannel.TryGetValue(g.Channel, out var c)) c = (0, 0, new());
                c.Groups++; c.Records += records.Count;
                foreach (var r in records)
                {
                    string cls = r.Body.Length >= 2 && schema.ByTag(BitConverter.ToUInt16(r.Body.Span)) is { } k ? k.Name : "?";
                    c.Classes[cls] = c.Classes.GetValueOrDefault(cls) + 1;
                }
                byChannel[g.Channel] = c;
            }
            Assert.NotEmpty(groups);
            foreach (var (ch, c) in byChannel)
                output.WriteLine($"{name} channel {ch}: {c.Groups} groups, {c.Records} records; " + string.Join(", ", c.Classes.OrderByDescending(x => x.Value).Take(12).Select(x => $"{x.Key} {x.Value}")));
        }
    }

    [Fact]
    public void The_saved_graphics_records_decode_by_the_schema()
    {
        if (Sample() is not { } path) return;
        var rvt = RevitFile.Open(path);
        var schema = rvt.Schema();
        var prepared = RevitStreams.StripPages(rvt.Container.Stream("Partitions/68"));
        int ok = 0, failed = 0;
        var errors = new Dictionary<string, int>();
        var classes = new Dictionary<string, int>();
        foreach (var (g, data) in RevitPartitions.Groups(prepared, schema).Where(x => x.Group.Channel == 103))
            foreach (var r in RevitPartitions.Records(g, data, 2024))
            {
                if (r.Body.Length < 2 || schema.ByTag(BitConverter.ToUInt16(r.Body.Span))?.Name != "GElement") continue;
                try
                {
                    var graph = RevitGraph.Decode(r.Body, schema);
                    ok++;
                    foreach (var o in graph.Objects) classes[o.ClassName] = classes.GetValueOrDefault(o.ClassName) + 1;
                }
                catch (FormatException e)
                {
                    failed++;
                    string key = System.Text.RegularExpressions.Regex.Replace(e.Message, @"\d+", "#");
                    errors[key] = errors.GetValueOrDefault(key) + 1;
                }
            }
        output.WriteLine($"GElement: {ok} decoded, {failed} not");
        foreach (var (k, v) in errors.OrderByDescending(x => x.Value).Take(10)) output.WriteLine($"  {v}× {k}");
        output.WriteLine(string.Join(", ", classes.OrderByDescending(x => x.Value).Take(40).Select(x => $"{x.Key} {x.Value}")));
        Assert.True(ok > 0);
    }

    [Fact]
    public void The_saved_geometry_becomes_triangles()
    {
        if (Sample() is not { } path) return;
        var report = new RevitGeometryReport();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var elements = RevitGeometry.Read(RevitFile.Open(path), report);
        output.WriteLine($"{sw.ElapsedMilliseconds} ms: {report.Elements} elements, {elements.Sum(e => e.Triangles.Count())} triangles, {report.Faces} faces; hidden {report.HiddenBranches}, rejected filters {report.RejectedFilters}, undecoded {report.UndecodedGraphs}");
        output.WriteLine("skipped surfaces: " + string.Join(", ", report.SkippedSurfaces.Select(x => $"{x.Key} {x.Value}")));
        foreach (var (k, v) in report.Unjudged.OrderByDescending(x => x.Value).Take(12)) output.WriteLine($"  unjudged {v}× {k}");
        output.WriteLine("classes: " + string.Join(", ", elements.GroupBy(e => e.Class).OrderByDescending(x => x.Count()).Take(25).Select(x => $"{x.Key} {x.Count()}")));
        var only = Environment.GetEnvironmentVariable("RVT_CLASS");
        var all = elements.Where(e => only is not { Length: > 0 } || e.Class == only).SelectMany(e => e.Triangles).ToList();
        var pts = all.SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
        output.WriteLine($"box: ({pts.Min(p => p.X):0.##}, {pts.Min(p => p.Y):0.##}, {pts.Min(p => p.Z):0.##}) – ({pts.Max(p => p.X):0.##}, {pts.Max(p => p.Y):0.##}, {pts.Max(p => p.Z):0.##}) m");
        if (Environment.GetEnvironmentVariable("RVT_STL") is { Length: > 0 } csvStl)
            File.WriteAllLines(csvStl + ".csv", elements.Select(e => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{e.Id},{e.Class},{e.Triangles.Average(t => t.A.X)},{e.Triangles.Average(t => t.A.Y)},{e.Triangles.Average(t => t.A.Z)}")));
        if (Environment.GetEnvironmentVariable("RVT_STL") is { Length: > 0 } stl)
            File.WriteAllBytes(stl, RsDocx.Core.Stl.StlWriter.WriteBinary(all.Select(t => new RsDocx.Core.Stl.StlTriangle(t.A * 1000, t.B * 1000, t.C * 1000)).ToList(), "revit<stl unit=MM>"));
        Assert.NotEmpty(elements);
    }

    [Fact]
    public void The_schema_is_read_whole()
    {
        if (Sample() is not { } path) return;
        var schema = RevitFile.Open(path).Schema();
        output.WriteLine($"{schema.Classes.Count} classes, {schema.Classes.Sum(c => c.Fields.Count)} fields; stopped: {schema.Stopped ?? "no"}");
        Assert.Null(schema.Stopped);
        // (read to the end, every inline tag as its reference says; rvt-rs's README names 4 126 classes and 11 562 fields
        // "on Revit 2024", which this file does not give: another file, or its earlier parser)
        Assert.Equal(4492, schema.Classes.Count);
        Assert.Equal(11982, schema.Classes.Sum(c => c.Fields.Count));
        for (int i = 0; i < schema.Classes.Count; i++) Assert.Equal(RevitSchema.FirstTag + i, schema.Classes[i].Tag);
        output.WriteLine(string.Join(", ", schema.Classes.Where(c => c.Name.Contains("Wall")).Select(c => c.Name)));
        var wall = schema.ByName("ArcWall")!;
        Assert.NotNull(wall);
        Assert.NotEmpty(schema.AllFields(wall));
        foreach (var f in schema.AllFields(wall)) output.WriteLine($"Wall.{f.Name}: {f.Base}/0x{f.Modifier:x2}{(f.ClassTag is { } t ? " → " + schema.ByTag(t)?.Name : "")}");
    }
}
