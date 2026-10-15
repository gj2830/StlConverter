using System.Buffers.Binary;
using System.Text;

namespace RsDocx.Core.Xt;

/// <summary>
/// Writes an <see cref="XtFile"/> as bare binary (little-endian) XT with embedded schema.
/// Schema information is emitted from each node's <see cref="XtSchemaEntry"/> the first time its node type occurs,
/// so a parsed file is written back byte-for-byte.
/// </summary>
public static class XtWriter
{
    public static byte[] Write(XtFile file)
    {
        var w = new Buf();
        w.U8((byte)file.Flag);
        w.I32(file.ModellerVersion.Length); w.Str(file.ModellerVersion);
        w.I32(file.SchemaVersion.Length); w.Str(file.SchemaVersion);
        if (file.MaxNodeTypes is short max) w.I16(max);
        w.I32(file.UserFieldSize);

        var seen = new HashSet<int>();
        foreach (var node in file.Nodes)
        {
            w.I16((short)node.NodeType);
            if (seen.Add(node.NodeType)) WriteSchemaEntry(w, node.Schema);
            var fields = node.Schema.Fields;
            if (node.Schema.IsVariable)
            {
                int varLen = node.VarLength ?? throw new InvalidOperationException($"Node {node} needs VarLength");
                for (int i = 0; i < fields.Count; i++)
                    if (fields[i].IsVariable && ((object[])node.Values[i]).Length != varLen)
                        throw new InvalidOperationException($"Node {node}: variable field '{fields[i].Name}' length != VarLength");
                w.I32(varLen);
            }
            w.Index(node.Index);
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                if (f.NElts == 0) WriteValue(w, f.Type, node.Values[i]);
                else
                {
                    var arr = (object[])node.Values[i];
                    if (f.NElts > 1 && arr.Length != f.NElts)
                        throw new InvalidOperationException($"Node {node}: field '{f.Name}' needs {f.NElts} elements");
                    foreach (var v in arr) WriteValue(w, f.Type, v);
                }
            }
        }
        w.I16(1); w.Index(0); // terminator
        return w.ToArray();
    }

    private static void WriteSchemaEntry(Buf w, XtSchemaEntry e)
    {
        switch (e.Kind)
        {
            case XtSchemaKind.SameAsBase:
                w.U8(0xFF);
                break;
            case XtSchemaKind.New:
                w.U8(checked((byte)e.Fields.Count));
                w.ShortStr(e.Name ?? ""); w.ShortStr(e.Description ?? "");
                foreach (var f in e.Fields) WriteFieldDef(w, f);
                break;
            case XtSchemaKind.Edited:
                w.U8(checked((byte)e.Fields.Count));
                foreach (var op in e.Ops!)
                {
                    w.U8((byte)op.Op);
                    if (op.Op is 'I' or 'A') WriteFieldDef(w, op.Field!);
                }
                w.U8((byte)'Z');
                break;
        }
    }

    private static void WriteFieldDef(Buf w, XtFieldDef f)
    {
        w.ShortStr(f.Name);
        w.I16(f.PtrClass);
        w.Index(f.NElts);
        if (f.PtrClass == 0) w.ShortStr(f.Type);
        if (f.NElts == 1) w.U8(f.Xmt ? (byte)1 : (byte)0);
    }

    private static void WriteValue(Buf w, string type, object v)
    {
        switch (type)
        {
            case "d": w.I32((int)v); break;
            case "p": w.Index((int)v); break;
            case "u" or "c" or "l": w.U8((byte)v); break;
            case "n" or "w": w.I16((short)v); break;
            case "f": w.F64((double)v); break;
            case "v" or "h" or "i" or "b":
                var a = (double[])v;
                int n = type switch { "i" => 2, "b" => 6, _ => 3 };
                if (a.Length != n) throw new InvalidOperationException($"Field type '{type}' needs {n} doubles");
                foreach (var d in a) w.F64(d);
                break;
            default: throw new InvalidOperationException($"Unknown field type '{type}'");
        }
    }

    private sealed class Buf
    {
        private readonly MemoryStream _ms = new();
        private readonly byte[] _tmp = new byte[8];
        public byte[] ToArray() => _ms.ToArray();
        public void U8(byte b) => _ms.WriteByte(b);
        public void I16(short v) { BinaryPrimitives.WriteInt16LittleEndian(_tmp, v); _ms.Write(_tmp, 0, 2); }
        public void I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(_tmp, v); _ms.Write(_tmp, 0, 4); }
        public void F64(double v) { BinaryPrimitives.WriteDoubleLittleEndian(_tmp, v); _ms.Write(_tmp, 0, 8); }
        public void Str(string s) { var b = Encoding.Latin1.GetBytes(s); _ms.Write(b); }
        public void ShortStr(string s) { U8(checked((byte)s.Length)); Str(s); }

        public void Index(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (index < 32767) I16((short)(index + 1));
            else { I16((short)-(index % 32767 + 1)); I16((short)(index / 32767)); }
        }
    }
}
