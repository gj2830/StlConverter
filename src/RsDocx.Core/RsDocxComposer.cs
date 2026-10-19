using System.Xml.Linq;
using RsDocx.Core.Package;
using RsDocx.Core.SpaceClaim;
using RsDocx.Core.Xt;

namespace RsDocx.Core;

/// <summary>A part of a design (what DesignSpark makes a component of: a PartDef): its name (null: "Component n"), its
/// bodies (indices of the bodies given to <see cref="RsDocxComposer.Compose(IReadOnlyList{Brep.BrepBody}, RsDocxPackage?, IReadOnlyList{string?}?, IReadOnlyList{RsDocxPart}?)"/>,
/// in the part's own coordinates) and the parts it uses.</summary>
public sealed record RsDocxPart(string? Name, IReadOnlyList<int> Bodies, IReadOnlyList<RsDocxUse> Uses);

/// <summary>A part used in another (a ComponentDef): which (an index in the parts) and where its coordinates stand in the
/// other one (a 4×4 matrix by rows, the move in the last column; null: where they are).</summary>
public sealed record RsDocxUse(int Part, IReadOnlyList<double>? Trans = null);

/// <summary>
/// Builds a complete .rsdocx from polyhedra, using an existing DesignSpark file as template for everything
/// except geometry. Based on test round 1 (NOTES.md): checksums, facets.bin, thumbnail and EncryptedData are left out.
/// </summary>
public static class RsDocxComposer
{
    public const string GeometryPart = "SpaceClaim/Geometry/part1bodies.x_b";
    public const string DocumentPart = "SpaceClaim/document.xml";
    public const string RenderListPart = "SpaceClaim/Graphics/renderlist.xml";

    /// <summary>The built-in template: an empty DesignSpark document (SpaceClaim/template.rsdocx, embedded in this
    /// assembly).</summary>
    public static RsDocxPackage DefaultTemplate()
    {
        using var s = typeof(RsDocxComposer).Assembly.GetManifestResourceStream("template.rsdocx")
            ?? throw new InvalidOperationException("Embedded template.rsdocx missing");
        return RsDocxPackage.Open(s);
    }

    private const string ContentTypesPart = "[Content_Types].xml";
    private const string DocumentRelsPart = "SpaceClaim/_rels/document.xml.rels";

    /// <summary>
    /// A template without geometry (an empty document) gets what DesignSpark writes once there is a body: the content
    /// type of .x_b parts and the relationship from document.xml to the geometry, typed with the document's guid (the
    /// first in versions.xml) and the part's moniker.
    /// </summary>
    private static void AddGeometryRelationship(RsDocxPackage pkg, XDocument doc) =>
        AddGeometryRelationship(pkg, ((string)doc.Root!.Descendants(DocumentBuilder.Nom + "PartDef").Single().Attribute("Id")!).Split(':')[1], GeometryPart);

    /// <summary>As <see cref="AddGeometryRelationship(RsDocxPackage, XDocument)"/>, for the geometry part <paramref name="target"/>
    /// of part number <paramref name="part"/> (the number after the colon of its PartDef's id).</summary>
    private static void AddGeometryRelationship(RsDocxPackage pkg, string part, string target)
    {
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        var types = pkg.GetXml(ContentTypesPart);
        if (!types.Root!.Elements(ct + "Default").Any(e => string.Equals((string?)e.Attribute("Extension"), "x_b", StringComparison.OrdinalIgnoreCase)))
        {
            types.Root.Add(new XElement(ct + "Default", new XAttribute("Extension", "x_b"), new XAttribute("ContentType", "application/binary ; modeler=Parasolid ; version=34.1.212")));
            pkg.SetXml(ContentTypesPart, types);
        }
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        var rels = pkg.GetXml(DocumentRelsPart);
        if (rels.Root!.Elements(rel + "Relationship").Any(e => (string?)e.Attribute("Target") == "/" + target)) return;
        string guid = DocumentGuid(pkg);
        rels.Root.Add(new XElement(rel + "Relationship",
            new XAttribute("Type", $"http://www.spaceclaim.com/relationships/internal/partBodyGeometry#{guid}:{part}"),
            new XAttribute("Target", "/" + target),
            new XAttribute("Id", "R" + Guid.NewGuid().ToString("N")[..16])));
        pkg.SetXml(DocumentRelsPart, rels);
    }

    /// <summary>The document's guid (the first in versions.xml): the part before the colon of a reference to a part.</summary>
    private static string DocumentGuid(RsDocxPackage pkg) => pkg.GetXml("SpaceClaim/versions.xml").Descendants().First(e => e.Name.LocalName == "guid").Value.Trim();

    public static RsDocxPackage Compose(IReadOnlyList<Polyhedron> bodies, RsDocxPackage? template = null) =>
        Compose(bodies.Select(Brep.BrepShapes.FromPolyhedron).ToList(), template);

