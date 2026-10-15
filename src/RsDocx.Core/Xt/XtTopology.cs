using System.Text;

namespace RsDocx.Core.Xt;

public sealed record XtVertexInfo(int Index, double X, double Y, double Z);
public sealed record XtFaceInfo(int Index, string? Name, int SurfaceType, double[]? PlaneOrigin, double[]? PlaneNormal, byte Sense, int LoopCount, int FinCount);
public sealed record XtEdgeInfo(int Index, string? Name, int StartVertex, int EndVertex);

public sealed class XtBodyInfo
{
    public required int Index { get; init; }
    public required string? Name { get; init; }
    public required byte BodyType { get; init; }
    public required List<XtFaceInfo> Faces { get; init; }
    public required List<XtEdgeInfo> Edges { get; init; }
    public required List<XtVertexInfo> Vertices { get; init; }
    public int EulerCharacteristic => Vertices.Count - Edges.Count + Faces.Count;
}

/// <summary>Walks the topology of a parsed XT file (body -> faces/edges/vertices via body chains).</summary>
public static class XtTopology
{
    /// <summary>Name of the attribute definition SpaceClaim uses to tag bodies/faces/edges with document.xml monikers.</summary>
    public const string NameAttribute = "SDL/TYSA_NAME";

    public static List<XtBodyInfo> Bodies(XtFile file)
    {
        var map = file.ByIndex();
        var root = file.Nodes[0];
        IEnumerable<int> bodyIdx = root.NodeType switch
        {
            XtNodeTypes.Body => [root.Index],
            XtNodeTypes.PartXmtBlock or XtNodeTypes.PointerLisBlock =>
                root.Array("entries").Cast<int>().Take(root.Int("n_entries")),
            _ => throw new NotSupportedException($"Root node type {root.NodeType} not supported"),
        };

        var result = new List<XtBodyInfo>();
        foreach (int bi in bodyIdx)
        {
            var body = map[bi];
            var faces = new List<XtFaceInfo>();
            var edges = new List<XtEdgeInfo>();
            var verts = new List<XtVertexInfo>();

            foreach (var v in Chain(map, body.Ptr("vertex"), "next"))
            {
                var p = map[v.Ptr("point")].Vec("pvec");
                verts.Add(new XtVertexInfo(v.Index, p[0], p[1], p[2]));
            }
            foreach (var e in Chain(map, body.Ptr("edge"), "next"))
            {
                var fin = map[e.Ptr("fin")];
                int other = fin.Ptr("other");
                int v1 = fin.Ptr("vertex");
                int v0 = other != 0 ? map[other].Ptr("vertex") : 0;
                // edge->fin is a positive fin: its vertex is the forward (end) vertex
                edges.Add(new XtEdgeInfo(e.Index, NameOf(map, e), v0, v1));
            }
            // Faces: all faces of all shells of all regions.
            var seen = new HashSet<int>();
            foreach (var region in Chain(map, body.Ptr("region"), "next"))
                foreach (var shell in Chain(map, region.Ptr("shell"), "next"))
                {
                    foreach (var f in Chain(map, shell.Ptr("face"), "next"))
                        if (seen.Add(f.Index)) faces.Add(FaceInfo(map, f));
                    if (shell.Has("front_face"))
                        foreach (var f in Chain(map, shell.Ptr("front_face"), "next_front"))
                            if (seen.Add(f.Index)) faces.Add(FaceInfo(map, f));
                }

            result.Add(new XtBodyInfo
            {
                Index = bi, Name = NameOf(map, body), BodyType = body.Byte("body_type"),
                Faces = faces, Edges = edges, Vertices = verts,
            });
        }
        return result;
    }

    private static XtFaceInfo FaceInfo(Dictionary<int, XtNode> map, XtNode f)
    {
        var surf = map[f.Ptr("surface")];
        double[]? o = null, n = null;
        if (surf.NodeType == XtNodeTypes.Plane) { o = surf.Vec("pvec"); n = surf.Vec("normal"); }
        int loops = 0, fins = 0;
        foreach (var loop in Chain(map, f.Ptr("loop"), "next"))
        {
            loops++;
            int first = loop.Ptr("fin"), cur = first;
            do { fins++; cur = map[cur].Ptr("forward"); } while (cur != first && fins < 100000);
        }
        return new XtFaceInfo(f.Index, NameOf(map, f), surf.NodeType, o, n, f.Byte("sense"), loops, fins);
    }

    public static IEnumerable<XtNode> Chain(Dictionary<int, XtNode> map, int start, string nextField)
    {
        var guard = new HashSet<int>();
        for (int i = start; i != 0 && map.ContainsKey(i) && guard.Add(i); i = map[i].Ptr(nextField))
            yield return map[i];
    }

    /// <summary>Returns the SDL/TYSA_NAME attribute string of a node (e.g. "0:4165"), or null.</summary>
    public static string? NameOf(Dictionary<int, XtNode> map, XtNode node)
    {
        if (!node.Has("attributes_groups")) return null;
        foreach (var a in Chain(map, node.Ptr("attributes_groups"), "next"))
        {
            if (a.NodeType != XtNodeTypes.Attribute) continue;
            var def = map[a.Ptr("definition")];
            if (CharString(map[def.Ptr("identifier")], "string") != NameAttribute) continue;
            var fields = a.Array("fields");
            if (fields.Length > 0 && map.TryGetValue((int)fields[0], out var cv))
                return CharString(cv, "values");
        }
        return null;
    }

    public static string CharString(XtNode node, string field) =>
        Encoding.Latin1.GetString(node.Array(field).Select(o => (byte)o).ToArray());
}
