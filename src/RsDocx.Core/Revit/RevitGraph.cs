using System.Buffers.Binary;
using System.Text;

namespace RsDocx.Core.Revit;

/// <summary>A pointer in a record: its token (0: null; FFFFFFFF: an object of its own, not shared), the class it points
/// to (null: null or outside the record) and the object it resolves to in the graph (null: none).</summary>
public sealed record RevitPointer(uint Token, ushort? Tag, int Offset)
{
    public int? Target { get; internal set; }
}

/// <summary>An object of a record: its class, its pointer token, and its fields by name (a name declared by more than one
/// class of its lineage as "Class::name"). Values: bool, byte, short, int, uint, float, double, long, string, Guid,
/// <see cref="RevitPointer"/>, a list (arrays, containers) or a dictionary (an object inline).</summary>
public sealed record RevitObject(ushort Tag, string ClassName, uint Token, IReadOnlyDictionary<string, object?> Fields);

/// <summary>
/// A record decoded by the schema alone (as rvt-rs's native_parameters::decode_graph, see THIRD-PARTY-NOTICES.md): the
/// root object's class tag (u16), its fields (those of its base classes first), then every object a pointer in it named,
/// in the order they were met, each its fields. A pointer is a u32 token and, when not null and not external, the u16
/// tag of its class; a token met again is the same object. The record must be used up exactly.
/// </summary>
public sealed class RevitGraph
{
    public IReadOnlyList<RevitObject> Objects { get; }

    private readonly Dictionary<uint, int> _byToken = new();

    private RevitGraph(List<RevitObject> objects)
    {
        Objects = objects;
        // (a token held by more than one object resolves to none)
        for (int i = 0; i < objects.Count; i++)
            if (objects[i].Token is not (0 or uint.MaxValue))
                _byToken[objects[i].Token] = _byToken.ContainsKey(objects[i].Token) ? -1 : i;
    }

    /// <summary>The objects of a class.</summary>
    public IEnumerable<RevitObject> OfClass(string name) => Objects.Where(o => o.ClassName == name);

    /// <summary>The index of the object a pointer points to, or null: the one it was read with, or, for a pointer that
    /// only names a token (a non-owning reference back to an object of the record), the object with that token.</summary>
    public int? IndexOf(object? pointer) => pointer switch
    {
        RevitPointer { Target: { } t } when t < Objects.Count => t,
        RevitPointer { Token: not (0 or uint.MaxValue) } p when _byToken.TryGetValue(p.Token, out int i) && i >= 0 => i,
        _ => null,
    };

    /// <summary>The object a pointer points to, or null (see <see cref="IndexOf"/>).</summary>
    public RevitObject? Follow(object? pointer) => IndexOf(pointer) is { } i ? Objects[i] : null;

    public static RevitGraph Decode(ReadOnlyMemory<byte> body, RevitSchema schema, int maxObjects = 100_000, int maxValues = 1_000_000)
    {
        if (body.Length < 2) throw new FormatException("record too short");
        var c = new Cursor(body, schema, maxValues);
        var objects = new List<RevitObject>();
        var shared = new Dictionary<uint, (int Index, ushort Tag)>();
        var queue = new Queue<(uint Token, ushort Tag)>();
        queue.Enqueue((0, BinaryPrimitives.ReadUInt16LittleEndian(body.Span)));
        c.Pos = 2;
        while (queue.Count > 0)
        {
            if (objects.Count >= maxObjects) throw new FormatException("too many objects");
            var (token, tag) = queue.Dequeue();
            var cls = schema.ByTag(tag) ?? throw new FormatException($"unknown class {tag}");
            int pendingStart = c.Pending.Count;
            var fields = c.Class(tag, 0);
            objects.Add(new RevitObject(tag, cls.Name, token, fields));
            for (int i = pendingStart; i < c.Pending.Count; i++)
            {
                var p = c.Pending[i];
                if (shared.TryGetValue(p.Token, out var known))
                {
                    if (known.Tag != p.Tag) throw new FormatException($"pointer {p.Token} changes class");
                    p.Target = known.Index;
                    continue;
                }
                // (the order they are met in is the order the objects follow in)
                int index = objects.Count + queue.Count;
                queue.Enqueue((p.Token, p.Tag!.Value));
                if (p.Token != uint.MaxValue) shared[p.Token] = (index, p.Tag!.Value);
                p.Target = index;
            }
        }
        if (c.Pos != body.Length) throw new FormatException($"{body.Length - c.Pos} bytes left over");
        return new RevitGraph(objects);
    }

