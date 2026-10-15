using System.Globalization;
using RsDocx.Core.Package;
using RsDocx.Core.Xt;

// rsdump <file.rsdocx|file.x_b> [--nodes]
// Prints the package structure, relationships and a summary of every Parasolid part.

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: rsdump <file.rsdocx|file.x_b> [--nodes]");
    return 2;
}
string path = args[0];
bool dumpNodes = args.Contains("--nodes");

if (path.EndsWith(".x_b", StringComparison.OrdinalIgnoreCase))
{
    DumpXt(path, File.ReadAllBytes(path), dumpNodes);
    return 0;
}

var pkg = RsDocxPackage.Open(path);
Console.WriteLine($"Package: {path}");
Console.WriteLine($"{"Part",-80} {"Size",8} {"Zipped",8} Method");
foreach (var e in pkg.Entries.OrderBy(e => e.Path, StringComparer.Ordinal))
    Console.WriteLine($"{e.Path,-80} {e.Length,8} {e.CompressedLength,8} {(e.Stored ? "stored" : "deflate")}");

Console.WriteLine();
Console.WriteLine("Relationships:");
foreach (var r in pkg.Relationships())
    Console.WriteLine($"  [{(r.Source == "" ? "package" : r.Source)}] {r.Id}: {r.Type} -> {r.Target}");

foreach (var e in pkg.Entries.Where(e => e.Path.EndsWith(".x_b", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine();
    DumpXt(e.Path, pkg.GetPart(e.Path), dumpNodes);
}
return 0;

static void DumpXt(string name, byte[] data, bool dumpNodes)
{
    var file = XtReader.Read(data, out int consumed);
    Console.WriteLine($"XT {name}: {data.Length} bytes, parser consumed {consumed} ({(consumed == data.Length ? "exact" : "MISMATCH")})");
    Console.WriteLine($"  modeller: '{file.ModellerVersion}'");
    Console.WriteLine($"  schema:   '{file.SchemaVersion}', max node types {file.MaxNodeTypes}, user field size {file.UserFieldSize}");
    Console.WriteLine($"  nodes: {file.Nodes.Count}, root = node type {file.Nodes[0].NodeType}");
    foreach (var g in file.Nodes.GroupBy(n => n.NodeType).OrderBy(g => g.Key))
    {
        var s = g.First().Schema;
        string label = s.Name ?? (XtBaseSchema.Types.TryGetValue(g.Key, out var b) ? b.Name : "?");
        string schema = s.Kind switch
        {
            XtSchemaKind.SameAsBase => "base",
            XtSchemaKind.New => "new",
            _ => "edit " + string.Concat(s.Ops!.Select(o => o.Op)) + "Z",
        };
        Console.WriteLine($"    {g.Key,4} {label,-18} x{g.Count(),-4} {schema}");
    }

    var bodies = XtTopology.Bodies(file);
    var allVerts = new HashSet<(double, double, double)>();
    foreach (var body in bodies)
    {
        Console.WriteLine($"  body #{body.Index} name={body.Name} body_type={body.BodyType}: " +
                          $"F={body.Faces.Count} E={body.Edges.Count} V={body.Vertices.Count} V-E+F={body.EulerCharacteristic}");
        foreach (var v in body.Vertices)
        {
            allVerts.Add((v.X, v.Y, v.Z));
            if (dumpNodes) Console.WriteLine($"      vertex #{v.Index} ({v.X * 1000:0.###}, {v.Y * 1000:0.###}, {v.Z * 1000:0.###}) mm");
        }
        foreach (var f in body.Faces)
            Console.WriteLine($"      face #{f.Index} {f.Name,-8} surf={f.SurfaceType} sense={(char)f.Sense} loops={f.LoopCount} fins={f.FinCount}" +
                              (f.PlaneNormal is { } n ? $" n=({n[0]:0.###},{n[1]:0.###},{n[2]:0.###}) o=({f.PlaneOrigin![0] * 1000:0.###},{f.PlaneOrigin[1] * 1000:0.###},{f.PlaneOrigin[2] * 1000:0.###})mm" : ""));
        if (dumpNodes)
            foreach (var e in body.Edges)
                Console.WriteLine($"      edge #{e.Index} {e.Name,-8} {e.StartVertex} -> {e.EndVertex}");
    }
    Console.WriteLine($"  unique vertex positions: {allVerts.Count}");

    if (dumpNodes)
        foreach (var n in file.Nodes)
            Console.WriteLine($"  #{n.Index} [{n.NodeType}] " + string.Join(", ",
                n.Schema.Fields.Select((f, i) => $"{f.Name}={Fmt(n.Values[i])}")));
}

static string Fmt(object v) => v switch
{
    double[] d => "(" + string.Join(",", d.Select(x => x.ToString("R"))) + ")",
    object[] a => "[" + string.Join(",", a.Select(Fmt)) + "]",
    double d => d.ToString("R"),
    _ => v.ToString() ?? "",
};
