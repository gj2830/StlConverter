using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RsDocx.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using Point = Avalonia.Point;
using Path = System.IO.Path;
using Vector = Avalonia.Vector;

namespace StlConverter.Desktop;

/// <summary>
/// Everything shown is drawn from the triangles as they are in the file ("source triangles"), so selections work before
/// and after recognition; after recognition each source triangle also knows the face it ended up in.
/// </summary>
public partial class ConverterWindow : Window
{
    /// <summary>🌐: the other language, at once (the status text changes with the next step), and kept.</summary>
    private void Language_Click(object? sender, RoutedEventArgs e)
    {
        Lang.Switch();
        ApplyTexts();
    }

    /// <summary>The window's texts in the language chosen (tooltips, labels, button texts; from either language).</summary>
    private void ApplyTexts()
    {
        foreach (var node in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(this).Prepend(this))
        {
            if (node is Control c && ToolTip.GetTip(c) is string tip) ToolTip.SetTip(c, Texts.Tr(tip));
            switch (node)
            {
                case TextBlock tb when tb.Text is { Length: > 0 } t: tb.Text = Texts.Tr(t); break;
                case ContentControl cc when cc.Content is string text: cc.Content = Texts.Tr(text); break;
            }
        }
        LanguageButton.Content = Lang.English ? "🌐  EN" : "🌐  NL";
    }

    /// <summary>A text in the language chosen (see <see cref="Lang"/>).</summary>
    private static string T(string nl, string en) => Lang.T(nl, en);

    private string? _path;
    private StlFile? _stl;
    private List<BrepBody>? _bodies;
    private MeshLabels? _labels;

    // per source triangle
    private Vector3D[] _normal = [];
    private int[][] _neighbours = [];
    private (int body, int face)[] _faceOf = [];
    private double _overlayLift; // overlays float this far above the surface (file units)

    private LineLayer? _edges;

    /// <summary>The modifier keys with the last mouse or key event.</summary>
    private KeyModifiers _mods;

    private readonly HashSet<int> _selection = new();
    private readonly List<(List<int> tris, FaceKind kind)> _groups = new();

    /// <summary>Groups made flat on purpose ("📐 plat"): their vertices are moved onto the plane however far they are off.</summary>
    private readonly HashSet<List<int>> _forcedGroups = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<List<int>, string> _groupFailed = new(ReferenceEqualityComparer.Instance);

    /// <summary>Groups detached on purpose ("✂️ los"): their triangles stay single facets (they are not passed as hints).</summary>
    private readonly HashSet<List<int>> _looseGroups = new(ReferenceEqualityComparer.Instance);

    /// <summary>The user's groups in the order they went into the last hints (the loose ones left out).</summary>
    private List<(List<int> tris, FaceKind kind)> _hintGroups = new();
    private bool _painting;

    private static readonly (FaceKind kind, Color color, string emoji)[] Kinds =
    [
        (FaceKind.Plane, Color.FromRgb(0xD6, 0xDB, 0xEA), "▭"),
        (FaceKind.Facet, Color.FromRgb(0xFF, 0x8A, 0x65), "🔺"),
        (FaceKind.Cylinder, Color.FromRgb(0x4D, 0xA3, 0xFF), "🥫"),
        (FaceKind.Sphere, Color.FromRgb(0x3D, 0xD6, 0x9C), "⚽"),
        (FaceKind.Torus, Color.FromRgb(0xB0, 0x7C, 0xFF), "🍩"),
        (FaceKind.Cone, Color.FromRgb(0xFF, 0xB3, 0x47), "🍦"),
    ];

    /// <summary>A kind of face as shown, in the language chosen.</summary>
    private static string KindName(FaceKind kind) => kind switch
    {
        FaceKind.Plane => T("Vlak", "Plane"),
        FaceKind.Facet => T("Losse driehoek", "Loose triangle"),
        FaceKind.Cylinder => T("Cilinder", "Cylinder"),
        FaceKind.Sphere => T("Bol", "Sphere"),
        FaceKind.Torus => T("Torus", "Torus"),
        FaceKind.Cone => T("Kegel", "Cone"),
        _ => kind.ToString(),
    };
    private static readonly Color RawColor = Color.FromRgb(0xD8, 0xDC, 0xE0);
    private static readonly Color FadedColor = Color.FromRgb(0xC8, 0xCE, 0xD8);
    private static readonly Color SelectionColor = Color.FromRgb(0xFF, 0xC1, 0x07);
    private static readonly Color GroupColor = Color.FromRgb(0x14, 0xB8, 0xA6);

    public ConverterWindow() : this(null, embedded: false) { }

    /// <summary>
    /// Opened by another program (PushPullMe) with a mesh file: with a button that hands the result over
    /// (<see cref="Result"/>, the window closing with true) instead of saving it.
    /// </summary>
    public ConverterWindow(string path) : this(path, embedded: true) { }

    /// <summary>Opened by another program (see <see cref="ConverterWindow(string)"/>).</summary>
    private readonly bool _embedded;

    /// <summary>What was handed over (embedded only), once <c>Overnemen</c> is clicked.</summary>
    public ConverterResult? Result { get; private set; }