    private sealed class Cursor(ReadOnlyMemory<byte> body, RevitSchema schema, int maxValues)
    {
        public int Pos;
        public readonly List<RevitPointer> Pending = new();
        private int _values;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Pos + n > body.Length) throw new FormatException($"record ends inside a field at {Pos}");
            var s = body.Span.Slice(Pos, n); Pos += n; return s;
        }
        private uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        private RevitPointer Pointer(bool external)
        {
            int at = Pos;
            uint token = U32();
            ushort? tag = null;
            if (token != 0 && !external)
            {
                tag = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
                if (schema.ByTag(tag.Value) == null) throw new FormatException($"pointer to unknown class {tag}");
                var p = new RevitPointer(token, tag, at);
                Pending.Add(p);
                return p;
            }
            return new RevitPointer(token, tag, at);
        }

        public Dictionary<string, object?> Class(ushort tag, int depth)
        {
            if (depth > 128) throw new FormatException("objects nest too deep");
            var cls = schema.ByTag(tag) ?? throw new FormatException($"unknown class {tag}");
            var lineage = schema.Lineage(cls);
            // (a name declared by more than one class of the lineage: qualified by its class)
            var counts = new Dictionary<string, int>();
            foreach (var k in lineage)
                foreach (var f in k.Fields)
                    if ((f.Flags & 2) == 0) counts[f.Name] = counts.GetValueOrDefault(f.Name) + 1;
            var values = new Dictionary<string, object?>();
            for (int d = lineage.Count - 1; d >= 0; d--)
            {
                var k = lineage[d];
                foreach (var f in k.Fields)
                {
                    // (flag 2: not stored; other flags only where measured)
                    if ((f.Flags & 2) != 0) continue;
                    if (f.Flags != 0 && !(k.Name == "VarExpr" && f.Name == "m_refCt" && f.Flags == 4 && f.Base == 4 && f.Modifier == 0))
                        throw new FormatException($"unknown field flags {k.Name}.{f.Name}: {f.Flags}");
                    object? value;
                    if (k.Name == "ClassDefinitionRef" && f.Name == "m_ref" && f.Base == 10 && f.Modifier == 0)
                        value = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
                    else if (k.Name == "ESEntity" && f.Name == "m_blob" && f.Base == 14 && f.Modifier == 1)
                    {
                        int disc = (int)U32();
                        if (disc == -1) throw new FormatException("extensible storage entity (not read yet)");
                        if (disc != 0) throw new FormatException($"unknown entity discriminator {disc}");
                        value = null;
                    }
                    else if (k.Name == "VarSketchObj" && f.Name == "m_params" && f.Base == 14 && f.Modifier == 0x54)
                        value = Field(f with { Modifier = 0x51 }, depth + d + 1);
                    else
                    {
                        try { value = Field(f, depth + d + 1); }
                        catch (FormatException e) when (!e.Message.Contains(" at field ")) { throw new FormatException($"{e.Message} at field {k.Name}.{f.Name}"); }
                    }
                    values[counts[f.Name] > 1 ? $"{k.Name}::{f.Name}" : f.Name] = value;
                }
            }
            return values;
        }

        private object? Field(RevitField f, int depth)
        {
            if (depth > 128) throw new FormatException("fields nest too deep");
            if (++_values > maxValues) throw new FormatException("too many values");
            switch (f.Modifier)
            {
                case >= 1 and <= 3 when f.Base == 14:
                    return Pointer(f.Modifier == 3);
                case 0x60 when f.Base == 8:
                {
                    int n = (int)U32();
                    if (n > 1_000_000) throw new FormatException("string too long");
                    return Encoding.Unicode.GetString(Take(n * 2));
                }
                case (>= 0x10 and <= 0x13) or (>= 0x50 and <= 0x53):
                {
                    uint n = f.Modifier < 0x50 ? f.ArrayCount ?? throw new FormatException("array without count") : U32();
                    if (n > 100_000) throw new FormatException("container too large");
                    var item = f with { Modifier = (byte)(f.Modifier & 0xf), ArrayCount = null };
                    var list = new List<object?>((int)n);
                    for (int i = 0; i < n; i++) list.Add(Field(item, depth + 1));
                    return list;
                }
                case 0:
                    switch (f.Base)
                    {
                        case 1:
                            byte b = Take(1)[0];
                            if (b > 1) throw new FormatException("invalid bool");
                            return b != 0;
                        case 2: return Take(1)[0];
                        case 3: return BinaryPrimitives.ReadInt16LittleEndian(Take(2));
                        case 4: return BinaryPrimitives.ReadInt32LittleEndian(Take(4));
                        case 5: return U32();
                        case 6:
                            float fl = BinaryPrimitives.ReadSingleLittleEndian(Take(4));
                            if (!float.IsFinite(fl)) throw new FormatException("non-finite float");
                            return fl;
                        case 7:
                            double db = BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
                            if (!double.IsFinite(db)) throw new FormatException("non-finite double");
                            return db;
                        case 9: return new Guid(Take(16));
                        case 11: return BinaryPrimitives.ReadInt64LittleEndian(Take(8));
                        case 13: return Field(f.Nested ?? throw new FormatException("composite without inner type"), depth + 1);
                        case 14:
                            if (f.References.Count != 1) throw new FormatException("inline object without one class");
                            return Class(f.References[0], depth + 1);
                        default: throw new FormatException($"unknown scalar {f.Base}");
                    }
                default: throw new FormatException($"unknown modifier 0x{f.Modifier:x2}");
            }
        }
    }
}
