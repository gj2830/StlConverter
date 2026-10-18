using System.Globalization;
using System.Text;

namespace RsDocx.Core.Step;

/// <summary>
/// The data section of a STEP file (ISO 10303-21) as entities: each "#n=TYPE(arguments);" (or a complex one,
/// "#n=(A(..)B(..));", as its parts by type). Arguments are kept as they are written: numbers (double), strings,
/// enumerations (".T."), references (<see cref="StepRef"/>), lists, typed values ("LENGTH_MEASURE(1.)") and $ or *
/// (null).
/// </summary>
public sealed class StepFile
{
    /// <summary>One entity: its type, and its arguments; a complex entity has several (type, arguments).</summary>
    public sealed record Entity(int Id, IReadOnlyList<(string Type, IReadOnlyList<object?> Args)> Parts)
    {
        public string Type => Parts[0].Type;
        public IReadOnlyList<object?> Args => Parts[0].Args;
        /// <summary>The arguments of the part of this type (a complex entity), or null.</summary>
        public IReadOnlyList<object?>? Part(string type) => Parts.FirstOrDefault(p => p.Type == type).Args;
        public bool Is(string type) => Parts.Any(p => p.Type == type);
    }

    public readonly record struct StepRef(int Id);
    public sealed record Typed(string Type, IReadOnlyList<object?> Args);
    public sealed record Enum(string Value);

    public Dictionary<int, Entity> Entities { get; } = new();

    public Entity this[StepRef r] => Entities.TryGetValue(r.Id, out var e) ? e : throw new StepException($"#{r.Id} ontbreekt in het bestand / is missing from the file.");

    public IEnumerable<Entity> OfType(string type) => Entities.Values.Where(e => e.Is(type)).OrderBy(e => e.Id);

    public static StepFile Parse(string text)
    {
        var f = new StepFile();
        int data = text.IndexOf("DATA;", StringComparison.Ordinal);
        if (!text.TrimStart().StartsWith("ISO-10303-21", StringComparison.Ordinal) || data < 0)
            throw new StepException("Dit is geen STEP-bestand (ISO-10303-21). / This is not a STEP file (ISO-10303-21).");
        int i = data + 5;
        while (true)
        {
            Skip(text, ref i);
            if (i >= text.Length || text.AsSpan(i).StartsWith("ENDSEC")) break;
            if (text[i] != '#') throw Bad(text, i);
            i++;
            int id = (int)Integer(text, ref i);
            Skip(text, ref i);
            Expect(text, ref i, '=');
            Skip(text, ref i);
            var parts = new List<(string, IReadOnlyList<object?>)>();
            if (text[i] == '(')
            {
                // a complex entity: several typed parts in one pair of brackets
                i++;
                while (true)
                {
                    Skip(text, ref i);
                    if (text[i] == ')') { i++; break; }
                    string type = Word(text, ref i);
                    Skip(text, ref i);
                    parts.Add((type, List(text, ref i)));
                }
            }
            else
            {
                string type = Word(text, ref i);
                Skip(text, ref i);
                parts.Add((type, List(text, ref i)));
            }
            Skip(text, ref i);
            Expect(text, ref i, ';');
            f.Entities[id] = new Entity(id, parts);
        }
        return f;
    }

    private static void Skip(string t, ref int i)
    {
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i])) i++;
            else if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '*')
            {
                int end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? t.Length : end + 2;
            }
            else break;
        }
    }

    private static void Expect(string t, ref int i, char c)
    {
        if (i >= t.Length || t[i] != c) throw Bad(t, i);
        i++;
    }

    private static StepException Bad(string t, int i)
    {
        int line = t.Take(Math.Min(i, t.Length)).Count(ch => ch == '\n') + 1;
        return new StepException($"Het STEP-bestand klopt niet bij regel {line}. / The STEP file is not right at line {line}.");
    }

    private static string Word(string t, ref int i)
    {
        int s = i;
        while (i < t.Length && (char.IsLetterOrDigit(t[i]) || t[i] == '_' || t[i] == '-')) i++;
        if (i == s) throw Bad(t, i);
        return t[s..i].ToUpperInvariant();
    }

    private static long Integer(string t, ref int i)
    {
        int s = i;
        while (i < t.Length && char.IsDigit(t[i])) i++;
        return long.Parse(t.AsSpan(s, i - s), CultureInfo.InvariantCulture);
    }

    /// <summary>A bracketed list of arguments.</summary>
    private static List<object?> List(string t, ref int i)
    {
        Expect(t, ref i, '(');
        var r = new List<object?>();
        Skip(t, ref i);
        if (t[i] == ')') { i++; return r; }
        while (true)
        {
            Skip(t, ref i);
            r.Add(Value(t, ref i));
            Skip(t, ref i);
            if (t[i] == ',') { i++; continue; }
            Expect(t, ref i, ')');
            return r;
        }
    }

    private static object? Value(string t, ref int i)
    {
        char c = t[i];
        if (c == '$' || c == '*') { i++; return null; }
        if (c == '#') { i++; return new StepRef((int)Integer(t, ref i)); }
        if (c == '(') return List(t, ref i);
        if (c == '\'') return Str(t, ref i);
        if (c == '.')
        {
            int end = t.IndexOf('.', i + 1);
            if (end < 0) throw Bad(t, i);
            var e = new Enum(t[(i + 1)..end].ToUpperInvariant());
            i = end + 1;
            return e;
        }
        if (c == '"')
        {
            // a binary: kept as its text
            int end = t.IndexOf('"', i + 1);
            var b = t[(i + 1)..end]; i = end + 1; return b;
        }
        if (char.IsDigit(c) || c == '-' || c == '+')
        {
            int s = i;
            while (i < t.Length && (char.IsDigit(t[i]) || t[i] is '.' or '-' or '+' or 'E' or 'e')) i++;
            return double.Parse(t.AsSpan(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        if (char.IsLetter(c))
        {
            string type = Word(t, ref i);
            Skip(t, ref i);
            return new Typed(type, List(t, ref i));
        }
        throw Bad(t, i);
    }

    /// <summary>A string: '' is a quote; \X2\hhhh…\X0\ (and \X\hh) characters beyond ASCII; \\ a backslash.</summary>
    private static string Str(string t, ref int i)
    {
        var sb = new StringBuilder();
        i++;
        while (true)
        {
            if (i >= t.Length) throw Bad(t, i);
            char c = t[i];
            if (c == '\'')
            {
                if (i + 1 < t.Length && t[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                i++;
                return sb.ToString();
            }
            if (c == '\\' && t.AsSpan(i).StartsWith("\\X2\\"))
            {
                int end = t.IndexOf("\\X0\\", i + 4, StringComparison.Ordinal);
                if (end < 0) throw Bad(t, i);
                var hex = t[(i + 4)..end];
                for (int k = 0; k + 3 < hex.Length; k += 4) sb.Append((char)Convert.ToInt32(hex.Substring(k, 4), 16));
                i = end + 4;
                continue;
            }
            if (c == '\\' && t.AsSpan(i).StartsWith("\\X\\") && i + 5 <= t.Length)
            {
                sb.Append((char)Convert.ToInt32(t.Substring(i + 3, 2), 16));
                i += 5;
                continue;
            }
            if (c == '\\' && i + 1 < t.Length && t[i + 1] == '\\') { sb.Append('\\'); i += 2; continue; }
            sb.Append(c);
            i++;
        }
    }
}
