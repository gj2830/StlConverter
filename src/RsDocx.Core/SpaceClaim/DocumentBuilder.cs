using System.Globalization;
using System.Xml.Linq;

namespace RsDocx.Core.SpaceClaim;

/// <summary>Monikers for one body as they appear in document.xml and in the x_b name attributes.</summary>
public sealed record BodyIds(string Body, string BodyUpdateState, IReadOnlyList<string> Faces, IReadOnlyList<string> Edges, string Caption);

/// <summary>Hands out monikers "&lt;prefix&gt;:&lt;n&gt;" from a counter.</summary>
public sealed class MonikerAllocator(int prefix, int start)
{
    public int Next { get; private set; } = start;
    public string Take() => $"{prefix}:{Next++}";

    public BodyIds ForBody(int faceCount, int edgeCount) => new(
        Take(), Take(),
        Enumerable.Range(0, faceCount).Select(_ => Take()).ToList(),
        Enumerable.Range(0, edgeCount).Select(_ => Take()).ToList(),
        Take());
}

/// <summary>
/// Edits a SpaceClaim document.xml taken from a template: replaces all bodies of the (single) PartDef.
/// Element layout copied from testdata/kubus.rsdocx; a template without bodies (an empty document) gets the settings
/// DesignSpark gave the bodies there.
/// </summary>
public static class DocumentBuilder
{
    public static readonly XNamespace Core = "urn:core";
    public static readonly XNamespace Nom = "urn:nom";
    public static readonly XNamespace Presentation = "urn:presentation";

    public static void ReplaceBodies(XDocument doc, IReadOnlyList<BodyIds> bodies, int nextId, bool removeEncryptedData = false)
    {
        var root = doc.Root!;
        var partDef = root.Descendants(Nom + "PartDef").Single();
        var oldBodies = partDef.Elements(Nom + "NominalBodyDef").ToList();
        var proto = oldBodies.Count > 0 ? oldBodies[0] : DefaultBody(root);
        var oldIds = oldBodies.Select(b => (string)b.Attribute("Id")!).ToHashSet();

        // Insert new bodies where the old ones were (after everything else in an empty part).
        var anchor = oldBodies.Count > 0 ? oldBodies[0].PreviousNode : partDef.LastNode;
        foreach (var b in oldBodies) b.Remove();
        var newElems = bodies.Select(b => BodyElement(proto, b)).ToList();
        if (anchor is null) partDef.AddFirst(newElems); else anchor.AddAfterSelf(newElems);

        // Replacement history refers to objects that no longer exist.
        root.Element(Core + "originalToReplacements_Keys")?.RemoveNodes();
        root.Element(Core + "originalToReplacements_Values")?.RemoveNodes();

        // Captions ("Vaste vorm") per body. CaptionDef lives in urn:presentation (RootCaptionDef in urn:nom).
        var pres = root.Element(Presentation + "PresentationDef")!;
        var oldCaptions = pres.Elements(Presentation + "CaptionDef").Where(c => oldIds.Contains((string)c.Element(Presentation + "subjectId")!)).ToList();
        var protoCaption = oldCaptions.FirstOrDefault() ?? DefaultCaption;
        foreach (var c in oldCaptions) c.Remove();
        foreach (var b in bodies)
            {
                var c = new XElement(protoCaption);
                c.SetAttributeValue("Id", b.Caption);
                // (its own moniker, as DesignSpark writes a caption; an empty one keeps DesignSpark from opening the file)
                c.Element(Presentation + "updateState")!.Value = b.Caption;
                c.Element(Presentation + "subjectId")!.Value = b.Body;
                pres.Add(c);
            }

        root.Element(Core + "nextId")!.Value = nextId.ToString(CultureInfo.InvariantCulture);

        if (removeEncryptedData)
            root.Elements().Where(e => e.Name.LocalName == "EncryptedData").Remove();
    }

    /// <summary>A body's settings as DesignSpark wrote them in testdata/kubus.rsdocx: on the document's first layer, a
    /// solid, plastic, opaque, no material, not locked, medium gloss.</summary>
    private static XElement DefaultBody(XElement root)
    {
        string layer = (string?)root.Descendants().FirstOrDefault(e => e.Name.LocalName == "LayerDef")?.Attribute("Id") ?? "0:9";
        return new XElement(Nom + "NominalBodyDef",
            new XElement(Nom + "updateState"),
            new XElement(Nom + "layerId", layer),
            new XElement(Nom + "type", "Solid"),
            new XElement(Nom + "renderingStyle", "Plastic"),
            new XElement(Nom + "fillStyle", "Opaque"),
            new XElement(Nom + "materialId", "0:0"),
            new XElement(Nom + "modificationLock", "None"),
            new XElement(Nom + "finishStyle", "MediumGloss"));
    }

    /// <summary>A body's caption as DesignSpark wrote it in testdata/kubus.rsdocx ("Vaste vorm": a solid, in Dutch).</summary>
    private static readonly XElement DefaultCaption = new(Presentation + "CaptionDef",
        new XElement(Presentation + "updateState"),
        new XElement(Presentation + "subjectId"),
        new XElement(Presentation + "name", "Vaste vorm"),
        new XElement(Presentation + "description", ""),
        new XElement(Presentation + "type", new XAttribute("version", "82"), "Mutable"));

    private static XElement BodyElement(XElement proto, BodyIds ids)
    {
        var e = new XElement(Nom + "NominalBodyDef", new XAttribute("Id", ids.Body));
        e.Add(new XElement(Nom + "updateState", ids.BodyUpdateState));
        // copy the body settings (layer, type Solid, rendering, ...) from the template body
        foreach (var child in proto.Elements())
        {
            string n = child.Name.LocalName;
            if (n is "updateState" or "NominalFaceDef" or "NominalEdgeDef") continue;
            e.Add(new XElement(child));
        }
        foreach (var f in ids.Faces)
            e.Add(new XElement(Nom + "NominalFaceDef", new XAttribute("Id", f), new XElement(Nom + "updateState", f)));
        foreach (var ed in ids.Edges)
            e.Add(new XElement(Nom + "NominalEdgeDef", new XAttribute("Id", ed), new XElement(Nom + "updateState", ed),
                new XElement(Nom + "isReversed", "False")));
        return e;
    }

    /// <summary>Rewrites renderlist.xml for the given bodies and bounding box (metres).</summary>
    public static void UpdateRenderList(XDocument renderList, IReadOnlyList<string> bodyIds, (double x0, double y0, double z0, double x1, double y1, double z1) box)
    {
        string boxStr = string.Join(",", new[] { box.x0, box.y0, box.z0, box.x1, box.y1, box.z1 }.Select(d => d.ToString("R", CultureInfo.InvariantCulture)));
        foreach (var view in renderList.Root!.Elements("View"))
        {
            view.SetAttributeValue("Box", boxStr);
            foreach (var item in view.Elements("Item"))
            {
                // (an empty document has no body to copy: then as DesignSpark wrote them in testdata/kubus.rsdocx)
                var proto = item.Elements("Body").FirstOrDefault() ?? new XElement("Body", new XAttribute("Visible", "1"), new XAttribute("Color", "-7360625"),
                    new XAttribute("FillStyle", "1"), new XAttribute("BodyType", "0"), new XAttribute("RenderingStyle", "1"), new XAttribute("FinishStyle", "3"));
                item.Elements("Body").Remove();
                foreach (var id in bodyIds)
                {
                    var b = new XElement(proto);
                    b.SetAttributeValue("Id", id);
                    item.Add(b);
                }
                item.SetAttributeValue("Bodies", bodyIds.Count.ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}