    /// <summary>The window, with a file to open (or none); <paramref name="embedded"/>: opened by another program.</summary>
    public ConverterWindow(string? path, bool embedded)
    {
        InitializeComponent();
        _embedded = embedded;
        TakeButton.IsVisible = embedded;
        ApplyTexts();
        UpdateSliderTexts();
        Wire();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            _mods = e.KeyModifiers;
            if (e.Key == Key.D && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)) { DumpGroups(); return; }
            if (e.Key != Key.Escape) return;
            if (_addPick) { _addPick = false; RebuildOverlay(); }
            else if (_selection.Count > 0) ClearSelection();
            else CloseFace();
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => _mods = e.KeyModifiers, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, Window_Drop);
        if (path != null) Opened += (_, _) => Load(path);
    }

    /// <summary>The buttons, sliders and the view's mouse to what they do.</summary>
    private void Wire()
    {
        LanguageButton.Click += Language_Click;
        OpenButton.Click += Open_Click;
        CleanButton.Click += Clean_Click; UndoCleanButton.Click += UndoClean_Click; SaveStlButton.Click += SaveStl_Click;
        DefaultsButton.Click += Defaults_Click;
        RecognizeButton.Click += Recognize_Click; PlanesButton.Click += Planes_Click;
        TuneButton.Click += Autotune_Click; SaveButton.Click += Save_Click; TakeButton.Click += Take_Click;
        StopButton.Click += Stop_Click;
        EdgesBox.Click += Edges_Click;
        foreach (var b in ViewChips.Children.OfType<Button>()) b.Click += ViewButton_Click;
        foreach (var b in ShapeButtons.Children.OfType<Button>().Concat(OpenFaceButtons.Children.OfType<Button>()).Where(b => b.Tag is string)) b.Click += MakeGroup_Click;
        AddToButton.Click += AddTo_Click; ClearSelectionButton.Click += ClearSelection_Click;
        ThreadButton.Click += Thread_Click; CloseThreadButton.Click += CloseThread_Click; ReplaceThreadButton.Click += ReplaceThread_Click;
        OpenFaceButton.Click += OpenPickedFace_Click; CloseFaceButton.Click += CloseFace_Click;
        SelectOpenFaceButton.Click += SelectOpenFace_Click; DeselectOpenFaceButton.Click += DeselectOpenFace_Click;
        foreach (var sl in new[] { AngleSlider, FitSlider, JoinSlider, TangentSlider, RadiusSlider, RoundsSlider, BrushSlider, WandSlider, DoubleSlider })
            sl.ValueChanged += (_, _) => UpdateSliderTexts();
        SimplifySlider.ValueChanged += (_, _) => SimplifySlider_Changed();
        UnitBox.SelectionChanged += (_, _) => UpdateSliderTexts();
        // the left button selects (the view itself turns, pans and zooms with the others)
        View.PointerPressed += (_, e) =>
        {
            _mods = e.KeyModifiers;
            var pt = e.GetCurrentPoint(View);
            if (pt.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed) View_LeftDown(pt.Position, e.ClickCount, e);
        };
        View.PointerMoved += (_, e) => { _mods = e.KeyModifiers; View_Move(e.GetPosition(View), e); };
        View.PointerReleased += (_, e) =>
        {
            _mods = e.KeyModifiers;
            if (e.GetCurrentPoint(View).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased) View_LeftUp(e.GetPosition(View), e);
        };
    }

    // ------------------------------------------------------------------ options

    private double AngleDeg => Math.Pow(10, AngleSlider.Value);
    private double FitMicron => Math.Pow(10, FitSlider.Value);
    private double JoinMicron => JoinSlider.Value;
    private double MaxRadiusMm => Math.Pow(10, RadiusSlider.Value);

    private void UpdateSliderTexts()
    {
        if (AngleText == null || RoundsText == null || BrushText == null || WandText == null || DoubleText == null) return;
        var c = CultureInfo.CurrentCulture;
        AngleText.Text = string.Format(c, "{0:0.####}°", AngleDeg);
        FitText.Text = string.Format(c, "{0:0.###} µm", FitMicron);
        JoinText.Text = string.Format(c, "{0:0.#} µm", JoinMicron);
        TangentText.Text = string.Format(c, "{0:0.#}°", TangentSlider.Value);
        RadiusText.Text = string.Format(c, "{0:0} mm", MaxRadiusMm);
        RoundsText.Text = string.Format(c, "{0:0}×", RoundsSlider.Value);
        BrushText.Text = BrushSlider.Value < 0.05 ? T("1 driehoekje", "1 triangle") : string.Format(c, "{0:0.0} {1}", BrushSlider.Value, UnitName);
        WandText.Text = string.Format(c, "{0:0}°", WandSlider.Value);
        DoubleText.Text = DoubleSlider.Value < 0.5 ? T("0° · alleen vlak", "0° · flat only") : string.Format(c, "{0:0}°", DoubleSlider.Value);
    }

    private string UnitName => (string)((ComboBoxItem)UnitBox.SelectedItem!).Content!;

    private void Defaults_Click(object? sender, RoutedEventArgs e)
    {
        AngleSlider.Value = -2; FitSlider.Value = -1; JoinSlider.Value = 10; TangentSlider.Value = 3; RadiusSlider.Value = 3; RoundsSlider.Value = 300;
        CylBox.IsChecked = SphBox.IsChecked = TorBox.IsChecked = true;
    }

    private MeshOptions Options() => new()
    {
        Scale = double.Parse((string)((ComboBoxItem)UnitBox.SelectedItem!).Tag!, CultureInfo.InvariantCulture),
        MergeCoplanar = true,
        AngleToleranceDeg = AngleDeg,
        RecognizeCylinders = CylBox.IsChecked == true,
        RecognizeSpheres = SphBox.IsChecked == true,
        RecognizeTori = TorBox.IsChecked == true,
        RecognizeCones = ConeBox.IsChecked == true,
        CylinderFitTolerance = FitMicron * 1e-6,
        CylinderJoinTolerance = JoinMicron * 1e-6,
        AbsorbTolerance = JoinMicron * 1e-6,
        TangentToleranceDeg = TangentSlider.Value,
        MaxCurvedRadius = MaxRadiusMm / 1000,
        RecognitionRounds = (int)RoundsSlider.Value,
        Hints = BuildHints(),
        KeepFacets = _looseGroups.Count == 0 ? null : _looseGroups.SelectMany(t => t).ToHashSet(),
    };

    /// <summary>Number of soft hints (kept planes) in front of the user's groups in the last <see cref="BuildHints"/>.</summary>
    private int _softCount;

    /// <summary>Triangles of the groups just made or removed: only around them is the shown result redone.</summary>
    private HashSet<int>? _changed;

    /// <summary>The triangles that may change in a local redo (see <see cref="BuildHints"/>); null = all.</summary>
    private HashSet<int>? _free;

    /// <summary>
    /// The user's groups, preceded by soft hints from the shown result.
    /// <para>After a group was made or removed (and "keep the rest" is on): every face of the shown result as a soft hint
    /// (with its exact surface), except the faces next to the changed triangles; only those, the changed triangles and
    /// the groups are converted again (<see cref="_free"/>), the rest of the model is left alone. Fast, and nothing far
    /// away can change.</para>
    /// <para>Otherwise (✨): the planes of the shown result as soft hints, everything else recognised again.</para>
    /// </summary>
    private List<ShapeHint> BuildHints()
    {
        var hints = new List<ShapeHint>();
        _free = null;
        if (KeepPlanesBox.IsChecked == true && _labels != null && _fullMode && _changed != null)
        {
            // the faces next to the changed triangles (two rings of neighbours: also the faces that only touch in a corner)
            var ring = new HashSet<int>(_changed);
            for (int k = 0; k < 2; k++) foreach (int t in ring.ToList()) ring.UnionWith(_neighbours[t]);
            var touched = new HashSet<(int, int)>(ring.Select(t => _faceOf[t]));
            var inGroup = new HashSet<int>(_groups.SelectMany(g => g.tris));
            _free = new HashSet<int>(_changed);
            foreach (var face in _labels.Triangles.GroupBy(t => (t.Body, t.Face)))
            {
                var info = _labels.Faces[face.Key.Body][face.Key.Face];
                var all = face.Select(t => t.Source).ToList();
                if (touched.Contains(face.Key)) { _free.UnionWith(all); continue; }
                if (info.Kind == FaceKind.Facet) continue; // facets stay facets
                // (the user's groups come below; the triangles their face took in besides them are kept here, on the
                // group's exact surface, or they would end up frozen as single facets: bigbox, a 605-facet tube piece
                // taken into a 30-facet group fell apart at the next change elsewhere)
                var tris = all.Where(i => !inGroup.Contains(i)).ToList();
                if (tris.Count > 0) hints.Add(new ShapeHint(tris, info.Kind, Soft: true, Surface: info));
            }
            _free.UnionWith(inGroup);
        }
        else if (KeepPlanesBox.IsChecked == true && _labels != null)
        {
            var inGroup = new HashSet<int>(_groups.SelectMany(g => g.tris));
            foreach (var face in _labels.Triangles.GroupBy(t => (t.Body, t.Face)))
            {
                var info = _labels.Faces[face.Key.Body][face.Key.Face];
                if (info.Kind != FaceKind.Plane || info.Group >= _softCount) continue; // planes only, not the user's own groups
                var tris = face.Select(t => t.Source).Where(i => !inGroup.Contains(i)).ToList();
                if (tris.Count >= 2) hints.Add(new ShapeHint(tris, FaceKind.Plane, Soft: true, Surface: info));
            }
        }
        _softCount = hints.Count;
        _hintGroups = _groups.Where(g => !_looseGroups.Contains(g.tris)).ToList();
        hints.AddRange(_hintGroups.Select(g => new ShapeHint(g.tris, g.kind, Force: _forcedGroups.Contains(g.tris))));
        return hints;
    }

    // ------------------------------------------------------------------ session log (RSUI_LOG=<file>)

    private static readonly string? SessionLog = Environment.GetEnvironmentVariable("RSUI_LOG");

    /// <summary>When started with RSUI_LOG: every step (open, group, result) appended to that file, with the groups and
    /// the faces, so someone helping can follow a session without seeing the screen.</summary>
    private void Log(string what)
    {
        if (SessionLog == null) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {what}");
            if (_stl != null && _labels != null)
            {
                sb.AppendLine("  " + string.Join(" ", Enumerable.Range(0, _faceOf.Length).GroupBy(i => InfoOf(i)?.Kind).Select(g => $"{g.Key}:{g.Count()}")));
                sb.AppendLine("  curved: " + string.Join(" ", Enumerable.Range(0, _faceOf.Length).Where(i => InfoOf(i) is { Kind: not (FaceKind.Plane or FaceKind.Facet) })
                    .GroupBy(i => _faceOf[i]).Select(g => (info: InfoOf(g.First())!, n: g.Count())).OrderByDescending(x => x.n).Take(30)
                    .Select(x => string.Format(CultureInfo.InvariantCulture, "{0}{1:0.###}{2}:{3}", x.info.Kind, x.info.Radius * 1000, x.info.Group >= 0 ? "(g)" : "", x.n))));
            }
            foreach (var (tris, kind) in _groups)
                sb.AppendLine($"  group {_groups.FindIndex(g => ReferenceEquals(g.tris, tris)) + 1}: {kind}{(_forcedGroups.Contains(tris) ? " force" : "")}{(_looseGroups.Contains(tris) ? " loose" : "")} {tris.Count} tris"
                    + $" {(GroupApplied((tris, kind)) ? "applied" : _groupFailed.TryGetValue(tris, out var why) ? "FAILED " + why : "pending")}: {string.Join(",", tris)}");
            if (_selection.Count > 0)
            {
                sb.AppendLine($"  selection {_selection.Count}: {string.Join(",", _selection.Order().Take(2000))}");
                // (the whole selection, to replay it: <log>.selection.txt)
                File.WriteAllText(SessionLog + ".selection.txt", string.Join(",", _selection.Order()));
            }
            File.AppendAllText(SessionLog, sb.ToString());
        }
        catch (IOException) { }
    }

    // ------------------------------------------------------------------ groups dump (for reproducing a session)

    /// <summary>
    /// Ctrl+Shift+D: the user's groups and the settings to a text file next to the mesh ("&lt;name&gt;.groepen.txt"), so a
    /// session can be replayed exactly (test aid RSUI_GROUPS). One group per line: kind, flags, triangle numbers.
    /// </summary>
    private void DumpGroups()
    {
        if (_stl == null || _path == null) return;
        var c = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Format(c, T("# {0} · {1} driehoekjes{2}", "# {0} · {1} triangles{2}"), Path.GetFileName(_path), _stl.Triangles.Count, _original != null ? T(" · opgeschoond", " · cleaned up") : ""));
        sb.AppendLine(string.Format(c, "settings unit={0} angle={1} fit={2} join={3} tangent={4} radius={5} rounds={6} cyl={7} sph={8} tor={9} cone={10} keep={11} full={12}",
            UnitBox.SelectedIndex, AngleSlider.Value, FitSlider.Value, JoinSlider.Value, TangentSlider.Value, RadiusSlider.Value, RoundsSlider.Value,
            CylBox.IsChecked == true ? 1 : 0, SphBox.IsChecked == true ? 1 : 0, TorBox.IsChecked == true ? 1 : 0, ConeBox.IsChecked == true ? 1 : 0,
            KeepPlanesBox.IsChecked == true ? 1 : 0, _fullMode ? 1 : 0));
        foreach (var (tris, kind) in _groups)
            sb.AppendLine($"group {kind} force={(_forcedGroups.Contains(tris) ? 1 : 0)} loose={(_looseGroups.Contains(tris) ? 1 : 0)}: {string.Join(",", tris)}");
        if (_selection.Count > 0) sb.AppendLine($"selection: {string.Join(",", _selection.Order())}");
        string file = Path.Combine(Path.GetDirectoryName(_path)!, Path.GetFileNameWithoutExtension(_path) + ".groepen.txt");
        try
        {
            File.WriteAllText(file, sb.ToString());
            ShowToast(T("📝 Groepen bewaard", "📝 Groups saved"), file);
        }
        catch (IOException ex) { ShowToast(T("😕 Niet bewaard", "😕 Not saved"), ex.Message); }
    }

    /// <summary>Test aid: settings and groups from a <see cref="DumpGroups"/> file (the selection is not restored).</summary>
    private void LoadGroups(string file)
    {
        var c = CultureInfo.InvariantCulture;
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.StartsWith("settings "))
            {
                var kv = line[9..].Split(' ').Select(x => x.Split('=')).ToDictionary(x => x[0], x => double.Parse(x[1], c));
                UnitBox.SelectedIndex = (int)kv["unit"]; AngleSlider.Value = kv["angle"]; FitSlider.Value = kv["fit"]; JoinSlider.Value = kv["join"];
                TangentSlider.Value = kv["tangent"]; RadiusSlider.Value = kv["radius"]; RoundsSlider.Value = kv["rounds"];
                CylBox.IsChecked = kv["cyl"] == 1; SphBox.IsChecked = kv["sph"] == 1; TorBox.IsChecked = kv["tor"] == 1; ConeBox.IsChecked = kv["cone"] == 1;
                KeepPlanesBox.IsChecked = kv["keep"] == 1;
            }
            else if (line.StartsWith("group "))
            {
                var head = line[..line.IndexOf(':')].Split(' ');
                var tris = line[(line.IndexOf(':') + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
                _groups.Add((tris, Enum.Parse<FaceKind>(head[1])));
                if (head.Contains("force=1")) _forcedGroups.Add(tris);
                if (head.Contains("loose=1")) _looseGroups.Add(tris);
            }
        }
    }

    // ------------------------------------------------------------------ file

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Mesh openen", "Open a mesh"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Mesh") { Patterns = ["*.stl", "*.3mf"] }, new FilePickerFileType(T("Alle bestanden", "All files")) { Patterns = ["*"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) Load(path);
    }


    private void Window_Drop(object? sender, DragEventArgs e)
    {
        if (OpenButton.IsEnabled && e.DataTransfer.TryGetFiles() is { Length: > 0 } files && files[0].TryGetLocalPath() is { } path) Load(path);
    }

    private void Load(string path)
    {
        try
        {
            _stl = StlReader.Read(path);
        }
        catch (Exception ex) when (ex is StlFormatException or IOException)
        {
            _ = ShowMessage(T("Kan bestand niet lezen", "Cannot read the file"), ex.Message);
            return;
        }
        _path = path; _original = null;
        if (SessionLog != null) try { File.AppendAllText(SessionLog, $"[{DateTime.Now:HH:mm:ss}] open {path} ({_stl.Triangles.Count} triangles)\n"); } catch (IOException) { }
        if (_stl.Header.Contains("unit=MM", StringComparison.OrdinalIgnoreCase)) UnitBox.SelectedIndex = 0;
        ShowNewMesh();
        if (_openEdges + _nonManifoldEdges > 0)
            ShowToast(T("⚠️ De mesh is niet dicht", "⚠️ The mesh is not closed"), T($"{_openEdges:N0} open randen, {_nonManifoldEdges:N0} randen met meer dan twee driehoekjes. Herkennen lukt pas na 🧹 Opschonen.", $"{_openEdges:N0} open edges, {_nonManifoldEdges:N0} edges with more than two triangles. Recognising only works after 🧹 Clean up."));
        else if (_pinched > 0)
            ShowToast(T("⚠️ Delen raken elkaar in een punt", "⚠️ Parts touch in a point"), T($"Op {_pinched:N0} {(_pinched == 1 ? "plek raken" : "plekken raken")} twee stukken oppervlak elkaar in één punt. Herkennen lukt pas na 🧹 Opschonen.", $"In {_pinched:N0} {(_pinched == 1 ? "place" : "places")} two pieces of surface touch in a single point. Recognising only works after 🧹 Clean up."));
        // test aid (see Snapshot): RSUI_RECOGNIZE=1 recognises first
        if (Environment.GetEnvironmentVariable("RSUI_SNAPSHOT") is { Length: > 0 } shot)
        {
            // RSUI_DOUBLECLICK=triangle index: as a double click on it
            if (Environment.GetEnvironmentVariable("RSUI_DOUBLECLICK") is { Length: > 0 } dc)
            {
                if (Environment.GetEnvironmentVariable("RSUI_DOUBLEDEG") is { Length: > 0 } deg) DoubleSlider.Value = double.Parse(deg, CultureInfo.InvariantCulture);
                AutoDoubleBox.IsChecked = Environment.GetEnvironmentVariable("RSUI_AUTODOUBLE") == "1";
                var (tris, flat) = DoubleClickPatch(int.Parse(dc));
                _selection.UnionWith(tris);
                RebuildOverlay();
                File.WriteAllText(shot + ".txt", $"{tris.Count} {(flat ? "flat" : "curved")}");
                _ = Snapshot(shot);
                return;
            }
            // RSUI_RECOGNIZE=1: ✨ first; RSUI_GROUP=Cylinder:12,13,14: then that group (as if selected and chosen)
            // triangles per kind of face, and the curved faces (test log)
            string KindCounts() => string.Join(" ", Enumerable.Range(0, _faceOf.Length).GroupBy(i => InfoOf(i)?.Kind).Select(g => $"{g.Key}:{g.Count()}"))
                + " | " + string.Join(" ", Enumerable.Range(0, _faceOf.Length).Where(i => InfoOf(i) is { Kind: FaceKind.Cylinder or FaceKind.Cone }).GroupBy(i => _faceOf[i])
                    .Select(g => (info: InfoOf(g.First())!, n: g.Count())).OrderByDescending(x => x.n).Select(x => $"{x.info.Kind}{x.info.Radius * 1000:0.##}:{x.n}"));
            var log = new System.Text.StringBuilder();
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try { await RunTestSteps(); }
                catch (Exception ex)
                {
                    // (an unexpected error: written down, and the window closes instead of waiting forever)
                    log.AppendLine("EXCEPTION " + ex);
                    File.WriteAllText(shot + ".txt", log.ToString());
                    Close();
                }
            });
            async Task RunTestSteps()
            {
                if (Environment.GetEnvironmentVariable("RSUI_CLEAN") == "1")
                {
                    if (Environment.GetEnvironmentVariable("RSUI_SIMPLIFY") is { Length: > 0 } pct) SimplifySlider.Value = double.Parse(pct, CultureInfo.InvariantCulture);
                    var sw = Stopwatch.StartNew();
                    await Clean();
                    log.AppendLine($"🧹 {sw.ElapsedMilliseconds} ms: {StatusText.Text} | {ToastTitle.Text} {ToastText.Text}");
                }
                string? groupsFile = Environment.GetEnvironmentVariable("RSUI_GROUPS");
                bool groupsAfter = Environment.GetEnvironmentVariable("RSUI_GROUPSAFTER") == "1";
                if (groupsFile != null && !groupsAfter && Environment.GetEnvironmentVariable("RSUI_GROUPSSTEP") != "1") { LoadGroups(groupsFile); UpdateGroupList(); log.AppendLine($"groups: {_groups.Count}"); }
                // RSUI_PLANES=1: "▭ Platte vlakken" first
                if (Environment.GetEnvironmentVariable("RSUI_PLANES") == "1")
                {
                    var swp = Stopwatch.StartNew();
                    await Recognize(PlanesButton);
                    log.AppendLine($"▭ {swp.ElapsedMilliseconds} ms: {StatusText.Text} | {KindCounts()}");
                }
                if (Environment.GetEnvironmentVariable("RSUI_RECOGNIZE") == "1")
                {
                    // RSUI_STOPAFTER=ms: snapshot of the busy panel (<png>.busy.png), then as a click on Stop
                    if (Environment.GetEnvironmentVariable("RSUI_STOPAFTER") is { Length: > 0 } stopMs)
                    {
                        var stopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(int.Parse(stopMs)) };
                        stopTimer.Tick += async (_, _) => { stopTimer.Stop(); await Snapshot(shot + ".busy.png", close: false); Stop_Click(this, new RoutedEventArgs()); };
                        stopTimer.Start();
                    }
                    var sw = Stopwatch.StartNew();
                    await Recognize(RecognizeButton);
                    log.AppendLine($"✨ {sw.ElapsedMilliseconds} ms: {StatusText.Text} | {KindCounts()}");
                }
                if (Environment.GetEnvironmentVariable("RSUI_GROUP") is { Length: > 0 } grp && grp.Split(':', 2) is [var k, var list])
                {
                    var sw = Stopwatch.StartNew();
                    // "f18454": the connected piece of loose facets around that triangle
                    // "@file": the numbers from a file; RSUI_SKIPKIND=Cylinder: leave out triangles of that kind
                    var items = list.StartsWith('@') ? File.ReadAllText(list[1..]).Trim().Split(',') : list.Split(',');
                    if (Environment.GetEnvironmentVariable("RSUI_SKIPKIND") is { Length: > 0 } skip)
                        items = items.Where(x => x.StartsWith('f') || InfoOf(int.Parse(x))?.Kind.ToString() != skip).ToArray();
                    foreach (var item in items)
                    {
                        if (!item.StartsWith('f')) { _selection.Add(int.Parse(item)); continue; }
                        var piece = new List<int> { int.Parse(item[1..]) };
                        var inPiece = piece.ToHashSet();
                        for (int qi = 0; qi < piece.Count; qi++)
                            foreach (int nb in _neighbours[piece[qi]])
                                if (InfoOf(nb) is { Kind: FaceKind.Facet } && inPiece.Add(nb)) piece.Add(nb);
                        _selection.UnionWith(piece);
                    }
                    log.AppendLine($"selected: {string.Join(",", _selection.Order())}");
                    MakeGroup(Enum.Parse<FaceKind>(k));
                    if (_running != null) await _running;
                    if (Environment.GetEnvironmentVariable("RSUI_DUMP") == "1") DumpGroups();
                    log.AppendLine($"group {sw.ElapsedMilliseconds} ms: {StatusText.Text} | {ToastTitle.Text} {ToastText.Text} | {KindCounts()}");
                }
                // RSUI_GROUPSSTEP=1: after ✨, the groups of the file one by one, each as if selected and chosen
                if (groupsFile != null && Environment.GetEnvironmentVariable("RSUI_GROUPSSTEP") == "1")
                {
                    foreach (var line in File.ReadAllLines(groupsFile).Where(l => l.StartsWith("group ")))
                    {
                        var sw = Stopwatch.StartNew();
                        var head = line[..line.IndexOf(':')];
                        _selection.UnionWith(line[(line.IndexOf(':') + 1)..].Split(',', StringSplitOptions.TrimEntries).Select(int.Parse));
                        MakeGroup(Enum.Parse<FaceKind>(head.Split(' ')[1]), force: head.Contains("force=1"), loose: head.Contains("loose=1"));
                        if (_running != null) await _running;
                        log.AppendLine($"step {head}: {sw.ElapsedMilliseconds} ms: {StatusText.Text} | {KindCounts()}");
                    }
                }
                if (groupsFile != null && groupsAfter)
                {
                    var sw = Stopwatch.StartNew();
                    LoadGroups(groupsFile);
                    _changed = _groups.SelectMany(g => g.tris).ToHashSet();
                    await Recognize(this);
                    log.AppendLine($"groups after: {sw.ElapsedMilliseconds} ms: {StatusText.Text} | {KindCounts()}");
                }
                // RSUI_THREAD=<numbers or @file>: those triangles selected and measured as a thread
                if (Environment.GetEnvironmentVariable("RSUI_THREAD") is { Length: > 0 } th)
                {
                    var items = th.StartsWith('@') ? File.ReadAllText(th[1..]).Trim().Split(',') : th.Split(',');
                    _selection.UnionWith(items.Select(int.Parse));
                    RebuildOverlay();
                    await MeasureThread();
                    log.AppendLine($"thread: {ThreadTitle.Text} | {ThreadText.Text}");
                    // RSUI_REPLACE=1: then "vervang door gladde kern + echte draad"
                    if (Environment.GetEnvironmentVariable("RSUI_REPLACE") == "1")
                    {
                        var swr = Stopwatch.StartNew();
                        await ReplaceThread();
                        // RSUI_UNDO=1: and ↩ again
                        if (Environment.GetEnvironmentVariable("RSUI_UNDO") == "1")
                        {
                            log.AppendLine($"after replace: {_stl!.Triangles.Count} triangles, {_groups.Count} groups, extra bodies {_extraBodies.Count}, undo points {_threadUndos.Count}");
                            await UndoThread();
                            log.AppendLine($"after undo: {_stl!.Triangles.Count} triangles, {_groups.Count} groups ({string.Join(",", _groups.Select(g => $"{g.kind}:{g.tris.Count}"))}), extra bodies {_extraBodies.Count}, undo points {_threadUndos.Count} | {StatusText.Text} | {KindCounts()}");
                        }
                        log.AppendLine($"replace {swr.ElapsedMilliseconds} ms (surgery {_replaceSurgeryMs} ms): {ToastTitle.Text} {ToastText.Text} | {StatusText.Text} | {KindCounts()} | extra bodies {_extraBodies.Count}");
                    }
                }
                // RSUI_FILTER=Facet: the legend filter on that kind
                if (Environment.GetEnvironmentVariable("RSUI_FILTER") is { Length: > 0 } flt)
                {
                    SetLegendFilter(Enum.Parse<FaceKind>(flt));
                    await Task.Delay(800); // (the zoom animates)
                    log.AppendLine($"filter {flt}");
                }
                // RSUI_RECOGNIZE2=1: ✨ again after the group
                if (Environment.GetEnvironmentVariable("RSUI_RECOGNIZE2") == "1")
                {
                    var sw = Stopwatch.StartNew();
                    await Recognize(RecognizeButton);
                    log.AppendLine($"✨2 {sw.ElapsedMilliseconds} ms: {StatusText.Text} | {KindCounts()}");
                }
                // RSUI_OPEN=Cylinder (largest face of that kind) or a triangle: as a double click on it; RSUI_PICK=n: its
                // first n triangles clicked; RSUI_LOOSE=1: ✂️ los; RSUI_ADDTO=Plane (largest plane) or a triangle: ➕ bij vlak
                int Pieces(List<int> tris)
                {
                    var set = tris.ToHashSet(); var seen = new HashSet<int>(); int n = 0;
                    foreach (int t0 in tris)
                    {
                        if (!seen.Add(t0)) continue;
                        n++;
                        var q = new Queue<int>([t0]);
                        while (q.TryDequeue(out int t)) foreach (int nb in _neighbours[t]) if (set.Contains(nb) && seen.Add(nb)) q.Enqueue(nb);
                    }
                    return n;
                }
                int LargestOf(string what) => int.TryParse(what, out int t) ? t
                    : Enumerable.Range(0, _faceOf.Length).Where(i => InfoOf(i)?.Kind == Enum.Parse<FaceKind>(what) && !_selection.Contains(i))
                        .GroupBy(i => _faceOf[i]).MaxBy(g => g.Count())!.First();
                if (Environment.GetEnvironmentVariable("RSUI_OPEN") is { Length: > 0 } open)
                {
                    int seedTri = LargestOf(open); ShowFace(seedTri); OpenFace(seedTri);
                    log.AppendLine($"open: {FaceTitle.Text}");
                }
                if (Environment.GetEnvironmentVariable("RSUI_PICK") is { Length: > 0 } pick)
                {
                    _selection.UnionWith(_openTris.Order().Take(int.Parse(pick)));
                    RebuildOverlay();
                    log.AppendLine($"picked: {string.Join(",", _selection.Order())}");
                }
                if (Environment.GetEnvironmentVariable("RSUI_LOOSE") == "1")
                {
                    MakeGroup(FaceKind.Facet, loose: true);
                    if (_running != null) await _running;
                    log.AppendLine($"loose: {StatusText.Text} | {ToastTitle.Text} {ToastText.Text} | {FaceTitle.Text} | kinds {string.Join(",", _selection.Select(t => InfoOf(t)?.Kind))}");
                }
                if (Environment.GetEnvironmentVariable("RSUI_ADDTO") is { Length: > 0 } addTo)
                {
                    _addPick = true;
                    AddSelectionTo(LargestOf(addTo));
                    if (_running != null) await _running;
                    var last = _groups[^1].tris;
                    log.AppendLine($"addto: {StatusText.Text} | {ToastTitle.Text} {ToastText.Text} | group {last.Count} tris, faces {last.Select(t => _faceOf[t]).Distinct().Count()}, kind {InfoOf(last[0])?.Kind}, pieces {Pieces(last)}");
                }
                // RSUI_FACEDUMP=1: every face of the body with the most curved user-group facets: kind, size, group, z range
                if (Environment.GetEnvironmentVariable("RSUI_FACEDUMP") == "1" && _labels != null)
                {
                    var c = CultureInfo.InvariantCulture;
                    int body = Enumerable.Range(0, _faceOf.Length).Where(i => InfoOf(i) is { Kind: FaceKind.Cylinder, Group: >= 0 } f && f.Group >= _softCount)
                        .GroupBy(i => _faceOf[i].body).MaxBy(g => g.Count())?.Key ?? 0;
                    foreach (var g in Enumerable.Range(0, _faceOf.Length).Where(i => _faceOf[i].body == body).GroupBy(i => _faceOf[i].face).OrderBy(g => g.Key))
                    {
                        var info = InfoOf(g.First())!;
                        if (info.Kind == FaceKind.Facet) continue;
                        var zs = g.SelectMany(i => new[] { _stl!.Triangles[i].A.Z, _stl.Triangles[i].B.Z, _stl.Triangles[i].C.Z }).ToList();
                        var nbFaces = g.SelectMany(i => _neighbours[i]).Select(n => _faceOf[n].face).Where(f => f != g.Key).Distinct().ToList();
                        log.AppendLine(string.Format(c, "face {0}: {1} r{2:0.###} ax({3:0.###},{4:0.###},{5:0.###}) group {6} {7} tris z {8:0.##}..{9:0.##} first {10} nb {11}",
                            g.Key, info.Kind, info.Radius * 1000, info.Axis.X, info.Axis.Y, info.Axis.Z, info.Group >= _softCount ? "USER" : info.Group >= 0 ? "kept" : "-",
                            g.Count(), zs.Min(), zs.Max(), g.First(), string.Join(",", nbFaces.Take(12))));
                    }
                }
                // RSUI_SAVE=<file.rsdocx>: the shown result saved
                if (Environment.GetEnvironmentVariable("RSUI_SAVE") is { Length: > 0 } save && _bodies != null)
                {
                    RsDocxComposer.Compose([.. _bodies, .. _extraBodies]).Save(save);
                    log.AppendLine($"saved {save}");
                }
                // RSUI_TAKE=1 (embedded): the result handed over, as the Overnemen button does
                bool take = _embedded && Environment.GetEnvironmentVariable("RSUI_TAKE") == "1";
                File.WriteAllText(shot + ".txt", log.ToString());
                await Snapshot(shot, close: !take);
                if (take) await Take();
            }
        }
    }

    /// <summary>A new mesh (opened, cleaned or back to the original): everything that refers to triangle numbers starts over.</summary>
    private void ShowNewMesh()
    {
        _bodies = null; _labels = null; _softCount = 0; _fullMode = false; _planeMode = false; _changed = null; _free = null; _legendFilter = null;
        _selection.Clear(); _groups.Clear(); _forcedGroups.Clear(); _looseGroups.Clear(); _hintGroups.Clear(); _repaired.Clear();
        _addPick = false; CloseFace(); ThreadCard.IsVisible = false; _thread = null;
        _extraBodies.Clear(); View.Coil = []; _coilMeshes.Clear(); _threadFilter = false;
        if (!_keepUndo) _threadUndos.Clear();
        BuildTopology();
        SaveButton.IsEnabled = RecognizeButton.IsEnabled = TuneButton.IsEnabled = true;
        FileText.Text = T($"{Path.GetFileName(_path)} · {_stl!.Triangles.Count:N0} driehoekjes ({_stl.Format})", $"{Path.GetFileName(_path)} · {_stl!.Triangles.Count:N0} triangles ({_stl.Format})") + (_original != null ? T(" · opgeschoond 🧹", " · cleaned up 🧹") : "");
        EmptyHint.IsVisible = false;
        PickCard.IsVisible = false;
        StatusText.Text = T("Je ziet de kale mesh: selecteer zelf (dubbelklik, penseel, toverstaf) of klik op ✨ om alles te herkennen.", "You see the bare mesh: select yourself (double-click, brush, magic wand) or click ✨ to recognise everything.");
        Legend.Children.Clear();
        RebuildBase();
        RebuildOverlay();
        UpdateGroupList();
        View.SetView(new Vector3D(0, -200, 100), new Vector3D(-1, 1, -1) * 100, new Vector3D(0, 0, 1));
        View.ZoomExtents();
        UpdateClean();
    }

    // ------------------------------------------------------------------ cleaning

    /// <summary>The mesh as read from the file, while a cleaned one is shown (null otherwise).</summary>
    private StlFile? _original;

    /// <summary>Edges with one triangle (holes) and with more than two, of the shown mesh.</summary>
    private int _openEdges, _nonManifoldEdges;

    /// <summary>Vertices where two parts of the surface touch in one point (see <see cref="MeshConverter.CountPinchedVertices"/>).</summary>
    private int _pinched;

    /// <summary>Anything the conversion refuses: open or over-full edges, parts touching in a point.</summary>
    private bool MeshBroken => _openEdges + _nonManifoldEdges + _pinched > 0;

    private void UpdateClean()
    {
        bool closed = !MeshBroken;
        CleanBadge.Text = _stl == null ? "" : closed ? T("✅ dicht", "✅ closed")
            : _openEdges + _nonManifoldEdges > 0 ? T($"⚠️ {_openEdges + _nonManifoldEdges:N0} foute randen", $"⚠️ {_openEdges + _nonManifoldEdges:N0} bad edges") + (_pinched > 0 ? T($" · {_pinched:N0} raakpunten", $" · {_pinched:N0} touch points") : "")
            : T($"⚠️ {_pinched:N0} {(_pinched == 1 ? "raakpunt" : "raakpunten")}", $"⚠️ {_pinched:N0} {(_pinched == 1 ? "touch point" : "touch points")}");
        ToolTip.SetTip(CleanBadge, closed ? T("Elke rand heeft precies twee driehoekjes en nergens raken delen elkaar in één punt", "Every edge has exactly two triangles and nowhere do parts touch in a single point")
            : T($"{_openEdges:N0} open randen (gaten), {_nonManifoldEdges:N0} randen met meer dan twee driehoekjes, {_pinched:N0} punten waar twee delen elkaar raken", $"{_openEdges:N0} open edges (holes), {_nonManifoldEdges:N0} edges with more than two triangles, {_pinched:N0} points where two parts touch"));
        if (!closed) CleanToggle.IsChecked = true;
        CleanButton.IsEnabled = SaveStlButton.IsEnabled = _stl != null;
        UndoCleanButton.IsEnabled = _original != null;
        SimplifySlider_Changed();
    }

    private int SimplifyTarget() => _stl == null || SimplifySlider.Value >= 100 ? 0 : (int)Math.Round(_stl.Triangles.Count * SimplifySlider.Value / 100);

    private void SimplifySlider_Changed()
    {
        if (SimplifyText == null) return;
        SimplifyText.Text = SimplifySlider.Value >= 100 ? T("uit", "off") : _stl == null ? $"{SimplifySlider.Value:0} %" : $"{SimplifySlider.Value:0} % ≈ {SimplifyTarget():N0}";
    }

    private async void Clean_Click(object? sender, RoutedEventArgs e) => await Clean();

    private async Task Clean()
    {
        if (_stl == null) return;
        var mo = Options();
        var opt = new MeshRepairOptions
        {
            FillHoles = HoleBox.IsChecked == true, FixNonManifold = ManifoldBox.IsChecked == true,
            SmallPieceFraction = PiecesBox.IsChecked == true ? 0.01 : 0, TargetTriangles = SimplifyTarget(),
            WeldTolerance = mo.WeldTolerance / mo.Scale,
        };
        var before = _stl;
        OpenButton.IsEnabled = RecognizeButton.IsEnabled = SaveButton.IsEnabled = CleanButton.IsEnabled = false;
        var sw = Stopwatch.StartNew();
        string[] jokes = [T("Gaten stoppen 🧦", "Plugging holes 🧦"), T("Snippers vegen 🧹", "Sweeping up scraps 🧹"), T("Randen rechtzetten 📐", "Straightening edges 📐"), T("Driehoekjes uitdunnen ✂️", "Thinning out triangles ✂️")];
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => BusyText.Text = $"{jokes[(int)(sw.Elapsed.TotalSeconds / 2.5) % jokes.Length]}   {sw.Elapsed.TotalSeconds:0} s";
        BusyText.Text = jokes[0];
        BusyPanel.IsVisible = true;
        timer.Start();
        try
        {
            var (result, st) = await Task.Run(() => { var r = MeshRepair.Repair(before, opt, out var s); return (r, s); });
            _original ??= before;
            _stl = result;
            SimplifySlider.Value = 100; // done: a second click must not simplify again
            ShowNewMesh();
            if (st.OpenEdges == 0) CleanToggle.IsChecked = false; // closed: make room again
            // show what is new (filled holes, flipped triangles): selected, Esc clears
            int changed = 0;
            if (opt.TargetTriangles == 0)
            {
                static (double, double, double) K(Vec3 p) => (Math.Round(p.X, 6), Math.Round(p.Y, 6), Math.Round(p.Z, 6));
                static string Key(StlTriangle t) => string.Join("|", new[] { K(t.A), K(t.B), K(t.C) }.OrderBy(k => k));
                var old = before.Triangles.Select(Key).ToHashSet();
                for (int i = 0; i < result.Triangles.Count; i++) if (!old.Contains(Key(result.Triangles[i]))) { _repaired.Add(i); changed++; }
                RebuildOverlay();
            }
            string what = string.Join(", ", new[]
            {
                st.HolesFilled > 0 ? T($"{st.HolesFilled} {(st.HolesFilled == 1 ? "gat" : "gaten")} gedicht", $"{st.HolesFilled} {(st.HolesFilled == 1 ? "hole" : "holes")} closed") : null,
                st.DuplicatesRemoved > 0 ? T($"{st.DuplicatesRemoved} dubbele driehoekjes weg", $"{st.DuplicatesRemoved} duplicate triangles removed") : null,
                st.NonManifoldRemoved > 0 ? T($"{st.NonManifoldRemoved} non-manifold driehoekjes weg", $"{st.NonManifoldRemoved} non-manifold triangles removed") : null,
                st.PiecesRemoved > 0 ? T($"{st.PiecesRemoved} {(st.PiecesRemoved == 1 ? "snipper" : "snippers")} weg", $"{st.PiecesRemoved} {(st.PiecesRemoved == 1 ? "scrap" : "scraps")} removed") : null,
                st.FlatFixed > 0 ? T($"{st.FlatFixed} platte driehoekjes rechtgezet", $"{st.FlatFixed} flat triangles straightened") : null,
                opt.TargetTriangles > 0 ? T($"vereenvoudigd {st.InputTriangles:N0} → {st.OutputTriangles:N0}", $"simplified {st.InputTriangles:N0} → {st.OutputTriangles:N0}") : null,
            }.Where(x => x != null));
            ShowToast(st.OpenEdges == 0 ? T("🧹 Opgeschoond, de mesh is dicht", "🧹 Cleaned up, the mesh is closed") : T($"🧹 Opgeschoond, nog {st.OpenEdges} open randen", $"🧹 Cleaned up, {st.OpenEdges} open edges left"),
                (what.Length > 0 ? what : T("Er was niets op te ruimen", "There was nothing to clean up")) + $" · {sw.Elapsed.TotalSeconds:0.0} s");
            StatusText.Text = T($"🧹 Opgeschoond in {sw.Elapsed.TotalSeconds:0.0} s: {(what.Length > 0 ? what : "niets veranderd")}.", $"🧹 Cleaned up in {sw.Elapsed.TotalSeconds:0.0} s: {(what.Length > 0 ? what : "nothing changed")}.")
                + (changed > 0 ? T($"\nDe {changed:N0} nieuwe of rechtgezette driehoekjes zijn roze.", $"\nThe {changed:N0} new or straightened triangles are pink.") : "")
                + T("\n↩ origineel zet het terug. Klik op ✨ om te herkennen.", "\n↩ original puts it back. Click ✨ to recognise.");
        }
        catch (Exception ex) when (ex is MeshException or InvalidOperationException or ArgumentException)
        {
            StatusText.Text = T("😕 Opschonen mislukt: ", "😕 Cleaning up failed: ") + ex.Message;
        }
        finally
        {
            timer.Stop();
            BusyPanel.IsVisible = false;
            OpenButton.IsEnabled = RecognizeButton.IsEnabled = CleanButton.IsEnabled = SaveButton.IsEnabled = true;
        }
    }

    private void UndoClean_Click(object? sender, RoutedEventArgs e)
    {
        if (_original == null) return;
        _stl = _original; _original = null;
        ShowNewMesh();
        ShowToast(T("↩ Origineel terug", "↩ Original back"), T($"{_stl.Triangles.Count:N0} driehoekjes zoals in het bestand", $"{_stl.Triangles.Count:N0} triangles as in the file"));
    }

    /// <summary>Normals and edge neighbours of the source triangles (corners matched by position).</summary>
    private void BuildTopology()
    {
        var tris = _stl!.Triangles;
        int n = tris.Count;
        _normal = new Vector3D[n];
        _faceOf = Enumerable.Repeat((-1, -1), n).ToArray();
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var t in tris)
            foreach (var v in new[] { t.A, t.B, t.C })
            {
                minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X); minY = Math.Min(minY, v.Y);
                maxY = Math.Max(maxY, v.Y); minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
            }
        double diag = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ));
        _overlayLift = diag * 4e-4;
        double q = Math.Max(diag * 1e-9, 1e-12);
        var ids = new Dictionary<(long, long, long), int>();
        int Id(Vec3 v)
        {
            var k = ((long)Math.Round(v.X / q), (long)Math.Round(v.Y / q), (long)Math.Round(v.Z / q));
            if (!ids.TryGetValue(k, out int id)) ids[k] = id = ids.Count;
            return id;
        }
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var t = tris[i];
            var nn = Vector3D.CrossProduct(D(t.B - t.A), D(t.C - t.A));
            if (nn.Length > 0) nn.Normalize();
            _normal[i] = nn;
            int a = Id(t.A), b = Id(t.B), c = Id(t.C);
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = x < y ? (x, y) : (y, x);
                if (!byEdge.TryGetValue(key, out var l)) byEdge[key] = l = new List<int>(2);
                l.Add(i);
            }
        }
        // open and over-full edges exactly as the conversion counts them
        (_openEdges, _nonManifoldEdges) = MeshConverter.CountBadEdges(_stl, Options());
        _pinched = _openEdges + _nonManifoldEdges == 0 ? MeshConverter.CountPinchedVertices(_stl, Options()) : 0;
        var nb = Enumerable.Range(0, n).Select(_ => new List<int>(3)).ToArray();
        foreach (var l in byEdge.Values)
            foreach (int x in l) foreach (int y in l) if (x != y) nb[x].Add(y);
        _neighbours = nb.Select(l => l.ToArray()).ToArray();
    }

    private static Vector3D D(Vec3 v) => new(v.X, v.Y, v.Z);
    private static Point3D P(Vec3 v) => new(v.X, v.Y, v.Z);

    // ------------------------------------------------------------------ recognition

    /// <summary>
    /// False until the user asks to recognise everything (✨): until then only their groups become faces and every other
    /// triangle stays a separate facet ("the rest stays mesh").
    /// </summary>
    private bool _fullMode;

    private async void Recognize_Click(object? sender, RoutedEventArgs e) => await Recognize(sender ?? this);

    /// <summary>
    /// "▭ Platte vlakken": without ✨, flat pieces are merged into planes (no curved surfaces are searched: fast); the
    /// user's groups stay, the rest stays triangles. From then on, converting after a new group merges them too.
    /// </summary>
    private bool _planeMode;

    private async void Planes_Click(object? sender, RoutedEventArgs e) => await Recognize(sender ?? this);

    private Task? _running;

    private async Task Recognize(object sender)
    {
        if (_stl == null) return;
        if (sender == RecognizeButton) _fullMode = true;
        if (sender == RecognizeButton) { _changed = null; _repaired.Clear(); } // ✨: everything again
        if (sender == PlanesButton) { _planeMode = true; _changed = null; }
        var opt = Options();
        if (!_fullMode)
            opt = opt with { OnlyHints = !_planeMode, MergeCoplanar = _planeMode, RecognizePlanes = _planeMode, RecognizeCylinders = false, RecognizeSpheres = false, RecognizeTori = false, RecognizeCones = false };
        else if (_free != null) opt = opt with { FreeTriangles = _free };
        _changed = null;
        var stl = _stl;
        OpenButton.IsEnabled = RecognizeButton.IsEnabled = SaveButton.IsEnabled = false;
        opt = opt with { Cancel = StartCancellable(), Progress = BusyProgress() };
        var sw = Stopwatch.StartNew();
        string[] jokes =
        [
            T("Driehoekjes tellen…", "Counting triangles…"), T("Vlakken strijken…", "Ironing faces…"), T("Cilinders opsporen 🥫", "Tracking down cylinders 🥫"), T("Bollen oppompen ⚽", "Pumping up spheres ⚽"), T("Donuts bakken 🍩", "Baking doughnuts 🍩"),
            T("Naden dichtnaaien 🧵", "Sewing up seams 🧵"), T("Randen rechttrekken 📏", "Straightening edges 📏"), T("Even goed kijken 🧐", "Taking a good look 🧐"), T("Conflicten sussen 🕊️", "Calming conflicts 🕊️"),
        ];
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => BusyText.Text = $"{jokes[(int)(sw.Elapsed.TotalSeconds / 2.5) % jokes.Length]}   {sw.Elapsed.TotalSeconds:0} s";
        BusyText.Text = jokes[0];
        BusyPanel.IsVisible = true;
        timer.Start();
        try
        {
            var (bodies, stats, labels) = await Task.Run(() =>
            {
                var b = MeshConverter.ConvertToBrep(stl, opt, out var st, out var lb);
                return (b, st, lb);
            });
            ShowResult(bodies, stats, labels, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = T($"⏹ Gestopt na {sw.Elapsed.TotalSeconds:0} s.", $"⏹ Stopped after {sw.Elapsed.TotalSeconds:0} s.") + (_labels != null ? T(" Het vorige resultaat blijft staan.", " The previous result stays.") : "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // (any error, also an unexpected one: shown, never a crash of the app)
            bool open = ex.Message.StartsWith("Mesh is not watertight") || ex.Message.Contains("non-manifold vertex");
            StatusText.Text = ex.Message.Contains("non-manifold vertex")
                ? T("😕 Twee stukken oppervlak raken elkaar in één punt; daar kan geen solid van gemaakt worden. Gebruik eerst 🧹 Opschonen (dat splitst zo'n punt).", "😕 Two pieces of surface touch in a single point; no solid can be made of that. Use 🧹 Clean up first (it splits such a point).")
                : open
                ? T($"😕 De mesh is niet dicht ({_openEdges:N0} open randen, {_nonManifoldEdges:N0} randen met meer dan twee driehoekjes). Gebruik eerst 🧹 Opschonen.", $"😕 The mesh is not closed ({_openEdges:N0} open edges, {_nonManifoldEdges:N0} edges with more than two triangles). Use 🧹 Clean up first.")
                : T("😕 Mislukt: ", "😕 Failed: ") + ex.Message;
            if (open) CleanToggle.IsChecked = true;
        }
        finally
        {
            timer.Stop();
            EndCancellable();
            BusyPanel.IsVisible = false;
            OpenButton.IsEnabled = RecognizeButton.IsEnabled = SaveButton.IsEnabled = true;
        }
    }

    private CancellationTokenSource? _cts;

    /// <summary>Shows the stop button; the token stops the conversion.</summary>
    private CancellationToken StartCancellable()
    {
        _cts = new CancellationTokenSource();
        StopButton.IsEnabled = true;
        StopButton.IsVisible = true;
        BusyDetail.Text = "";
        return _cts.Token;
    }

    private void EndCancellable()
    {
        _cts?.Dispose(); _cts = null;
        StopButton.IsVisible = false;
        BusyDetail.Text = "";
    }

    private void Stop_Click(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopButton.IsEnabled = false;
        BusyDetail.Text = T("stoppen…", "stopping…");
    }

    /// <summary>"12 van 37 lichamen klaar · grootste bezig: 4.210 driehoekjes, ronde 3" under the busy text.</summary>
    private IProgress<MeshProgress> BusyProgress() => new Progress<MeshProgress>(p =>
    {
        if (_cts is not { IsCancellationRequested: false }) return;
        // (the bodies run in parallel: how many are done, and the largest one still busy)
        var parts = new List<string>();
        if (p.Bodies > 1) parts.Add(T($"{p.Done} van {p.Bodies} lichamen klaar", $"{p.Done} of {p.Bodies} bodies done"));
        if (p.Triangles > 0)
            parts.Add((p.Bodies > 1 ? T("grootste bezig: ", "largest in progress: ") : "") + T($"{p.Triangles:N0} driehoekjes", $"{p.Triangles:N0} triangles") + (p.Round > 1 ? T($", ronde {p.Round}", $", round {p.Round}") : ""));
        BusyDetail.Text = string.Join(" · ", parts);
    });

    /// <summary>Test aid: renders the window's own content to a PNG and closes (no screen capture involved).</summary>
    private async Task Snapshot(string path, bool close = true)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
        // (the window rendered by Avalonia at 96 dpi, the 3D view, drawn by OpenGL, laid in where it is, and what lies over it again)
        var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var window = new RenderTargetBitmap(size, new Vector(96, 96));
        window.Render(this);
        var (w, h, rgba) = await View.CaptureAsync();
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
        using (var fb = bmp.Lock())
            for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(rgba, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        var at = View.TranslatePoint(new Point(0, 0), this) ?? default;
        using var whole = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var dc = whole.CreateDrawingContext())
        {
            dc.DrawImage(window, new Rect(0, 0, Bounds.Width, Bounds.Height));
            dc.DrawImage(bmp, new Rect(at.X, at.Y, View.Bounds.Width, View.Bounds.Height));
            foreach (var over in new Control[] { EmptyHint, Legend, EdgesBox, ViewChips, SelectionBar, Toast, BusyPanel, PickCard, ThreadCard, FaceBar })
            {
                if (!over.IsVisible || over.Bounds.Width < 1 || over.TranslatePoint(new Point(0, 0), this) is not { } o) continue;
                using var part = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(over.Bounds.Width), (int)Math.Ceiling(over.Bounds.Height)), new Vector(96, 96));
                part.Render(over);
                dc.DrawImage(part, new Rect(o.X, o.Y, part.PixelSize.Width, part.PixelSize.Height));
            }
        }
        whole.Save(path, new PngBitmapEncoderOptions());
        if (close) Close();
    }

    /// <summary>A message with an OK button (as WPF's MessageBox).</summary>
    private async Task ShowMessage(string title, string text)
    {
        var dlg = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (IBrush)this.FindResource("Page")!,
        };
        var ok = new Button { Classes = { "pill", "primary" }, Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, ok } };
        dlg.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://StlConverter.Desktop/")) { Source = new Uri("avares://StlConverter.Desktop/Theme.axaml") });
        await dlg.ShowDialog(this);
    }

    private void ShowResult(List<BrepBody> bodies, MeshStats stats, MeshLabels labels, TimeSpan took, string? extra = null)
    {
        var wasOpen = _openTris;
        CloseFace();
        _bodies = bodies; _labels = labels;
        _groupFailed.Clear();
        foreach (var (g, reason) in stats.HintFailures)
            if (g - _softCount is int ug && ug >= 0 && ug < _hintGroups.Count) _groupFailed[_hintGroups[ug].tris] = Reason(reason);
        Array.Fill(_faceOf, (-1, -1));
        foreach (var t in labels.Triangles) _faceOf[t.Source] = (t.Body, t.Face);
        RebuildBase();
        RebuildOverlay();
        UpdateGroupList();
        var counts = labels.Faces.SelectMany(f => f).GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());
        int facets = counts.GetValueOrDefault(FaceKind.Facet);
        int applied = _groups.Count(GroupApplied);
        StatusText.Text = (_fullMode
            ? T($"🎉 {bodies.Sum(b => b.Faces.Count):N0} vlakken in {bodies.Count} {(bodies.Count == 1 ? "body" : "bodies")} · {took.TotalSeconds:0.0} s", $"🎉 {bodies.Sum(b => b.Faces.Count):N0} faces in {bodies.Count} {(bodies.Count == 1 ? "body" : "bodies")} · {took.TotalSeconds:0.0} s")
              + (facets > 0 ? T($"\n{facets:N0} losse driehoekjes konden nergens bij (oranje).", $"\n{facets:N0} loose triangles did not fit anywhere (orange).") : T("\nAlles netjes herkend!", "\nEverything neatly recognised!"))
            : _planeMode
            ? T($"▭ {bodies.Sum(b => b.Faces.Count):N0} vlakken · {took.TotalSeconds:0.0} s: platte stukken samengevoegd", $"▭ {bodies.Sum(b => b.Faces.Count):N0} faces · {took.TotalSeconds:0.0} s: flat pieces merged") + (_groups.Count > 0 ? T($", {applied} van {_groups.Count} {(_groups.Count == 1 ? "groep" : "groepen")} omgezet", $", {applied} of {_groups.Count} {(_groups.Count == 1 ? "group" : "groups")} converted") : "")
              + T($"\nDe rest blijft driehoekjes ({facets:N0}). Maak er zelf groepen van, of ✨ herkent alles.", $"\nThe rest stays triangles ({facets:N0}). Make groups of them yourself, or ✨ recognises everything.")
            : T($"🧩 {applied} van {_groups.Count} {(_groups.Count == 1 ? "groep" : "groepen")} omgezet · {took.TotalSeconds:0.0} s\nDe rest blijft mesh ({facets:N0} driehoekjes). ✨ herkent alles.", $"🧩 {applied} of {_groups.Count} {(_groups.Count == 1 ? "group" : "groups")} converted · {took.TotalSeconds:0.0} s\nThe rest stays mesh ({facets:N0} triangles). ✨ recognises everything."))
            + string.Concat(stats.HintFailures.Where(f => f.group >= _softCount && f.group - _softCount < _hintGroups.Count)
                .Select(f => T($"\n🙈 Groep {_groups.FindIndex(g => ReferenceEquals(g.tris, _hintGroups[f.group - _softCount].tris)) + 1} paste niet en is overgeslagen (de rest is gewoon herkend): {Reason(f.reason)}", $"\n🙈 Group {_groups.FindIndex(g => ReferenceEquals(g.tris, _hintGroups[f.group - _softCount].tris)) + 1} did not fit and was skipped (the rest was recognised as usual): {Reason(f.reason)}")))
            + string.Concat(stats.Warnings.Select(w => "\n⚠️ " + w))
            + (extra != null ? "\n\n" + extra : "");
        UpdateLegend(counts);
        SaveButton.IsEnabled = true;
        Log($"result: {StatusText.Text.Replace("\n", " / ")}");
        // the opened face stays open (as far as its triangles are still one face)
        if (wasOpen.Where(t => InfoOf(t) is { Kind: not FaceKind.Facet }).Select(t => (int?)t).FirstOrDefault() is int reopen) OpenFace(reopen);
        if (_groups.Count > 0)
        {
            var g = _groups[^1];
            if (_looseGroups.Contains(g.tris))
                ShowToast(T($"✂️ {g.tris.Count:N0} driehoekjes losgekoppeld", $"✂️ {g.tris.Count:N0} triangles detached"), g.tris.All(_selection.Contains)
                    ? T("Ze blijven losse driehoekjes en zijn nog geselecteerd: kies ➕ bij vlak om ze bij een ander vlak te voegen.", "They stay loose triangles and are still selected: choose ➕ to face to add them to another face.")
                    : T("Ze blijven losse driehoekjes.", "They stay loose triangles."));
            else if (_groupFailed.TryGetValue(g.tris, out var why)) ShowToast(T($"❌ Groep {_groups.Count} paste niet", $"❌ Group {_groups.Count} did not fit"), why);
            else if (GroupApplied(g) && GroupFace(g.tris) is { } info)
            {
                var c = CultureInfo.CurrentCulture;
                string what = info.Kind switch
                {
                    FaceKind.Cylinder => string.Format(c, T("🥫 cilinder, straal {0:0.###} mm", "🥫 cylinder, radius {0:0.###} mm"), info.Radius * 1000),
                    FaceKind.Sphere => string.Format(c, T("⚽ bol, straal {0:0.###} mm", "⚽ sphere, radius {0:0.###} mm"), info.Radius * 1000),
                    FaceKind.Torus => string.Format(c, T("🍩 torus, R {0:0.###} / r {1:0.###} mm", "🍩 torus, R {0:0.###} / r {1:0.###} mm"), info.Radius * 1000, info.MinorRadius * 1000),
                    FaceKind.Cone => string.Format(c, T("🍦 kegel, halve tophoek {0:0.###}°", "🍦 cone, half apex angle {0:0.###}°"), Math.Atan(info.MinorRadius) * 180 / Math.PI),
                    _ => T("▭ vlak", "▭ plane"),
                };
                int left = g.tris.Count(t => InfoOf(t)?.Group != info.Group);
                ShowToast(T($"✅ Groep {_groups.Count}: {what}", $"✅ Group {_groups.Count}: {what}"), string.Format(c, T("afwijking {0:0.####} mm", "deviation {0:0.####} mm"), info.Deviation * 1000)
                    + (_joined > 0 ? T($" · samen met {_joined} {(_joined == 1 ? "aangrenzend vlak" : "aangrenzende vlakken")} op hetzelfde oppervlak", $" · together with {_joined} {(_joined == 1 ? "adjoining face" : "adjoining faces")} on the same surface") : "")
                    + (left > 0 ? T($" · {left} driehoekjes pasten niet en zijn eruit gelaten", $" · {left} triangles did not fit and were left out") : ""));
            }
        }
    }

    private DispatcherTimer? _toastTimer;

    /// <summary>A short message floating over the 3D view for a few seconds.</summary>
    private void ShowToast(string title, string text)
    {
        ToastTitle.Text = title; ToastText.Text = text;
        Toast.IsVisible = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) => { Toast.IsVisible = false; _toastTimer?.Stop(); };
        _toastTimer.Start();
    }

    /// <summary>The converter's reasons are technical English; the most common ones in plain Dutch.</summary>
    private static string Reason(string reason) => reason switch
    {
        var r when r.Contains("not a circle") => T("hij sluit aan op een buurvlak langs een kromme lijn die (nog) niet exact te maken is. Probeer de selectie iets kleiner te maken, zodat hij alleen aan zijn eigen buren grenst.", "it joins a neighbouring face along a curved line that cannot be made exactly (yet). Try making the selection a little smaller, so that it only borders its own neighbours."),
        var r when r.Contains("deviates") => T("de gekozen vorm wijkt meer dan 0,1 mm af van de selectie; kies een andere vorm, selecteer preciezer, of kies 📐 plat om er hoe dan ook een plat vlak van te maken.", "the chosen shape deviates more than 0.1 mm from the selection; choose another shape, select more precisely, or choose 📐 flat to make a flat face of it anyway."),
        var r when r.Contains("counter-clockwise") => T("de vorm past niet goed genoeg op de geselecteerde driehoekjes.", "the shape does not fit the selected triangles well enough."),
        var r when r.Contains("no ") && r.Contains("fits") => T("deze vorm past niet op de selectie.", "this shape does not fit the selection."),
        _ => reason,
    };

    private CancellationTokenSource? _tuneCancel;

    /// <summary>
    /// Tries a grid of fit tolerances and join margins in parallel (all within safe limits: at most 1 µm fit error and
    /// 20 µm vertex movement) and keeps the one with the fewest faces, then the fewest single facets.
    /// </summary>
    private async void Autotune_Click(object? sender, RoutedEventArgs e)
    {
        if (_stl == null) return;
        if (_tuneCancel != null) { _tuneCancel.Cancel(); return; }
        double[] fits = [-1.5, -1, -0.5, 0]; // log10 µm: 0.03, 0.1, 0.3, 1
        double[] joins = [5, 10, 20];
        var basis = Options();
        var trials = (from f in fits from j in joins select (f, j)).ToList();
        var stl = _stl;
        _tuneCancel = new CancellationTokenSource();
        var token = _tuneCancel.Token;
        OpenButton.IsEnabled = RecognizeButton.IsEnabled = SaveButton.IsEnabled = false;
        TuneButton.Content = T("⏹  Stop autotune", "⏹  Stop autotune");
        var sw = Stopwatch.StartNew();
        int done = 0, bestFaces = int.MaxValue;
        var results = new List<(double f, double j, int faces, int facets, List<BrepBody> bodies, MeshStats stats, MeshLabels labels)>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => BusyText.Text = T($"🎛️ Autotune {Volatile.Read(ref done)}/{trials.Count}", $"🎛️ Autotune {Volatile.Read(ref done)}/{trials.Count}")
            + (bestFaces < int.MaxValue ? T($" · beste tot nu: {bestFaces:N0} vlakken", $" · best so far: {bestFaces:N0} faces") : "") + $"   {sw.Elapsed.TotalSeconds:0} s";
        BusyText.Text = T("🎛️ Autotune: instellingen proberen…", "🎛️ Autotune: trying settings…");
        BusyPanel.IsVisible = true;
        timer.Start();
        try
        {
            await Task.Run(() => Parallel.ForEach(trials,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = token },
                trial =>
                {
                    var opt = basis with
                    {
                        CylinderFitTolerance = Math.Pow(10, trial.f) * 1e-6,
                        CylinderJoinTolerance = trial.j * 1e-6,
                        AbsorbTolerance = trial.j * 1e-6,
                    };
                    try
                    {
                        var b = MeshConverter.ConvertToBrep(stl, opt, out var st, out var lb);
                        int faces = b.Sum(x => x.Faces.Count);
                        int facets = lb.Faces.SelectMany(x => x).Count(x => x.Kind == FaceKind.Facet);
                        lock (results)
                        {
                            results.Add((trial.f, trial.j, faces, facets, b, st, lb));
                            bestFaces = Math.Min(bestFaces, faces);
                        }
                    }
                    catch (Exception ex) when (ex is MeshException or InvalidOperationException) { }
                    Interlocked.Increment(ref done);
                }));
        }
        catch (OperationCanceledException) { }
        finally
        {
            timer.Stop();
            BusyPanel.IsVisible = false;
            _tuneCancel = null;
            TuneButton.Content = T("🎛️  Autotune: zoek de beste instellingen", "🎛️  Autotune: find the best settings");
            OpenButton.IsEnabled = RecognizeButton.IsEnabled = SaveButton.IsEnabled = true;
        }
        if (results.Count == 0) { StatusText.Text = T("😕 Autotune vond geen werkende instelling.", "😕 Autotune found no setting that works."); return; }
        var ranked = results.OrderBy(r => r.faces).ThenBy(r => r.facets).ThenBy(r => Math.Abs(r.f + 1) + Math.Abs(r.j - 10) / 10).ToList();
        var best = ranked[0];
        FitSlider.Value = best.f;
        JoinSlider.Value = best.j;
        var c = CultureInfo.CurrentCulture;
        string table = T($"🎛️ Autotune ({results.Count} geprobeerd):\n", $"🎛️ Autotune ({results.Count} tried):\n") + string.Join("\n", ranked.Take(5).Select((r, i) =>
            string.Format(c, T("{0} {1:0.###} µm / {2:0} µm → {3:N0} vlakken", "{0} {1:0.###} µm / {2:0} µm → {3:N0} faces"), i == 0 ? "🏆" : "     ", Math.Pow(10, r.f), r.j, r.faces)));
        ShowResult(best.bodies, best.stats, best.labels, sw.Elapsed, table);
    }

    /// <summary>
    /// Saves the shown result; when nothing was converted yet, the mesh is converted first without recognition (flat
    /// pieces merged into planes, everything else single triangles).
    /// </summary>
    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_stl == null) return;
        if (_bodies == null && MeshBroken)
        {
            ShowToast(T("⚠️ Eerst opschonen", "⚠️ Clean up first"), T("De mesh is niet dicht, zo kan DesignSpark er geen solid van maken. Gebruik 🧹 Opschonen.", "The mesh is not closed, so DesignSpark cannot make a solid of it. Use 🧹 Clean up."));
            CleanToggle.IsChecked = true;
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Opslaan", "Save"), SuggestedFileName = Path.GetFileNameWithoutExtension(_path) + ".rsdocx", DefaultExtension = "rsdocx", ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("DesignSpark Mechanical") { Patterns = ["*.rsdocx"] }, new FilePickerFileType(T("STEP, voor elk CAD-programma", "STEP, for any CAD program")) { Patterns = ["*.step"] }],
            SuggestedStartLocation = Path.GetDirectoryName(_path) is { } folder ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null,
        });
        if (file?.TryGetLocalPath() is not { } fileName) return;
        if (await ResultBodies(T("Opslaan", "Saving")) is not var (bodies, _, note)) return;
        try
        {
            List<BrepBody> all = [.. bodies, .. _extraBodies];
            if (fileName.EndsWith(".step", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".stp", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(fileName, RsDocx.Core.Step.StepWriter.Write(new RsDocx.Core.Step.StepModel(
                    all.Select((b, i) => new RsDocx.Core.Step.StepBody(b, T($"Vaste vorm {i + 1}", $"Solid {i + 1}"))).ToList(), []), Path.GetFileName(fileName)));
            else RsDocxComposer.Compose(all).Save(fileName);
            StatusText.Text += T($"\n💾 Opgeslagen: {Path.GetFileName(fileName)}{note}", $"\n💾 Saved: {Path.GetFileName(fileName)}{note}");
            ShowToast(T("💾 Opgeslagen", "💾 Saved"), Path.GetFileName(fileName) + note);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            await ShowMessage(T("Opslaan mislukt", "Saving failed"), ex.Message);
        }
    }

    /// <summary>
    /// The bodies to save or hand over: the recognised ones, or (nothing recognised yet) the mesh converted without
    /// recognition, flat pieces merged; with which triangle became which face, and a note for the user. Null when that
    /// was stopped or failed (the user has been told).
    /// </summary>
    private async Task<(List<BrepBody> Bodies, MeshLabels? Labels, string Note)?> ResultBodies(string what)
    {
        if (_bodies != null) return (_bodies, _labels, "");
        var stl = _stl!;
        var opt = Options() with { RecognizeCylinders = false, RecognizeSpheres = false, RecognizeTori = false, RecognizeCones = false };
        OpenButton.IsEnabled = RecognizeButton.IsEnabled = SaveButton.IsEnabled = CleanButton.IsEnabled = false;
        BusyText.Text = T("Omzetten zonder herkenning 💾", "Converting without recognition 💾");
        opt = opt with { Cancel = StartCancellable(), Progress = BusyProgress() };
        BusyPanel.IsVisible = true;
        try
        {
            MeshLabels? labels = null;
            var bodies = await Task.Run(() => MeshConverter.ConvertToBrep(stl, opt, out _, out labels));
            return (bodies, labels, T($" (zonder herkenning: {bodies.Sum(b => b.Faces.Count):N0} vlakken, platte stukken samengevoegd)", $" (without recognition: {bodies.Sum(b => b.Faces.Count):N0} faces, flat pieces merged)"));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text += T($"\n⏹ {what} gestopt.", $"\n⏹ {what} stopped.");
            return null;
        }
        catch (Exception ex) when (ex is MeshException or InvalidOperationException)
        {
            await ShowMessage(T("Omzetten mislukt", "Converting failed"), ex.Message);
            return null;
        }
        finally
        {
            EndCancellable();
            BusyPanel.IsVisible = false;
            OpenButton.IsEnabled = RecognizeButton.IsEnabled = SaveButton.IsEnabled = CleanButton.IsEnabled = true;
        }
    }

    /// <summary>Embedded: the result handed over to the program that opened this window, which then closes.</summary>
    private async void Take_Click(object? sender, RoutedEventArgs e) => await Take();

    private async Task Take()
    {
        if (_stl == null || !_embedded) return;
        if (_bodies == null && MeshBroken)
        {
            ShowToast(T("⚠️ Eerst opschonen", "⚠️ Clean up first"), T("De mesh is niet dicht, zo is er geen solid van te maken. Gebruik 🧹 Opschonen.", "The mesh is not closed, so no solid can be made of it. Use 🧹 Clean up."));
            CleanToggle.IsChecked = true;
            return;
        }
        if (await ResultBodies(T("Overnemen", "Taking over")) is not var (bodies, labels, note)) return;
        Result = new ConverterResult(_path!, [.. bodies, .. _extraBodies], labels, _stl, note.Trim());
        Close(true);
    }

    /// <summary>The shown mesh (cleaned or not) as a binary STL, in the units of the file it came from.</summary>
    private async void SaveStl_Click(object? sender, RoutedEventArgs e)
    {
        if (_stl == null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "STL", DefaultExtension = "stl", ShowOverwritePrompt = true,
            SuggestedFileName = Path.GetFileNameWithoutExtension(_path) + (_original != null ? T("_opgeschoond", "_cleaned") : "") + ".stl",
            FileTypeChoices = [new FilePickerFileType("STL") { Patterns = ["*.stl"] }],
            SuggestedStartLocation = Path.GetDirectoryName(_path) is { } folder ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null,
        });
        if (file?.TryGetLocalPath() is not { } fileName) return;
        try
        {
            File.WriteAllBytes(fileName, StlWriter.WriteBinary(_stl.Triangles, _original != null ? "cleaned by StlConverter" : ""));
            ShowToast(T("💾 STL opgeslagen", "💾 STL saved"), T($"{Path.GetFileName(fileName)} · {_stl.Triangles.Count:N0} driehoekjes", $"{Path.GetFileName(fileName)} · {_stl.Triangles.Count:N0} triangles"));
        }
        catch (IOException ex)
        {
            await ShowMessage(T("Opslaan mislukt", "Saving failed"), ex.Message);
        }
    }

    // ------------------------------------------------------------------ view

    private void ViewButton_Click(object? sender, RoutedEventArgs e)
    {
        // look direction and up vector (z is up in most STL files)
        (Vector3D look, Vector3D up)? v = (((Button)sender!).Tag as string) switch
        {
            "iso" => (new Vector3D(-1, 1, -1), new Vector3D(0, 0, 1)),
            "top" => (new Vector3D(0, 0, -1), new Vector3D(0, 1, 0)),
            "front" => (new Vector3D(0, 1, 0), new Vector3D(0, 0, 1)),
            "side" => (new Vector3D(-1, 0, 0), new Vector3D(0, 0, 1)),
            _ => null,
        };
        if (v is { } d)
        {
            var look = d.look; look.Normalize(); look *= 100;
            View.SetView(-look, look, d.up);
        }
        View.ZoomExtents();
    }

    private void Edges_Click(object? sender, RoutedEventArgs e) => View.SetLines("edges", EdgesBox.IsChecked == true ? _edges : null);

    /// <summary>
    /// Above this many edges no edge lines are drawn: HelixToolkit turns every segment into a camera-facing quad and
    /// recomputes them all on every camera move (obj_1_BCons: 2.2 M segments, the app ran out of memory).
    /// </summary>
    private const int MaxEdgeLines = 250_000;

    private FaceInfo? InfoOf(int source)
    {
        var (b, f) = _faceOf[source];
        return _labels == null || b < 0 ? null : _labels.Faces[b][f];
    }

    /// <summary>The model: one colour per kind of face (or neutral before recognition), lines between faces.</summary>
    private static Vector4 V4(Color c, double opacity = 1) => new(c.R / 255f, c.G / 255f, c.B / 255f, (float)(c.A / 255.0 * opacity));

    /// <summary>The triangles as a layer's positions (each lifted along its normal by <paramref name="lift"/>).</summary>
    private float[] Positions(IEnumerable<int> src, double lift, out List<int> list)
    {
        var pts = new List<float>();
        list = new List<int>();
        foreach (int i in src)
        {
            var t = _stl!.Triangles[i];
            var l = _normal[i] * lift;
            foreach (var v in new[] { t.A, t.B, t.C }) { pts.Add((float)(v.X + l.X)); pts.Add((float)(v.Y + l.Y)); pts.Add((float)(v.Z + l.Z)); }
            list.Add(i);
        }
        return pts.ToArray();
    }

    private void RebuildBase()
    {
        var tris = _stl!.Triangles;
        var byColor = new Dictionary<Color, List<int>>();
        for (int i = 0; i < tris.Count; i++)
        {
            var info = InfoOf(i);
            var color = info == null || (!_fullMode && info.Kind == FaceKind.Facet) ? RawColor : Kinds.First(k => k.kind == info.Kind).color;
            if (_threadFilter || (_legendFilter is FaceKind lf && (info?.Kind ?? FaceKind.Facet) != lf)) color = FadedColor;
            if (!byColor.TryGetValue(color, out var l)) byColor[color] = l = new List<int>();
            l.Add(i);
        }
        // (filtered out: faded and see-through, so what is shown is not hidden behind it)
        View.Base = byColor.Select(kv => new MeshLayer
        {
            Positions = Positions(kv.Value, 0, out var src), Source = src,
            Color = kv.Key == FadedColor ? V4(FadedColor, 0.15) : V4(kv.Key),
            BackColor = kv.Key == FadedColor ? null : V4(Colors.DarkRed),
        }).ToList();
        View.Highlight = null;

        _edges = null;
        View.SetLines("edges", null);
        // before recognition: every triangle edge (the bare mesh, to pick triangles from); after it: the face boundaries
        bool raw = _labels == null;
        bool meshLines = raw || !_fullMode;
        int count = 0;
        for (int i = 0; i < tris.Count; i++)
            foreach (int j in _neighbours[i])
                if (j > i && (raw || _faceOf[i] != _faceOf[j])) count++;
        if (count > MaxEdgeLines)
        {
            EdgesBox.IsEnabled = false;
            ToolTip.SetTip(EdgesBox, T($"{count:N0} randen: te veel om te tekenen (boven {MaxEdgeLines:N0} zie je alleen de vlakken, dat scheelt veel geheugen)", $"{count:N0} edges: too many to draw (above {MaxEdgeLines:N0} you only see the faces, which saves a lot of memory)"));
            return;
        }
        EdgesBox.IsEnabled = true;
        ToolTip.SetTip(EdgesBox, T("Randen tonen of verbergen", "Show or hide edges"));
        var pts = new List<float>(6 * count);
        for (int i = 0; i < tris.Count; i++)
            foreach (int j in _neighbours[i])
            {
                if (j < i || (!raw && _faceOf[i] == _faceOf[j])) continue;
                // the shared edge: the two corners of i that j also has
                var shared = new[] { tris[i].A, tris[i].B, tris[i].C }.Where(p => new[] { tris[j].A, tris[j].B, tris[j].C }.Any(o => (o - p).Length < 1e-9 * (1 + p.Length))).ToList();
                if (shared.Count == 2) foreach (var q in shared) { pts.Add((float)q.X); pts.Add((float)q.Y); pts.Add((float)q.Z); }
            }
        _edges = new LineLayer { Points = pts.ToArray(), Color = V4(meshLines ? Color.FromRgb(0x5A, 0x6B, 0x8C) : Color.FromRgb(0x30, 0x30, 0x30)), Width = 1 };
        if (EdgesBox.IsChecked == true) View.SetLines("edges", _edges);
    }

    /// <summary>Selection and groups waiting for recognition, lifted a little above the surface.</summary>
    /// <summary>Triangles added or changed by the last cleaning (pink until ✨ or the next mesh).</summary>
    private readonly HashSet<int> _repaired = new();

    private static readonly Color RepairedColor = Color.FromRgb(0xFF, 0x4D, 0xA6);
    private static readonly Color OpenFaceColor = Color.FromRgb(0xEE, 0xE8, 0xFF);

    private void RebuildOverlay()
    {
        var layers = new List<MeshLayer>();
        void Add(IEnumerable<int> src, Color color, double opacity)
        {
            var pos = Positions(src, _overlayLift, out var list);
            if (list.Count == 0) return;
            layers.Add(new MeshLayer { Positions = pos, Source = list, Color = V4(color, opacity) });
        }
        // groups that are not yet part of the shown result
        foreach (var g in _groups.Where(g => !GroupApplied(g))) Add(g.tris, GroupColor, 0.85);
        Add(_repaired, RepairedColor, 0.9);
        Add(_openTris.Where(t => !_selection.Contains(t)), OpenFaceColor, 0.6);
        Add(_selection, SelectionColor, 0.95);
        View.Overlay = layers;

        SelectionBar.IsVisible = _selection.Count > 0;
        // an opened face: its picked triangles are detached or added to another face; otherwise a shape is chosen
        bool inFace = _openTris.Count > 0;
        ShapeButtons.IsVisible = !(inFace);
        OpenFaceButtons.IsVisible = inFace;
        MeasureRow.IsVisible = !(inFace);
        SelectionText.Text = _addPick
            ? T($"👉 Klik nu op het vlak waar deze {_selection.Count:N0} driehoekjes bij moeten (Esc = annuleren)", $"👉 Now click the face these {_selection.Count:N0} triangles belong to (Esc = cancel)")
            : inFace ? T($"✋ {_selection.Count:N0} driehoekjes gekozen — haal ze uit dit vlak:", $"✋ {_selection.Count:N0} triangles chosen — take them out of this face:")
            : T($"✋ {_selection.Count:N0} driehoekjes geselecteerd — maak er één vorm van:", $"✋ {_selection.Count:N0} triangles selected — make one shape of them:");
    }


    /// <summary>A group is applied when the shown result has all its triangles in one face from that group.</summary>
    private bool GroupApplied((List<int> tris, FaceKind kind) g)
    {
        if (_labels == null) return false;
        if (_looseGroups.Contains(g.tris)) return g.tris.All(t => InfoOf(t) is { Kind: FaceKind.Facet });
        // most of its triangles in faces made from this group, however many (a full cylinder, a bore, is written as
        // several faces: with "most in one face" it stayed "not recognised")
        int hi = _hintGroups.FindIndex(x => ReferenceEquals(x.tris, g.tris));
        if (hi < 0) return false;
        int inGroup = g.tris.Count(t => InfoOf(t)?.Group == _softCount + hi);
        return inGroup * 5 >= g.tris.Count * 4;
    }

    /// <summary>The face most of a group's triangles ended up in (a few may have been left out of it).</summary>
    private FaceInfo? GroupFace(List<int> tris) => InfoOf(tris.GroupBy(t => _faceOf[t]).MaxBy(x => x.Count())!.First());

    /// <summary>The legend as a filter: only faces of this kind in colour, the rest faded (null: all).</summary>
    private FaceKind? _legendFilter;
    private Dictionary<FaceKind, int> _legendCounts = new();

    private void UpdateLegend(Dictionary<FaceKind, int> counts)
    {
        _legendCounts = counts;
        Legend.Children.Clear();
        foreach (var (kind, color, _) in Kinds)
        {
            int n = counts.GetValueOrDefault(kind);
            if (n == 0) continue;
            var chip = !_fullMode && kind == FaceKind.Facet ? Chip(RawColor, "Mesh", n.ToString("N0")) : Chip(color, KindName(kind), n.ToString("N0"));
            chip.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(chip, _legendFilter == kind ? T("Klik: weer alles tonen", "Click: show everything again") : T("Klik: alleen deze laten zien (de rest wordt doorzichtig)", "Click: show only these (the rest becomes see-through)"));
            if (_legendFilter == kind) { chip.BorderBrush = (IBrush)this.FindResource("Accent")!; chip.BorderThickness = new Thickness(2); }
            else if (_legendFilter != null) chip.Opacity = 0.55;
            var k = kind;
            chip.PointerReleased += (_, e) => { SetLegendFilter(_legendFilter == k ? null : k); e.Handled = true; };
            Legend.Children.Add(chip);
        }
        if (_extraBodies.Count > 0)
        {
            var chip = Chip(CoilColor, T("Draad (apart lichaam)", "Thread (separate body)"), _extraBodies.Count.ToString());
            chip.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(chip, _threadFilter ? T("Klik: weer alles tonen", "Click: show everything again") : T("Klik: alleen de draad laten zien", "Click: show only the thread"));
            if (_threadFilter) { chip.BorderBrush = (IBrush)this.FindResource("Accent")!; chip.BorderThickness = new Thickness(2); }
            else if (_legendFilter != null) chip.Opacity = 0.55;
            chip.PointerReleased += (_, e) => { SetThreadFilter(!_threadFilter); e.Handled = true; };
            Legend.Children.Add(chip);
        }
    }

    /// <summary>Only the thread coil in colour (the mesh faded).</summary>
    private bool _threadFilter;

    private void SetThreadFilter(bool on)
    {
        _threadFilter = on; _legendFilter = null;
        RebuildBase();
        UpdateLegend(_legendCounts);
        ShowCoil(null);
    }

    /// <summary>Shows only the faces of one kind in colour (and zooms to them when they are few: a handful of loose
    /// triangles is otherwise hard to find).</summary>
    private void SetLegendFilter(FaceKind? kind)
    {
        _legendFilter = kind; _threadFilter = false;
        RebuildBase();
        UpdateLegend(_legendCounts);
        ShowCoil(null);
        if (kind is not FaceKind k || _stl == null) { View.ZoomExtents(); return; }
        var hit = Enumerable.Range(0, _stl.Triangles.Count).Where(i => (InfoOf(i)?.Kind ?? FaceKind.Facet) == k).ToList();
        if (hit.Count == 0 || hit.Count > 0.02 * _stl.Triangles.Count) return;
        var pts = hit.SelectMany(i => new[] { _stl.Triangles[i].A, _stl.Triangles[i].B, _stl.Triangles[i].C }).ToList();
        double x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y), z0 = pts.Min(p => p.Z);
        double sx = pts.Max(p => p.X) - x0, sy = pts.Max(p => p.Y) - y0, sz = pts.Max(p => p.Z) - z0;
        double pad = Math.Max(Math.Max(sx, Math.Max(sy, sz)) * 0.5, _overlayLift * 50);
        View.ZoomExtents(new Rect3D(x0 - pad, y0 - pad, z0 - pad, sx + 2 * pad, sy + 2 * pad, sz + 2 * pad));
        ShowToast($"🔎 {_legendCounts.GetValueOrDefault(k):N0} × {KindName(k).ToLowerInvariant()}", T("Ingezoomd. Klik nog eens op het kaartje om alles weer te zien.", "Zoomed in. Click the card again to see everything."));
    }

    private Border Chip(Color color, string name, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 7, 0) });
        row.Children.Add(new TextBlock { Text = $"{name}  ", FontWeight = FontWeight.SemiBold });
        row.Children.Add(new TextBlock { Text = value, Foreground = (IBrush)this.FindResource("Muted")! });
        return new Border
        {
            Child = row, Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(6, 0, 0, 6), BoxShadow = BoxShadows.Parse("0 4 24 0 #1F1E3A8A"),
        };
    }


    // ------------------------------------------------------------------ picking and selecting

    private int? SourceAt(Point pos, out Point3D at)
    {
        at = default;
        foreach (var (layer, tri, hit) in View.FindHits(pos))
        {
            if (layer.Source is not { } src || tri >= src.Count) continue;
            at = hit;
            return src[tri];
        }
        return null;
    }

    private bool Removing => (_mods & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

    /// <summary>The selection before the first click of a possible double click (a double click on a face undoes that click).</summary>
    private HashSet<int>? _beforeClick;

    /// <summary>The button-up after a Shift-double click that opened a face does nothing (it would select the whole face).</summary>
    private bool _skipUp;

    private IPointer? _paintPointer;

    private void View_LeftDown(Point pos, int clickCount, PointerPressedEventArgs e)
    {
        if (_stl == null) return;
        bool shift = (_mods & KeyModifiers.Shift) != 0;
        // ➕ bij vlak: this click picks the face the selection goes to
        if (_addPick)
        {
            if (SourceAt(pos, out _) is int target) AddSelectionTo(target);
            e.Handled = true;
            return;
        }
        // in an opened face a click picks single triangles (a quick second click too, it is not a double click)
        if (_openTris.Count > 0 && !shift && SourceAt(pos, out var atOpen) is int inFace && _openTris.Contains(inFace))
        {
            if (ModeBrush.IsChecked == true)
            {
                _painting = true;
                _paintPointer = e.Pointer; e.Pointer.Capture(View);
                Paint(inFace, atOpen);
            }
            else
            {
                if (Removing || _selection.Contains(inFace)) _selection.Remove(inFace); else _selection.Add(inFace);
                RebuildOverlay();
            }
            e.Handled = true;
            return;
        }
        if (clickCount == 2)
        {
            var seed = SourceAt(pos, out _);
            if (seed == null) return;
            // Shift-double click on a recognised face: open it (its triangles shown, to pick from); the first click (a
            // Shift-click selects the whole face) is undone, and so is the one that comes with this button-up
            if (shift && InfoOf(seed.Value) is { Kind: not FaceKind.Facet })
            {
                if (_beforeClick != null) { _selection.Clear(); _selection.UnionWith(_beforeClick); }
                OpenFace(seed.Value);
                _skipUp = true;
                e.Handled = true;
                return;
            }
            // a flat face; on a curved surface (only a strip is flat) the smooth patch around it
            var (patch, flat) = DoubleClickPatch(seed.Value);
            if (Removing) _selection.ExceptWith(patch); else _selection.UnionWith(patch);
            RebuildOverlay();
            ShowToast(flat ? T($"🪄 {patch.Count:N0} driehoekjes in hetzelfde vlak geselecteerd", $"🪄 {patch.Count:N0} triangles in the same plane selected")
                : AutoDoubleBox.IsChecked == true ? T($"🪄 {patch.Count:N0} driehoekjes van dit gebogen stuk geselecteerd", $"🪄 {patch.Count:N0} triangles of this curved piece selected") : T($"🪄 {patch.Count:N0} driehoekjes geselecteerd (helling tot {DoubleSlider.Value:0}° anders)", $"🪄 {patch.Count:N0} triangles selected (slope up to {DoubleSlider.Value:0}° different)"),
                T("Kies hieronder wat het is.", "Choose below what it is."));
            e.Handled = true;
            return;
        }
        _beforeClick = new HashSet<int>(_selection);
        if (ModeView.IsChecked == true || shift) return;
        var src = SourceAt(pos, out var at);
        if (src == null) return;
        if (ModeBrush.IsChecked == true)
        {
            _painting = true;
            _paintPointer = e.Pointer; e.Pointer.Capture(View);
            Paint(src.Value, at);
        }
        else Wand(src.Value);
        e.Handled = true;
    }

    private void View_Move(Point pos, PointerEventArgs e)
    {
        if (!_painting) return;
        if (!e.GetCurrentPoint(View).Properties.IsLeftButtonPressed) { StopPainting(); return; }
        var src = SourceAt(pos, out var at);
        if (src != null) Paint(src.Value, at);
    }

    private void StopPainting()
    {
        _painting = false;
        _paintPointer?.Capture(null); _paintPointer = null;
    }

    private void View_LeftUp(Point pos, PointerReleasedEventArgs e)
    {
        if (_painting) { StopPainting(); return; }
        if (_skipUp) { _skipUp = false; return; }
        if (_stl == null) return;
        var src = SourceAt(pos, out _);
        if (src == null) return;
        if ((_mods & KeyModifiers.Shift) != 0)
        {
            // the whole recognised face (or a smooth patch before recognition)
            if (InfoOf(src.Value) == null) { Wand(src.Value); return; }
            var face = _faceOf[src.Value];
            var tris = Enumerable.Range(0, _faceOf.Length).Where(i => _faceOf[i] == face);
            if (Removing) _selection.ExceptWith(tris); else _selection.UnionWith(tris);
            RebuildOverlay();
            return;
        }
        if (ModeView.IsChecked == true && !_openTris.Contains(src.Value)) ShowFace(src.Value);
    }

    /// <summary>Brush: the triangles around the hit point within the brush radius that face the camera, connected to it.</summary>
    private void Paint(int src, Point3D at)
    {
        double r = BrushSlider.Value;
        var look = View.LookDirection;
        bool inFace = _openTris.Contains(src);
        var found = new List<int> { src };
        if (r >= 0.05)
        {
            var seen = new HashSet<int> { src };
            for (int qi = 0; qi < found.Count; qi++)
                foreach (int n in _neighbours[found[qi]])
                {
                    if (!seen.Add(n) || Vector3D.DotProduct(_normal[n], look) > 0 || (inFace && !_openTris.Contains(n))) continue;
                    var t = _stl!.Triangles[n];
                    var c = new Point3D((t.A.X + t.B.X + t.C.X) / 3, (t.A.Y + t.B.Y + t.C.Y) / 3, (t.A.Z + t.B.Z + t.C.Z) / 3);
                    if ((c - at).Length <= r) found.Add(n);
                }
        }
        bool changed = Removing ? found.Count(_selection.Remove) > 0 : found.Count(_selection.Add) > 0;
        if (changed) RebuildOverlay();
    }

    /// <summary>Magic wand: flood over edges whose bend is below the wand angle.</summary>
    private void Wand(int src)
    {
        double cosMax = Math.Cos(WandSlider.Value * Math.PI / 180);
        var found = new List<int> { src };
        var seen = new HashSet<int> { src };
        for (int qi = 0; qi < found.Count; qi++)
            foreach (int n in _neighbours[found[qi]])
                if (seen.Add(n) && Vector3D.DotProduct(_normal[found[qi]], _normal[n]) >= cosMax) found.Add(n);
        if (Removing) _selection.ExceptWith(found); else _selection.UnionWith(found);
        RebuildOverlay();
    }

    /// <summary>The seed triangle and every triangle connected to it that lies in its plane (normals within 0.5°,
    /// corners within 5 µm of the seed's plane).</summary>
    private List<int> CoplanarPatch(int seed)
    {
        double scale = double.Parse((string)((ComboBoxItem)UnitBox.SelectedItem!).Tag!, CultureInfo.InvariantCulture);
        double tol = 5e-6 / scale, cosMax = Math.Cos(0.5 * Math.PI / 180);
        var n0 = _normal[seed]; var t0 = _stl!.Triangles[seed];
        double d0 = Vector3D.DotProduct(n0, D(t0.A));
        bool OnPlane(int i)
        {
            var t = _stl!.Triangles[i];
            return new[] { t.A, t.B, t.C }.All(p => Math.Abs(Vector3D.DotProduct(n0, D(p)) - d0) <= tol);
        }
        var found = new List<int> { seed };
        var seen = new HashSet<int> { seed };
        for (int qi = 0; qi < found.Count; qi++)
            foreach (int n in _neighbours[found[qi]])
                if (seen.Add(n) && Vector3D.DotProduct(_normal[n], n0) >= cosMax && OnPlane(n)) found.Add(n);
        return found;
    }

    /// <summary>
    /// The automatic double click. The flat piece under the cursor (all triangles in its plane) is a face of its own when
    /// it is large compared with the flat pieces next to it across gentle bends (a big top face of two triangles between
    /// roundings). Otherwise it is one strip of a rounding, and the selection grows over neighbouring strips of similar size
    /// that bend less than the wand angle, stopping at big flat faces; on a cylinder only over strips around its axis, so
    /// it does not run on into the fillets and tori that join it smoothly.
    /// </summary>
    private (List<int> tris, bool flat) SmartPatch(int seed)
    {
        double cosMax = Math.Cos(WandSlider.Value * Math.PI / 180);
        var pieceOf = new Dictionary<int, int>();
        var pieces = new List<(List<int> tris, double area)>();
        int Piece(int t)
        {
            if (pieceOf.TryGetValue(t, out int id)) return id;
            var tris = CoplanarPatch(t);
            id = pieces.Count;
            pieces.Add((tris, tris.Sum(TriArea)));
            foreach (int x in tris) pieceOf.TryAdd(x, id);
            return id;
        }
        IEnumerable<int> Smooth(int p) => pieces[p].tris.SelectMany(t => _neighbours[t].Where(n => !pieces[p].tris.Contains(n)
            && Vector3D.DotProduct(_normal[t], _normal[n]) >= cosMax)).Select(Piece).Where(q => q != p).Distinct().ToList();

        int p0 = Piece(seed);
        var around = Smooth(p0).ToList();
        if (around.Count == 0) return (pieces[p0].tris, true);
        var areas = around.Select(q => pieces[q].area).OrderBy(a => a).ToList();
        double typical = areas[areas.Count / 2];
        if (pieces[p0].area > 4 * typical) return (pieces[p0].tris, true);

        double limit = 4 * Math.Max(pieces[p0].area, typical);
        Vector3D N(int p) { var v = new Vector3D(); foreach (int t in pieces[p].tris) v += _normal[t] * TriArea(t); v.Normalize(); return v; }
        Vector3D? axis = null;
        if (around.Count >= 2)
        {
            var a1 = Vector3D.CrossProduct(N(p0), N(around[0])); var a2 = Vector3D.CrossProduct(N(p0), N(around[1]));
            if (a1.Length > 1e-6 && a2.Length > 1e-6)
            {
                a1.Normalize(); a2.Normalize();
                if (Math.Abs(Vector3D.DotProduct(a1, a2)) > Math.Cos(2 * Math.PI / 180)) axis = a1;
            }
        }
        double sinAxis = Math.Sin(2 * Math.PI / 180);
        var chosen = new List<int> { p0 };
        var seen = new HashSet<int> { p0 };
        for (int qi = 0; qi < chosen.Count && chosen.Count < 20000; qi++)
            foreach (int q in Smooth(chosen[qi]))
                if (seen.Add(q) && pieces[q].area <= limit && (axis is not { } ax || Math.Abs(Vector3D.DotProduct(N(q), ax)) <= sinAxis)) chosen.Add(q);
        return (chosen.SelectMany(q => pieces[q].tris).Distinct().ToList(), false);
    }

    private double TriArea(int i)
    {
        var t = _stl!.Triangles[i];
        return Vector3D.CrossProduct(D(t.B) - D(t.A), D(t.C) - D(t.A)).Length / 2;
    }

    /// <summary>
    /// What a double click selects: the clicked triangle and every triangle connected to it whose slope differs at most the
    /// double-click angle from the clicked one (0° = only its flat face: same plane within 5 µm).
    /// </summary>
    private (List<int> tris, bool flat) DoubleClickPatch(int seed)
    {
        if (AutoDoubleBox.IsChecked == true) return SmartPatch(seed);
        double maxDeg = DoubleSlider.Value;
        if (maxDeg < 0.5) return (CoplanarPatch(seed), true);
        double cosMax = Math.Cos(maxDeg * Math.PI / 180);
        var n0 = _normal[seed];
        var found = new List<int> { seed };
        var seen = new HashSet<int> { seed };
        for (int qi = 0; qi < found.Count; qi++)
            foreach (int n in _neighbours[found[qi]])
                if (seen.Add(n) && Vector3D.DotProduct(_normal[n], n0) >= cosMax) found.Add(n);
        return (found, false);
    }

    private void ClearSelection_Click(object? sender, RoutedEventArgs e) => ClearSelection();

    private void ClearSelection()
    {
        if (_selection.Count == 0) return;
        _selection.Clear();
        RebuildOverlay();
    }

    private void MakeGroup_Click(object? sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender!).Tag!;
        if (tag == "ForcePlane") MakeGroup(FaceKind.Plane, force: true);
        else if (tag == "Loose") MakeGroup(FaceKind.Facet, loose: true);
        else MakeGroup(Enum.Parse<FaceKind>(tag));
    }

    /// <param name="loose">"✂️ los": the triangles stay single facets, and stay selected (to ➕ them to another face).</param>
    private void MakeGroup(FaceKind kind, bool force = false, bool loose = false)
    {
        if (_selection.Count == 0) return;
        _addPick = false;
        _joined = kind == FaceKind.Facet || loose ? 0 : JoinMatchingNeighbours(kind);
        // a triangle belongs to one group only: the newest wins
        foreach (var g in _groups) g.tris.RemoveAll(_selection.Contains);
        _forcedGroups.RemoveWhere(l => l.Count == 0); _looseGroups.RemoveWhere(l => l.Count == 0);
        _groups.RemoveAll(g => g.tris.Count == 0);
        var list = _selection.OrderBy(i => i).ToList();
        _groups.Add((list, kind));
        if (force) _forcedGroups.Add(list);
        if (loose) _looseGroups.Add(list);
        _changed = new HashSet<int>(_selection);
        if (!loose) _selection.Clear();
        RebuildOverlay();
        UpdateGroupList();
        if (RecognizeButton.IsEnabled) _running = Recognize(this);
    }

    /// <summary>Faces the last group was joined with (see <see cref="JoinMatchingNeighbours"/>).</summary>
    private int _joined;

    /// <summary>
    /// Adds to the selection the recognised faces of this kind next to it whose exact surface the selection lies on
    /// (within the group tolerance, 0.1 mm). Two faces on (nearly) the same surface cannot meet: their edge is undefined
    /// ("arc bulges away from its mesh edge"). And a few facets alone hardly define a cylinder, the face next to them
    /// does (bigbox: 13 facets in a wall of r 13 mm failed; with the wall piece next to them the group is r 13.000).
    /// </summary>
    private int JoinMatchingNeighbours(FaceKind kind)
    {
        if (_labels == null) return 0;
        double scale = double.Parse((string)((ComboBoxItem)UnitBox.SelectedItem!).Tag!, CultureInfo.InvariantCulture);
        var corners = _selection.SelectMany(t => new[] { _stl!.Triangles[t].A, _stl.Triangles[t].B, _stl.Triangles[t].C })
            .Distinct().Select(p => p * scale).ToList();
        var faces = _selection.SelectMany(t => _neighbours[t]).Where(n => !_selection.Contains(n)).Select(n => _faceOf[n])
            .Distinct().Where(f => f.body >= 0 && _labels.Faces[f.body][f.face].Kind == kind).ToList();
        var join = faces.Where(f => corners.All(p => _labels.Faces[f.body][f.face].SurfaceDistance(p) <= 1e-4)).ToHashSet();
        if (join.Count == 0) return 0;
        _selection.UnionWith(Enumerable.Range(0, _faceOf.Length).Where(i => join.Contains(_faceOf[i])));
        return join.Count;
    }

    private void UpdateGroupList()
    {
        GroupList.Children.Clear();
        for (int k = 0; k < _threadUndos.Count; k++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            if (k == _threadUndos.Count - 1)
            {
                var undo = new Button { Content = "↩", Classes = { "pill" }, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
                ToolTip.SetTip(undo, T("Draad terugzetten: de oorspronkelijke driehoekjes terug, de aparte draad weg", "Put the thread back: the original triangles back, the separate thread gone"));
                undo.Click += async (_, _) => { if (RecognizeButton.IsEnabled) await UndoThread(); };
                DockPanel.SetDock(undo, Dock.Right);
                row.Children.Add(undo);
            }
            row.Children.Add(new TextBlock { Text = T($"🔩 {_threadUndos[k].Name}: vervangen door gladde kern + draad", $"🔩 {_threadUndos[k].Name}: replaced by a smooth core + thread"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            GroupList.Children.Add(row);
        }
        if (_groups.Count == 0) return;
        GroupList.Children.Add(new TextBlock { Text = T("Jouw groepen", "Your groups"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        for (int gi = 0; gi < _groups.Count; gi++)
        {
            var (tris, kind) = _groups[gi];
            bool forced = _forcedGroups.Contains(tris), loose = _looseGroups.Contains(tris);
            string what = loose ? T("✂️ los (blijven driehoekjes)", "✂️ loose (stay triangles)") : forced ? T("📐 plat (gedwongen)", "📐 flat (forced)") : kind == FaceKind.Facet ? T("✨ kies zelf", "✨ choose itself") : $"{Kinds.First(k => k.kind == kind).emoji} {KindName(kind).ToLowerInvariant()}";
            string result = "";
            if (!loose && GroupApplied(_groups[gi]) && GroupFace(tris) is { } info)
            {
                var c = CultureInfo.CurrentCulture;
                result = info.Kind switch
                {
                    FaceKind.Cylinder or FaceKind.Sphere => string.Format(c, T(" → {0} r {1:0.###} mm", " → {0} r {1:0.###} mm"), KindName(info.Kind).ToLowerInvariant(), info.Radius * 1000),
                    FaceKind.Torus => string.Format(c, T(" → torus R {0:0.###} / r {1:0.###} mm", " → torus R {0:0.###} / r {1:0.###} mm"), info.Radius * 1000, info.MinorRadius * 1000),
                    FaceKind.Cone => string.Format(c, T(" → kegel, halve tophoek {0:0.###}°", " → cone, half apex angle {0:0.###}°"), Math.Atan(info.MinorRadius) * 180 / Math.PI),
                    _ => T(" → vlak", " → plane"),
                } + string.Format(c, forced ? T(", punten tot {0:0.###} mm verschoven", ", points moved up to {0:0.###} mm") : T(", afwijking {0:0.###} mm", ", deviation {0:0.###} mm"), info.Deviation * 1000);
            }
            bool applied = GroupApplied(_groups[gi]);
            bool failed = _groupFailed.TryGetValue(tris, out var why);
            string status = applied ? "✅" : failed ? "❌" : "⏳";
            if (failed) result = T(" — paste niet: ", " — did not fit: ") + why;
            else if (!applied) result = T(" — nog niet herkend", " — not recognised yet");
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            int index = gi;
            var del = new Button { Content = "✖", Classes = { "pill" }, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            del.Click += (_, _) =>
            {
                _changed = new HashSet<int>(_groups[index].tris);
                _forcedGroups.Remove(_groups[index].tris); _looseGroups.Remove(_groups[index].tris);
                _groups.RemoveAt(index); RebuildOverlay(); UpdateGroupList();
                if (_labels != null && RecognizeButton.IsEnabled) _ = Recognize(this);
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            row.Children.Add(new TextBlock
            {
                Text = T($"{status} {gi + 1}. {what} · {tris.Count:N0} driehoekjes{result}", $"{status} {gi + 1}. {what} · {tris.Count:N0} triangles{result}"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Foreground = applied ? (IBrush)this.FindResource("Ink")! : failed ? new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)) : new SolidColorBrush(GroupColor),
            });
            GroupList.Children.Add(row);
        }
    }

    // ------------------------------------------------------------------ opened face

    /// <summary>Triangles of the face opened with a double click (empty: none).</summary>
    private HashSet<int> _openTris = new();


    /// <summary>Above this many triangles an opened face is shown without its triangle edges (see <see cref="MaxEdgeLines"/>).</summary>
    private const int MaxOpenFaceLines = 40_000;

    /// <summary>Opens the recognised face of a triangle: its triangles are shown, and a click picks single ones.</summary>
    private void OpenFace(int seed)
    {
        var info = InfoOf(seed);
        if (info == null) return;
        var face = _faceOf[seed];
        _openTris = Enumerable.Range(0, _faceOf.Length).Where(i => _faceOf[i] == face).ToHashSet();
        View.SetLines("face", null);
        if (_openTris.Count <= MaxOpenFaceLines)
        {
            // every triangle's edges, lifted above the overlays along its normal
            var pts = new List<float>(18 * _openTris.Count);
            foreach (int i in _openTris)
            {
                var t = _stl!.Triangles[i];
                var lift = _normal[i] * (2 * _overlayLift);
                Point3D a = P(t.A) + lift, b = P(t.B) + lift, c = P(t.C) + lift;
                foreach (var q in new[] { a, b, b, c, c, a }) { pts.Add((float)q.X); pts.Add((float)q.Y); pts.Add((float)q.Z); }
            }
            View.SetLines("face", new LineLayer { Points = pts.ToArray(), Color = V4(Color.FromRgb(0x6D, 0x28, 0xD9)), Width = 1.5 });
        }
        FaceTitle.Text = T($"🔍 {FaceName(info)} · {_openTris.Count:N0} driehoekjes", $"🔍 {FaceName(info)} · {_openTris.Count:N0} triangles");
        FaceBar.IsVisible = true;
        RebuildOverlay();
    }

    private void CloseFace()
    {
        View.SetLines("face", null);
        FaceBar.IsVisible = false;
        if (_openTris.Count == 0) return;
        _openTris = new HashSet<int>();
        if (_stl != null) RebuildOverlay();
    }

    private void CloseFace_Click(object? sender, RoutedEventArgs e) => CloseFace();

    private void SelectOpenFace_Click(object? sender, RoutedEventArgs e)
    {
        _selection.UnionWith(_openTris);
        RebuildOverlay();
    }

    private void DeselectOpenFace_Click(object? sender, RoutedEventArgs e)
    {
        _selection.ExceptWith(_openTris);
        RebuildOverlay();
    }

    private static string FaceName(FaceInfo info)
    {
        var c = CultureInfo.CurrentCulture;
        return info.Kind switch
        {
            FaceKind.Cylinder => string.Format(c, T("🥫 Cilinder r {0:0.###} mm", "🥫 Cylinder r {0:0.###} mm"), info.Radius * 1000),
            FaceKind.Sphere => string.Format(c, T("⚽ Bol r {0:0.###} mm", "⚽ Sphere r {0:0.###} mm"), info.Radius * 1000),
            FaceKind.Torus => string.Format(c, T("🍩 Torus R {0:0.###} / r {1:0.###} mm", "🍩 Torus R {0:0.###} / r {1:0.###} mm"), info.Radius * 1000, info.MinorRadius * 1000),
            FaceKind.Cone => string.Format(c, T("🍦 Kegel, halve tophoek {0:0.###}°", "🍦 Cone, half apex angle {0:0.###}°"), Math.Atan(info.MinorRadius) * 180 / Math.PI),
            _ => T("▭ Vlak", "▭ Plane"),
        };
    }

    // ------------------------------------------------------------------ 🔩 draad

    /// <summary>The last thread measured (for turning it into a cylinder or a real thread later).</summary>
    private ThreadMeasure? _thread;

    private async void Thread_Click(object? sender, RoutedEventArgs e) => await MeasureThread();

    private void CloseThread_Click(object? sender, RoutedEventArgs e) => ThreadCard.IsVisible = false;

    /// <summary>How long the last thread replacement's mesh work took (test log).</summary>
    private long _replaceSurgeryMs;

    /// <summary>The triangles the last thread was measured on.</summary>
    private List<int> _threadSelection = new();

    /// <summary>Exact bodies written along with the converted mesh (the thread coil of "vervang door … echte draad").</summary>
    private readonly List<BrepBody> _extraBodies = new();

    private async void ReplaceThread_Click(object? sender, RoutedEventArgs e) => await ReplaceThread();

    /// <summary>
    /// The measured thread replaced (<see cref="ThreadReplace"/>): the mesh gets a smooth core at the root diameter (a
    /// cylinder group), the thread becomes a separate exact coil, shown in gold and saved as a second body.
    /// </summary>
    private async Task ReplaceThread()
    {
        if (_stl == null || _thread == null || _threadSelection.Count == 0) return;
        double scale = double.Parse((string)((ComboBoxItem)UnitBox.SelectedItem!).Tag!, CultureInfo.InvariantCulture);
        var stl = _stl; var m = _thread; var sel = _threadSelection;
        ThreadReplacement r;
        var swSurgery = Stopwatch.StartNew();
        try { r = await Task.Run(() => ThreadReplace.Replace(stl, sel, m, scale)); }
        catch (MeshException ex) { ShowToast("🔩 Vervangen lukt niet", ex.Message); Log("thread replace failed: " + ex.Message); return; }
        _replaceSurgeryMs = swSurgery.ElapsedMilliseconds;
        // what was done on the old mesh carries over to the kept triangles (they keep their order): the groups with their
        // flags, and the recognised faces of the shown result
        var oldGroups = _groups.Select(g => (g.tris, g.kind, forced: _forcedGroups.Contains(g.tris), loose: _looseGroups.Contains(g.tris))).ToList();
        var oldLabels = _labels; bool wasFull = _fullMode, wasPlanes = _planeMode; int oldSoft = _softCount;
        var oldExtras = _extraBodies.ToList(); var oldCoils = _coilMeshes.ToList();
        var before = _stl;
        _stl = r.Mesh;
        _original ??= before;
        _keepUndo = true; ShowNewMesh(); _keepUndo = false;
        _extraBodies.AddRange(oldExtras); _coilMeshes.AddRange(oldCoils);
        int Map(int i) => r.NewIndexOf[i];
        foreach (var g in oldGroups)
        {
            var nt = g.tris.Select(Map).Where(i => i >= 0).ToList();
            if (nt.Count == 0) continue;
            _groups.Add((nt, g.kind));
            if (g.forced) _forcedGroups.Add(nt);
            if (g.loose) _looseGroups.Add(nt);
        }
        if (oldLabels != null)
        {
            _labels = new MeshLabels
            {
                Vertices = oldLabels.Vertices, Faces = oldLabels.Faces,
                Triangles = oldLabels.Triangles.Where(t => Map(t.Source) >= 0).Select(t => t with { Source = Map(t.Source) }).ToList(),
            };
            foreach (var t in _labels.Triangles) _faceOf[t.Source] = (t.Body, t.Face);
            _fullMode = wasFull; _softCount = oldSoft;
            RebuildBase();
        }
        _planeMode = wasPlanes;
        _extraBodies.Add(r.Coil);
        var coilMesh = AddCoil(r.CoilTriangles);
        string what = m.Standard ?? T("de draad", "the thread");
        _threadUndos.Add(new ThreadUndo(before, r.NewIndexOf, oldLabels, oldSoft, r.Coil, coilMesh, what));
        Log($"thread replaced: {r.Removed} triangles, band {r.Band.Count}, coil {m.Describe(CultureInfo.InvariantCulture)}");
        // the core as a cylinder group; only around the new triangles is it converted again (the rest as it was)
        var band = r.Band.OrderBy(i => i).ToList();
        _groups.Add((band, FaceKind.Cylinder));
        _changed = new HashSet<int>(r.Added);
        RebuildOverlay();
        UpdateGroupList();
        _running = Recognize(this);
        await _running;
        // (after the group's own message)
        string text = T($"{r.Removed:N0} driehoekjes zijn een gladde kern geworden; {what} is een apart lichaam (goud). Bij opslaan komen beide mee: voeg ze in DesignSpark samen (Combine).", $"{r.Removed:N0} triangles became a smooth core; {what} is a separate body (gold). Both are saved: combine them in DesignSpark (Combine).");
        ShowToast(T("🔩 Draad vervangen", "🔩 Thread replaced"), text);
        StatusText.Text += "\n🔩 " + text;
    }

    private static readonly Color CoilColor = Color.FromRgb(0xE0, 0xA8, 0x20);

    /// <summary>The coils shown (one per replaced thread, as <see cref="_extraBodies"/>).</summary>
    private readonly List<float[]> _coilMeshes = new();

    private float[] AddCoil(IReadOnlyList<StlTriangle> tris)
    {
        var mesh = tris.SelectMany(t => new[] { t.A, t.B, t.C }).SelectMany(v => new[] { (float)v.X, (float)v.Y, (float)v.Z }).ToArray();
        _coilMeshes.Add(mesh);
        ShowCoil(null);
        return mesh;
    }

    /// <summary>The coils in gold; faded while the legend filters on something else.</summary>
    private void ShowCoil(object? _)
    {
        var color = _legendFilter != null ? V4(FadedColor, 0.15) : V4(CoilColor);
        View.Coil = _coilMeshes.Select(m => new MeshLayer { Positions = m, Color = color }).ToList();
    }

    // ------------------------------------------------------------------ undoing a thread replacement

    /// <summary>What a thread replacement changed, to undo it: the mesh before, how its triangles were renumbered, the
    /// recognised faces of then, and the coil it added.</summary>
    private sealed record ThreadUndo(StlFile Mesh, IReadOnlyList<int> NewIndexOf, MeshLabels? Labels, int SoftCount, BrepBody Coil, float[] CoilMesh, string Name);

    private readonly List<ThreadUndo> _threadUndos = new();

    /// <summary>Set while a replacement or its undo swaps the mesh (a new file or cleaning forgets the undo points).</summary>
    private bool _keepUndo;

    /// <summary>
    /// Undoes the last thread replacement: the mesh as it was (the thread's triangles back, the coil gone); groups made
    /// since stay (mapped back; the core and the skirts go with the replacement), and around the thread it is converted
    /// again.
    /// </summary>
    private async Task UndoThread()
    {
        if (_stl == null || _threadUndos.Count == 0) return;
        var u = _threadUndos[^1];
        _threadUndos.RemoveAt(_threadUndos.Count - 1);
        var back = Enumerable.Repeat(-1, _stl.Triangles.Count).ToArray();
        for (int i = 0; i < u.NewIndexOf.Count; i++) if (u.NewIndexOf[i] >= 0) back[u.NewIndexOf[i]] = i;
        var groups = _groups.Select(g => (tris: g.tris.Select(t => back[t]).Where(t => t >= 0).ToList(), g.kind,
            forced: _forcedGroups.Contains(g.tris), loose: _looseGroups.Contains(g.tris))).Where(g => g.tris.Count > 0).ToList();
        bool full = _fullMode, planes = _planeMode;
        var extras = _extraBodies.Where(b => !ReferenceEquals(b, u.Coil)).ToList();
        var coils = _coilMeshes.Where(c => !ReferenceEquals(c, u.CoilMesh)).ToList();
        _stl = u.Mesh;
        _keepUndo = true; ShowNewMesh(); _keepUndo = false;
        foreach (var g in groups)
        {
            _groups.Add((g.tris, g.kind));
            if (g.forced) _forcedGroups.Add(g.tris);
            if (g.loose) _looseGroups.Add(g.tris);
        }
        _fullMode = full; _planeMode = planes;
        if (u.Labels != null)
        {
            _labels = u.Labels; _softCount = u.SoftCount;
            foreach (var t in _labels.Triangles) _faceOf[t.Source] = (t.Body, t.Face);
            RebuildBase();
        }
        _extraBodies.AddRange(extras); _coilMeshes.AddRange(coils); ShowCoil(null);
        // converted again where the thread was
        _changed = Enumerable.Range(0, u.NewIndexOf.Count).Where(i => u.NewIndexOf[i] < 0).ToHashSet();
        RebuildOverlay();
        UpdateGroupList();
        Log($"thread replacement undone ({u.Name})");
        _running = Recognize(this);
        await _running;
        ShowToast(T("↩ Draad teruggezet", "↩ Thread put back"), T($"{u.Name}: de oorspronkelijke driehoekjes zijn terug, de aparte draad is weg.", $"{u.Name}: the original triangles are back, the separate thread is gone."));
    }

    /// <summary>Measures the thread in the selection (<see cref="ThreadFit"/>); the model is not changed.</summary>
    private async Task MeasureThread()
    {
        if (_stl == null || _selection.Count == 0) return;
        double scale = double.Parse((string)((ComboBoxItem)UnitBox.SelectedItem!).Tag!, CultureInfo.InvariantCulture);
        var tris = _selection.Select(i => _stl.Triangles[i]).Select(t => (t.A * scale, t.B * scale, t.C * scale)).ToList();
        var m = await Task.Run(() => ThreadFit.Measure(tris));
        var c = CultureInfo.CurrentCulture;
        if (m == null || m.Fit < 0.6)
        {
            ThreadCard.IsVisible = false;
            ShowToast(T("🔩 Geen draad gevonden", "🔩 No thread found"), T("In deze selectie liggen geen toppen op één schroeflijn. Selecteer alleen het draadgedeelte (de toverstaf of het penseel werkt goed).", "No crests in this selection lie on one helix. Select only the threaded part (the magic wand or the brush works well)."));
            return;
        }
        _thread = m;
        _threadSelection = _selection.ToList();
        ReplaceThreadButton.IsEnabled = !m.Internal;
        ToolTip.SetTip(ReplaceThreadButton, m.Internal ? T("Binnendraad vervangen kan nog niet (alleen buitendraad).", "Replacing an internal thread is not possible yet (only external threads).") : Texts.Tr("De draaddriehoekjes worden een gladde cilinder op de kerndiameter; de draad wordt een apart, exact lichaam (zoals een helix-sweep in DesignSpark). Bij opslaan komen beide mee: voeg ze in DesignSpark samen (Combine)."));
        string what = m.Describe(c);
        string where = string.Format(c, T("as door ({0:0.###}; {1:0.###}; {2:0.###}) mm, richting ({3:0.###}; {4:0.###}; {5:0.###})", "axis through ({0:0.###}; {1:0.###}; {2:0.###}) mm, direction ({3:0.###}; {4:0.###}; {5:0.###})"),
            m.AxisPoint.X * 1000, m.AxisPoint.Y * 1000, m.AxisPoint.Z * 1000, m.Axis.X, m.Axis.Y, m.Axis.Z);
        // a card that stays (a toast was gone before it was read)
        ThreadTitle.Text = "🔩 " + (m.Standard ?? T("Schroefdraad (geen standaard M-maat)", "Screw thread (no standard M size)")) + (m.Internal ? T(" binnendraad", " internal thread") : T(" buitendraad", " external thread"));
        ThreadText.Text = string.Format(c,
            T("{0}, {1}\nbuiten-Ø {2:0.##} mm · kern-Ø {3:0.##} mm\nspoed {4:0.###} mm · flankhoek {5:0}°\n{6:0.#} mm lang ({7:0.#} gangen)\n{8}\nzekerheid {9:0}%", "{0}, {1}\nouter Ø {2:0.##} mm · core Ø {3:0.##} mm\npitch {4:0.###} mm · flank angle {5:0}°\n{6:0.#} mm long ({7:0.#} turns)\n{8}\ncertainty {9:0}%"),
            m.Internal ? T("binnendraad", "internal thread") : T("buitendraad", "external thread"), m.RightHanded ? T("rechts", "right-handed") : T("links", "left-handed"), m.MajorDiameter * 1000, m.MinorDiameter * 1000,
            m.Pitch * 1000, m.ProfileAngleDeg, m.Length * 1000, m.Turns, where, m.Fit * 100);
        ThreadCard.IsVisible = true;
        StatusText.Text = "🔩 " + what;
        Log("thread: " + what + " | " + where);
    }

    // ------------------------------------------------------------------ ➕ bij vlak

    /// <summary>Waiting for the click on the face the selection is added to.</summary>
    private bool _addPick;

    private void AddTo_Click(object? sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0) return;
        if (_labels == null || !_labels.Faces.SelectMany(f => f).Any(f => f.Kind != FaceKind.Facet))
        {
            ShowToast(T("➕ Nog geen vlakken", "➕ No faces yet"), T("Herken eerst (✨, of maak een groep); daarna kun je driehoekjes bij een vlak voegen.", "Recognise first (✨, or make a group); then you can add triangles to a face."));
            return;
        }
        _addPick = true;
        RebuildOverlay();
        ShowToast(T("👉 Klik op het vlak", "👉 Click the face"), T($"De {_selection.Count:N0} geselecteerde driehoekjes gaan bij het vlak waar je nu op klikt. Esc = annuleren.", $"The {_selection.Count:N0} selected triangles go to the face you click now. Esc = cancel."));
    }

    /// <summary>The selection and the face of <paramref name="target"/> become one group of that face's kind (a group the
    /// user forced flat stays forced).</summary>
    private void AddSelectionTo(int target)
    {
        if (_selection.Contains(target))
        {
            ShowToast(T("👉 Klik op een ander vlak", "👉 Click another face"), T("Dat is de selectie zelf; klik op het vlak waar ze bij moeten (Esc = annuleren).", "That is the selection itself; click the face they belong to (Esc = cancel)."));
            return;
        }
        var info = InfoOf(target);
        if (info is not { Kind: not FaceKind.Facet })
        {
            ShowToast(T("🤔 Dat is geen vlak", "🤔 That is not a face"), T("Klik op een herkend (gekleurd) vlak, niet op losse driehoekjes. Esc = annuleren.", "Click a recognised (coloured) face, not loose triangles. Esc = cancel."));
            return;
        }
        var face = _faceOf[target];
        bool force = info.Group >= _softCount && info.Group - _softCount < _hintGroups.Count && _forcedGroups.Contains(_hintGroups[info.Group - _softCount].tris);
        _selection.UnionWith(Enumerable.Range(0, _faceOf.Length).Where(i => _faceOf[i] == face));
        MakeGroup(info.Kind, force);
    }

    private void ShowFace(int source)
    {
        var info = InfoOf(source);
        if (info == null)
        {
            // the bare mesh: only the triangle's number (stable: its place in the file)
            View.Highlight = null;
            PickTitle.Text = T("🔺  Driehoekje", "🔺  Triangle");
            PickText.Text = T($"nr. {source}", $"no. {source}");
            OpenFaceButton.IsVisible = false;
            PickCard.IsVisible = true;
            return;
        }
        var face = _faceOf[source];
        var tris = Enumerable.Range(0, _faceOf.Length).Where(i => _faceOf[i] == face).ToList();
        int n = tris.Count;
        View.Highlight = new MeshLayer { Positions = Positions(tris, _overlayLift * 0.5, out var src), Source = src, Color = V4(Color.FromRgb(0xFF, 0xD5, 0x4F)) };
        var c = CultureInfo.CurrentCulture;
        string size = info.Kind switch
        {
            FaceKind.Cylinder or FaceKind.Sphere => string.Format(c, T(", straal {0:0.####} mm", ", radius {0:0.####} mm"), info.Radius * 1000),
            FaceKind.Torus => string.Format(c, ", R {0:0.####} mm, r {1:0.####} mm", info.Radius * 1000, info.MinorRadius * 1000),
            FaceKind.Cone => string.Format(c, T(", halve tophoek {0:0.####}°", ", half apex angle {0:0.####}°"), Math.Atan(info.MinorRadius) * 180 / Math.PI),
            _ => "",
        };
        var k = Kinds.First(x => x.kind == info.Kind);
        PickTitle.Text = $"{k.emoji}  {KindName(k.kind)}{size}";
        PickText.Text = T($"{n:N0} driehoekjes · body {face.body + 1}, vlak {face.face + 1} · aangeklikt driehoekje nr. {source}", $"{n:N0} triangles · body {face.body + 1}, face {face.face + 1} · clicked triangle no. {source}")
            + (info.Group >= 0 ? string.Format(c, T(" · jouw groep {0}, afwijking {1:0.###} mm", " · your group {0}, deviation {1:0.###} mm"), info.Group + 1, info.Deviation * 1000) : "")
            + T("\nShift-klik = dit hele vlak selecteren · Shift-dubbelklik = openen.", "\nShift-click = select this whole face · Shift-double-click = open.");
        _picked = source;
        OpenFaceButton.IsVisible = !(info.Kind == FaceKind.Facet);
        PickCard.IsVisible = true;
    }

    /// <summary>The triangle whose face the face card shows.</summary>
    private int _picked = -1;

    private void OpenPickedFace_Click(object? sender, RoutedEventArgs e)
    {
        if (_picked >= 0 && _picked < _faceOf.Length && InfoOf(_picked) is { Kind: not FaceKind.Facet }) OpenFace(_picked);
    }
}
