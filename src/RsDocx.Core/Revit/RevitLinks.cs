namespace RsDocx.Core.Revit;

/// <summary>A Revit model linked into another and placed there: the linked file's path as saved (relative to the model
/// that links it, or absolute), its name, and where it stands (a 4×4 matrix by rows, the move in the last column, in
/// metres).</summary>
public sealed record RevitLink(ulong Instance, string Path, string Name, double[] Placement);

/// <summary>
/// The Revit models a model links (Revit links): every RvtLinkInstance (where a link is placed: its InstanceInfo's
/// transform, and the RvtLinkSymbol it places) with its symbol's model location (m_otherDocModelLocation's last known
/// path) and name.
/// </summary>
public static class RevitLinks
{
    private const double Foot = 0.3048;

    public static List<RevitLink> Read(RevitFile file)
    {
        var schema = file.Schema();
        var records = file.Records(102);
        RevitGraph? Graph(ulong id) { try { return records.TryGetValue(id, out var r) ? RevitGraph.Decode(r.Body, schema) : null; } catch (FormatException) { return null; } }
        static object? F(RevitObject o, string n) => o.Fields.GetValueOrDefault(n);
        static double[]? Numbers(object? v) => v is List<object?> { Count: 3 } l && l.All(x => x is double) ? l.Cast<double>().ToArray() : null;
        var links = new List<RevitLink>();
        foreach (var (id, (cls, _)) in records.Where(kv => kv.Value.Class == "RvtLinkInstance"))
        {
            if (Graph(id) is not { } g || g.OfClass("InstanceInfo").FirstOrDefault() is not { } info) continue;
            if (RevitGeometry.Identifier(F(info, "m_symbolId")) is not { } sid || Graph((ulong)sid) is not { } sg || sg.Objects.Count == 0) continue;
            var symbol = sg.Objects[0];
            string? path = F(symbol, "m_otherDocModelLocation") is IReadOnlyDictionary<string, object?> loc
                && loc.GetValueOrDefault("m_lastKnownModelPath") is IReadOnlyDictionary<string, object?> known ? known.GetValueOrDefault("m_path") as string : null;
            string name = sg.OfClass("SymbolInfo").FirstOrDefault() is { } si && F(si, "m_name") is string n ? n : System.IO.Path.GetFileName(path ?? "");
            if (string.IsNullOrWhiteSpace(path)) continue;
            // (the transform: its rows and the move, the move in feet)
            var m = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            if (F(info, "m_Trf") is IReadOnlyDictionary<string, object?> trf && trf.GetValueOrDefault("m_3x3") is List<object?> { Count: 3 } rows && Numbers(trf.GetValueOrDefault("m_or")) is { } or)
                for (int i = 0; i < 3; i++)
                {
                    if (Numbers(rows[i]) is not { } row) continue;
                    for (int j = 0; j < 3; j++) m[i * 4 + j] = row[j];
                    m[i * 4 + 3] = or[i] * Foot;
                }
            links.Add(new RevitLink(id, path, name, m));
        }
        return links;
    }
}
