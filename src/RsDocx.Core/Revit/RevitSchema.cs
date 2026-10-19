using System.Buffers.Binary;
using System.Text;

namespace RsDocx.Core.Revit;

/// <summary>
/// A field of a class in Revit's schema: its name, its descriptor (base type 1..14, modifier, flags), the count of a
/// fixed array (modifier 0x10..0x13), the classes it refers to (a class or a pointer to one: base 14; for a composite,
/// base 13, those of its inner type, repeated per array entry) and the inner type of a composite.
/// </summary>
public sealed record RevitField(string Name, byte Base, byte Modifier, ushort Flags, uint? ArrayCount, IReadOnlyList<ushort> References, RevitField? Nested)
{
    /// <summary>The class it refers to first, or null.</summary>
    public ushort? ClassTag => References.Count > 0 ? References[0] : null;
}

/// <summary>A class of Revit's schema: its tag (the order it is defined in, from 12), name, the tag of its base class
/// (null: none), its version and its own fields.</summary>
public sealed record RevitClass(ushort Tag, string Name, ushort? Base, uint Version, IReadOnlyList<RevitField> Fields);

/// <summary>
/// The schema a Revit file carries (the inflated, page-stripped Formats/Latest stream): every class it stores with its
/// fields. Read as rvt-rs's schema_registry does (see THIRD-PARTY-NOTICES.md): classes one after another until 8 zero
/// bytes; a class is a zero u16, a u16-counted name, a reference to its base class, a u32 version, a u32 field count, the
/// fields and a u32-counted list of 16-byte entries. A field is a u32-counted name and a u32 descriptor (base byte,
/// modifier byte, flags u16); modifier 0x10..0x13 adds a u32 array count; base 14 with modifier 0, 0x10 or 0x50 adds a
/// class reference; base 13 (a composite) adds its inner field (named " "), its references repeated for every further
/// array entry. A reference is a u16, bit 15 meaning the class is defined right there (its tag must be the next one).
/// Anything else, or bytes left over, is an error.
/// </summary>
public sealed class RevitSchema
{
    public const ushort FirstTag = 12;

    public IReadOnlyList<RevitClass> Classes { get; }

    /// <summary>Why reading stopped before the end (null: it did not).</summary>
    public string? Stopped { get; }

    private readonly Dictionary<string, RevitClass> _byName;

    private RevitSchema(List<RevitClass> classes, string? stopped)
    {
        Classes = classes;
        Stopped = stopped;
        _byName = new Dictionary<string, RevitClass>(StringComparer.Ordinal);
        foreach (var c in classes) _byName.TryAdd(c.Name, c);
    }

    /// <summary>The class with this tag, or null.</summary>
    public RevitClass? ByTag(ushort tag) => tag >= FirstTag && tag - FirstTag < Classes.Count ? Classes[tag - FirstTag] : null;

    /// <summary>The (first) class with this name, or null.</summary>
    public RevitClass? ByName(string name) => _byName.GetValueOrDefault(name);

    /// <summary>A class's fields, those of its base classes first.</summary>
    public List<RevitField> AllFields(RevitClass c) => Lineage(c).AsEnumerable().Reverse().SelectMany(k => k.Fields).ToList();

    /// <summary>A class and its base classes, the class first.</summary>
    public List<RevitClass> Lineage(RevitClass c)
    {
        var chain = new List<RevitClass>();
        for (RevitClass? k = c; k != null && chain.Count < 128; k = k.Base is { } b ? ByTag(b) : null) chain.Add(k);
        return chain;
    }

    public static RevitSchema Parse(byte[] data)
    {
        var r = new Reader(data);
        string? stopped = null;
        try
        {
            while (data.Length - r.Pos > 8) r.Definition();
            if (!data.AsSpan(r.Pos).SequenceEqual(new byte[8])) stopped = $"no 8 zero bytes at the end (0x{r.Pos:x})";
        }
        catch (FormatException e) { stopped = e.Message; }
        var classes = r.Classes.TakeWhile(c => c != null).Select(c => c!).ToList();
        return new RevitSchema(classes, stopped);
    }

