namespace RsDocx.Core.Xt;

public enum XtSchemaKind
{
    /// <summary>Node type not in the base schema: full definition transmitted.</summary>
    New,
    /// <summary>Identical to base schema (flag 0xFF).</summary>
    SameAsBase,
    /// <summary>Edit sequence (C/D/I/A/Z) relative to base schema.</summary>
    Edited,
}

/// <summary>One edit instruction: 'C', 'D', 'I' or 'A' (Field set for I/A). 'Z' is implicit at the end.</summary>
public sealed record XtEditOp(char Op, XtFieldDef? Field = null);

/// <summary>Embedded-schema information transmitted with the first node of a node type.</summary>
public sealed class XtSchemaEntry
{
    public required int NodeType { get; init; }
    public required XtSchemaKind Kind { get; init; }
    public required IReadOnlyList<XtFieldDef> Fields { get; init; }
    public string? Name { get; init; }          // Kind == New
    public string? Description { get; init; }   // Kind == New
    public IReadOnlyList<XtEditOp>? Ops { get; init; } // Kind == Edited
    public bool IsVariable => Fields.Any(f => f.IsVariable);
    public int IndexOf(string field)
    {
        // (looked up millions of times when a large body is written: a table, built once; a race only builds it twice)
        var index = _index;
        if (index == null)
        {
            index = new Dictionary<string, int>(Fields.Count);
            for (int i = Fields.Count - 1; i >= 0; i--) index[Fields[i].Name] = i; // first one wins, as before
            _index = index;
        }
        return index.TryGetValue(field, out int k) ? k : -1;
    }

    private volatile Dictionary<string, int>? _index;
}

/// <summary>
/// A node. Values[i] belongs to Schema.Fields[i]. Scalars are stored as:
/// d/p: int, u/c/l: byte, n/w: short, f: double, v/h: double[3], i: double[2], b: double[6].
/// Array or variable fields (NElts != 0) are stored as object[] of those.
/// </summary>
public sealed class XtNode
{
    public required int NodeType { get; init; }
    public required int Index { get; set; }
    public required XtSchemaEntry Schema { get; init; }
    public required object[] Values { get; init; }
    /// <summary>Length of the variable field, if the node type has one.</summary>
    public int? VarLength { get; set; }

    public object this[string field]
    {
        get => Values[FieldIndex(field)];
        set => Values[FieldIndex(field)] = value;
    }

    private int FieldIndex(string field)
    {
        int i = Schema.IndexOf(field);
        if (i < 0) throw new KeyNotFoundException($"Node type {NodeType} has no field '{field}'");
        return i;
    }

    public bool Has(string field) => Schema.IndexOf(field) >= 0;
    public int Int(string field) => (int)this[field];
    public int Ptr(string field) => (int)this[field];
    public byte Byte(string field) => (byte)this[field];
    public double Double(string field) => (double)this[field];
    public double[] Vec(string field) => (double[])this[field];
    public object[] Array(string field) => (object[])this[field];

    public override string ToString() => $"#{Index} type {NodeType}";
}

public sealed class XtFile
{
    public required char Flag { get; init; }              // 'B' = bare binary
    public required string ModellerVersion { get; init; } // ": TRANSMIT FILE created by modeller version ..."
    public required string SchemaVersion { get; init; }   // "SCH_3401212_34101_13006"
    public short? MaxNodeTypes { get; init; }              // only present for embedded-schema files
    public required int UserFieldSize { get; init; }
    /// <summary>Nodes in file order. The first node is the root.</summary>
    public required List<XtNode> Nodes { get; init; }

    public bool HasEmbeddedSchema => MaxNodeTypes.HasValue;

    public Dictionary<int, XtNode> ByIndex() => Nodes.ToDictionary(n => n.Index);
}

public static class XtNodeTypes
{
    public const int Body = 12, Shell = 13, Face = 14, Loop = 15, Edge = 16, Fin = 17, Vertex = 18, Region = 19;
    public const int Point = 29, Line = 30, Circle = 31, Ellipse = 32, Plane = 50, Cylinder = 51, Cone = 52, Sphere = 53, Torus = 54,
        BsplineVertices = 45, KnotMult = 127, KnotSet = 128, BCurve = 134, CurveData = 135, NurbsCurve = 136,
        BSurface = 124, SurfaceData = 125, NurbsSurf = 126, HelixSuForm = 163, HelixCuForm = 184;
    public const int List = 70, PointerLisBlock = 74, AttDefId = 79, AttribDef = 80, Attribute = 81;
    public const int IntValues = 82, RealValues = 83, CharValues = 84;
    public const int PartXmtBlock = 176;
    /// <summary>Double value Parasolid uses for "unset" (e.g. face tolerance).</summary>
    public const double NullDouble = -3.14158e13;
    public const int NullInt = -32764;
}
