using System.Buffers.Binary;
using System.Text;

namespace RsDocx.Core.Xt;

public sealed class XtFormatException(string message, long offset) : Exception($"{message} (offset {offset})")
{
    public long Offset { get; } = offset;
}

/// <summary>
/// Reader for bare-binary ('B', little-endian) Parasolid XT data with embedded schema (base SCH_13006).
/// </summary>
public sealed class XtReader
{
    private readonly byte[] _b;
    private int _o;

    private XtReader(byte[] data) { _b = data; }

    /// <summary>Number of bytes consumed by the last Read (header + nodes + terminator).</summary>
    public int BytesConsumed => _o;

    public static XtFile Read(byte[] data) => Read(data, out _);

    public static XtFile Read(byte[] data, out int bytesConsumed)
    {
        var r = new XtReader(data);
        var file = r.ReadFile();
        bytesConsumed = r._o;
        return file;
    }

    private XtFile ReadFile()
    {
        char flag = (char)U8();
        if (flag != 'B')
            throw new XtFormatException($"Only bare binary ('B') XT is supported, got '{flag}'", 0);

        // Observed: both lengths are 4-byte ints here (the V35 spec text says 2-byte for the first).
        string modeller = Str(I32());
        string schema = Str(I32());
        short? maxNodeTypes = null;
        if (schema.Count(c => c == '_') >= 3) maxNodeTypes = I16();
        int userField = I32();
        if (userField != 0) throw new XtFormatException("User fields are not supported", _o);
        if (maxNodeTypes is null) throw new XtFormatException("Only XT data with embedded schema is supported", _o);

        var schemas = new Dictionary<int, XtSchemaEntry>();
        var nodes = new List<XtNode>();
        while (true)
        {
            int start = _o;
            short nodeType = I16();
            if (nodeType == 1)
            {
                int idx = Index();
                if (idx != 0) throw new XtFormatException($"Terminator followed by index {idx}, expected 0", start);
                break;
            }
            if (!schemas.TryGetValue(nodeType, out var entry))
            {
                try { entry = ReadSchemaEntry(nodeType); }
                catch (XtFormatException e)
                {
                    throw new XtFormatException($"Embedded schema of node type {nodeType}: {e.Message}; previous node was " +
                        (nodes.Count > 0 ? $"type {nodes[^1].NodeType} (#{nodes[^1].Index})" : "none"), start);
                }
                schemas[nodeType] = entry;
            }
            int? varLen = entry.IsVariable ? I32() : null;
            if (varLen is < 0 || varLen > _b.Length)
                throw new XtFormatException($"Node type {nodeType}: implausible variable length {varLen}; previous node was " +
                    (nodes.Count > 0 ? $"type {nodes[^1].NodeType} (#{nodes[^1].Index}), whose field list is probably wrong" : "none"), start);
            int index = Index();
            var values = new object[entry.Fields.Count];
            for (int i = 0; i < values.Length; i++)
            {
                var f = entry.Fields[i];
                if (f.NElts == 0) values[i] = ReadValue(f.Type);
                else
                {
                    int count = f.NElts == 1 ? varLen!.Value : f.NElts;
                    var arr = new object[count];
                    for (int k = 0; k < count; k++) arr[k] = ReadValue(f.Type);
                    values[i] = arr;
                }
            }
            nodes.Add(new XtNode { NodeType = nodeType, Index = index, Schema = entry, Values = values, VarLength = varLen });
            if (Trace) Console.Error.WriteLine($"[xt] @{start} type {nodeType} #{index} len {varLen} -> {_o}");
        }

        return new XtFile
        {
            Flag = flag, ModellerVersion = modeller, SchemaVersion = schema, MaxNodeTypes = maxNodeTypes,
            UserFieldSize = userField, Nodes = nodes,
        };
    }

    /// <summary>RSXTTRACE=1: every node read (offset, type, index) to stderr, to find where a field list is wrong.</summary>
    private static readonly bool Trace = Environment.GetEnvironmentVariable("RSXTTRACE") == "1";