    /// <summary>Points whose box encloses the body: vertices plus the extents of spheres and circular edges
    /// (a full sphere or a ring edge has no vertices).</summary>
    private static IEnumerable<Stl.Vec3> BoundingPoints(Brep.BrepBody b)
    {
        foreach (var v in b.Vertices) yield return v;
        foreach (var f in b.Faces)
            if (f.Surface is Brep.BrepSphere sp)
                foreach (var d in Axes()) yield return sp.Centre + d * sp.Radius;
            else if (f.Surface is Brep.BrepTorus to)
                foreach (var d in Axes()) yield return to.Centre + d * (to.MajorRadius + to.MinorRadius);
        foreach (var e in b.Edges)
            if (e.Curve is Brep.BrepCircle c)
                foreach (var d in Axes()) yield return c.Centre + d * c.Radius;
            else if (e.Curve is Brep.BrepBSpline bs)
                foreach (var q in bs.ControlPoints) yield return q; // (a B-spline lies within its control polygon: a helix winds far from its two ends)
        static IEnumerable<Stl.Vec3> Axes() =>
            [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
    }

    /// <summary>
    /// A design of parts (DesignSpark's components, see <see cref="RsDocxPart"/>; the first is the design itself): each
    /// part its PartDef with its bodies, its own geometry part and its caption, each use of a part a ComponentDef; a part
    /// used more than once is one component placed more than once. The renderlist gets an item per place a part stands
    /// (as DesignSpark wrote testdata/blokken_gelinkt.rsdocx). Without parts (or one, using none): as
    /// <see cref="Compose(IReadOnlyList{Brep.BrepBody}, RsDocxPackage?, IReadOnlyList{string?}?)"/>.
    /// </summary>
    public static RsDocxPackage Compose(IReadOnlyList<Brep.BrepBody> bodies, RsDocxPackage? template, IReadOnlyList<string?>? names, IReadOnlyList<RsDocxPart>? parts)
    {
        if (parts == null || parts.Count == 0 || parts.Count == 1 && parts[0].Uses.Count == 0) return Compose(bodies, template, names);
        var pkg = template ?? DefaultTemplate();
        var alloc = new MonikerAllocator(0, 5000);
        var ids = bodies.Select((p, k) => alloc.ForBody(p.Faces.Count, p.Edges.Count, p.Sheet, names != null && k < names.Count ? names[k] : null)).ToList();
        var doc = pkg.GetXml(DocumentPart);
        var rootPart = doc.Root!.Descendants(DocumentBuilder.Nom + "PartDef").Single();
        var partIds = parts.Select((_, k) => k == 0 ? (string)rootPart.Attribute("Id")! : alloc.Take()).ToList();
        var useIds = parts.Select(p => p.Uses.Select(_ => alloc.Take()).ToList()).ToList();
        var captionIds = parts.Select((_, k) => k == 0 ? null : alloc.Take()).ToList();
        // each part's bodies its own geometry
        for (int k = 0; k < parts.Count; k++)
        {
            if (parts[k].Bodies.Count == 0) continue;
            var named = parts[k].Bodies.Select(b => (bodies[b], new Brep.BrepNames { Body = ids[b].Body, Faces = ids[b].Faces, Edges = ids[b].Edges })).ToList();
            var xt = new Brep.XtBrepBuilder().Build(named);
            var errors = XtValidator.Validate(xt);
            if (errors.Count > 0) throw new InvalidOperationException("Generated geometry failed validation:\n" + string.Join("\n", errors.Take(20)));
            string path = k == 0 ? GeometryPart : $"SpaceClaim/Geometry/part{k + 1}bodies.x_b";
            pkg.SetPart(path, XtWriter.Write(xt), stored: false);
            AddGeometryRelationship(pkg, partIds[k].Split(':')[1], path);
        }
        // the bodies (and their captions) as in a single part, then each moved into its own
        DocumentBuilder.ReplaceBodies(doc, ids, alloc.Next, removeEncryptedData: true);
        DocumentBuilder.AddParts(doc, DocumentGuid(pkg), parts.Select((p, k) => new DocumentBuilder.PartIds(
            partIds[k], captionIds[k], p.Name ?? $"Component{k}", p.Bodies.Select(b => ids[b].Body).ToList(),
            p.Uses.Select((u, i) => (useIds[k][i], partIds[u.Part], Matrix(u.Trans))).ToList())).ToList(), alloc.Next);
        pkg.SetXml(DocumentPart, doc);

        if (pkg.Contains(RenderListPart))
        {
            // every place a part stands (its uses first, as DesignSpark lists them), and the box round all its bodies
            var items = new List<(string Part, string Path, double[] At, IReadOnlyList<(string Id, bool Sheet)> Bodies)>();
            var all = new List<(double X, double Y, double Z)>();
            void Walk(int k, List<string> path, double[] at)
            {
                for (int i = 0; i < parts[k].Uses.Count; i++)
                    Walk(parts[k].Uses[i].Part, [.. path, useIds[k][i]], Times(at, Matrix(parts[k].Uses[i].Trans)));
                items.Add((partIds[k], string.Join(",", path), at, parts[k].Bodies.Select(b => (ids[b].Body, ids[b].Sheet)).ToList()));
                foreach (int b in parts[k].Bodies)
                    foreach (var v in BoundingPoints(bodies[b]))
                        all.Add((at[0] * v.X + at[1] * v.Y + at[2] * v.Z + at[3], at[4] * v.X + at[5] * v.Y + at[6] * v.Z + at[7], at[8] * v.X + at[9] * v.Y + at[10] * v.Z + at[11]));
            }
            Walk(0, [], Matrix(null));
            if (all.Count == 0) all.Add((0, 0, 0));
            var rl = pkg.GetXml(RenderListPart);
            DocumentBuilder.WriteRenderList(rl, items, (all.Min(v => v.X), all.Min(v => v.Y), all.Min(v => v.Z), all.Max(v => v.X), all.Max(v => v.Y), all.Max(v => v.Z)));
            pkg.SetXml(RenderListPart, rl);
        }

        foreach (var part in new[] { "SpaceClaim/checksums.bin", "SpaceClaim/_rels/checksums.bin.rels" }) pkg.RemovePart(part);
        foreach (var part in new[] { "SpaceClaim/Graphics/facets.bin", "docProps/thumbnail.png" })
        {
            pkg.RemovePart(part);
            pkg.RemoveRelationshipsTo(part);
        }
        return pkg;
    }

    /// <summary>A placement as a 4×4 matrix by rows (null: none).</summary>
    private static double[] Matrix(IReadOnlyList<double>? m) => m is { Count: 16 } ? [.. m] : [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// <summary>Two placements one after the other: <paramref name="b"/> inside <paramref name="a"/>.</summary>
    private static double[] Times(double[] a, double[] b)
    {
        var r = new double[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 4; k++) r[i * 4 + j] += a[i * 4 + k] * b[k * 4 + j];
        return r;
    }

    /// <param name="names">A name per body (null: DesignSpark's "Vaste vorm" or "Oppervlak").</param>
    public static RsDocxPackage Compose(IReadOnlyList<Brep.BrepBody> bodies, RsDocxPackage? template = null, IReadOnlyList<string?>? names = null)
    {
        var pkg = template ?? DefaultTemplate();

        var alloc = new MonikerAllocator(0, 5000);
        var ids = new List<BodyIds>();
        var named = new List<(Brep.BrepBody, Brep.BrepNames)>();
        for (int k = 0; k < bodies.Count; k++)
        {
            var p = bodies[k];
            var bi = alloc.ForBody(p.Faces.Count, p.Edges.Count, p.Sheet, names != null && k < names.Count ? names[k] : null);
            ids.Add(bi);
            named.Add((p, new Brep.BrepNames { Body = bi.Body, Faces = bi.Faces, Edges = bi.Edges }));
        }
        var xt = new Brep.XtBrepBuilder().Build(named);
        var errors = XtValidator.Validate(xt);
        if (errors.Count > 0) throw new InvalidOperationException("Generated geometry failed validation:\n" + string.Join("\n", errors.Take(20)));
        var bytes = XtWriter.Write(xt);
        // deflated, although DesignSpark itself stores this part uncompressed: DS opens it either way (tested with a 1.5 M
        // facet model), and a mesh of single facets shrinks 2-3x (99 MB -> 43 MB)
        pkg.SetPart(GeometryPart, bytes, stored: false);

        var doc = pkg.GetXml(DocumentPart);
        AddGeometryRelationship(pkg, doc);
        DocumentBuilder.ReplaceBodies(doc, ids, alloc.Next, removeEncryptedData: true);
        pkg.SetXml(DocumentPart, doc);

        if (pkg.Contains(RenderListPart))
        {
            var all = bodies.SelectMany(BoundingPoints).Select(v => (v.X, v.Y, v.Z)).ToList();
            var rl = pkg.GetXml(RenderListPart);
            DocumentBuilder.UpdateRenderList(rl, ids.Select(i => (i.Body, i.Sheet)).ToList(),
                (all.Min(v => v.X), all.Min(v => v.Y), all.Min(v => v.Z), all.Max(v => v.X), all.Max(v => v.Y), all.Max(v => v.Z)));
            pkg.SetXml(RenderListPart, rl);
        }

        foreach (var part in new[] { "SpaceClaim/checksums.bin", "SpaceClaim/_rels/checksums.bin.rels" }) pkg.RemovePart(part);
        foreach (var part in new[] { "SpaceClaim/Graphics/facets.bin", "docProps/thumbnail.png" })
        {
            pkg.RemovePart(part);
            pkg.RemoveRelationshipsTo(part);
        }
        return pkg;
    }
}
