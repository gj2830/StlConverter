using System.Xml.Linq;
using RsDocx.Core.Package;
using RsDocx.Core.SpaceClaim;
using RsDocx.Core.Xt;

namespace RsDocx.Core;

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
    private static void AddGeometryRelationship(RsDocxPackage pkg, XDocument doc)
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
        if (rels.Root!.Elements(rel + "Relationship").Any(e => (string?)e.Attribute("Target") == "/" + GeometryPart)) return;
        string guid = pkg.GetXml("SpaceClaim/versions.xml").Descendants().First(e => e.Name.LocalName == "guid").Value.Trim();
        string part = ((string)doc.Root!.Descendants(DocumentBuilder.Nom + "PartDef").Single().Attribute("Id")!).Split(':')[1];
        rels.Root.Add(new XElement(rel + "Relationship",
            new XAttribute("Type", $"http://www.spaceclaim.com/relationships/internal/partBodyGeometry#{guid}:{part}"),
            new XAttribute("Target", "/" + GeometryPart),
            new XAttribute("Id", "R" + Guid.NewGuid().ToString("N")[..16])));
        pkg.SetXml(DocumentRelsPart, rels);
    }

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

    public static RsDocxPackage Compose(IReadOnlyList<Brep.BrepBody> bodies, RsDocxPackage? template = null)
    {
        var pkg = template ?? DefaultTemplate();

        var alloc = new MonikerAllocator(0, 5000);
        var ids = new List<BodyIds>();
        var named = new List<(Brep.BrepBody, Brep.BrepNames)>();
        foreach (var p in bodies)
        {
            var bi = alloc.ForBody(p.Faces.Count, p.Edges.Count);
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
            DocumentBuilder.UpdateRenderList(rl, ids.Select(i => i.Body).ToList(),
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