    private XtSchemaEntry ReadSchemaEntry(int nodeType)
    {
        int at = _o;
        byte flag = U8();
        XtBaseSchema.Types.TryGetValue(nodeType, out var baseDef);
        if (baseDef is null)
        {
            if (flag == 0xFF)
                throw new XtFormatException($"Node type {nodeType} marked as equal to base schema, but it is not in our base schema table", at);
            // Assumption: a node type outside our table that has an edit sequence would be misread here.
            // We detect that by checking that the bytes look like a short string (name) afterwards.
            string name = ShortStr();
            string desc = ShortStr();
            var fields = new List<XtFieldDef>();
            for (int i = 0; i < flag; i++) fields.Add(ReadFieldDef());
            return new XtSchemaEntry { NodeType = nodeType, Kind = XtSchemaKind.New, Fields = fields, Name = name, Description = desc };
        }
        if (flag == 0xFF)
            return new XtSchemaEntry { NodeType = nodeType, Kind = XtSchemaKind.SameAsBase, Fields = baseDef.Fields };

        var ops = new List<XtEditOp>();
        var cur = new List<XtFieldDef>();
        int bi = 0;
        while (true)
        {
            char op = (char)U8();
            switch (op)
            {
                case 'C':
                    if (bi >= baseDef.Fields.Count) throw new XtFormatException($"Copy beyond base fields of node type {nodeType}", _o);
                    cur.Add(baseDef.Fields[bi++]); ops.Add(new XtEditOp('C'));
                    break;
                case 'D':
                    if (bi >= baseDef.Fields.Count) throw new XtFormatException($"Delete beyond base fields of node type {nodeType}", _o);
                    bi++; ops.Add(new XtEditOp('D'));
                    break;
                case 'I':
                case 'A':
                    var fd = ReadFieldDef(); cur.Add(fd); ops.Add(new XtEditOp(op, fd));
                    break;
                case 'Z':
                    if (cur.Count != flag)
                        throw new XtFormatException($"Node type {nodeType}: edit sequence gives {cur.Count} fields, header says {flag}", at);
                    return new XtSchemaEntry { NodeType = nodeType, Kind = XtSchemaKind.Edited, Fields = cur, Ops = ops };
                default:
                    throw new XtFormatException($"Unknown edit op 0x{(byte)op:X2} for node type {nodeType}", _o - 1);
            }
        }
    }

    private XtFieldDef ReadFieldDef()
    {
        string name = ShortStr();
        short ptrClass = I16();
        int at = _o;
        int nElts = Index(); // "positive integer", same encoding as indices
        if (nElts < 0) throw new XtFormatException($"Field '{name}': invalid element count {nElts}", at);
        string type = ptrClass != 0 ? "p" : ShortStr();
        bool xmt = true;
        if (nElts == 1) xmt = U8() != 0;
        return new XtFieldDef(name, ptrClass, nElts, type, xmt);
    }

    private object ReadValue(string type) => type switch
    {
        "d" => I32(),
        "p" => Index(),
        "u" or "c" or "l" => U8(),
        "n" or "w" => I16(),
        "f" => F64(),
        "v" or "h" => new[] { F64(), F64(), F64() },
        "i" => new[] { F64(), F64() },
        "b" => new[] { F64(), F64(), F64(), F64(), F64(), F64() },
        _ => throw new XtFormatException($"Unknown field type '{type}'", _o),
    };

    private void Need(int n)
    {
        if (_o + n > _b.Length) throw new XtFormatException($"Unexpected end of data (need {n} bytes)", _o);
    }
    private byte U8() { Need(1); return _b[_o++]; }
    private short I16() { Need(2); var v = BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(_o)); _o += 2; return v; }
    private int I32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(_o)); _o += 4; return v; }
    private double F64() { Need(8); var v = BinaryPrimitives.ReadDoubleLittleEndian(_b.AsSpan(_o)); _o += 8; return v; }
    private string Str(int n) { Need(n); var s = Encoding.Latin1.GetString(_b, _o, n); _o += n; return s; }
    private string ShortStr() => Str(U8());

    /// <summary>Pointer index / positive integer: spec 3.3.3.</summary>
    private int Index()
    {
        short r = I16();
        int q = 0;
        if (r < 0) { q = I16(); r = (short)-r; }
        return q * 32767 + r - 1;
    }
}