    private sealed class Reader(byte[] data)
    {
        public int Pos;
        public readonly List<RevitClass?> Classes = new();
        private int _references, _fields, _depth;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Pos + n > data.Length) throw new FormatException($"schema ends at 0x{Pos:x}");
            var s = data.AsSpan(Pos, n); Pos += n; return s;
        }
        private ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        private uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        private string Text(int n)
        {
            if (n is < 1 or > 4096) throw new FormatException($"bad name length {n} at 0x{Pos:x}");
            return Encoding.UTF8.GetString(Take(n));
        }

        private ushort Reference()
        {
            if (++_references > 1_000_000) throw new FormatException("too many references");
            int at = Pos;
            ushort raw = U16();
            ushort tag = (ushort)(raw & 0x7fff);
            if ((raw & 0x8000) != 0)
            {
                if (tag != FirstTag + Classes.Count) throw new FormatException($"definition out of order ({tag}) at 0x{at:x}");
                Definition();
            }
            else if (tag >= FirstTag + Classes.Count) throw new FormatException($"reference ahead ({tag}) at 0x{at:x}");
            return tag;
        }

        private RevitField Field(int depth)
        {
            if (depth > 64 || ++_fields > 100_000) throw new FormatException("fields nest too deep");
            string name = Text((int)U32());
            int at = Pos;
            uint raw = U32();
            byte b = (byte)raw, modifier = (byte)(raw >> 8);
            ushort flags = (ushort)(raw >> 16);
            if (b is < 1 or > 14 || modifier is not (0 or 1 or 2 or 3 or 4 or 16 or 17 or 18 or 19 or 80 or 81 or 82 or 83 or 84 or 96))
                throw new FormatException($"unknown field descriptor 0x{raw:x8} at 0x{at:x}");
            uint? count = null;
            if (modifier is >= 16 and <= 19)
            {
                count = U32();
                if (count is < 1 or > 1_000_000) throw new FormatException($"array count {count} at 0x{Pos:x}");
            }
            var refs = new List<ushort>();
            if (b == 14 && modifier is 0 or 16 or 80) refs.Add(Reference());
            RevitField? nested = null;
            if (b == 13)
            {
                nested = Field(depth + 1);
                refs.AddRange(nested.References);
                for (uint i = 1; i < (count ?? 1); i++)
                    foreach (var expected in nested.References)
                        if (Reference() != expected) throw new FormatException($"composite reference differs at 0x{Pos:x}");
                // (a composite that is no array, over a fixed array of class references, repeats them once more)
                if (count == null && nested.Base == 14 && nested.ArrayCount != null)
                    foreach (var expected in nested.References)
                        if (Reference() != expected) throw new FormatException($"composite reference differs at 0x{Pos:x}");
            }
            return new RevitField(name, b, modifier, flags, count, refs, nested);
        }

        public void Definition()
        {
            if (++_depth > 128 || Classes.Count >= 32756) throw new FormatException("classes nest too deep");
            int at = Pos;
            if (U16() != 0) throw new FormatException($"class does not start with 0 at 0x{at:x}");
            string name = Text(U16());
            int ordinal = Classes.Count;
            Classes.Add(null);
            ushort parent = Reference();
            uint version = U32(), count = U32();
            if (count > 100_000) throw new FormatException($"field count {count} at 0x{Pos:x}");
            var fields = new List<RevitField>();
            for (int i = 0; i < count; i++) fields.Add(Field(0));
            uint entries = U32();
            if (entries > 1_000_000) throw new FormatException($"entry count {entries} at 0x{Pos:x}");
            Take((int)entries * 16);
            Classes[ordinal] = new RevitClass((ushort)(FirstTag + ordinal), name, parent >= FirstTag ? parent : null, version, fields);
            _depth--;
        }
    }
}
