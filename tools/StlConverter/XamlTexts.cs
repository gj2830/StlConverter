using System.Windows;
using System.Windows.Controls;

namespace StlConverter;

/// <summary>
/// The texts of the window as written in its XAML (Dutch) and in English: every tooltip, label and button text is
/// looked up here when the window opens and when the language is switched.
/// </summary>
internal static class XamlTexts
{
    private static readonly Dictionary<string, string> En = new()
    {
        ["Van driehoekjes naar échte vlakken, cilinders, bollen en donuts."] = "From triangles to real faces, cylinders, spheres and doughnuts.",
        ["📂  BESTAND"] = "📂  FILE",
        ["Open STL / 3MF…"] = "Open STL / 3MF…",
        ["Eenheid in het bestand"] = "Unit in the file",
        ["…of sleep een bestand op het venster."] = "…or drag a file onto the window.",
        ["Opschonen tonen of verbergen"] = "Show or hide clean-up",
        ["🧹  OPSCHONEN"] = "🧹  CLEAN UP",
        ["🕳️  gaten dichten"] = "🕳️  close holes",
        ["Open randen dichtmaken; een plat gat wordt plat gevuld."] = "Close open edges; a flat hole is filled flat.",
        ["Dubbele driehoekjes en driehoekjes op een rand die al twee buren heeft weghalen (het gat dat dat geeft wordt gedicht)."] =
            "Remove duplicate triangles and triangles on an edge that already has two neighbours (the hole this leaves is closed).",
        ["✂️  snippers weg"] = "✂️  remove scraps",
        ["Losse stukjes kleiner dan 1 % van het grootste stuk weghalen."] = "Remove loose pieces smaller than 1 % of the largest piece.",
        ["Vereenvoudigen tot"] = "Simplify to",
        ["Minder driehoekjes (voor heel grote meshes). De hoekpunten blijven originele punten. Let op: te ver vereenvoudigen maakt rondingen moeilijker te herkennen."] =
            "Fewer triangles (for very large meshes). The vertices stay original points. Mind: simplifying too far makes roundings harder to recognise.",
        ["🧹  Opschonen"] = "🧹  Clean up",
        ["↩  origineel"] = "↩  original",
        ["Terug naar de mesh zoals hij uit het bestand kwam"] = "Back to the mesh as it came from the file",
        ["De mesh zoals je hem nu ziet (opgeschoond of niet) opslaan als STL, bijvoorbeeld voor MeshLab of je slicer"] =
            "Save the mesh as you see it now (cleaned up or not) as STL, for example for MeshLab or your slicer",
        ["🔍  WAT MOET IK HERKENNEN?"] = "🔍  WHAT SHOULD I RECOGNISE?",
        ["🥫  Cilinders"] = "🥫  Cylinders",
        ["⚽  Bollen"] = "⚽  Spheres",
        ["🍩  Tori"] = "🍩  Tori",
        ["🍦  Kegels"] = "🍦  Cones",
        ["Schuine wanden rond een as (lossingshoek, verzinking, afschuining); vanaf 1° schuinte"] =
            "Slanting walls round an axis (draft angle, countersink, chamfer); from 1° slant",
        ["Nieuwe groep na ✨"] = "New group after ✨",
        ["🔒  rest vasthouden"] = "🔒  keep the rest",
        ["Aan: maak of verwijder je een groep nadat alles herkend is, dan wordt alleen het stuk rond die groep opnieuw bekeken; de rest blijft precies zoals hij was (snel). Bij ✨ blijven de vlakke vlakken staan. Uit: steeds alles opnieuw."] =
            "On: when you make or remove a group after everything is recognised, only the part round that group is looked at again; the rest stays exactly as it was (fast). With ✨ the flat faces stay. Off: everything again each time.",
        ["🖌️  ZELF AANWIJZEN"] = "🖌️  POINT IT OUT YOURSELF",
        ["👆  bekijken"] = "👆  look",
        ["🖌️  penseel"] = "🖌️  brush",
        ["🪄  toverstaf"] = "🪄  magic wand",
        ["Penseel"] = "Brush",
        ["Straal van het penseel in de eenheid van het bestand (0 = één driehoekje)"] = "Radius of the brush in the unit of the file (0 = one triangle)",
        ["Toverstaf stopt bij een knik van"] = "Magic wand stops at a bend of",
        ["De toverstaf kruipt over randen tussen driehoekjes zolang de knik kleiner is dan deze hoek"] =
            "The magic wand creeps over edges between triangles as long as the bend is smaller than this angle",
        ["🧠  dubbelklik automatisch"] = "🧠  automatic double-click",
        ["Aan: dubbelklik kiest zelf: een plat vlak, of op een ronding de stroken rond dezelfde as. Uit: het schuifje hieronder bepaalt het."] =
            "On: double-click chooses itself: a flat face, or on a rounding the strips round the same axis. Off: the slider below decides.",
        ["Dubbelklik: helling mag afwijken"] = "Double-click: slope may differ",
        ["Dubbelklik selecteert de aansluitende driehoekjes waarvan de helling hooguit zoveel graden afwijkt van het aangeklikte driehoekje. 0° = alleen het platte vlak; voor een ronding bijvoorbeeld 45° of 90°."] =
            "Double-click selects the adjoining triangles whose slope differs at most this many degrees from the clicked triangle. 0° = only the flat face; for a rounding for example 45° or 90°.",
        ["Dubbelklik = erbij selecteren · Ctrl = weghalen · Shift-klik = herkend vlak · Shift-dubbelklik = vlak openen (driehoekjes aanklikken) · Esc = wissen"] =
            "Double-click = add to selection · Ctrl = remove · Shift-click = recognised face · Shift-double-click = open face (click triangles) · Esc = clear",
        ["Schuifjes tonen of verbergen"] = "Show or hide sliders",
        ["🎚️  FIJNAFSTELLING"] = "🎚️  FINE TUNING",
        ["↺ standaard"] = "↺ default",
        ["Vlakken samenvoegen tot"] = "Merge faces up to",
        ["Maximale hoek tussen driehoeken die één plat vlak worden"] = "Largest angle between triangles that become one flat face",
        ["Hoe precies moet het passen?"] = "How precisely must it fit?",
        ["Hoekpunten moeten zó dicht op een cilinder/bol/torus liggen om die te herkennen. Ruimer = meer herkend, maar ook meer valse vormen."] =
            "Vertices must lie this close to a cylinder/sphere/torus to recognise it. Looser = more recognised, but also more false shapes.",
        ["Bijpakken binnen"] = "Take in within",
        ["Driehoeken die zo dicht bij een herkend gebogen vlak liggen gaan erin op (hun hoekpunten worden er exact op gelegd)"] =
            "Triangles this close to a recognised curved face become part of it (their vertices are put exactly on it)",
        ["Rakend maken tot"] = "Make tangent up to",
        ["Vlakken die binnen deze hoek langs een cilinder lopen worden exact rakend gemaakt"] = "Faces running along a cylinder within this angle are made exactly tangent",
        ["Grootste straal"] = "Largest radius",
        ["Bijna vlakke facetten passen op enorme stralen; grotere stralen worden niet geaccepteerd"] = "Almost flat facets fit enormous radii; larger radii are not accepted",
        ["Geduld (rondes)"] = "Patience (rounds)",
        ["Hoe vaak conflicten opgelost mogen worden (meer = trager, soms beter)"] = "How often conflicts may be resolved (more = slower, sometimes better)",
        ["✨  Herken vormen"] = "✨  Recognise shapes",
        ["Zoekt alles: vlakken, cilinders, bollen, tori en kegels (kan bij grote modellen minuten duren)"] =
            "Looks for everything: planes, cylinders, spheres, tori and cones (may take minutes for large models)",
        ["▭  Platte vlakken"] = "▭  Flat faces",
        ["Alleen platte stukken samenvoegen tot vlakken (snel). Jouw groepen blijven; de rest blijft driehoekjes tot je er zelf een groep van maakt. Daarna blijven platte stukken ook bij nieuwe groepen samengevoegd."] =
            "Only merge flat pieces into faces (fast). Your groups stay; the rest stays triangles until you make a group of it yourself. After that flat pieces stay merged with new groups too.",
        ["🎛️  Autotune"] = "🎛️  Autotune",
        ["Probeert een reeks instellingen naast elkaar en kiest die met de minste vlakken"] = "Tries a range of settings side by side and chooses the one with the fewest faces",
        ["💾  Opslaan"] = "💾  Save",
        ["Opslaan als .rsdocx voor DesignSpark"] = "Save as .rsdocx for DesignSpark",
        ["✅  Overnemen in PushPullMe"] = "✅  Take over in PushPullMe",
        ["Het herkende model (of, zonder herkenning, de platte vlakken) gaat naar PushPullMe; dit venster sluit"] =
            "The recognised model (or, without recognition, the flat faces) goes to PushPullMe; this window closes",
        ["Nog niets herkend."] = "Nothing recognised yet.",
        ["Tekst kun je selecteren en kopiëren (Ctrl+C)"] = "You can select and copy text (Ctrl+C)",
        ["Sleep een STL of 3MF hierheen"] = "Drag an STL or 3MF here",
        ["rechtsklik-slepen = draaien · wiel = zoomen · klik = vlak bekijken of selecteren"] = "right-drag = orbit · wheel = zoom · click = look at or select a face",
        ["✏️  randen"] = "✏️  edges",
        ["🔭  passend"] = "🔭  fit",
        ["🧊  iso"] = "🧊  iso",
        ["⬇️  boven"] = "⬇️  top",
        ["👀  voor"] = "👀  front",
        ["↔️  zij"] = "↔️  side",
        ["▭  vlak"] = "▭  plane",
        ["🥫  cilinder"] = "🥫  cylinder",
        ["⚽  bol"] = "⚽  sphere",
        ["🍩  torus"] = "🍩  torus",
        ["🍦  kegel"] = "🍦  cone",
        ["📐  plat"] = "📐  flat",
        ["Maak hier hoe dan ook één plat vlak van: het best passende vlak, en alle punten (ook op de rand) worden erop gezet. De buurvlakken lopen mee. Voor een vlak dat bedoeld plat is maar bulten heeft."] =
            "Make one flat face of this anyway: the best fitting plane, with all points (on the edge too) put on it. The neighbouring faces follow. For a face that is meant to be flat but has bumps.",
        ["✨  kies zelf"] = "✨  choose itself",
        ["De eenvoudigste vorm die past: vlak, cilinder, bol, torus of kegel"] = "The simplest shape that fits: plane, cylinder, sphere, torus or cone",
        ["✂️  los"] = "✂️  loose",
        ["Koppel deze driehoekjes los van het vlak: ze blijven losse driehoekjes (en blijven geselecteerd, zodat je ze met ➕ bij een ander vlak kunt voegen)"] =
            "Detach these triangles from the face: they stay loose triangles (and stay selected, so you can add them to another face with ➕)",
        ["➕  bij vlak"] = "➕  to face",
        ["Voeg deze driehoekjes toe aan een ander herkend vlak: klik daarna op dat vlak"] = "Add these triangles to another recognised face: then click that face",
        ["Selectie wissen (Esc)"] = "Clear the selection (Esc)",
        ["📏 meten:"] = "📏 measure:",
        ["🔩  schroefdraad"] = "🔩  screw thread",
        ["Meet de schroefdraad in de selectie: maat, binnen/buiten, links/rechts, diameters, spoed, flankhoek en de as. Het model verandert niet."] =
            "Measures the screw thread in the selection: size, internal/external, left/right, diameters, pitch, flank angle and the axis. The model does not change.",
        ["⏹  Stop"] = "⏹  Stop",
        ["Stoppen; het vorige resultaat blijft staan"] = "Stop; the previous result stays",
        ["🔍  open: driehoekjes tonen"] = "🔍  open: show triangles",
        ["Laat de driehoekjes van dit vlak zien, om er losse uit te halen (ook: Shift-dubbelklik op het vlak)"] =
            "Shows the triangles of this face, to take loose ones out (also: Shift-double-click on the face)",
        ["Sluiten"] = "Close",
        ["Selecteren en kopiëren kan (Ctrl+C)"] = "You can select and copy (Ctrl+C)",
        ["🔩  vervang door gladde kern + echte draad"] = "🔩  replace by a smooth core + real thread",
        ["De draaddriehoekjes worden een gladde cilinder op de kerndiameter; de draad wordt een apart, exact lichaam (zoals een helix-sweep in DesignSpark). Bij opslaan komen beide mee: voeg ze in DesignSpark samen (Combine)."] =
            "The thread triangles become a smooth cylinder at the core diameter; the thread becomes a separate, exact body (like a helix sweep in DesignSpark). Both are saved: combine them in DesignSpark (Combine).",
        ["Vlak sluiten (Esc)"] = "Close the face (Esc)",
        ["Klik driehoekjes aan (nog eens klikken = eraf, penseel werkt ook). Kies dan ✂️ los, of ➕ bij vlak om ze bij een ander vlak te voegen."] =
            "Click triangles (click again = off, the brush works too). Then choose ✂️ loose, or ➕ to face to add them to another face.",
        ["☑️  hele vlak"] = "☑️  whole face",
        ["Alle driehoekjes van dit vlak selecteren"] = "Select all triangles of this face",
        ["☐  niets"] = "☐  none",
        ["Alle driehoekjes van dit vlak deselecteren"] = "Deselect all triangles of this face",
    };

    private static readonly Dictionary<string, string> Nl = En.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>The window's texts in the language chosen, all through it (from either language).</summary>
    public static void Apply(DependencyObject root) => Walk(root);

    private static string Tr(string s) => (Lang.English ? En : Nl).TryGetValue(s, out var t) ? t : s;

    private static void Walk(object node)
    {
        if (node is FrameworkElement fe && fe.ToolTip is string tip) fe.ToolTip = Tr(tip);
        switch (node)
        {
            case TextBlock tb when tb.Text is { Length: > 0 }: tb.Text = Tr(tb.Text); break;
            case ContentControl cc when cc.Content is string text: cc.Content = Tr(text); break;
        }
        if (node is DependencyObject d)
            foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
    }
}
