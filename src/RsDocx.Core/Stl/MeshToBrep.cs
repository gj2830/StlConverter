using System.Globalization;
using RsDocx.Core.Brep;

namespace RsDocx.Core.Stl;

/// <summary>
/// Turns one closed, consistently oriented triangle shell into a B-rep with planes and recognised cylinders.
/// See NOTES.md ("Plan cilinderherkenning"). Throws <see cref="MeshException"/> when the result cannot be made exact;
/// the caller then falls back to plain planar facets.
/// </summary>
public sealed class MeshToBrep
{
    private sealed class Region
    {
        public readonly List<int> Tris = new();
        public bool IsCylinder;
        /// <summary>Sphere: Origin = centre, Radius, Hollow.</summary>
        public bool IsSphere;
        /// <summary>Torus: Origin = centre, Axis, Radius = major radius, Minor = tube radius, Hollow.</summary>
        public bool IsTorus; public double Minor;
        /// <summary>Cone: Origin on the axis, Radius there, Minor = slope (tan of the half angle, &gt; 0) along Axis, Hollow.</summary>
        public bool IsCone;
        public bool Curved => IsCylinder || IsSphere || IsTorus || IsCone;
        public Cone Con => new(Origin, Axis, Radius, Minor);
        public Torus Tor => new(Origin, Axis, Radius, Minor);
        /// <summary>Given by the user (a shape hint): never dissolved, never grown into another surface.</summary>
        public bool Fixed;
        /// <summary>A face kept from an earlier result: fixed, but it may give way in a conflict.</summary>
        public bool Soft;
        /// <summary>For hinted regions: largest distance of a vertex from the fitted surface (metres).</summary>
        public double FitDeviation;
        // plane
        public Vec3 N; public double D;
        // cylinder
        public Vec3 Axis, Origin; public double Radius; public bool Hollow;
        public Vec3 Centroid;
        /// <summary>A single planar triangle: no fitted constraint, its plane follows its (placed) vertices.</summary>
        public bool Free => !Curved && Tris.Count == 1;
    }

    /// <summary>Thrown inside a round: these triangles must stay single facets; the round is repeated.</summary>
    private sealed class RetryException(HashSet<int> tris, string reason, HashSet<int>? absorb = null, HashSet<int>? peel = null) : Exception(reason)
    {
        public HashSet<int> Tris { get; } = tris;
        /// <summary>Facets at the edge of a curved surface that must stay out of it (the surface shrinks a little).</summary>
        public HashSet<int> Peel { get; } = peel ?? new();
        /// <summary>Single facets that lie on a neighbouring curved surface and should be taken into it.</summary>
        public HashSet<int> Absorb { get; } = absorb ?? new();
    }

    /// <summary>Facets to be joined to a curved neighbour regardless of their (unreliable) normal.</summary>
    private readonly HashSet<int> _absorb = new();
    /// <summary>Facets that may not become part of a cylinder or sphere.</summary>
    private readonly HashSet<int> _noCurve = new();
    /// <summary>Facets that may not become part of a torus (a torus there caused a conflict: tori are the least trusted fit,
    /// e.g. a short piece of a rolling-ball blend also fits a torus closely).</summary>
    private readonly HashSet<int> _noTorus = new();
    /// <summary>Facets of a dissolved cylinder / sphere: they may not form that kind of surface again, but may still join
    /// others (a false "cylinder" on a sphere must be able to rejoin the sphere). A second conflict forces them single.</summary>
    private readonly HashSet<int> _noCylinder = new(), _noSphere = new(), _noCone = new();
    private int ExclusionCount => _hintExclude.Count + _droppedHints.Count + _forced.Count + _absorb.Count + _noCurve.Count + _noTorus.Count + _noCylinder.Count + _noSphere.Count + _noCone.Count;
    /// <summary>Facets in a torus in the current round.</summary>
    private readonly HashSet<int> _torusTris = new();

    private readonly HashSet<int> _forced = new();
    public int Rounds { get; private set; }

    private readonly RecognitionCache _cache;

    /// <summary>Indices (into the hints) of groups the user forces into their kind, however far their facets are off.</summary>
    private readonly IReadOnlySet<int> _forceFit;

    /// <summary>Facets the user detached from a face: they stay single facets (also in <see cref="_forced"/>) and are never
    /// absorbed into a surface (see <see cref="MeshOptions.KeepFacets"/>).</summary>
    private readonly HashSet<int> _keep = new();

    /// <summary>Called at the start of every recognition round (progress display).</summary>
    public Action<int>? RoundStarted { get; set; }

    /// <summary>Faces of the result that are a single facet (0: everything became a surface).</summary>
    public int SingleFacets { get; private set; }

    private readonly List<Vec3> _v;
    private readonly List<int[]> _t;
    private readonly MeshOptions _opt;
    private int[] _regionOf = [];
    private readonly List<Region> _regions = new();
    private readonly Dictionary<(int, int), int> _triOfHalfEdge = new();

    public int CylindersFound { get; private set; }
    public int SpheresFound { get; private set; }
    public int ToriFound { get; private set; }
    public int ConesFound { get; private set; }
    /// <summary>Edges written as traced B-spline intersection curves (after a successful <see cref="Build"/>).</summary>
    public int TracedEdges { get; private set; }

    /// <summary>After a successful <see cref="Build"/>: face index (in the body) of every input triangle.</summary>
    public IReadOnlyList<int> FaceOfTriangle => _regionOf;

    /// <summary>After a successful <see cref="Build"/>: kind and size of every face.</summary>
    public IReadOnlyList<FaceInfo> FaceInfos => _regions.Select(r =>
        (r.IsTorus ? new FaceInfo(FaceKind.Torus, r.Radius, r.Minor)
        : r.IsCone ? new FaceInfo(FaceKind.Cone, r.Radius, r.Minor)
        : r.IsSphere ? new FaceInfo(FaceKind.Sphere, r.Radius)
        : r.IsCylinder ? new FaceInfo(FaceKind.Cylinder, r.Radius)
        : new FaceInfo(r.Free && !r.Fixed ? FaceKind.Facet : FaceKind.Plane)) with
        {
            Group = r.Fixed ? _hintOf[r] : -1, Deviation = r.FitDeviation, Hollow = r.Hollow,
            Origin = r.Curved ? r.Origin : r.N * r.D, Axis = r.Curved ? r.Axis : r.N,
        }).ToList();
    private static readonly bool Debug = Environment.GetEnvironmentVariable("RSDEBUG") == "1";
    private static int _dbgCount;
    private static void Dbg(string s) { if (Debug && _dbgCount++ < 20000) Console.Error.WriteLine("[rec] " + s); }
    public List<string> Notes { get; } = new();

    private static readonly int TraceTri = int.TryParse(Environment.GetEnvironmentVariable("RSTRACE"), out int tt) ? tt : -1;
    private void Trace(string phase)
    {
        if (TraceTri < 0 || TraceTri >= _t.Count) return;
        string D(int t)
        {
            var r = _regions[_regionOf[t]];
            string kind = r.IsSphere ? $"sphere r={r.Radius * 1000:0.###}" : r.IsTorus ? "torus" : r.IsCylinder ? "cyl" : r.Tris.Count == 1 ? "facet" : $"plane{r.Tris.Count}";
            return $"{t}:{_regionOf[t]}:{kind}" + (_forced.Contains(t) ? " F" : "") + (_noCurve.Contains(t) ? " P" : "") + (_absorb.Contains(t) ? " A" : "") + (_noSphere.Contains(t) ? " nS" : "") + (_noTorus.Contains(t) ? " nT" : "") + (_noCylinder.Contains(t) ? " nC" : "");
        }
        Dbg($"trace {phase}: {D(TraceTri)} | " + string.Join(" | ", Enumerable.Range(0, 3).Select(k => D(Across(TraceTri, k)))));
    }

    private readonly IReadOnlyList<(IReadOnlyList<int> tris, FaceKind kind, bool soft, FaceInfo? surface)> _hints;
    /// <summary>Triangles left out of the user's groups because they do not fit the group's surface.</summary>
    private readonly HashSet<int> _hintExclude = new();
    /// <summary>Soft hints that gave way in a conflict: not used again.</summary>
    private readonly HashSet<int> _droppedHints = new();
    private readonly HashSet<int> _hinted = new();

    /// <param name="hints">Groups of triangles (indices into <paramref name="tris"/>) that must become one face of the given
    /// kind (<see cref="FaceKind.Facet"/> = automatic: the best fitting of plane, cylinder, sphere and torus).</param>
    /// <param name="frozen">Triangles that stay single facets unless a hint covers them (see <see cref="MeshOptions.FreeTriangles"/>).</param>
    public MeshToBrep(List<Vec3> verts, List<int[]> tris, MeshOptions opt, IReadOnlyList<(IReadOnlyList<int> tris, FaceKind kind, bool soft, FaceInfo? surface)>? hints = null,
        IReadOnlySet<int>? frozen = null, RecognitionCache? cache = null, IReadOnlySet<int>? forceFit = null, IReadOnlySet<int>? keepFacets = null)
    {
        _forceFit = forceFit ?? new HashSet<int>();
        _v = verts; _t = tris; _opt = opt;
        _cache = cache ?? new RecognitionCache();
        _hints = hints ?? [];
        foreach (var h in _hints) _hinted.UnionWith(h.tris);
        // (a soft hint that gives way later frees its triangles: they are not frozen)
        if (frozen != null) foreach (int t in frozen) if (!_hinted.Contains(t)) _forced.Add(t);
        if (keepFacets != null) foreach (int t in keepFacets) if (!_hinted.Contains(t)) { _keep.Add(t); _forced.Add(t); }
        for (int t = 0; t < tris.Count; t++)
            for (int k = 0; k < 3; k++) _triOfHalfEdge[(tris[t][k], tris[t][(k + 1) % 3])] = t;
    }

    public BrepBody Build()
    {
        string last = "";
        for (Rounds = 1; Rounds <= _opt.RecognitionRounds; Rounds++)
        {
            _opt.Cancel.ThrowIfCancellationRequested();
            RoundStarted?.Invoke(Rounds);
            _regions.Clear();
            CylindersFound = 0; SpheresFound = 0; ToriFound = 0; ConesFound = 0; TracedEdges = 0; _torusTris.Clear();
            int before = ExclusionCount; // some steps add exclusions before they throw
            var sw = System.Diagnostics.Stopwatch.StartNew();
            void Lap(string what) { if (Debug) Dbg($"  {what}: {sw.ElapsedMilliseconds} ms"); sw.Restart(); }
            try
            {
                SegmentPlanes(); Lap("segment"); Trace("segment");
                FitPlanes(); Lap("fit planes");
                FitHints(); Lap("hints");
                // cylinders first: they are found very reliably (sleutelrek: fit 1e-9 m); facets around a bend between two
                // rods can also fit a sphere and must not be taken by it. Columns of sphere facets are kept out of the
                // cylinders by the sphere test in GrowCylinder.
                if (_opt.RecognizeCylinders) FindCylinders(); Lap("cylinders");
                if (_opt.RecognizeCylinders) MergeCoincidentCylinders();
                Trace("cylinders");
                // cones before spheres and tori: a band between two circles fits all three exactly (see FindCones)
                if (_opt.RecognizeCones) { FindCones(); Lap("cones"); }
                if (_opt.RecognizeSpheres) { FindSpheres(); Lap("spheres"); Trace("spheres"); MergeOnSpheres(); Lap("merge spheres"); }
                if (_opt.RecognizeTori) { FindTori(); Lap("tori"); if (_opt.RecognizeCylinders) GrowTangentCylinders(); }
                AbsorbFacets(); Trace("absorb");
                MergeIntoGroups();
                MergeGroupPieces();
                if (_opt.RecognizeCylinders) MergeCoincidentCylinders(); // absorbed facets can make two pieces neighbours
                DropEmptyRegions();
                Regularise(); if (_opt.RecognizeCones) RegulariseCones(); Lap("regularise"); Trace("regularise");
                var result = MakeBody(); Lap("make body");
                SingleFacets = _regions.Count(r => r.Free);
                if (Debug)
                {
                    Dbg($"result after {Rounds} rounds: {result.Faces.Count} faces, {_regions.Count(r => r.Free)} single facets");
                    // where are the single facets? (2 mm cells)
                    foreach (var g in _regions.Where(r => r.Free).GroupBy(r => { var c = _v[_t[r.Tris[0]][0]]; return (Math.Floor(c.X * 500), Math.Floor(c.Y * 500), Math.Floor(c.Z * 500)); })
                        .OrderByDescending(g => g.Count()).Take(15))
                        Dbg(FormattableString.Invariant($"  facets near ({g.Key.Item1 * 2 + 1:F0},{g.Key.Item2 * 2 + 1:F0},{g.Key.Item3 * 2 + 1:F0}) mm: {g.Count()}{(g.Any(r => _forced.Contains(r.Tris[0])) ? " (some forced)" : "")}"));
                    foreach (var r in _regions.Where(r => r.Curved && r.Tris.Count > 0))
                    {
                        var c = r.Tris.SelectMany(t => _t[t]).Distinct().Aggregate(new Vec3(0, 0, 0), (acc, v) => acc + _v[v]);
                        int n = r.Tris.SelectMany(t => _t[t]).Distinct().Count();
                        c = c * (1.0 / n);
                        Dbg(FormattableString.Invariant($"  {(r.IsCylinder ? "cyl" : r.IsSphere ? "sphere" : "torus")} r={r.Radius * 1000:F4} {r.Tris.Count} tris around ({c.X * 1000:F2},{c.Y * 1000:F2},{c.Z * 1000:F2})"));
                    }
                }
                return result;
            }
            catch (RetryException e)
            {
                Lap("step that asked for a retry");
                // a dissolved curved surface only stops its facets from forming that kind of surface again
                foreach (int t in e.Tris)
                {
                    var r = _regions[_regionOf[t]];
                    if (r.Soft && _hintOf.TryGetValue(r, out int sh)) { _droppedHints.Add(sh); continue; }
                    var ex = r.IsTorus ? _noTorus : r.IsCylinder ? _noCylinder : r.IsSphere ? _noSphere : r.IsCone ? _noCone : null;
                    if (ex == null || !ex.Add(t)) _forced.Add(t);
                }
                _absorb.UnionWith(e.Absorb);
                _noCurve.UnionWith(e.Peel);
                last = e.Message;
                Dbg($"round {Rounds}: {e.Message}; {_forced.Count} facets forced, {_absorb.Count} to absorb, {_noCurve.Count} peeled");
                if (ExclusionCount == before) break;
            }
        }
        throw new MeshException($"no exact geometry after {Rounds - 1} rounds (last problem: {last})");
    }

    // ------------------------------------------------------------------ segmentation

    private Vec3 TriNormal(int t) => (_v[_t[t][1]] - _v[_t[t][0]]).Cross(_v[_t[t][2]] - _v[_t[t][0]]).Normalized();
    private int Across(int t, int k) => _triOfHalfEdge[(_t[t][(k + 1) % 3], _t[t][k])];

    /// <summary>
    /// How far a facet's normal may turn from the plane and still join it: the angle tolerance, or more for a small facet
    /// far from the origin, whose float32 corners (rounded to about 6e-8 of their coordinates) alone tilt its normal by up
    /// to 2 x rounding / height (bigbox, 300 mm out: facets 0.09 mm high are off by 0.02 degrees, the tolerance is 0.01,
    /// and a wall and floor of 1600 such facets stayed facets). Three times that, at most 1 degree; the distance check
    /// stays as it is.
    /// </summary>
    private double CosAllowed(int t, double cosTol)
    {
        var a = _v[_t[t][0]]; var b = _v[_t[t][1]]; var c = _v[_t[t][2]];
        double l = Math.Max((b - a).Length, Math.Max((c - b).Length, (a - c).Length));
        if (l <= 0) return cosTol;
        double h = (b - a).Cross(c - a).Length / l;
        double far = new[] { a, b, c }.Max(p => Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))));
        double rounding = 6e-8 * far;
        double slack = h > 0 ? Math.Min(Math.PI / 180, 3 * 2 * rounding / h) : 0;
        return Math.Min(cosTol, Math.Cos(slack));
    }

    private void SegmentPlanes()
    {
        _regionOf = Enumerable.Repeat(-1, _t.Count).ToArray();
        // user groups: one region per connected piece
        for (int hi = 0; hi < _hints.Count; hi++)
        {
            if (_droppedHints.Contains(hi)) continue;
            var set = new HashSet<int>(_hints[hi].tris.Where(t => !_hintExclude.Contains(t)));
            foreach (int seed in set)
            {
                if (_regionOf[seed] >= 0) continue;
                var r = new Region { Fixed = true, Soft = _hints[hi].soft };
                int id = _regions.Count;
                _regions.Add(r);
                r.Tris.Add(seed); _regionOf[seed] = id;
                for (int qi = 0; qi < r.Tris.Count; qi++)
                    for (int k = 0; k < 3; k++)
                    {
                        int nb = Across(r.Tris[qi], k);
                        if (_regionOf[nb] >= 0 || !set.Contains(nb)) continue;
                        _regionOf[nb] = id; r.Tris.Add(nb);
                    }
                _hintOf[r] = hi;
            }
        }
        double cosTol = Math.Cos(_opt.AngleToleranceDeg * Math.PI / 180);
        for (int seed = 0; seed < _t.Count; seed++)
        {
            if (_regionOf[seed] >= 0) continue;
            var r = new Region();
            int id = _regions.Count;
            _regions.Add(r);
            var n0 = TriNormal(seed); var p0 = _v[_t[seed][0]];
            r.Tris.Add(seed); _regionOf[seed] = id;
            if (_forced.Contains(seed) || _noCurve.Contains(seed) || _opt.OnlyHints) continue;
            for (int qi = 0; qi < r.Tris.Count; qi++)
            {
                int t = r.Tris[qi];
                for (int k = 0; k < 3; k++)
                {
                    int nb = Across(t, k);
                    if (_regionOf[nb] >= 0 || _forced.Contains(nb) || _noCurve.Contains(nb) || TriNormal(nb).Dot(n0) < CosAllowed(nb, cosTol)) continue;
                    if (_t[nb].Any(v => Math.Abs(n0.Dot(_v[v] - p0)) > _opt.DistanceTolerance)) continue;
                    _regionOf[nb] = id; r.Tris.Add(nb);
                }
            }
        }
        // regions with a pinched boundary (two boundary edges leaving one vertex): the facets at that vertex become single
        // facets, the rest stays one plane (bigbox: a wall of 15 facets touching a cylinder's seam in one corner used to fall
        // apart completely); only when that would leave almost nothing is the whole region split
        foreach (var (r, id) in _regions.ToList().Select((r, i) => (r, i)))
        {
            if (r.Tris.Count < 2 || r.Fixed) continue;
            while (r.Tris.Count >= 2)
            {
                var outgoing = new HashSet<int>();
                int pinchAt = -1;
                foreach (int t in r.Tris)
                    for (int k = 0; k < 3; k++)
                        if (_regionOf[Across(t, k)] != id && !outgoing.Add(_t[t][k])) { pinchAt = _t[t][k]; break; }
                if (pinchAt < 0) break;
                var at = r.Tris.Where(t => _t[t].Contains(pinchAt)).ToList();
                var split = at.Count >= r.Tris.Count - 1 ? r.Tris.Skip(1).ToList() : at;
                foreach (int t in split)
                {
                    r.Tris.Remove(t);
                    var nr = new Region(); nr.Tris.Add(t);
                    _regionOf[t] = _regions.Count; _regions.Add(nr);
                }
                if (split != at) break;
            }
        }
    }

    private IEnumerable<int> VerticesOf(Region r) => r.Tris.SelectMany(t => _t[t]).Distinct();

    /// <summary>A single needle-like facet (smallest height below 0.01 mm): its normal is unreliable.</summary>
    /// <summary>Peeled (kept out of curved surfaces), unless later found to lie on one (absorb wins: the sets only grow).</summary>
    private bool Peeled(int t) => _noCurve.Contains(t) && !_absorb.Contains(t);

    private bool IgnoreNormal(Region r) => IsSliver(r) || (r.Tris.Count == 1 && _absorb.Contains(r.Tris[0]));

    private bool IsSliver(Region r)
    {
        if (r.Tris.Count != 1) return false;
        var t = _t[r.Tris[0]];
        var a = _v[t[0]]; var b = _v[t[1]]; var c = _v[t[2]];
        double longest = Math.Max((b - a).Length, Math.Max((c - b).Length, (a - c).Length));
        return (b - a).Cross(c - a).Length / longest < 1e-5;
    }

    private static Vec3 Grad(Region r, Vec3 p) { Signed(r, p, out var g); return g; }

    private static double SurfaceDistance(Region r, Vec3 p) => Math.Abs(Signed(r, p, out _));

    /// <summary>Signed distance from p to the region's surface (positive on the side of its natural normal) and its gradient.</summary>
    private static double Signed(Region s, Vec3 p, out Vec3 grad)
    {
        if (s.IsTorus) return s.Tor.Distance(p, out grad);
        if (s.IsCone) return s.Con.Distance(p, out grad);
        if (s.IsSphere) { var q = p - s.Origin; double lq = q.Length; grad = q * (1 / lq); return lq - s.Radius; }
        if (!s.IsCylinder) { grad = s.N; return s.N.Dot(p) - s.D; }
        var w = p - s.Origin; w -= s.Axis * w.Dot(s.Axis);
        double l = w.Length; grad = w * (1 / l);
        return l - s.Radius;
    }

    /// <summary>Surface area of a region. When surfaces conflict, the smallest one is turned into facets: large flat
    /// faces are the most trustworthy and should survive (a long wall of 18 facets must not lose to a small fillet).</summary>
    /// <summary>Which surface gives way in a conflict: a small torus first (short pieces of blends also fit tori, yubikey),
    /// otherwise the smallest surface (a large torus is as trustworthy as a sphere: boltest's fillet has 1781 facets).</summary>
    /// <summary>Extra tolerance factor for facets in the tangent seam of a torus.</summary>
    private const double SeamFactor = 4;

    private (int, double) VictimRank(Region r) => (r.IsTorus && r.Tris.Count <= _opt.PeelThreshold * 4 ? 0 : 1, Area(r));

    private double Area(Region r) => r.Tris.Sum(t => (_v[_t[t][1]] - _v[_t[t][0]]).Cross(_v[_t[t][2]] - _v[_t[t][0]]).Length / 2);

    private readonly Dictionary<Region, int> _hintOf = new();

    /// <summary>Fits the user's groups with the requested kind of surface (or the best fitting one).</summary>
    private void FitHints()
    {
        // one fit per group over all its triangles (a group can fall apart in several pieces; one strip alone does not
        // define a cylinder), then the same surface for every piece
        foreach (var pieces in _regions.Where(r => r.Fixed).GroupBy(r => _hintOf[r]).ToList())
        {
            int hi = pieces.Key;
            var kind = _hints[hi].kind;
            var tris = pieces.SelectMany(x => x.Tris).ToList();
            Region r;
            if (_hints[hi].surface is { } sf && sf.Kind == kind)
            {
                // the exact surface of an earlier result: keep it (refitting from the facets is less exact)
                r = TempRegion(tris);
                r.Origin = sf.Origin; r.Axis = sf.Axis; r.Radius = sf.Radius; r.Minor = sf.MinorRadius; r.Hollow = sf.Hollow;
                switch (kind)
                {
                    case FaceKind.Cylinder: r.IsCylinder = true; break;
                    case FaceKind.Sphere: r.IsSphere = true; break;
                    case FaceKind.Torus: r.IsTorus = true; break;
                    case FaceKind.Cone: r.IsCone = true; break;
                    default: r.N = sf.Axis; r.D = sf.Axis.Dot(sf.Origin); break;
                }
                r.FitDeviation = VerticesOf(r).Max(v => SurfaceDistance(r, _v[v]));
            }
            else
            {
                r = TempRegion(tris);
                var chosen = FitKind(r, kind) ?? throw new MeshException($"group {hi + 1}: no {kind} fits these triangles");
                if (chosen.dev > _opt.MaxHintDeviation && pieces.All(x => x.Soft))
                {
                    _droppedHints.Add(hi);
                    throw new RetryException(new HashSet<int>(), $"kept face {hi + 1} no longer fits");
                }
                // forced ("make it flat"): the fit stands however far the facets are off; their vertices are moved onto it
                // (a vertex of a group may move twice its deviation, see the vertex placement)
                if (chosen.dev > _opt.MaxHintDeviation && !_forceFit.Contains(hi))
                {
                    // the selection was a little too wide: leave out the worst fitting triangle and fit again, at most
                    // a fifth of them (a few outliers pull a least-squares fit far off, so one at a time)
                    var cur = new List<int>(tris);
                    var dropped = new List<int>();
                    var kindNow = chosen.kind;
                    while (chosen.dev > _opt.MaxHintDeviation && dropped.Count < tris.Count / 5 && cur.Count > 2)
                    {
                        var worst = cur.MaxBy(t => _t[t].Max(v => SurfaceDistance(r, _v[v])));
                        cur.Remove(worst); dropped.Add(worst);
                        r = TempRegion(cur);
                        chosen = FitKind(r, kind) ?? chosen;
                        Dbg(FormattableString.Invariant($"group {hi + 1}: without triangle {worst} ({_t[worst].Max(v => SurfaceDistance(r, _v[v])) * 1000:0.###} mm off now): {chosen.kind} deviates {chosen.dev * 1000:0.####} mm, r = {r.Radius * 1000:0.###}"));
                    }
                    if (chosen.dev <= _opt.MaxHintDeviation)
                    {
                        _hintExclude.UnionWith(dropped);
                        Dbg($"group {hi + 1}: leaving out {dropped.Count} triangles that do not fit its {chosen.kind}");
                        throw new RetryException(new HashSet<int>(), $"group {hi + 1} trimmed");
                    }
                    throw new MeshException(FormattableString.Invariant($"group {hi + 1}: the {kindNow} deviates {chosen.dev * 1000:0.###} mm from the selected triangles (more than {_opt.MaxHintDeviation * 1000:0.###} mm)"));
                }
                r.FitDeviation = chosen.dev;
                Dbg(FormattableString.Invariant($"group {hi + 1}: {chosen.kind}, max deviation {chosen.dev * 1000:F4} mm, {pieces.Count()} piece(s){(_forceFit.Contains(hi) ? ", forced" : "")}"));
            }
            foreach (var x in pieces)
            {
                x.IsCylinder = r.IsCylinder; x.IsSphere = r.IsSphere; x.IsTorus = r.IsTorus; x.IsCone = r.IsCone;
                x.Origin = r.Origin; x.Axis = r.Axis; x.Radius = r.Radius; x.Minor = r.Minor; x.Hollow = r.Hollow;
                x.FitDeviation = r.FitDeviation;
                if (!r.Curved) { x.N = r.N; x.D = r.D; }
            }
        }
    }

    /// <summary>A scratch region over some triangles, with area-weighted normal and centroid (for fitting).</summary>
    private Region TempRegion(List<int> tris)
    {
        var r = new Region();
        r.Tris.AddRange(tris);
        var nsum = new Vec3(0, 0, 0); var csum = new Vec3(0, 0, 0); double asum = 0;
        foreach (int t in tris)
        {
            var cr = (_v[_t[t][1]] - _v[_t[t][0]]).Cross(_v[_t[t][2]] - _v[_t[t][0]]);
            nsum += cr; double ar = cr.Length; asum += ar;
            csum += (_v[_t[t][0]] + _v[_t[t][1]] + _v[_t[t][2]]) * (ar / 3);
        }
        r.N = nsum.Length > 1e-30 ? nsum.Normalized() : TriNormal(tris[0]);
        r.Centroid = asum > 0 ? csum * (1 / asum) : _v[_t[tris[0]][0]];
        return r;
    }

    /// <summary>Fits the kind (Facet = automatic: the simplest that fits about as well as the best) to the region's
    /// triangles and applies it to the region; null when nothing could be fitted.</summary>
    private (FaceKind kind, double dev)? FitKind(Region r, FaceKind kind)
    {
        var pts = VerticesOf(r).Select(v => _v[v]).ToList();
        var fits = new List<(FaceKind kind, double dev, Action apply)>();
        if (kind is FaceKind.Plane or FaceKind.Facet && pts.Count >= 3)
        {
            var pl = FitPlane(pts, out double dev);
            fits.Add((FaceKind.Plane, dev, () => { r.N = pl.N.Dot(r.N) >= 0 ? pl.N : pl.N * -1; r.D = r.N.Dot(pl.Centroid); }));
        }
        if (kind is FaceKind.Cylinder or FaceKind.Facet && pts.Count >= 4)
        {
            // axis: the direction the facet normals are perpendicular to; then a geometric least-squares refinement
            // (robust: every pair of facet normals proposes an axis, their cross product; the axis that most facets are
            // perpendicular to wins, and only those facets are used: a few facets of a neighbouring face do not spoil it)
            var cpts = pts;
            var nrm = r.Tris.Select(t => (t, n: TriNormal(t))).Where(x => double.IsFinite(x.n.X)).ToList();
            double sin5 = Math.Sin(5 * Math.PI / 180);
            Vec3 axis = default; int bestVotes = -1;
            int step = Math.Max(1, nrm.Count / 40);
            for (int x = 0; x < nrm.Count; x += step)
                for (int y = x + 1; y < nrm.Count; y += step)
                {
                    var cand = nrm[x].n.Cross(nrm[y].n);
                    if (cand.Length < 0.05) continue; // nearly parallel normals say little
                    cand = cand.Normalized();
                    int votes = nrm.Count(z => Math.Abs(z.n.Dot(cand)) < sin5);
                    if (votes > bestVotes) { bestVotes = votes; axis = cand; }
                }
            if (bestVotes > 0)
            {
                var m = new double[3, 3];
                foreach (var (t, n) in nrm.Where(z => Math.Abs(z.n.Dot(axis)) < sin5))
                {
                    double w = (_v[_t[t][1]] - _v[_t[t][0]]).Cross(_v[_t[t][2]] - _v[_t[t][0]]).Length;
                    double[] nv = [n.X, n.Y, n.Z];
                    for (int a2 = 0; a2 < 3; a2++) for (int b2 = 0; b2 < 3; b2++) m[a2, b2] += w * nv[a2] * nv[b2];
                }
                var (lam, u) = MeshConverter.Eigen3(m);
                int k = Array.IndexOf(lam, lam.Min());
                axis = new Vec3(u[0, k], u[1, k], u[2, k]).Normalized();
                // fit on the points of the facets that agree with the axis
                var inl = nrm.Where(z => Math.Abs(z.n.Dot(axis)) < sin5).SelectMany(z => _t[z.t]).Distinct().Select(v => _v[v]).ToList();
                if (inl.Count >= 4) cpts = inl;
            }
            if (FitCircle(cpts, axis, out var o, out double rad, out _) && rad > 0 && CylinderFit.Refine(cpts, ref o, ref axis, ref rad))
            {
                var (oo, aa, rr) = (o, axis, rad);
                double dev = pts.Max(p => Math.Abs(CylinderFit.Distance(p, oo, aa, rr, out _)));
                fits.Add((FaceKind.Cylinder, dev, () =>
                {
                    r.IsCylinder = true; r.Axis = aa; r.Origin = oo; r.Radius = rr;
                    // by the majority of the facets (the summed normal of a full ring is about zero)
                    int inward = r.Tris.Count(t =>
                    {
                        var rd = (_v[_t[t][0]] + _v[_t[t][1]] + _v[_t[t][2]]) * (1.0 / 3) - oo; rd -= aa * rd.Dot(aa);
                        return rd.Dot(TriNormal(t)) < 0;
                    });
                    r.Hollow = 2 * inward > r.Tris.Count;
                }));
            }
        }
        if (kind is FaceKind.Sphere or FaceKind.Facet && FitSphere(pts, out var c, out double sr, out double sdev))
            fits.Add((FaceKind.Sphere, sdev, () => { r.IsSphere = true; r.Origin = c; r.Radius = sr; r.Hollow = (r.Centroid - c).Dot(r.N) < 0; }));
        if (kind is FaceKind.Torus or FaceKind.Facet)
        {
            var lines = r.Tris.Select(t => ((_v[_t[t][0]] + _v[_t[t][1]] + _v[_t[t][2]]) * (1.0 / 3), TriNormal(t))).ToList();
            if (TorusFit.Fit(pts, lines, out var tor, out double tdev))
                fits.Add((FaceKind.Torus, tdev, () =>
                {
                    r.IsTorus = true; r.Origin = tor.Centre; r.Axis = tor.Axis; r.Radius = tor.R; r.Minor = tor.r;
                    tor.Distance(r.Centroid, out var g); r.Hollow = g.Dot(r.N) < 0;
                }));
        }
        if (kind is FaceKind.Cone or FaceKind.Facet)
        {
            var lines = r.Tris.Select(t => ((_v[_t[t][0]] + _v[_t[t][1]] + _v[_t[t][2]]) * (1.0 / 3), TriNormal(t))).ToList();
            if (ConeFit.Fit(pts, lines, out var cone, out double cdev))
                fits.Add((FaceKind.Cone, cdev, () =>
                {
                    r.IsCone = true; r.Origin = cone.Origin; r.Axis = cone.Axis; r.Radius = cone.Radius; r.Minor = cone.Slope;
                    cone.Distance(r.Centroid, out var g); r.Hollow = g.Dot(r.N) < 0;
                }));
        }
        if (fits.Count == 0) return null;
        double best = fits.Min(f => f.dev);
        var chosen = fits.First(f => f.dev <= Math.Max(_opt.CylinderFitTolerance, 1.5 * best));
        r.IsCylinder = r.IsSphere = r.IsTorus = r.IsCone = false;
        chosen.apply();
        return (chosen.kind, chosen.dev);
    }

    private void FitPlanes()
    {
        foreach (var r in _regions)
        {
            var n = new Vec3(0, 0, 0);
            var c = new Vec3(0, 0, 0); double area = 0;
            foreach (int t in r.Tris)
            {
                var cr = (_v[_t[t][1]] - _v[_t[t][0]]).Cross(_v[_t[t][2]] - _v[_t[t][0]]);
                n += cr;
                double a = cr.Length;
                c += (_v[_t[t][0]] + _v[_t[t][1]] + _v[_t[t][2]]) * (a / 3); area += a;
            }
            r.N = n.Normalized();
            r.Centroid = c * (1 / area);
            r.D = VerticesOf(r).Average(v => r.N.Dot(_v[v]));
        }
    }

    // ------------------------------------------------------------------ cylinders

    /// <summary>Mesh edges shared by regions a and b (as vertex pairs).</summary>
    private Dictionary<(int, int), List<(int, int)>> SharedEdges()
    {
        var shared = new Dictionary<(int, int), List<(int, int)>>();
        for (int t = 0; t < _t.Count; t++)
            for (int k = 0; k < 3; k++)
            {
                int ra = _regionOf[t], rb = _regionOf[Across(t, k)];
                if (ra >= rb) continue; // each pair and edge once
                if (!shared.TryGetValue((ra, rb), out var l)) shared[(ra, rb)] = l = new();
                l.Add((_t[t][k], _t[t][(k + 1) % 3]));
            }
        return shared;
    }

    private bool AllParallel(List<(int, int)> edges, Vec3 dir, double sinTol) =>
        edges.All(e => { var d = _v[e.Item2] - _v[e.Item1]; return d.Cross(dir).Length <= sinTol * d.Length; });

    /// <summary>During <see cref="FindCylinders"/>: per region, may it join a cylinder (see ExclusionFlags).</summary>
    private bool[]? _cylJoinOf;

    private void FindCylinders()
    {
        double sinTol = Math.Sin(_opt.CylinderAngleToleranceDeg * Math.PI / 180);
        double minStep = Math.Cos(_opt.AngleToleranceDeg * Math.PI / 180), maxStep = Math.Cos(_opt.CylinderMaxStepDeg * Math.PI / 180);
        var shared = SharedEdges();
        var merged = new Dictionary<int, List<(int r, List<int> tris)>>();
        var nbrs = new Dictionary<int, List<int>>();
        foreach (var (a, b) in shared.Keys)
        {
            if (!nbrs.TryGetValue(a, out var la)) nbrs[a] = la = new(); la.Add(b);
            if (!nbrs.TryGetValue(b, out var lb)) nbrs[b] = lb = new(); lb.Add(a);
        }
        List<(int, int)> Shared(int a, int b) => shared[a < b ? (a, b) : (b, a)];
        // (exclusions per region once: see ExclusionFlags)
        (bool[] freeOf, _cylJoinOf) = ExclusionFlags(_noCylinder);
        bool Step(int a, int b, Vec3 axis)
        {
            var ra = _regions[a]; var rb = _regions[b];
            if (ra.Curved || rb.Curved || ra.Tris.Count == 0 || rb.Tris.Count == 0 || ra.Fixed || rb.Fixed) return false;
            if (!freeOf[a] || !freeOf[b]) return false;
            if (Math.Abs(rb.N.Dot(axis)) > sinTol || Math.Abs(ra.N.Dot(axis)) > sinTol) return false;
            double c = ra.N.Dot(rb.N);
            if (c > minStep || c < maxStep) return false; // (nearly) coplanar or too sharp
            return AllParallel(Shared(a, b), axis, sinTol);
        }

        int pairNo = 0;
        foreach (var ((a, b), edges) in shared.OrderByDescending(kv => kv.Value.Count))
        {
            if ((pairNo++ & 255) == 0) _opt.Cancel.ThrowIfCancellationRequested();
            if (_regions[a].Curved || _regions[b].Curved || _regions[a].Tris.Count == 0 || _regions[b].Tris.Count == 0) continue;
            var e0 = edges.MaxBy(e => (_v[e.Item2] - _v[e.Item1]).Length);
            var axis = (_v[e0.Item2] - _v[e0.Item1]).Normalized();
            if (!Step(a, b, axis)) continue;

            if (GrowCylinder(a, b, axis, nbrs, Step) is not var (cluster, origin, radius, hollow)) continue;
            if (Debug)
            {
                var dpts = cluster.SelectMany(r => VerticesOf(_regions[r])).Distinct().Select(v => _v[v]).ToList();
                FitCircle(dpts, axis, out _, out _, out double dres);
                double maxStepDeg = 0;
                foreach (int r1 in cluster) foreach (int r2 in nbrs[r1]) if (cluster.Contains(r2))
                    maxStepDeg = Math.Max(maxStepDeg, Math.Acos(Math.Clamp(_regions[r1].N.Dot(_regions[r2].N), -1, 1)) * 180 / Math.PI);
                Dbg($"cylinder from {a}-{b}: {cluster.Count} strips, r={radius * 1000:F4} mm, {(hollow ? "hollow" : "solid")}, resid {dres:E1} m, max step {maxStepDeg:F2} deg");
            }

            // merge into one cylinder region (remember the strips, to undo it for torus-like neighbours)
            var cyl = _regions[a];
            merged[a] = cluster.Select(r => (r, _regions[r].Tris.ToList())).ToList();
            foreach (int r in cluster.Skip(1))
            {
                foreach (int t in _regions[r].Tris) { _regionOf[t] = a; cyl.Tris.Add(t); }
                _regions[r].Tris.Clear();
            }
            cyl.IsCylinder = true; cyl.Axis = axis; cyl.Origin = origin; cyl.Radius = radius; cyl.Hollow = hollow;
            CylindersFound++;
        }

        // A torus is tessellated as columns that each look like a short cylinder; neighbouring columns have the same
        // radius and axes a few degrees apart. Real mitres (45 or 90 degrees) do not look like that: undo such pairs.
        var undo = new HashSet<int>();
        foreach (var ((a, b), edges) in SharedEdges())
        {
            var ra = _regions[a]; var rb = _regions[b];
            if (!ra.IsCylinder || !rb.IsCylinder) continue;
            double ang = Math.Acos(Math.Clamp(Math.Abs(ra.Axis.Dot(rb.Axis)), -1, 1)) * 180 / Math.PI;
            if (ang > _opt.CylinderAngleToleranceDeg && ang < 20 && Math.Abs(ra.Radius - rb.Radius) <= Math.Max(JoinTol(ra.Radius), 1e-3 * ra.Radius))
            { undo.Add(a); undo.Add(b); }
        }
        foreach (int c in undo)
        {
            if (!merged.TryGetValue(c, out var parts)) continue;
            foreach (var (r, tris) in parts)
            {
                _regions[r].Tris.Clear(); _regions[r].Tris.AddRange(tris);
                foreach (int t in tris) _regionOf[t] = r;
            }
            _regions[c].IsCylinder = false;
            CylindersFound--;
            Dbg($"undid cylinder r={_regions[c].Radius * 1000:F3} mm: torus-like neighbour");
        }
        _cylJoinOf = null;
        DropEmptyRegions();
    }

    private void DropEmptyRegions()
    {
        var keep = _regions.Select((r, i) => (r, i)).Where(x => x.r.Tris.Count > 0).ToList();
        var newId = new int[_regions.Count];
        for (int k = 0; k < keep.Count; k++) newId[keep[k].i] = k;
        for (int t = 0; t < _t.Count; t++) _regionOf[t] = newId[_regionOf[t]];
        var kept = keep.Select(x => x.r).ToList();
        _regions.Clear(); _regions.AddRange(kept);
    }

    /// <summary>
    /// Spheres: seed = a facet region and its neighbours; the points must fit a sphere tightly and the facet normals must
    /// all point away from (solid) or towards (hollow) its centre. Then neighbours are added while their vertices lie on
    /// the sphere (tight, refitting), and finally loosely (as for cylinders). Accepted with enough facets and turning.
    /// </summary>
    private double _sphereRadiusHint;

    /// <summary>
    /// One sphere can come out as several neighbouring regions, with a thin "cylinder" between them (a narrow band round
    /// the equator also fits a cylinder). Neighbours that lie on the same sphere are merged into it.
    /// </summary>
    /// <summary>
    /// Neighbouring pieces of one cylinder (same axis line and radius; separated by a strip that joined later, or split by
    /// growth order) become one region: two regions on the same cylinder cannot share an edge (yubikey R5).
    /// </summary>
    private void MergeCoincidentCylinders()
    {
        double sinTol = Math.Sin(_opt.CylinderAngleToleranceDeg * Math.PI / 180);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var ((a, b), _) in SharedEdges())
            {
                var ra = _regions[a]; var rb = _regions[b];
                if (!ra.IsCylinder || !rb.IsCylinder || ra.Hollow != rb.Hollow || ra.Tris.Count == 0 || rb.Tris.Count == 0 || ra.Fixed || rb.Fixed) continue;
                var (big, small, bi) = Area(ra) >= Area(rb) ? (ra, rb, a) : (rb, ra, b);
                double tol = Math.Max(JoinTol(big.Radius), _opt.CylinderFitTolerance);
                if (big.Axis.Cross(small.Axis).Length > sinTol || Math.Abs(big.Radius - small.Radius) > tol) continue;
                var w = small.Origin - big.Origin; w -= big.Axis * w.Dot(big.Axis);
                if (w.Length > tol) continue;
                // (a cylinder grown from a torus has its seam vertices a little further off: see GrowTangentCylinders)
                if (!VerticesOf(small).All(v => { var q = _v[v] - big.Origin; q -= big.Axis * q.Dot(big.Axis); return Math.Abs(q.Length - big.Radius) <= SeamFactor * tol; })) continue;
                foreach (int t in small.Tris) { _regionOf[t] = bi; big.Tris.Add(t); }
                small.Tris.Clear(); small.IsCylinder = false;
                CylindersFound--;
                Dbg(FormattableString.Invariant($"merged two pieces of cylinder r={big.Radius * 1000:F3} mm"));
                changed = true;
                break;
            }
        }
        DropEmptyRegions();
    }

    private void MergeOnSpheres()
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var ((a, b), _) in SharedEdges())
            {
                var ra = _regions[a]; var rb = _regions[b];
                if (ra.Tris.Count == 0 || rb.Tris.Count == 0 || ra.Fixed || rb.Fixed) continue;
                Region? sph = ra.IsSphere ? ra : rb.IsSphere ? rb : null;
                if (sph == null) continue;
                var other = sph == ra ? rb : ra;
                if (!other.Curved) continue; // planar facets were already offered to the sphere while growing
                if (other.Tris.Any(_noSphere.Contains)) continue;
                double tol = JoinTol(sph.Radius);
                if (other.IsSphere && ((other.Origin - sph.Origin).Length > tol || Math.Abs(other.Radius - sph.Radius) > tol)) continue;
                if (other.Hollow != sph.Hollow) continue;
                if (!VerticesOf(other).All(v => Math.Abs((_v[v] - sph.Origin).Length - sph.Radius) <= tol)) continue;
                int si = sph == ra ? a : b;
                foreach (int t in other.Tris) { _regionOf[t] = si; sph.Tris.Add(t); }
                if (other.IsCylinder) CylindersFound--; else SpheresFound--;
                other.Tris.Clear(); other.IsCylinder = other.IsSphere = false;
                Dbg($"merged a {(other.IsCylinder ? "cylinder" : "region")} into sphere r={sph.Radius * 1000:F3} mm");
                changed = true;
                break; // shared edges changed
            }
        }
        DropEmptyRegions();
    }

    private void FindSpheres()
    {
        double minStep = Math.Cos(_opt.AngleToleranceDeg * Math.PI / 180), maxStep = Math.Cos(_opt.CylinderMaxStepDeg * Math.PI / 180);
        var shared = SharedEdges();
        var nbrs = new Dictionary<int, List<int>>();
        foreach (var (a, b) in shared.Keys)
        {
            if (!nbrs.TryGetValue(a, out var la)) nbrs[a] = la = new(); la.Add(b);
            if (!nbrs.TryGetValue(b, out var lb)) nbrs[b] = lb = new(); lb.Add(a);
        }
        var (freeOf, joinOf) = ExclusionFlags(_noSphere);
        bool Usable(int r) => r < _regions.Count && !_regions[r].Curved && !_regions[r].Fixed && _regions[r].Tris.Count > 0 && freeOf[r];
        bool Joinable(int r) => r < _regions.Count && !_regions[r].Curved && !_regions[r].Fixed && _regions[r].Tris.Count > 0 && joinOf[r];
        double sagitta = 1 - Math.Cos(_opt.CylinderMaxStepDeg * Math.PI / 180 / 2);
        bool Facing(int r, Vec3 c, bool hollow)
        {
            var off = _regions[r].Centroid - c;
            var rad = off.Normalized();
            double d = rad.Dot(_regions[r].N) * (hollow ? -1 : 1);
            // a facet's centroid lies just under the sphere (a flat end cap whose rim is on the sphere lies deep inside)
            return d > Math.Cos(_opt.FacetNormalToleranceDeg * Math.PI / 180) && off.Length >= _sphereRadiusHint * (1 - sagitta);
        }
        bool OnSphere(int r, Vec3 c, double rad, double tol) => VerticesOf(_regions[r]).All(v => Math.Abs((_v[v] - c).Length - rad) <= tol);
        var tried = new HashSet<int>();

        for (int seed = 0; seed < _regions.Count; seed++)
        {
            if ((seed & 255) == 0) _opt.Cancel.ThrowIfCancellationRequested();
            if (!Usable(seed) || tried.Contains(seed) || !nbrs.TryGetValue(seed, out var sn)) continue;
            var cluster = new List<int> { seed };
            foreach (int nb in sn)
            {
                if (!Usable(nb)) continue;
                double c0 = _regions[seed].N.Dot(_regions[nb].N);
                if (c0 < minStep && c0 > maxStep) cluster.Add(nb);
            }
            if (cluster.Count < 4) continue;
            var pts = cluster.SelectMany(r => VerticesOf(_regions[r])).Distinct().Select(v => _v[v]).ToList();
            if (pts.Count < 6 || !FitSphere(pts, out var centre, out double radius, out double res) || res > _opt.CylinderFitTolerance) continue;
            _sphereRadiusHint = radius;
            bool hollow = (_regions[seed].Centroid - centre).Dot(_regions[seed].N) < 0;
            if (!cluster.All(r => Facing(r, centre, hollow))) continue;

            var inCluster = new HashSet<int>(cluster);
            // tight growth with refits
            bool grew = true;
            while (grew)
            {
                grew = false;
                var added = new List<int>();
                foreach (int m in cluster.ToList())
                    foreach (int c in nbrs[m])
                        if (!inCluster.Contains(c) && Usable(c) && Facing(c, centre, hollow) && OnSphere(c, centre, radius, _opt.CylinderFitTolerance))
                        { inCluster.Add(c); cluster.Add(c); added.Add(c); grew = true; }
                if (grew)
                {
                    var all = cluster.SelectMany(r => VerticesOf(_regions[r])).Distinct().Select(v => _v[v]).ToList();
                    if (FitSphere(all, out var c2, out double r2, out double res2) && res2 <= _opt.CylinderFitTolerance) { centre = c2; radius = r2; _sphereRadiusHint = r2; }
                    else
                    {
                        foreach (int c in added) { inCluster.Remove(c); cluster.Remove(c); }
                        break;
                    }
                }
            }
            // loose growth (fit fixed)
            double join = JoinTol(radius);
            grew = join > 0;
            while (grew)
            {
                grew = false;
                foreach (int m in cluster.ToList())
                    foreach (int c in nbrs[m])
                        if (!inCluster.Contains(c) && Joinable(c) && (IgnoreNormal(_regions[c]) || Facing(c, centre, hollow)) && OnSphere(c, centre, radius, join))
                        { inCluster.Add(c); cluster.Add(c); grew = true; }
            }
            tried.UnionWith(cluster);
            if (Debug)
                foreach (int m in cluster)
                    foreach (int c in nbrs[m])
                        if (!inCluster.Contains(c) && _regions[c].Tris.Count == 1 && _absorb.Contains(_regions[c].Tris[0]))
                            Dbg(FormattableString.Invariant($"  sphere r={radius * 1000:F3}: absorbed facet {_regions[c].Tris[0]} not joined: joinable {Joinable(c)}, on {OnSphere(c, centre, radius, join)} (max {VerticesOf(_regions[c]).Max(v => Math.Abs((_v[v] - centre).Length - radius)):E1}, join {join:E1}), curved {_regions[c].Curved}"));
            double span = 0;
            foreach (int r1 in cluster.Where(r => !IsSliver(_regions[r]))) foreach (int r2 in cluster.Where(r => !IsSliver(_regions[r])))
                span = Math.Max(span, Math.Acos(Math.Clamp(_regions[r1].N.Dot(_regions[r2].N), -1, 1)));
            if (cluster.Count < _opt.SphereMinRegions || span * 180 / Math.PI < _opt.CylinderMinSpanDeg || radius > _opt.MaxCurvedRadius) continue;
            // the vertices of a short cylinder lie on two circles, and those lie exactly on a sphere: reject when all facet
            // normals are perpendicular to one direction (cylinder strips), which is never the case on a real sphere patch
            var nn = new double[3, 3];
            foreach (int r in cluster)
            {
                var n = _regions[r].N; double w = Area(_regions[r]); double[] v = [n.X, n.Y, n.Z];
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) nn[i, j] += w * v[i] * v[j];
            }
            var (lam, _) = MeshConverter.Eigen3(nn);
            if (lam.Min() < 1e-4 * lam.Max())
            {
                Dbg($"rejected sphere r={radius * 1000:F4} mm: facet normals all perpendicular to one axis (cylinder)");
                continue;
            }

            var sph = _regions[seed];
            foreach (int r in cluster.Skip(1))
            {
                foreach (int t in _regions[r].Tris) { _regionOf[t] = seed; sph.Tris.Add(t); }
                _regions[r].Tris.Clear();
            }
            sph.IsSphere = true; sph.Origin = centre; sph.Radius = radius; sph.Hollow = hollow;
            SpheresFound++;
            Dbg(FormattableString.Invariant($"sphere from {seed}: {cluster.Count} regions, r={radius * 1000:F4} mm, c=({centre.X * 1000:F4},{centre.Y * 1000:F4},{centre.Z * 1000:F4}), {(hollow ? "hollow" : "solid")}, span {span * 180 / Math.PI:F0} deg"));
        }
    }

    /// <summary>
    /// Grows a cylinder from two neighbouring strips: a third strip gives the first circle fit, after which a neighbouring
    /// strip is only added when its vertices lie on the current circle and it faces the same way (solid / hollow).
    /// </summary>
    private (List<int> cluster, Vec3 origin, double radius, bool hollow)? GrowCylinder(int a, int b, Vec3 axis,
        Dictionary<int, List<int>> nbrs, Func<int, int, Vec3, bool> step)
    {
        var cluster = new List<int> { a, b };
        Vec3 origin = default; double radius = 0; bool hollow;
        var inCluster = new HashSet<int> { a, b };
        List<Vec3> Pts() => cluster.SelectMany(r => VerticesOf(_regions[r])).Distinct().Select(v => _v[v]).ToList();
        double maxStepCos = Math.Cos(_opt.FacetNormalToleranceDeg * Math.PI / 180);
        bool Faces(int r, Vec3 o, bool hol)
        {
            // the strip normal must point (nearly) radially away from the axis (solid) or towards it (hollow)
            var rad = (_regions[r].Centroid - o); rad -= axis * rad.Dot(axis);
            double c = rad.Normalized().Dot(_regions[r].N) * (hol ? -1 : 1);
            // a strip's centroid lies just inside the cylinder
            return c > maxStepCos && (radius <= 0 || rad.Length >= radius * Math.Cos(_opt.CylinderMaxStepDeg * Math.PI / 180 / 2));
        }
        bool OnCircle(int r, Vec3 o, double rr) => VerticesOf(_regions[r]).All(v =>
        {
            var w = _v[v] - o; w -= axis * w.Dot(axis);
            return Math.Abs(w.Length - rr) <= _opt.CylinderFitTolerance;
        });

        // third strip for the first fit
        bool seeded = false;
        foreach (int s0 in new[] { a, b })
        {
            foreach (int c in nbrs[s0])
            {
                if (inCluster.Contains(c) || !step(s0, c, axis)) continue;
                cluster.Add(c);
                if (FitCircle(Pts(), axis, out origin, out radius, out double res) && res <= _opt.CylinderFitTolerance)
                {
                    inCluster.Add(c); seeded = true; break;
                }
                cluster.RemoveAt(cluster.Count - 1);
            }
            if (seeded) break;
        }
        if (!seeded) return null;
        hollow = !Faces(a, origin, false);
        { var (o0, h0) = (origin, hollow); if (!cluster.All(r => Faces(r, o0, h0))) return null; }

        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (int m in cluster.ToList())
                foreach (int c in nbrs[m])
                {
                    if (inCluster.Contains(c) || !step(m, c, axis) || !Faces(c, origin, hollow) || !OnCircle(c, origin, radius)) continue;
                    inCluster.Add(c); cluster.Add(c); grew = true;
                }
            if (grew)
            {
                if (!FitCircle(Pts(), axis, out var o2, out double r2, out double res2) || res2 > _opt.CylinderFitTolerance) return null;
                origin = o2; radius = r2;
            }
        }
        if (cluster.Count < _opt.CylinderMinStrips || radius > _opt.MaxCurvedRadius) return null;

        // phase 2: the cylinder is fixed now; also take neighbouring facets whose shared edges are not along the axis
        // (zig-zag triangles where both rims are divided differently) and vertices slightly off the circle
        double sinTol = Math.Sin(_opt.CylinderAngleToleranceDeg * Math.PI / 180);
        bool OnLoose(int r) => VerticesOf(_regions[r]).All(v =>
        {
            var w = _v[v] - origin; w -= axis * w.Dot(axis);
            return Math.Abs(w.Length - radius) <= JoinTol(radius);
        });
        bool Joinable(int c)
        {
            var rc = _regions[c];
            if (rc.Curved || rc.Fixed || rc.Tris.Count == 0) return false;
            if (_cylJoinOf != null && c < _cylJoinOf.Length ? !_cylJoinOf[c] : rc.Tris.Any(Peeled) || rc.Tris.Any(_noCylinder.Contains)) return false;
            if (IgnoreNormal(rc)) return OnLoose(c); // a needle's normal is meaningless: only its vertices count
            if (Math.Abs(rc.N.Dot(axis)) > sinTol) return false;
            return Faces(c, origin, hollow) && OnLoose(c);
        }
        bool grew2 = true;
        while (grew2)
        {
            grew2 = false;
            foreach (int m in cluster.ToList())
                foreach (int c in nbrs[m])
                    if (!inCluster.Contains(c) && Joinable(c)) { inCluster.Add(c); cluster.Add(c); grew2 = true; }
        }

        // the strips must turn through a real angle (rules out nearly flat facets on smooth free-form surfaces)
        double span = 0;
        foreach (int r1 in cluster.Where(r => !IsSliver(_regions[r]))) foreach (int r2 in cluster.Where(r => !IsSliver(_regions[r])))
            span = Math.Max(span, Math.Acos(Math.Clamp(_regions[r1].N.Dot(_regions[r2].N), -1, 1)));
        if (span * 180 / Math.PI < _opt.CylinderMinSpanDeg) return null;
        // a column of sphere facets also fits a (short) cylinder: reject when a sphere fits much better
        var all = Pts();
        FitCircle(all, axis, out _, out _, out double cylRes);
        cylRes = Math.Max(cylRes, 1e-12);
        if (FitSphere(all, out _, out _, out double sphRes) && sphRes < cylRes / 3)
        {
            return null;
        }
        return (cluster, origin, radius, hollow);
    }

    /// <summary>Algebraic least-squares sphere x^2+y^2+z^2 + Dx + Ey + Fz + G = 0, then max |dist - r|.</summary>
    private static bool FitSphere(List<Vec3> pts, out Vec3 centre, out double radius, out double maxResid)
    {
        centre = default; radius = maxResid = double.NaN;
        if (pts.Count < 4) return false;
        var c0 = new Vec3(0, 0, 0);
        foreach (var p in pts) c0 += p;
        c0 = c0 * (1.0 / pts.Count);
        var m = new double[4, 4]; var b = new double[4];
        foreach (var q in pts)
        {
            var p = q - c0;
            double[] row = [p.X, p.Y, p.Z, 1];
            double rhs = -(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            for (int i = 0; i < 4; i++) { b[i] += row[i] * rhs; for (int j = 0; j < 4; j++) m[i, j] += row[i] * row[j]; }
        }
        // Gaussian elimination with partial pivoting
        for (int col = 0; col < 4; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 4; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < 1e-300) return false;
            for (int k = 0; k < 4; k++) (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]);
            (b[col], b[piv]) = (b[piv], b[col]);
            for (int r = 0; r < 4; r++)
            {
                if (r == col) continue;
                double f = m[r, col] / m[col, col];
                for (int k = 0; k < 4; k++) m[r, k] -= f * m[col, k];
                b[r] -= f * b[col];
            }
        }
        double D = b[0] / m[0, 0], E = b[1] / m[1, 1], F = b[2] / m[2, 2], G = b[3] / m[3, 3];
        var c = new Vec3(-D / 2, -E / 2, -F / 2);
        double r2 = c.Dot(c) - G;
        if (!(r2 > 0)) return false;
        double r0 = Math.Sqrt(r2);
        centre = c + c0; radius = r0;
        var cc = centre;
        maxResid = pts.Max(p => Math.Abs((p - cc).Length - r0));
        return true;
    }

    /// <summary>Tolerance for joining facets to an established cylinder: absolute, but at most 0.1% of the radius.</summary>
    /// <summary>Tolerance for taking single facets (and tiny planar pieces) into a curved neighbour (not in the variant
    /// without loose joining: allowing it there made yubikey worse, 2975 -> 3056 faces).</summary>
    private double AbsorbTol(double radius) => Math.Min(Math.Min(_opt.AbsorbTolerance, _opt.CylinderJoinTolerance), 1e-3 * radius);

    private double JoinTol(double radius) => Math.Min(_opt.CylinderJoinTolerance, 1e-3 * radius);

    /// <summary>Least-squares circle of the points projected onto the plane perpendicular to axis.</summary>
    private static bool FitCircle(List<Vec3> pts, Vec3 axis, out Vec3 origin, out double radius, out double maxResid)
    {
        var u = Math.Abs(axis.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        u = (u - axis * u.Dot(axis)).Normalized(); var w = axis.Cross(u);
        var p0 = pts[0];
        var xy = pts.Select(p => { var d = p - p0; return (x: d.Dot(u), y: d.Dot(w)); }).ToList();
        // Kasa: x^2+y^2 + D x + E y + F = 0, least squares
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, n = xy.Count, sxz = 0, syz = 0, sz = 0;
        foreach (var (x, y) in xy)
        {
            double z = x * x + y * y;
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y; sxz += x * z; syz += y * z; sz += z;
        }
        var m = new double[3, 3] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        var rhs = new[] { -sxz, -syz, -sz };
        if (!Solve3(m, rhs, out var sol)) { origin = default; radius = maxResid = double.NaN; return false; }
        double cx = -sol[0] / 2, cy = -sol[1] / 2;
        double r2 = cx * cx + cy * cy - sol[2];
        if (!(r2 > 0)) { origin = default; radius = maxResid = double.NaN; return false; }
        // Gauss-Newton refinement of the geometric fit
        double r = Math.Sqrt(r2);
        for (int it = 0; it < 20; it++)
        {
            double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
            foreach (var (x, y) in xy)
            {
                double dx = x - cx, dy = y - cy, di = Math.Sqrt(dx * dx + dy * dy);
                if (di == 0) continue;
                double j0 = -dx / di, j1 = -dy / di, j2 = -1, res = di - r;
                a00 += j0 * j0; a01 += j0 * j1; a02 += j0 * j2; a11 += j1 * j1; a12 += j1 * j2; a22 += j2 * j2;
                b0 -= j0 * res; b1 -= j1 * res; b2 -= j2 * res;
            }
            if (!Solve3(new double[3, 3] { { a00, a01, a02 }, { a01, a11, a12 }, { a02, a12, a22 } }, [b0, b1, b2], out var dlt)) break;
            cx += dlt[0]; cy += dlt[1]; r += dlt[2];
            if (Math.Abs(dlt[0]) + Math.Abs(dlt[1]) + Math.Abs(dlt[2]) < 1e-15) break;
        }
        origin = p0 + u * cx + w * cy;
        radius = r;
        maxResid = xy.Max(p => Math.Abs(Math.Sqrt((p.x - cx) * (p.x - cx) + (p.y - cy) * (p.y - cy)) - r));
        return true;
    }

    private static bool Solve3(double[,] a, double[] b, out double[] x)
    {
        double det = a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) - a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0]) + a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
        x = new double[3];
        if (Math.Abs(det) < 1e-300) return false;
        for (int c = 0; c < 3; c++)
        {
            var m = (double[,])a.Clone();
            for (int r = 0; r < 3; r++) m[r, c] = b[r];
            x[c] = (m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0])) / det;
        }
        return true;
    }

    // ------------------------------------------------------------------ regularisation

    private bool IsGenerator(int a, int b, Region cyl, double sinTol)
    {
        var d = _v[b] - _v[a];
        return d.Cross(cyl.Axis).Length <= sinTol * d.Length;
    }

    private void Regularise()
    {
        UnifyCoincidentPlanes();
        UnifyCoincidentSpheres();
        double sinTol = Math.Sin(_opt.CylinderAngleToleranceDeg * Math.PI / 180);
        double capTol = Math.Sin(_opt.CylinderAngleToleranceDeg * Math.PI / 180);
        var shared = SharedEdges();
        // vertices on the seam with a torus may lie a little further off a cylinder (see GrowTangentCylinders)
        var tori = _regions.Where(r => r.IsTorus).ToList();
        bool OnSeam(int v, double tol) => tori.Any(t => SurfaceDistance(t, _v[v]) <= tol);
        foreach (var (ci, cyl) in _regions.Select((r, i) => (i, r)).Where(x => x.r.IsCylinder).ToList())
        {
            var sides = new List<int>(); var caps = new List<int>();
            var contact = new Dictionary<int, Vec3>(); // side plane -> a point on the shared generator line
            bool mitred = false; // meets another cylinder along a curve: keep the fitted axis (moving it breaks the mitre)
            foreach (var ((a, b), edges) in shared)
            {
                int other = a == ci ? b : b == ci ? a : -1;
                if (other < 0) continue;
                if (_regions[other].IsCylinder)
                {
                    if (!edges.All(e => IsGenerator(e.Item1, e.Item2, cyl, sinTol))) mitred = true;
                    continue;
                }
                if (edges.All(e => IsGenerator(e.Item1, e.Item2, cyl, sinTol))
                    || (!_regions[other].Curved && !_regions[other].Free && Math.Abs(_regions[other].N.Dot(cyl.Axis)) < sinTol))
                {
                    // (a plane tangent to the cylinder meets it along a generator, but there the mesh boundary may
                    // zigzag: both surfaces agree within micrometres over a band, yubikey R5 end rounding)
                    sides.Add(other);
                    contact[other] = (_v[edges[0].Item1] + _v[edges[0].Item2]) * 0.5;
                }
                else caps.Add(other);
            }
            double tol = Math.Max(_opt.DistanceTolerance, _opt.CylinderFitTolerance);
            bool PlaneFits(Region r, Vec3 n, double d) => VerticesOf(r).All(v => Math.Abs(n.Dot(_v[v]) - d) <= tol);
            double cylTol = Math.Max(tol, JoinTol(cyl.Radius));
            // a user's group took in strips up to 20 µm off (AbsorbFacets): it may move as far
            if (cyl.Fixed && !cyl.Soft) cylTol = Math.Max(cylTol, Math.Max(2 * cyl.FitDeviation, Math.Min(_opt.MaxHintDeviation, 2e-5)));
            bool CylFits(Vec3 axis, Vec3 origin) => VerticesOf(cyl).All(v =>
            {
                var w = _v[v] - origin; w -= axis * w.Dot(axis);
                double d = Math.Abs(w.Length - cyl.Radius);
                return d <= cylTol || (d <= SeamFactor * cylTol && OnSeam(v, SeamFactor * cylTol));
            });

            // side planes kept from an earlier result do not move: the cylinder's axis turns to lie exactly parallel to them
            var fixedSides = sides.Select(i => _regions[i]).Where(r => r.Soft && !r.Free && !r.Curved).OrderByDescending(Area).ToList();
            if (!cyl.Soft && fixedSides.Count > 0)
            {
                var n1 = fixedSides[0].N;
                var n2 = fixedSides.Skip(1).Select(r => r.N).FirstOrDefault(n => n.Cross(n1).Length > 0.1);
                var ax = n2.Length > 0 ? n1.Cross(n2).Normalized() : (cyl.Axis - n1 * n1.Dot(cyl.Axis)).Normalized();
                if (ax.Dot(cyl.Axis) < 0) ax = ax * -1;
                if (CylFits(ax, cyl.Origin)) cyl.Axis = ax;
            }
            // axis exactly along a (nearly) parallel cap normal, if the cylinder still fits
            var cap = caps.Select(c => _regions[c]).Where(r => !r.Free && r.N.Cross(cyl.Axis).Length < capTol).MaxBy(r => r.Tris.Count);
            if (cap != null)
            {
                var ax = cap.N.Dot(cyl.Axis) > 0 ? cap.N : cap.N * -1;
                if (!cyl.Soft && CylFits(ax, cyl.Origin)) cyl.Axis = ax;
            }
            // caps exactly perpendicular to the axis, side planes exactly containing it (only if they still fit)
            foreach (int c in caps)
            {
                var r = _regions[c];
                if (r.Free || r.N.Cross(cyl.Axis).Length >= capTol) continue;
                var n = r.N.Dot(cyl.Axis) > 0 ? cyl.Axis : cyl.Axis * -1;
                double d = VerticesOf(r).Average(v => n.Dot(_v[v]));
                if (!r.Soft && PlaneFits(r, n, d)) { r.N = n; r.D = d; }
            }
            foreach (int sd in sides)
            {
                var r = _regions[sd];
                if (r.Free) continue;
                var n = (r.N - cyl.Axis * r.N.Dot(cyl.Axis)).Normalized();
                double d = VerticesOf(r).Average(v => n.Dot(_v[v]));
                if (!r.Soft && PlaneFits(r, n, d)) { r.N = n; r.D = d; }
            }
            // tangent side planes (fillets): move the axis so the cylinder touches them exactly, if it still fits
            double sgn = cyl.Hollow ? 1 : -1; // axis lies behind the plane (solid) or in front of it (hollow)
            // tangent = the plane's normal equals the cylinder's face normal along their shared line
            // (measured at the contact line, not at the plane's centroid: a long wall's centroid is far away)
            var tangent = sides.Where(sd => !_regions[sd].Free).Select(sd => (r: _regions[sd], at: contact[sd])).Where(x =>
            {
                var r = x.r;
                var rad = x.at - cyl.Origin; rad -= cyl.Axis * rad.Dot(cyl.Axis);
                var faceN = rad.Normalized() * (cyl.Hollow ? -1 : 1);
                return faceN.Dot(r.N) > Math.Cos(_opt.TangentToleranceDeg * Math.PI / 180);
            }).Select(x => x.r).ToList();
            Vec3? newOrigin = null;
            // two opposite walls touching it (the round bottom of a slot, yubikey r1.3): the cylinder lies exactly midway
            // and its radius is half their distance; making only one of them tangent leaves the other a few 1e-10 m off
            var walls = tangent.SelectMany(p => tangent.Where(q => p.N.Dot(q.N) < -Math.Cos(capTol)).Select(q => (p, q))).FirstOrDefault();
            if (!mitred && !cyl.Soft && walls.p != null)
            {
                var (p, q) = walls;
                var n = p.N;
                double dq = -q.D; // q as n·x = dq
                double rr = Math.Abs(p.D - dq) / 2, mid = (p.D + dq) / 2;
                var o2 = cyl.Origin + n * (mid - n.Dot(cyl.Origin));
                bool fits = Math.Abs(rr - cyl.Radius) <= cylTol && VerticesOf(cyl).All(v =>
                {
                    var w = _v[v] - o2; w -= cyl.Axis * w.Dot(cyl.Axis);
                    return Math.Abs(w.Length - rr) <= cylTol;
                }) && PlaneFits(q, n * -1, -dq);
                if (fits)
                {
                    if (!q.Soft) { q.N = n * -1; q.D = -dq; }
                    cyl.Origin = o2; cyl.Radius = rr;
                    continue;
                }
            }
            if (tangent.Count >= 2 && tangent[0].N.Cross(tangent[1].N).Length > 0.1)
            {
                var n1 = tangent[0].N; var n2 = tangent[1].N; var ax = cyl.Axis;
                var m = new double[3, 3] { { n1.X, n1.Y, n1.Z }, { n2.X, n2.Y, n2.Z }, { ax.X, ax.Y, ax.Z } };
                if (Solve3(m, [tangent[0].D + sgn * cyl.Radius, tangent[1].D + sgn * cyl.Radius, ax.Dot(cyl.Origin)], out var o))
                    newOrigin = new Vec3(o[0], o[1], o[2]);
            }
            else if (tangent.Count >= 1)
            {
                var r = tangent[0];
                double cur = r.N.Dot(cyl.Origin) - r.D;
                newOrigin = cyl.Origin + r.N * (sgn * cyl.Radius - cur);
            }
            if (!mitred && !cyl.Soft && newOrigin is { } no && CylFits(cyl.Axis, no)) cyl.Origin = no;
        }

        foreach (var (si, sph) in _regions.Select((r, i) => (i, r)).Where(x => x.r.IsSphere).ToList())
            foreach (var ((a, b), _) in shared)
            {
                int other = a == si ? b : b == si ? a : -1;
                if (other < 0 || !_regions[other].IsCylinder) continue;
                var cyl = _regions[other];
                var w = sph.Origin - cyl.Origin; w -= cyl.Axis * w.Dot(cyl.Axis);
                double tol = JoinTol(sph.Radius);
                if (w.Length > tol) continue;
                var c = sph.Origin - w;
                double r = Math.Abs(sph.Radius - cyl.Radius) <= tol ? cyl.Radius : sph.Radius;
                if (!sph.Soft && VerticesOf(sph).All(v => Math.Abs((_v[v] - c).Length - r) <= tol)) { sph.Origin = c; sph.Radius = r; }
            }
        RegulariseTori(shared);
    }

    /// <summary>
    /// Separate planar regions that lie in (nearly) the same plane get exactly the same plane: where they touch in a single
    /// vertex, two almost identical constraints would otherwise make the vertex position ill-conditioned (boltest).
    /// </summary>
    private void UnifyCoincidentPlanes()
    {
        double tol = Math.Max(Math.Max(_opt.DistanceTolerance, _opt.CylinderFitTolerance), _opt.CoplanarUnifyTolerance);
        var planes = _regions.Where(r => !r.Curved && !r.Free && r.Tris.Count > 0).OrderByDescending(Area).ToList();
        var done = new HashSet<Region>();
        foreach (var p in planes)
        {
            if (done.Contains(p)) continue;
            double cosTol = Math.Cos(_opt.AngleToleranceDeg * Math.PI / 180);
            var group = planes.Where(q => !done.Contains(q) && q.N.Dot(p.N) > cosTol && Math.Abs(q.D - p.D) < tol
                && VerticesOf(q).All(v => Math.Abs(p.N.Dot(_v[v]) - p.D) <= tol)).ToList();
            done.UnionWith(group);
            if (group.Count < 2) continue;
            // the largest region's plane wins, if all members still fit it
            foreach (var q in group.Where(q => !q.Soft)) { q.N = p.N; q.D = p.D; }
        }
    }

    /// <summary>
    /// Pieces of one sphere (separated by leftover facets) get exactly the same centre and radius: their independent
    /// fits differ by ~1e-9 m, which makes them "not coaxial" for a torus between them (boltest's R36 sphere: 4 pieces).
    /// </summary>
    private void UnifyCoincidentSpheres()
    {
        var spheres = _regions.Where(r => r.IsSphere && r.Tris.Count > 0).OrderByDescending(Area).ToList();
        var done = new HashSet<Region>();
        foreach (var s in spheres)
        {
            if (done.Contains(s)) continue;
            done.Add(s);
            double tol = Math.Max(JoinTol(s.Radius), _opt.CylinderFitTolerance);
            foreach (var q in spheres.Where(q => !done.Contains(q) && q.Hollow == s.Hollow
                && (q.Origin - s.Origin).Length < tol && Math.Abs(q.Radius - s.Radius) < tol).ToList())
            {
                if (!VerticesOf(q).All(v => Math.Abs((_v[v] - s.Origin).Length - s.Radius) <= tol)) continue;
                if (!q.Soft) { q.Origin = s.Origin; q.Radius = s.Radius; }
                done.Add(q);
            }
        }
    }

    // ------------------------------------------------------------------ topology + geometry

    private BrepBody MakeBody()
    {
        double sinTol = Math.Sin(_opt.CylinderAngleToleranceDeg * Math.PI / 180);
        // regions around each vertex
        var regionsAt = new Dictionary<int, HashSet<int>>();
        for (int t = 0; t < _t.Count; t++)
            foreach (int v in _t[t]) { if (!regionsAt.TryGetValue(v, out var s)) regionsAt[v] = s = new(); s.Add(_regionOf[t]); }

        // boundary half-edges: (a,b) of a triangle whose neighbour across lies in another region
        var next = new Dictionary<(int region, int other, int from), int>();
        var boundary = new List<(int a, int b, int r, int o)>();
        for (int t = 0; t < _t.Count; t++)
            for (int k = 0; k < 3; k++)
            {
                int r = _regionOf[t], o = _regionOf[Across(t, k)];
                if (r == o) continue;
                int a = _t[t][k], b = _t[t][(k + 1) % 3];
                boundary.Add((a, b, r, o));
                if (!next.TryAdd((r, o, a), b)) throw new MeshException("pinched boundary between two regions");
            }

        // corners: >= 3 regions, or a change between generator / non-generator along a cylinder boundary
        var corner = new HashSet<int>(regionsAt.Where(kv => kv.Value.Count >= 3).Select(kv => kv.Key));
        // single facets keep all three corners (their plane is defined by them)
        foreach (var reg in _regions.Where(r => r.Free)) corner.UnionWith(_t[reg.Tris[0]]);
        foreach (var (a, b, r, o) in boundary)
        {
            if (!next.TryGetValue((r, o, b), out int c)) continue;
            var cyl = _regions[r].IsCylinder ? _regions[r] : _regions[o].IsCylinder ? _regions[o] : null;
            if (cyl != null)
            {
                var otherReg = _regions[r] == cyl ? _regions[o] : _regions[r];
                if (!TangentPlane(cyl, otherReg) && IsGenerator(a, b, cyl, sinTol) != IsGenerator(b, c, cyl, sinTol)) corner.Add(b);
            }
            else if (!_regions[r].Curved && !_regions[o].Curved)
            {
                // a chain between two planes is straight; where the mesh bends, keep the vertex
                var d1 = _v[b] - _v[a]; var d2 = _v[c] - _v[b];
                if (d1.Cross(d2).Length > sinTol * d1.Length * d2.Length || d1.Dot(d2) <= 0) corner.Add(b);
            }
        }

        // chains -> edges
        var vertexId = new Dictionary<int, int>();
        var verts = new List<Vec3>();
        int Vid(int v) { if (!vertexId.TryGetValue(v, out int id)) { id = verts.Count; vertexId[v] = id; verts.Add(_v[v]); } return id; }
        var edges = new List<BrepEdge>();
        var edgeChains = new List<(List<int> pts, int r, int o)>();
        var halfEdge = new Dictionary<(int, int), (int edge, bool fwd)>();
        var prevOf = new Dictionary<(int region, int other, int to), int>();
        foreach (var (a, b, r, o) in boundary) prevOf[(r, o, b)] = a;
        foreach (var (a0, b0, r, o) in boundary)
        {
            if (r > o || halfEdge.ContainsKey((a0, b0))) continue;
            // walk back to a corner (or around a closed ring without corners)
            int start = a0, guard = 0;
            while (!corner.Contains(start))
            {
                start = prevOf[(r, o, start)];
                if (start == a0 || ++guard > _t.Count * 3) { start = a0; break; }
            }
            var pts = new List<int> { start };
            int cur = start;
            guard = 0;
            do
            {
                cur = next[(r, o, cur)];
                pts.Add(cur);
            } while (!corner.Contains(cur) && cur != start && ++guard < _t.Count * 3);
            bool ring = !corner.Contains(start);
            int e = edges.Count;
            for (int i = 0; i + 1 < pts.Count; i++) { halfEdge[(pts[i], pts[i + 1])] = (e, true); halfEdge[(pts[i + 1], pts[i])] = (e, false); }
            edges.Add(new BrepEdge(ring ? -1 : Vid(pts[0]), ring ? -1 : Vid(pts[^1]), new BrepLine())); // curve set below
            edgeChains.Add((pts, r, o));
        }

        // vertex positions: exactly on all fitted surfaces around them
        var finalPos = new Vec3[verts.Count];
        var retry = new HashSet<int>();
        string? firstProblem = null;
        foreach (var (mv, id) in vertexId)
        {
            var constraints = regionsAt[mv].Select(r => _regions[r]).Where(r => !r.Free).ToList();
            double maxMove = Math.Max(_opt.SnapTolerance * 10, _opt.CylinderJoinTolerance * 2);
            var onCircle = constraints.Count == 0 ? null : PlaceOnTorusCircle(_v[mv], constraints) ?? PlaceOnTangentLine(_v[mv], constraints);
            var (p, resid) = constraints.Count == 0 ? (_v[mv], 0.0) : onCircle ?? Project(_v[mv], constraints);
            // along a tangent torus boundary the surfaces agree within micrometres over a wide band: a vertex that lies on
            // all of them may slide a long way to the exact circle without changing the shape (boltest: 0.19 mm)
            if (onCircle != null && constraints.All(r => SurfaceDistance(r, _v[mv]) <= SeamFactor * Math.Max(JoinTol(r.IsTorus ? r.Minor : r.Radius), _opt.CylinderFitTolerance)))
                maxMove = Math.Max(maxMove, 0.05 * constraints.Where(r => r.IsTorus || r.IsCylinder).Min(r => r.IsTorus ? r.Minor : r.Radius));
            // the user's groups may need their vertices moved as far as their fit deviates
            // (a user's group at least as far as strips next to it are taken in, 20 µm: a group that fits its facets
            // exactly, the smooth core of a replaced thread, must still meet a noisy neighbour; it had 1 µm)
            foreach (var r in constraints.Where(r => r.Fixed))
                maxMove = Math.Max(maxMove, Math.Max(2 * r.FitDeviation, r.Soft ? 0 : Math.Min(_opt.MaxHintDeviation, 2e-5)) + 1e-6);
            if (resid > GeomTol || (p - _v[mv]).Length > maxMove)
            {
                firstProblem ??= FormattableString.Invariant($"vertex ({_v[mv].X / _opt.Scale:0.###}, {_v[mv].Y / _opt.Scale:0.###}, {_v[mv].Z / _opt.Scale:0.###}): residual {resid:E1} m, move {(p - _v[mv]).Length:E1} m: ")
                    + string.Join(", ", constraints.Select(r => Describe(r, _v[mv])));
                // dissolve the smallest fitted surface at this vertex into single facets and try again
                var victim = constraints.Where(r => (r.Tris.Count > 1 || r.Curved) && (!r.Fixed || r.Soft)).MinBy(VictimRank);
                if (victim is { Curved: true }) Dbg("  dissolve " + Describe(victim, _v[mv]) + FormattableString.Invariant($" at vertex ({_v[mv].X * 1000:0.###},{_v[mv].Y * 1000:0.###},{_v[mv].Z * 1000:0.###}) (resid {resid:E1}, move {(p - _v[mv]).Length:E1}, circle {onCircle != null}): ") + string.Join(", ", constraints.Select(r => Describe(r, _v[mv])))
                    + "; at the placed point: " + string.Join(", ", constraints.Select(r => Signed(r, p, out _).ToString("E1", CultureInfo.InvariantCulture))));
                if (victim != null) retry.UnionWith(victim.Tris);
                if (Debug) Dbg(FormattableString.Invariant($"  vertex conflict ({_v[mv].X * 1000:0.###},{_v[mv].Y * 1000:0.###},{_v[mv].Z * 1000:0.###}): resid {resid:E1}, move {(p - _v[mv]).Length:E1} (max {maxMove:E1}), victim {(victim == null ? "-" : Describe(victim, _v[mv]))}"));
            }
            finalPos[id] = p;
        }
        if (retry.Count > 0) throw new RetryException(retry, firstProblem!);
        if (firstProblem != null) throw new MeshException(firstProblem);

        // single facets: plane through their placed corners
        foreach (var reg in _regions.Where(r => r.Free))
        {
            var tv = _t[reg.Tris[0]];
            if (!tv.All(vertexId.ContainsKey)) throw new MeshException("facet corner was dropped");
            var p0 = finalPos[vertexId[tv[0]]]; var p1 = finalPos[vertexId[tv[1]]]; var p2 = finalPos[vertexId[tv[2]]];
            var n = (p1 - p0).Cross(p2 - p0);
            if (n.Length < 1e-30) throw new MeshException("facet became degenerate");
            reg.N = n.Normalized(); reg.D = reg.N.Dot(p0);
        }

        // curves
        var retryCurves = new HashSet<int>();
        var absorbNext = new HashSet<int>();
        var peelNext = new HashSet<int>();
        string? curveProblem = null;
        for (int e = 0; e < edges.Count; e++)
        {
            var (pts, r, o) = edgeChains[e];
            var ra = _regions[r]; var rb = _regions[o];
            BrepCurve curve;
            try
            {
            if (ra.IsTorus || rb.IsTorus) curve = TorusCurve(ra.IsTorus ? ra : rb, ra.IsTorus ? rb : ra, pts);
            else if (ra.IsCone || rb.IsCone) curve = ConeCurve(ra.IsCone ? ra : rb, ra.IsCone ? rb : ra, pts);
            else if (ra.IsSphere || rb.IsSphere) curve = SphereCurve(ra.IsSphere ? ra : rb, ra.IsSphere ? rb : ra, pts);
            else if (ra.IsCylinder && rb.IsCylinder)
            {
                bool genA = Enumerable.Range(0, pts.Count - 1).All(i => IsGenerator(pts[i], pts[i + 1], ra, sinTol));
                bool genB = Enumerable.Range(0, pts.Count - 1).All(i => IsGenerator(pts[i], pts[i + 1], rb, sinTol));
                if (genA && genB) curve = new BrepLine(); // tangent cylinders with parallel axes
                else
                {
                    // e.g. a mitre: the two cylinders meet in a planar ellipse
                    var plane = FitPlane(pts.Select(i => _v[i]).ToList(), out double flat);
                    if (pts.Count < 3 || flat > _opt.CylinderFitTolerance)
                    {
                        if (Debug)
                        {
                            Dbg(FormattableString.Invariant($"  cyl/cyl chain ({pts.Count} pts, flat {flat:E1}): axes ({ra.Origin.X * 1000:F3},{ra.Origin.Z * 1000:F3}) / ({rb.Origin.X * 1000:F3},{rb.Origin.Z * 1000:F3}) r {ra.Radius * 1000:F4}/{rb.Radius * 1000:F4}"));
                            for (int i = 0; i < pts.Count; i++)
                                Dbg(FormattableString.Invariant($"    ({_v[pts[i]].X * 1000:F4},{_v[pts[i]].Y * 1000:F4},{_v[pts[i]].Z * 1000:F4}) genA {(i + 1 < pts.Count && IsGenerator(pts[i], pts[i + 1], ra, sinTol))} genB {(i + 1 < pts.Count && IsGenerator(pts[i], pts[i + 1], rb, sinTol))}"));
                        }
                        throw new MeshException("cylinder meets cylinder along a non-planar curve (not supported yet)");
                    }
                    curve = Conic(ra, plane, pts);
                }
            }
            else if (!ra.IsCylinder && !rb.IsCylinder) curve = new BrepLine();
            else
            {
                var cyl = ra.IsCylinder ? ra : rb; var pl = ra.IsCylinder ? rb : ra;
                bool generator = Enumerable.Range(0, pts.Count - 1).All(i => IsGenerator(pts[i], pts[i + 1], cyl, sinTol));
                if (generator || TangentPlane(cyl, pl)) curve = new BrepLine();
                else curve = Conic(cyl, pl, pts);
            }
            if (curve is BrepLine && edges[e].Start < 0) throw new MeshException("closed chain between planes");
            CheckEdge(curve, edges[e], finalPos, ra, rb);
            CheckFollowsChain(curve, edges[e], finalPos, pts);
            }
            catch (MeshException ex)
            {
                if (_opt.TraceIntersections && (ra.Curved || rb.Curved))
                {
                    try
                    {
                        var traced = TraceIntersection(ra, rb, pts, edges[e], finalPos);
                        CheckEdge(traced, edges[e], finalPos, ra, rb);
                        edges[e] = edges[e] with { Curve = traced };
                        TracedEdges++;
                        continue;
                    }
                    catch (MeshException tex)
                    {
                        Dbg("  tracing failed too: " + tex.Message + " (analytic: " + ex.Message + ")");
                    }
                }
                var mid = _v[pts[pts.Count / 2]];
                curveProblem ??= ex.Message + ": " + Describe(ra, mid) + " / " + Describe(rb, mid);
                if (ra.Fixed || rb.Fixed) Dbg("  group edge fails: " + ex.Message + ": " + Describe(ra, mid) + " / " + Describe(rb, mid)
                    + FormattableString.Invariant($" at ({mid.X * 1000:0.###},{mid.Y * 1000:0.###},{mid.Z * 1000:0.###})"));
                var free = ra.Free ? ra : rb.Free ? rb : null;
                var curved = ra.Curved ? ra : rb.Curved ? rb : null;
                // a single facet that lies on a curved surface next to it (not necessarily the one of this edge: a facet in
                // the seam between a sphere and a tangent torus lies on the sphere) is taken into that surface next round
                // (next to a user's group as far as AbsorbFacets takes strips into it: a folded sliver there faces the wrong way)
                // (not a sliver of a wall that meets the group at an angle: that zigzags the group's boundary)
                bool Along(Region c) => Math.Abs(free!.N.Dot(Grad(c, free.Centroid))) >= Math.Cos(_opt.FacetNormalToleranceDeg * Math.PI / 180);
                bool Fits(Region c) => _t[free!.Tris[0]].All(v => SurfaceDistance(c, _v[v]) <= (c is { Fixed: true, Soft: false } && Along(c)
                    ? Math.Max(AbsorbTol(c.IsTorus ? c.Minor : c.Radius), Math.Min(_opt.MaxHintDeviation, 2e-5))
                    : Math.Max(AbsorbTol(c.IsTorus ? c.Minor : c.Radius), _opt.CylinderFitTolerance)));
                // in the tangent seam of a torus the mesh boundary is vague: facets there lie a little further off
                bool SeamFits(Region c) => c.IsTorus && _t[free!.Tris[0]].All(v => SurfaceDistance(c, _v[v]) <= SeamFactor * JoinTol(c.Minor));
                if (free != null && curved != null && !_absorb.Contains(free.Tris[0]) && !_hintExclude.Contains(free.Tris[0]) && !_keep.Contains(free.Tris[0])
                    && _t[free.Tris[0]].SelectMany(v => regionsAt[v]).Distinct().Select(i => _regions[i]).Any(c => c.Curved && (Fits(c) || SeamFits(c))))
                {
                    absorbNext.Add(free.Tris[0]);
                    continue;
                }
                if (free != null && curved is { IsCylinder: true } && ex.Message.StartsWith("arc bulges"))
                    Dbg($"  bulging facet {free.Tris[0]} " + string.Join(" ", _t[free.Tris[0]].Select(v => FormattableString.Invariant($"({_v[v].X * 1000:0.###},{_v[v].Y * 1000:0.###},{_v[v].Z * 1000:0.###}) d={SurfaceDistance(curved, _v[v]):E1}")))
                        + "; neighbours: " + string.Join(" | ", Enumerable.Range(0, 3).Select(k => Describe(_regions[_regionOf[Across(free.Tris[0], k)]], free.Centroid))));
                if (free != null && curved is { IsTorus: true })
                    Dbg($"  seam facet {free.Tris[0]} at " + string.Join(" ", _t[free.Tris[0]].Select(v => FormattableString.Invariant($"({_v[v].X * 1000:0.###},{_v[v].Y * 1000:0.###},{_v[v].Z * 1000:0.###})"))) + ": corner dist to torus " + string.Join(", ", _t[free.Tris[0]].Select(v => SurfaceDistance(curved, _v[v]).ToString("E1", CultureInfo.InvariantCulture)))
                        + "; neighbours: " + string.Join(" | ", Enumerable.Range(0, 3).Select(k => _regions[_regionOf[Across(free.Tris[0], k)]]).Select(c => (c.IsSphere ? $"sphere r={c.Radius * 1000:0.##}" : c.IsTorus ? "torus" : c.IsCylinder ? "cyl" : c.Free ? "facet" : $"plane {c.Tris.Count}") + (_forced.Contains(c.Tris[0]) ? " forced" : "") + (_noCurve.Contains(c.Tris[0]) ? " peeled" : "") + " corners " + string.Join(",", _t[free.Tris[0]].Select(v => SurfaceDistance(c, _v[v]).ToString("E0", CultureInfo.InvariantCulture)))))
                        + (_forced.Contains(free.Tris[0]) ? " [forced]" : "") + (_noCurve.Contains(free.Tris[0]) ? " [peeled]" : "") + (_absorb.Contains(free.Tris[0]) ? " [absorbed]" : "") + (_noSphere.Contains(free.Tris[0]) ? " [nosphere]" : "") + (_noTorus.Contains(free.Tris[0]) ? " [notorus]" : "")
                        + FormattableString.Invariant($", area {Area(free) * 1e6:0.###} mm2, normal vs torus {Math.Acos(Math.Clamp(Math.Abs(free.N.Dot(Grad(curved, free.Centroid))), -1, 1)) * 180 / Math.PI:0.#} deg"));
                var victim = new[] { ra, rb }.Where(x => (x.Curved || x.Tris.Count > 1) && (!x.Fixed || x.Soft)).MinBy(VictimRank);
                if (victim == null) throw;
                if (victim.Curved && !victim.IsTorus && victim.Tris.Count > _opt.PeelThreshold)
                {
                    // do not give up a large cylinder/sphere for one awkward edge: take its facets at this edge out of it
                    var chainVerts = new HashSet<int>(pts);
                    foreach (int t in victim.Tris) if (_t[t].Any(chainVerts.Contains)) peelNext.Add(t);
                    continue;
                }
                if (victim.Curved) Dbg("  dissolve " + Describe(victim, mid) + " at edge: " + ex.Message + ": " + Describe(ra, mid) + " / " + Describe(rb, mid));
                retryCurves.UnionWith(victim.Tris);
                continue;
            }
            edges[e] = edges[e] with { Curve = curve };
        }
        if (retryCurves.Count > 0 || absorbNext.Count > 0 || peelNext.Count > 0) throw new RetryException(retryCurves, curveProblem!, absorbNext, peelNext);

        // faces
        var faces = new List<BrepFace>();
        var boundaryOf = boundary.ToLookup(h => h.r);
        for (int ri = 0; ri < _regions.Count; ri++)
        {
            var reg = _regions[ri];
            var heOut = new Dictionary<int, int>();
            foreach (var h in boundaryOf[ri])
                if (!heOut.TryAdd(h.a, h.b))
                {
                    if (reg.Free) throw new MeshException("pinched facet boundary");
                    if (reg.Fixed && !reg.Soft && _hintOf.TryGetValue(reg, out int hp))
                    {
                        // a user's group that touches itself in one vertex: first a single facet in the gap that lies on its
                        // surface is taken in (a flat facet left in a curved wall cannot meet it: bigbox tube); otherwise
                        // (after trimming a corner) its triangles at that vertex are left out too, as long as most stays
                        if (reg.Curved)
                        {
                            double gtol = Math.Max(AbsorbTol(reg.IsTorus ? reg.Minor : reg.Radius), Math.Min(_opt.MaxHintDeviation, 2e-5));
                            var gap = Enumerable.Range(0, _t.Count).Where(t => _t[t].Contains(h.a) && _regions[_regionOf[t]] is { Free: true, Fixed: false }
                                && !_absorb.Contains(t) && !_keep.Contains(t) && _t[t].All(v => SurfaceDistance(reg, _v[v]) <= gtol)).ToHashSet();
                            if (gap.Count > 0)
                            {
                                _hintExclude.ExceptWith(gap);
                                throw new RetryException(new HashSet<int>(), $"group {hp + 1} touches itself in a vertex (facet taken in)", gap);
                            }
                        }
                        var at = reg.Tris.Where(t => _t[t].Contains(h.a)).ToList();
                        if (at.Count < reg.Tris.Count && _hintExclude.Count + at.Count <= (reg.Tris.Count + _hintExclude.Count) / 4)
                        {
                            _hintExclude.UnionWith(at);
                            throw new RetryException(new HashSet<int>(), $"group {hp + 1} touches itself in a vertex");
                        }
                        throw new MeshException($"group {hp + 1}: pinched boundary");
                    }
                    if (reg.Curved && !reg.Fixed && reg.Tris.Count > _opt.PeelThreshold)
                    {
                        // a large surface touching itself in one vertex (a zigzag seam with another surface): peel its
                        // facets at that vertex instead of giving the whole surface up
                        var at = new HashSet<int>(reg.Tris.Where(t => _t[t].Contains(h.a)));
                        if (Debug)
                            foreach (int t in Enumerable.Range(0, _t.Count).Where(t => _t[t].Contains(h.a)))
                                Dbg($"  pinch at ({_v[h.a].X * 1000:0.###},{_v[h.a].Y * 1000:0.###},{_v[h.a].Z * 1000:0.###}): tri {t} in {Describe(_regions[_regionOf[t]], _v[h.a])}, dist to this {string.Join("/", _t[t].Select(v => SurfaceDistance(reg, _v[v]).ToString("E1", CultureInfo.InvariantCulture)))}");
                        // first: a single facet in the gap that lies on this surface (it splits the surface's fans) is taken in
                        double tol = Math.Max(AbsorbTol(reg.IsTorus ? reg.Minor : reg.Radius), _opt.CylinderFitTolerance);
                        var gap = Enumerable.Range(0, _t.Count).Where(t => _t[t].Contains(h.a) && _regions[_regionOf[t]] is { Free: true, Fixed: false }
                            && !_absorb.Contains(t) && !_hintExclude.Contains(t) && !_keep.Contains(t) && _t[t].All(v => SurfaceDistance(reg, _v[v]) <= tol)).ToHashSet();
                        if (gap.Count > 0) throw new RetryException(new HashSet<int>(), "pinched boundary of a fitted surface (facet taken in)", gap);
                        if (!at.All(_noCurve.Contains)) throw new RetryException(new HashSet<int>(), "pinched boundary of a fitted surface (peeled)", peel: at);
                    }
                    throw new RetryException(new HashSet<int>(reg.Tris), "pinched boundary of a fitted surface");
                }
            var used = new HashSet<int>();
            var loops = new List<IReadOnlyList<BrepCoedge>>();
            foreach (int s in heOut.Keys)
            {
                if (used.Contains(s)) continue;
                var loop = new List<BrepCoedge>();
                int cur = s;
                do
                {
                    used.Add(cur);
                    int nx = heOut[cur];
                    var he = halfEdge[(cur, nx)];
                    if (loop.Count == 0 || loop[^1].Edge != he.edge || loop[^1].Forward != he.fwd) loop.Add(new BrepCoedge(he.edge, he.fwd));
                    cur = nx;
                } while (cur != s);
                if (loop.Count > 1 && loop[0] == loop[^1]) loop.RemoveAt(loop.Count - 1); // loop started mid-edge
                loops.Add(loop);
            }
            if (loops.Count == 0 && !reg.IsSphere && !reg.IsTorus) throw new MeshException("region without boundary");
            BrepSurface surf;
            if (reg.IsTorus) surf = new BrepTorus(reg.Origin, reg.Axis, Perp(reg.Axis), reg.Radius, reg.Minor);
            else if (reg.IsCone)
            {
                // as SpaceClaim writes it: pvec at the narrow end of the face (where the radius is smallest, still > 0)
                // (mesh points: on a ring edge the vertices are gone)
                double hMin = VerticesOf(reg).Min(v => (_v[v] - reg.Origin).Dot(reg.Axis));
                foreach (var loop in loops) foreach (var ce in loop)
                    if (edges[ce.Edge].Start < 0) hMin = Math.Min(hMin, RingLow(edges[ce.Edge].Curve, reg));
                double rMin = reg.Radius + reg.Minor * hMin;
                if (!(rMin > 1e-9)) throw new MeshException("cone face reaches its apex (not supported)");
                surf = new BrepCone(reg.Origin + reg.Axis * hMin, reg.Axis, Perp(reg.Axis), rMin, Math.Atan(reg.Minor));
            }
            else if (reg.IsSphere)
            {
                // a skew axis keeps the poles away from mesh vertices; with one loop the axis goes through it (along its
                // vector area), as DesignSpark does: one pole inside the face, the other outside, and the loop runs once
                // around the axis (a sphere minus a cap would otherwise hold both poles, which XtValidator refuses)
                var ax = new Vec3(1, 2, 3).Normalized();
                if (loops.Count == 1)
                {
                    var tmp = new BrepBody { Vertices = finalPos, Edges = edges, Faces = faces };
                    var pts = loops[0].SelectMany(ce => SampleCoedge(tmp, ce)).Select(p => p - reg.Origin).ToList();
                    var va = new Vec3(0, 0, 0);
                    for (int i = 0; i < pts.Count; i++) va += pts[i].Cross(pts[(i + 1) % pts.Count]);
                    if (va.Length > 1e-3 * reg.Radius * reg.Radius) ax = va.Normalized();
                }
                surf = new BrepSphere(reg.Origin, reg.Radius, ax, Perp(ax));
            }
            else if (reg.IsCylinder)
            {
                var xa = Perp(reg.Axis);
                surf = new BrepCylinder(reg.Origin, reg.Axis, xa, reg.Radius);
            }
            else surf = new BrepPlane(reg.N * reg.D, reg.N, Perp(reg.N));
            faces.Add(new BrepFace(surf, reg.Curved && reg.Hollow, loops));
        }
        var body = new BrepBody { Vertices = finalPos, Edges = edges, Faces = faces };
        CheckOrientation(body);
        return body;
    }

    /// <summary>
    /// Every face needs exactly one counter-clockwise loop (seen from outside): checked in the surface's (u,v) space like
    /// XtValidator. Faces that fail are dissolved (fitted) or make their fitted neighbours dissolve (single facets).
    /// </summary>
    private void CheckOrientation(BrepBody body)
    {
        var retry = new HashSet<int>();
        var peel = new HashSet<int>();
        var absorb = new HashSet<int>();
        string? problem = null;
        int excluded = _hintExclude.Count;
        for (int fi = 0; fi < body.Faces.Count; fi++)
        {
            var f = body.Faces[fi];
            var reg = _regions[fi];
            int ccw = 0; bool wraps = false;
            var polys = new List<(List<(double u, double v)> uv, List<int> edge)>();
            foreach (var loop in f.Loops)
            {
                var pts = new List<Vec3>(); var edgeOf = new List<int>();
                foreach (var ce in loop) { var sp = SampleCoedge(body, ce); pts.AddRange(sp); edgeOf.AddRange(Enumerable.Repeat(ce.Edge, sp.Count)); }
                var (area, turn, turnV) = ParamArea(f.Surface, pts, out var uv);
                polys.Add((uv, edgeOf));
                if (Math.Abs(turn) > Math.PI || Math.Abs(turnV) > Math.PI) wraps = true;
                else if (area * (f.Reversed ? -1 : 1) > 0) ccw++;
            }
            // a boundary that crosses itself (a few micrometres, at a corner where the selection zigzags): DesignSpark then
            // fills the wrong side (yubikey: a cylinder face closing a slot)
            // (checked on the user's groups: where the rest is still mesh, their boundaries zigzag through loose facets;
            // on small facets the sampled arcs of two neighbouring edges can overlap harmlessly)
            if (!wraps && reg.Fixed && !reg.Soft && polys.Count > 0 && polys.Count <= 4 && Crossing(polys) is var (e1, e2))
            {
                problem ??= $"face {fi}: its boundary crosses itself";
                Dbg($"  face {fi} ({Describe(reg, body.Vertices[Math.Max(body.Edges[e1].Start, 0)])}) boundary crosses itself at edges {e1}/{e2}");
                if (reg.Fixed && !reg.Soft)
                {
                    // leave the group's triangles at the crossing out of it
                    var ends = new[] { e1, e2 }.SelectMany(e => new[] { body.Edges[e].Start, body.Edges[e].End }).Where(v => v >= 0).Select(v => body.Vertices[v]).ToList();
                    // only triangles taken in automatically, not the user's own selection
                    var near = reg.Tris.Where(t => !_hinted.Contains(t) && _t[t].Any(v => ends.Any(q => (_v[v] - q).Length < Math.Max(2 * reg.FitDeviation, 2e-5)))).ToList();
                    Dbg("  near the crossing: " + string.Join(" ", near.Select(t => $"{t}{(_hinted.Contains(t) ? "" : "(absorbed)")}:" + string.Join("/", _t[t].Select(v => FormattableString.Invariant($"({_v[v].X * 1000:0.###},{_v[v].Y * 1000:0.###},{_v[v].Z * 1000:0.###})"))))));
                    if (near.Count > 0 && _hintExclude.Count + near.Count <= (reg.Tris.Count + _hintExclude.Count) / 4) { _hintExclude.UnionWith(near); continue; }
                    problem = $"group face {fi}: its boundary crosses itself";
                }
                continue;
            }
            if (f.Surface is BrepSphere sph && f.Loops.Count > 0)
            {
                // on a sphere the (u,v) test fails when the face holds both poles (a sphere minus a cap: its one loop runs
                // clockwise around the cap): compare the area left of the loops with the area of the facets instead
                double full = 4 * Math.PI * sph.Radius * sph.Radius, left = 0;
                foreach (var loop in f.Loops)
                {
                    var pts = loop.SelectMany(ce => SampleCoedge(body, ce)).ToList();
                    double om = SolidAngle(sph.Centre, pts) * (f.Reversed ? -1 : 1);
                    left += (om % (4 * Math.PI) + 4 * Math.PI) % (4 * Math.PI) * sph.Radius * sph.Radius;
                }
                left -= (f.Loops.Count - 1) * full;
                double mesh = Area(reg);
                if (Math.Abs(left - mesh) <= 0.2 * mesh) continue;
                Dbg($"  sphere face {fi}: {left * 1e6:0.###} mm2 left of its loops, facets {mesh * 1e6:0.###} mm2");
                ccw = 0;
            }
            else if (wraps || ccw == 1 || (f.Loops.Count == 0 && (reg.IsSphere || reg.IsTorus))) continue;
            if (!reg.Free && reg.IsTorus) Dbg($"  dissolve {Describe(reg, body.Vertices.Count > 0 ? body.Vertices[0] : reg.Origin)}: {ccw} of {f.Loops.Count} loops counter-clockwise");
            problem ??= $"face {fi} ({(reg.IsCylinder ? "cylinder" : reg.IsSphere ? "sphere" : reg.IsTorus ? "torus" : reg.Free ? "single facet" : "plane")}, {reg.Tris.Count} facets): {ccw} of {f.Loops.Count} loops counter-clockwise";
            if (reg.Fixed && !reg.Soft) { problem = $"group face {fi}: {ccw} of {f.Loops.Count} loops counter-clockwise (the group's surface does not fit)"; }
            else if (!reg.Free) retry.UnionWith(reg.Tris);
            else if (!_absorb.Contains(reg.Tris[0]) && Enumerable.Range(0, 3).Select(k => _regions[_regionOf[Across(reg.Tris[0], k)]])
                .Any(c => c.Curved && _t[reg.Tris[0]].All(v => SurfaceDistance(c, _v[v]) <= Math.Max(AbsorbTol(c.IsTorus ? c.Minor : c.Radius), _opt.CylinderFitTolerance))))
            {
                // a flipped facet that lies on a curved neighbour (one corner a little off): take it into that surface
                absorb.Add(reg.Tris[0]);
            }
            else
            {
                // a single facet flipped by its neighbours: dissolve the smallest fitted surface next to it
                var victim = reg.Tris.SelectMany(t => Enumerable.Range(0, 3).Select(k => _regions[_regionOf[Across(t, k)]]))
                    .Where(nb => !nb.Free && (!nb.Fixed || nb.Soft)).MinBy(VictimRank);
                if (victim != null)
                {
                    var tv = _t[reg.Tris[0]];
                    Dbg($"  flipped facet area {Area(reg) * 1e6:0.###} mm2, corner dist to {Describe(victim, _v[tv[0]])}: "
                        + string.Join(", ", tv.Select(v => SurfaceDistance(victim, _v[v]).ToString("E1", CultureInfo.InvariantCulture)))
                        + "; neighbours: " + string.Join(" | ", reg.Tris.SelectMany(t => Enumerable.Range(0, 3).Select(k => _regions[_regionOf[Across(t, k)]])).Select(nb => Describe(nb, _v[tv[0]]))));
                }
                if (victim != null && victim.Curved && !victim.IsTorus && victim.Tris.Count > _opt.PeelThreshold)
                {
                    // peel the large curved neighbour around this facet instead of giving it up
                    var corners = new HashSet<int>(_t[reg.Tris[0]]);
                    foreach (int t in victim.Tris) if (_t[t].Any(corners.Contains)) peel.Add(t);
                }
                else if (victim != null)
                {
                    if (victim.Curved) Dbg("  dissolve " + Describe(victim, _v[_t[reg.Tris[0]][0]]) + " for a flipped facet");
                    retry.UnionWith(victim.Tris);
                }
            }
        }
        if (retry.Count > 0 || peel.Count > 0 || absorb.Count > 0 || _hintExclude.Count > excluded) throw new RetryException(retry, problem!, absorb, peel);
        if (problem != null) throw new MeshException(problem);
    }

    /// <summary>The first pair of edges whose (u,v) segments properly cross (adjacent segments and shared ends excepted).</summary>
    private static (int, int)? Crossing(List<(List<(double u, double v)> uv, List<int> edge)> polys)
    {
        var segs = new List<(double ax, double ay, double bx, double by, int edge, int loop, int index, int count)>();
        for (int l = 0; l < polys.Count; l++)
        {
            var (uv, edge) = polys[l];
            for (int i = 0; i < uv.Count; i++)
            {
                var a = uv[i]; var b = uv[(i + 1) % uv.Count];
                segs.Add((a.u, a.v, b.u, b.v, edge[i], l, i, uv.Count));
            }
        }
        // sweep over u: only segments whose u ranges overlap are compared
        var order = segs.Select((sg, i) => (lo: Math.Min(sg.ax, sg.bx), hi: Math.Max(sg.ax, sg.bx), i)).OrderBy(x => x.lo).ToList();
        static double Orient(double ax, double ay, double bx, double by, double cx, double cy) => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        for (int p = 0; p < order.Count; p++)
            for (int q = p + 1; q < order.Count && order[q].lo <= order[p].hi; q++)
            {
                var s1 = segs[order[p].i]; var s2 = segs[order[q].i];
                if (s1.loop == s2.loop)
                {
                    int d = Math.Abs(s1.index - s2.index);
                    if (d <= 1 || d >= s1.count - 1) continue; // neighbours share an end
                }
                double o1 = Orient(s1.ax, s1.ay, s1.bx, s1.by, s2.ax, s2.ay), o2 = Orient(s1.ax, s1.ay, s1.bx, s1.by, s2.bx, s2.by);
                double o3 = Orient(s2.ax, s2.ay, s2.bx, s2.by, s1.ax, s1.ay), o4 = Orient(s2.ax, s2.ay, s2.bx, s2.by, s1.bx, s1.by);
                if (o1 * o2 < 0 && o3 * o4 < 0) return (s1.edge, s2.edge);
            }
        return null;
    }

    /// <summary>
    /// Signed solid angle (counter-clockwise seen from outside = positive) of a closed polygon on a sphere, as a fan of
    /// spherical triangles from its first point; defined up to multiples of 4π.
    /// </summary>
    private static double SolidAngle(Vec3 centre, List<Vec3> pts)
    {
        double om = 0;
        var a = (pts[0] - centre).Normalized();
        for (int i = 1; i + 1 < pts.Count; i++)
        {
            var b = (pts[i] - centre).Normalized(); var c = (pts[i + 1] - centre).Normalized();
            om += 2 * Math.Atan2(a.Dot(b.Cross(c)), 1 + a.Dot(b) + b.Dot(c) + c.Dot(a));
        }
        return om;
    }

    private static List<Vec3> SampleCoedge(BrepBody body, BrepCoedge ce)
    {
        var e = body.Edges[ce.Edge];
        var pts = new List<Vec3>();
        if (e.Curve is BrepLine) pts.Add(body.Vertices[ce.Forward ? e.Start : e.End]);
        else if (e.Curve is BrepBSpline bs)
        {
            int m = Math.Max(8, 2 * bs.ControlPoints.Count);
            for (int i = 0; i < m; i++)
            {
                double f = ce.Forward ? (double)i / m : (double)(m - i) / m;
                pts.Add(bs.At(bs.Start + (bs.End - bs.Start) * f));
            }
        }
        else
        {
            var (c, n, x, a, b) = e.Curve switch
            {
                BrepCircle ci => (ci.Centre, ci.Normal.Normalized(), ci.XAxis.Normalized(), ci.Radius, ci.Radius),
                BrepEllipse el => (el.Centre, el.Normal.Normalized(), el.XAxis.Normalized(), el.MajorRadius, el.MinorRadius),
                _ => throw new InvalidOperationException(),
            };
            var y = n.Cross(x);
            double Param(Vec3 p) { var d = p - c; return Math.Atan2(d.Dot(y) / b, d.Dot(x) / a); }
            double t0 = 0, span = 2 * Math.PI;
            if (e.Start >= 0)
            {
                t0 = Param(body.Vertices[e.Start]);
                span = Param(body.Vertices[e.End]) - t0;
                while (span <= 1e-12) span += 2 * Math.PI;
            }
            for (int i = 0; i < 8; i++)
            {
                double t = ce.Forward ? t0 + span * i / 8 : t0 + span * (8 - i) / 8;
                pts.Add(c + x * (a * Math.Cos(t)) + y * (b * Math.Sin(t)));
            }
        }
        return pts;
    }

    /// <summary>Signed area in (u,v) with du x dv along the natural surface normal; turn = net angle around a cylinder axis.</summary>
    private static (double area, double turn, double turnV) ParamArea(BrepSurface s, List<Vec3> pts) => ParamArea(s, pts, out _);

    private static (double area, double turn, double turnV) ParamArea(BrepSurface s, List<Vec3> pts, out List<(double u, double v)> polygon)
    {
        var uv = new List<(double u, double v)>();
        double turn = 0, turnV = 0;
        if (s is BrepTorus to)
        {
            // u = angle around the axis * R, v = angle around the tube * r (du x dv = outward normal)
            var a = to.Axis.Normalized(); var x = to.XAxis.Normalized(); var y = a.Cross(x);
            var us = Unwrap(pts.Select(p => { var d = p - to.Centre; return Math.Atan2(d.Dot(y), d.Dot(x)); }).ToList(), out turn);
            var vs = Unwrap(pts.Select(p =>
            {
                var d = p - to.Centre; double h = d.Dot(a);
                return Math.Atan2(h, (d - a * h).Length - to.MajorRadius);
            }).ToList(), out turnV);
            for (int i = 0; i < pts.Count; i++) uv.Add((us[i] * to.MajorRadius, vs[i] * to.MinorRadius));
        }
        else if (s is BrepPlane pl)
        {
            var x = pl.XAxis.Normalized(); var y = pl.Normal.Normalized().Cross(x);
            foreach (var p in pts) { var d = p - pl.Origin; uv.Add((d.Dot(x), d.Dot(y))); }
        }
        else if (s is BrepCylinder or BrepSphere or BrepCone)
        {
            bool sphere = s is BrepSphere;
            var (origin, axis, xaxis, rad) = s switch
            {
                BrepCylinder c => (c.Origin, c.Axis, c.XAxis, c.Radius),
                BrepCone co => (co.Origin, co.Axis, co.XAxis, co.Radius), // (u = angle, v = height: du x dv along the normal)
                BrepSphere sp => (sp.Centre, sp.Axis, sp.XAxis, sp.Radius),
                _ => throw new InvalidOperationException(),
            };
            var cy = (Origin: origin, Radius: rad);
            var a = axis.Normalized(); var x = xaxis.Normalized(); var y = a.Cross(x);
            double prev = double.NaN, acc = 0;
            foreach (var p in pts)
            {
                var d = p - cy.Origin;
                double ang = Math.Atan2(d.Dot(y), d.Dot(x));
                if (!double.IsNaN(prev))
                {
                    double st = ang - prev;
                    while (st > Math.PI) st -= 2 * Math.PI;
                    while (st < -Math.PI) st += 2 * Math.PI;
                    acc += st;
                }
                else acc = ang;
                prev = ang;
                uv.Add(sphere ? (acc, Math.Asin(Math.Clamp(d.Dot(a) / d.Length, -1, 1))) : (acc * cy.Radius, d.Dot(a)));
            }
            var d0 = pts[0] - cy.Origin;
            double a0 = Math.Atan2(d0.Dot(y), d0.Dot(x)), st0 = a0 - prev;
            while (st0 > Math.PI) st0 -= 2 * Math.PI;
            while (st0 < -Math.PI) st0 += 2 * Math.PI;
            turn = acc + st0 - a0;
        }
        double area = 0;
        for (int i = 0; i < uv.Count; i++)
        {
            var (u0, v0) = uv[i]; var (u1, v1) = uv[(i + 1) % uv.Count];
            area += u0 * v1 - u1 * v0;
        }
        polygon = uv;
        return (area / 2, turn, turnV);
    }

    /// <summary>Unwraps a sequence of angles; turn = net change around the closed loop.</summary>
    private static List<double> Unwrap(List<double> ang, out double turn)
    {
        var res = new List<double> { ang[0] };
        static double Step(double d) { while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; return d; }
        for (int i = 1; i < ang.Count; i++) res.Add(res[^1] + Step(ang[i] - ang[i - 1]));
        turn = res[^1] + Step(ang[0] - ang[^1]) - res[0];
        return res;
    }

    /// <summary>
    /// An arc must run along its mesh chain: the chain's points, in order, at increasing angles within the arc (and close to
    /// it). Both surfaces contain the arc either way, so without this check an arc that takes the long way round passes all
    /// other tests and shows up as a thin fin outside the model (yubikey, false cylinders on a blend).
    /// </summary>
    private void CheckFollowsChain(BrepCurve curve, BrepEdge edge, Vec3[] pos, List<int> pts)
    {
        if (curve is not (BrepCircle or BrepEllipse) || pts.Count < 2) return;
        var (c, n, x, a, b) = curve switch
        {
            BrepCircle ci => (ci.Centre, ci.Normal.Normalized(), ci.XAxis.Normalized(), ci.Radius, ci.Radius),
            BrepEllipse el => (el.Centre, el.Normal.Normalized(), el.XAxis.Normalized(), el.MajorRadius, el.MinorRadius),
            _ => throw new InvalidOperationException(),
        };
        var y = n.Cross(x);
        double Param(Vec3 p) { var d = p - c; return Math.Atan2(d.Dot(y) / b, d.Dot(x) / a); }
        double t0 = edge.Start >= 0 ? Param(pos[edge.Start]) : Param(_v[pts[0]]);
        double span = 2 * Math.PI;
        if (edge.Start >= 0) { span = Param(pos[edge.End]) - t0; while (span <= 1e-12) span += 2 * Math.PI; }
        double prev = 0, slack = 1e-6;
        // where two curved surfaces meet, the mesh seam zigzags across the curve (its vertices lie on one surface or the
        // other): a chain point may then fall a little behind the previous one (RondeVorm: 0.1 mm on the sphere seam)
        double back = Math.Min(5 * Math.PI / 180, 2e-4 / Math.Max(a, b));
        // a chain step may be at most a quarter turn: a coarse mesh never has longer arc segments between vertices
        for (int i = 1; i < pts.Count; i++)
        {
            var p = i == pts.Count - 1 && edge.End >= 0 ? pos[edge.End] : _v[pts[i]];
            double t = Param(p) - t0;
            while (t < -slack) t += 2 * Math.PI;
            if (i == pts.Count - 1 && (edge.End >= 0 || pts[^1] == pts[0])) t = span; // end vertex, or back at a ring's start
            else if (t > 2 * Math.PI - back && t - 2 * Math.PI >= prev - back) t -= 2 * Math.PI; // just behind the start
            // between two chain points the arc stays close to their chord (a mesh follows its curves within a small
            // chordal deviation; a stretched ellipse can bulge out by millimetres between two points)
            var pa = i == 1 && edge.Start >= 0 ? pos[edge.Start] : _v[pts[i - 1]];
            double chord = (p - pa).Length, dev = 0;
            for (int k = 1; k < 8; k++)
            {
                double tk = t0 + prev + (t - prev) * k / 8;
                var q = c + x * (a * Math.Cos(tk)) + y * (b * Math.Sin(tk));
                var d = p - pa; double u = chord > 0 ? Math.Clamp((q - pa).Dot(d) / (chord * chord), 0, 1) : 0;
                dev = Math.Max(dev, (q - (pa + d * u)).Length);
            }
            if (dev > Math.Max(0.25 * chord, 1e-4))
            {
                Dbg(FormattableString.Invariant($"  arc bulges {dev * 1000:F3} mm from a chord of {chord * 1000:F3} mm"));
                throw new MeshException("arc bulges away from its mesh edge");
            }
            if (t + back + slack < prev || t - prev > Math.PI / 2 + slack || t > span + slack)
            {
                Dbg(FormattableString.Invariant($"  arc does not follow chain: point {i}/{pts.Count - 1} at {t * 180 / Math.PI:F1} deg after {prev * 180 / Math.PI:F1}, span {span * 180 / Math.PI:F1}, ring {edge.Start < 0}"));
                throw new MeshException("arc does not follow its mesh edge");
            }
            prev = Math.Max(prev, t);
        }
    }

    /// <summary>
    /// The exact intersection of two surfaces along a mesh chain, as a cubic B-spline (written as SpaceClaim's B_CURVE).
    /// Every chain point is moved onto both surfaces (the ends are the edge's vertices), and wherever the spline between
    /// two points strays from the true intersection by more than 0.1 nm, the exact point there is added and it is fitted
    /// again. Used where the intersection is not a line, circle or ellipse (torus x plane, two skew cylinders, ...).
    /// </summary>
    private BrepBSpline TraceIntersection(Region ra, Region rb, List<int> pts, BrepEdge edge, Vec3[] pos)
    {
        const double tol = 1e-10;
        bool ring = edge.Start < 0;
        var pair = new List<Region> { ra, rb };
        var plane = !ra.Curved && rb.Curved ? ra : !rb.Curved && ra.Curved ? rb : null;
        var curved = plane == ra ? rb : ra;
        // dir: the curve's direction near x; span: how far the curve may lie from x
        (Vec3 p, bool ok) OnBoth(Vec3 x, Vec3 dir, double span)
        {
            var (p, res) = Project(x, pair);
            if (res <= tol && (p - x).Length < 1e-3) return (p, true);
            if (plane == null) return (p, false);
            // a plane nearly tangent to the curved surface (a facet next to a fillet: the rest of the model is still
            // mesh): Gauss-Newton stalls (parallel gradients), so search across the curve within the plane instead
            var x0 = x - plane.N * (plane.N.Dot(x) - plane.D);
            var d = plane.N.Cross(dir);
            if (d.Length < 1e-12) return (p, false);
            d = d.Normalized();
            double F(double t) => Signed(curved, x0 + d * t, out _);
            double f0 = F(0), h = span / 64;
            if (f0 == 0) return (x0, true);
            for (int k = 1; k <= 128; k++)
                foreach (double sg in new[] { 1.0, -1.0 })
                {
                    double a = sg * (k - 1) * h, b = sg * k * h, fa = F(a), fb = F(b);
                    if (Math.Sign(fa) == Math.Sign(fb)) continue;
                    for (int it = 0; it < 200 && Math.Abs(b - a) > 1e-16; it++)
                    {
                        double m = (a + b) / 2, fm = F(m);
                        if (Math.Sign(fm) == Math.Sign(fa)) { a = m; fa = fm; } else { b = m; fb = fm; }
                    }
                    var q = x0 + d * ((a + b) / 2);
                    var (q2, res2) = Project(q, pair);
                    return res2 <= tol ? (q2, true) : (q, Math.Abs(F((a + b) / 2)) <= tol && Math.Abs(plane.N.Dot(q) - plane.D) <= tol);
                }
            return (p, false);
        }
        Vec3 Dir(int i) => _v[pts[Math.Min(i + 1, pts.Count - 1)]] - _v[pts[Math.Max(i - 1, 0)]];
        double Span(int i) => Math.Max(Dir(i).Length, 1e-6);
        var list = new List<Vec3>();
        for (int i = 0; i < pts.Count; i++)
        {
            if (!ring && i == 0) { list.Add(pos[edge.Start]); continue; }
            if (!ring && i == pts.Count - 1) { list.Add(pos[edge.End]); continue; }
            if (ring && i == pts.Count - 1 && pts[i] == pts[0]) { list.Add(list[0]); continue; }
            var (p, ok) = OnBoth(_v[pts[i]], Dir(i), Span(i));
            if (!ok) throw new MeshException("intersection point not found (surfaces nearly tangent?)");
            list.Add(p);
        }
        if (ring && (list[^1] - list[0]).Length > 0) list.Add(list[0]);
        for (int round = 0; ; round++)
        {
            // at least 4 points for a cubic; then add exact points where the spline strays
            var u = new double[list.Count];
            for (int k = 1; k < list.Count; k++) u[k] = u[k - 1] + (list[k] - list[k - 1]).Length;
            var next = new List<Vec3> { list[0] };
            BrepBSpline? bs = list.Count >= 4 ? BSpline.Interpolate(list, ring) : null;
            bool added = false;
            double worst = 0;
            for (int k = 0; k + 1 < list.Count; k++)
            {
                var guess = bs != null ? bs.At((u[k] + u[k + 1]) / 2) : (list[k] + list[k + 1]) * 0.5;
                var (q, ok) = OnBoth(guess, list[k + 1] - list[k], Math.Max((list[k + 1] - list[k]).Length, 1e-7));
                if (!ok)
                {
                    Dbg(FormattableString.Invariant($"    trace: no intersection point near ({guess.X * 1000:0.###},{guess.Y * 1000:0.###},{guess.Z * 1000:0.###}): {Describe(ra, guess)} / {Describe(rb, guess)}"));
                    throw new MeshException("intersection point not found (surfaces nearly tangent?)");
                }
                worst = Math.Max(worst, (q - guess).Length);
                if (bs == null || (q - guess).Length > tol) { next.Add(q); added = true; }
                next.Add(list[k + 1]);
            }
            if (round >= 8) Dbg(FormattableString.Invariant($"    trace round {round}: {list.Count} points, worst {worst:E1} m"));
            if (!added && bs != null)
            {
                // the spline must follow the mesh chain, not bulge away between its points (as a fin outside the model)
                for (int k = 0; k + 1 < list.Count; k++)
                {
                    var a = list[k]; var b = list[k + 1]; double chord = (b - a).Length;
                    for (int s2 = 1; s2 < 4; s2++)
                    {
                        var q = bs.At(u[k] + (u[k + 1] - u[k]) * s2 / 4);
                        var d = b - a; double w = chord > 0 ? Math.Clamp((q - a).Dot(d) / (chord * chord), 0, 1) : 0;
                        if ((q - (a + d * w)).Length > Math.Max(0.25 * chord, 1e-4)) throw new MeshException("arc bulges away from its mesh edge");
                    }
                }
                return bs;
            }
            if (round >= 12 || next.Count > 5000) throw new MeshException("intersection curve does not converge");
            list = next;
        }
    }

    /// <summary>
    /// How far a vertex or edge may be off the surfaces it lies on: half of Parasolid's linear precision (res_linear =
    /// 1e-8 m in every body). Was 0.5 nm: three planes fitted to float32 corners 300 mm out (two of them nearly
    /// parallel) do not meet within that, and each such vertex dissolved a plane (bigbox: residuals of 0.6–15 nm).
    /// </summary>
    internal const double GeomTol = 5e-9;

    /// <summary>The edge's end points must lie on its curve, and points along it on both adjacent surfaces.</summary>
    private static void CheckEdge(BrepCurve curve, BrepEdge edge, Vec3[] pos, Region ra, Region rb)
    {
        const double tol = GeomTol;
        static double Dist(Region r, Vec3 p) => SurfaceDistance(r, p);
        var samples = new List<Vec3>();
        switch (curve)
        {
            case BrepLine:
                var a = pos[edge.Start]; var b = pos[edge.End];
                for (int i = 0; i <= 4; i++) samples.Add(a + (b - a) * (i / 4.0));
                break;
            case BrepBSpline bs:
            {
                if (edge.Start >= 0 && ((bs.At(bs.Start) - pos[edge.Start]).Length > tol || (bs.At(bs.End) - pos[edge.End]).Length > tol))
                    throw new MeshException("edge end point is not on its curve");
                int m = 4 * bs.ControlPoints.Count;
                for (int i = 0; i <= m; i++) samples.Add(bs.At(bs.Start + (bs.End - bs.Start) * i / m));
                break;
            }
            case BrepCircle or BrepEllipse:
            {
                var (c, n, x, ra_, rb_) = curve switch
                {
                    BrepCircle ci => (ci.Centre, ci.Normal.Normalized(), ci.XAxis.Normalized(), ci.Radius, ci.Radius),
                    BrepEllipse el => (el.Centre, el.Normal.Normalized(), el.XAxis.Normalized(), el.MajorRadius, el.MinorRadius),
                    _ => throw new InvalidOperationException(),
                };
                var y = n.Cross(x);
                double Param(Vec3 p) { var d = p - c; return Math.Atan2(d.Dot(y) / rb_, d.Dot(x) / ra_); }
                Vec3 At(double t) => c + x * (ra_ * Math.Cos(t)) + y * (rb_ * Math.Sin(t));
                double t0, span;
                if (edge.Start < 0) { t0 = 0; span = 2 * Math.PI; }
                else
                {
                    foreach (var p in new[] { pos[edge.Start], pos[edge.End] })
                    {
                        var q = At(Param(p));
                        if ((q - p).Length > tol || Math.Abs((p - c).Dot(n)) > tol)
                            throw new MeshException("edge end point is not on its arc");
                    }
                    t0 = Param(pos[edge.Start]);
                    span = Param(pos[edge.End]) - t0;
                    while (span <= 1e-12) span += 2 * Math.PI;
                }
                for (int i = 0; i <= 8; i++) samples.Add(At(t0 + span * i / 8));
                break;
            }
        }
        foreach (var p in samples)
            if (Dist(ra, p) > tol || Dist(rb, p) > tol) throw new MeshException("edge leaves one of its surfaces");
    }

    // ------------------------------------------------------------------ tori

    /// <summary>
    /// Tori (e.g. a round fillet around a rod, or between a sphere and a cylinder): seed = a facet region and two rings of
    /// neighbours that bend moderately; a torus is fitted (axis from the facet normal lines, see <see cref="TorusFit"/>) and
    /// must fit the vertices tightly, with every facet facing the same way. Then grown like a sphere: tight with refits,
    /// then loosely with the fit fixed.
    /// </summary>
    /// <summary>Identifies a seed region with its cluster by their triangles (64-bit FNV-1a, plus the count).</summary>
    private (ulong, int) ClusterKey(int seed, List<int> cluster)
    {
        ulong h = 14695981039346656037;
        void Mix(int x) { h = (h ^ (uint)x) * 1099511628211; }
        foreach (int t in _regions[seed].Tris.Order()) Mix(t);
        Mix(-1);
        var all = cluster.SelectMany(r => _regions[r].Tris).Order().ToList();
        foreach (int t in all) Mix(t);
        return (h, all.Count);
    }

    /// <summary>
    /// Cones (drafted walls, countersinks, chamfers around an axis): facets whose vertices lie on one cone, found like
    /// tori (a seed with two rings of bent neighbours, a fit, tight growth with refits, loose growth). They are looked for
    /// before spheres and tori: a band between two circles fits a cone, a sphere and a torus alike. A cone is therefore not
    /// taken where its smoothly continuing neighbours lie on one sphere or torus together with it (the latitude bands of
    /// a faceted ball are cones too).
    /// </summary>
    private void FindCones()
    {
        var swc = System.Diagnostics.Stopwatch.StartNew();
        double minStep = Math.Cos(_opt.AngleToleranceDeg * Math.PI / 180), maxStep = Math.Cos(_opt.CylinderMaxStepDeg * Math.PI / 180);
        var nbrs = Neighbours(SharedEdges());
        long tNbrs = swc.ElapsedMilliseconds;
        var (freeOf, joinOf) = ExclusionFlags(_noCone);
        long tFlags = swc.ElapsedMilliseconds;
        long tFit = 0, nFit = 0, tGrow = 0, tBeyond = 0, tLoose = 0, tSeedLoop = 0, nGrow = 0;
        bool Usable(int r) => r < _regions.Count && !_regions[r].Curved && !_regions[r].Fixed && _regions[r].Tris.Count > 0 && freeOf[r];
        bool Joinable(int r) => r < _regions.Count && !_regions[r].Curved && !_regions[r].Fixed && _regions[r].Tris.Count > 0 && joinOf[r];
        double cosFace = Math.Cos(_opt.FacetNormalToleranceDeg * Math.PI / 180);
        bool Facing(int r, Cone k, bool hollow) { k.Distance(_regions[r].Centroid, out var g); return g.Dot(_regions[r].N) * (hollow ? -1 : 1) > cosFace; }
        bool On(int r, Cone k, double tol) => VerticesOf(_regions[r]).All(v => Math.Abs(k.Distance(_v[v], out _)) <= tol);
        bool Bent(int m, int nb) { double c0 = _regions[m].N.Dot(_regions[nb].N); return c0 < minStep && c0 > maxStep; }
        // the seed cluster only from strips (a few facets each): a large flat face next to the cone (the rim of a bowl at
        // 31 degrees) must not be pulled in
        // (and no slivers: their normals are unreliable, and a micro relief of steps a few micrometres high fits a nearly
        // flat "cone" through them; bigbox: 100 such seeds a round, each grown at length before it failed)
        bool Strip(int r) => _regions[r].Tris.Count <= 4 && !IsSliver(_regions[r]);
        List<Vec3> Pts(IEnumerable<int> cl) => cl.SelectMany(r => VerticesOf(_regions[r])).Distinct().Select(v => _v[v]).ToList();
        List<(Vec3, Vec3)> Lines(IEnumerable<int> cl) => cl.Where(r => !IsSliver(_regions[r])).Select(r => (_regions[r].Centroid, _regions[r].N)).ToList();
        double fitTol = _opt.CylinderFitTolerance;
        var tried = new HashSet<int>();

        for (int seed = 0; seed < _regions.Count; seed++)
        {
            if ((seed & 255) == 0) _opt.Cancel.ThrowIfCancellationRequested();
            if (!Usable(seed) || !Strip(seed) || tried.Contains(seed) || !nbrs.ContainsKey(seed)) continue;
            var cluster = new List<int> { seed };
            var inCluster = new HashSet<int> { seed };
            for (int ring = 0; ring < 2; ring++)
                foreach (int m in cluster.ToList())
                    foreach (int nb in nbrs[m])
                        if (!inCluster.Contains(nb) && Usable(nb) && Strip(nb) && Bent(m, nb)) { inCluster.Add(nb); cluster.Add(nb); }
            if (cluster.Count < 5) continue;
            var key = ClusterKey(seed, cluster);
            if (_cache.NoCone.ContainsKey(key)) continue;
            var pts = Pts(cluster);
            long f0 = swc.ElapsedTicks;
            bool coneOk = ConeFit.Fit(pts, Lines(cluster), out var cone, out double res, giveUpRms: 10 * fitTol);
            tFit += swc.ElapsedTicks - f0; nFit++;
            if (Environment.GetEnvironmentVariable("RSCONE") == "1") Dbg(FormattableString.Invariant($"cone try {seed}: {cluster.Count} regions, {pts.Count} pts, ok {coneOk}, res {res:E2}, r {cone.Radius * 1000:F3} slope {cone.Slope:F4}"));
            if (!coneOk || res > fitTol || pts.Max(p => cone.Profile(p).rho) > _opt.MaxCurvedRadius)
            { _cache.NoCone.TryAdd(key, 0); continue; }
            cone.Distance(_regions[seed].Centroid, out var g0);
            bool hollow = g0.Dot(_regions[seed].N) < 0;
            if (!cluster.All(r => Facing(r, cone, hollow))) { _cache.NoCone.TryAdd(key, 0); continue; }

            // tight growth with refits
            long g0t = swc.ElapsedTicks; nGrow++;
            bool grew = true;
            while (grew)
            {
                grew = false;
                var added = new List<int>();
                foreach (int m in cluster.ToList())
                    foreach (int c in nbrs[m])
                        if (!inCluster.Contains(c) && Usable(c) && Facing(c, cone, hollow) && On(c, cone, fitTol))
                        { inCluster.Add(c); cluster.Add(c); added.Add(c); grew = true; }
                if (grew)
                {
                    var all = Pts(cluster);
                    var c2 = cone;
                    if (ConeFit.Refine(all, ref c2) && c2.Slope > ConeFit.MinSlope && c2.Slope < ConeFit.MaxSlope && all.Max(p => Math.Abs(c2.Distance(p, out _))) <= fitTol) cone = c2;
                    else
                    {
                        foreach (int c in added) { inCluster.Remove(c); cluster.Remove(c); }
                        break;
                    }
                }
            }
            tGrow += swc.ElapsedTicks - g0t; long b0t = swc.ElapsedTicks;
            // not a band of a sphere or torus: the smooth continuation beyond the cone does not lie on one with it
            var beyond = cluster.SelectMany(m => nbrs[m].Where(nb => !inCluster.Contains(nb) && Usable(nb) && Bent(m, nb))).Distinct().ToList();
            if (beyond.Count > 0)
            {
                var ext = Pts(cluster.Concat(beyond));
                bool sphere = FitSphere(ext, out _, out _, out double sres) && sres <= fitTol;
                bool torus = !sphere && TorusFit.Fit(ext, Lines(cluster.Concat(beyond)), out _, out double tres) && tres <= fitTol;
                if (sphere || torus)
                {
                    Dbg($"cone from {seed}: {cluster.Count} regions are a band of a {(sphere ? "sphere" : "torus")}");
                    tried.UnionWith(cluster);
                    _cache.NoCone.TryAdd(key, 0); // (the same seed cluster grows the same way next round)
                    continue;
                }
            }
            tBeyond += swc.ElapsedTicks - b0t; long l0t = swc.ElapsedTicks;
            // loose growth (fit fixed)
            double rMax = Pts(cluster).Max(p => cone.Profile(p).rho);
            double join = JoinTol(rMax);
            grew = join > 0;
            while (grew)
            {
                grew = false;
                foreach (int m in cluster.ToList())
                    foreach (int c in nbrs[m])
                        if (!inCluster.Contains(c) && Joinable(c) && (IgnoreNormal(_regions[c]) || Facing(c, cone, hollow)) && On(c, cone, join))
                        { inCluster.Add(c); cluster.Add(c); grew = true; }
            }
            tried.UnionWith(cluster);
            double span = 0;
            var solid = cluster.Where(r => !IsSliver(_regions[r])).ToList();
            foreach (int r1 in solid) foreach (int r2 in solid)
                span = Math.Max(span, Math.Acos(Math.Clamp(_regions[r1].N.Dot(_regions[r2].N), -1, 1)));
            tLoose += swc.ElapsedTicks - l0t;
            if (cluster.Count < Math.Max(3, _opt.CylinderMinStrips) || span * 180 / Math.PI < _opt.CylinderMinSpanDeg)
            { _cache.NoCone.TryAdd(key, 0); continue; }

            var reg = _regions[seed];
            foreach (int r in cluster.Skip(1))
            {
                foreach (int t in _regions[r].Tris) { _regionOf[t] = seed; reg.Tris.Add(t); }
                _regions[r].Tris.Clear();
            }
            reg.IsCone = true; reg.Origin = cone.Origin; reg.Axis = cone.Axis; reg.Radius = cone.Radius; reg.Minor = cone.Slope; reg.Hollow = hollow;
            ConesFound++;
            Dbg(FormattableString.Invariant($"cone from {seed}: {cluster.Count} regions, r={cone.Radius * 1000:F4} mm at ({cone.Origin.X * 1000:F4},{cone.Origin.Y * 1000:F4},{cone.Origin.Z * 1000:F4}), half angle {Math.Atan(cone.Slope) * 180 / Math.PI:F4} deg, a=({cone.Axis.X:F6},{cone.Axis.Y:F6},{cone.Axis.Z:F6}), {(hollow ? "hollow" : "solid")}, span {span * 180 / Math.PI:F0} deg"));
        }
        long F = System.Diagnostics.Stopwatch.Frequency / 1000;
        Dbg($"  cones: neighbours {tNbrs} ms, flags {tFlags - tNbrs} ms, {nFit} fits {tFit / F} ms, {nGrow} grown {tGrow / F} ms, beyond {tBeyond / F} ms, loose {tLoose / F} ms, total {swc.ElapsedMilliseconds} ms");
        DropEmptyRegions();
    }

    /// <summary>Height (along the cone's axis, from its origin) of the lowest point of a ring edge's circle.</summary>
    private static double RingLow(BrepCurve curve, Region cone) => curve switch
    {
        BrepCircle c => (c.Centre - cone.Origin).Dot(cone.Axis) - c.Radius * c.Normal.Normalized().Cross(cone.Axis).Length,
        _ => double.PositiveInfinity,
    };

    /// <summary>
    /// A cone's axis that is within half a degree of a neighbouring plane's normal is put on it exactly (and the cone
    /// refitted around it): the two then meet in an exact circle, as in SpaceClaim's own files.
    /// </summary>
    private void RegulariseCones()
    {
        var shared = SharedEdges();
        var nbrs = Neighbours(shared);
        double cosSnap = Math.Cos(0.5 * Math.PI / 180);
        for (int ri = 0; ri < _regions.Count; ri++)
        {
            var reg = _regions[ri];
            if (!reg.IsCone || reg.Tris.Count == 0 || (reg.Fixed && reg.Soft) || !nbrs.TryGetValue(ri, out var nb)) continue;
            var plane = nb.Select(i => _regions[i]).Where(o => !o.Curved && !o.Free && o.Tris.Count > 0 && Math.Abs(o.N.Dot(reg.Axis)) > cosSnap)
                .OrderByDescending(o => Math.Abs(o.N.Dot(reg.Axis))).FirstOrDefault();
            if (plane == null || Math.Abs(plane.N.Dot(reg.Axis)) == 1) continue;
            var axis = plane.N * Math.Sign(plane.N.Dot(reg.Axis));
            var pts = VerticesOf(reg).Select(v => _v[v]).ToList();
            var c = new Cone(reg.Origin, axis, reg.Radius, reg.Minor);
            if (!ConeFit.Refine(pts, ref c, fixAxis: true) || !(c.Slope > ConeFit.MinSlope)) continue;
            double dev = pts.Max(p => Math.Abs(c.Distance(p, out _)));
            if (dev > Math.Max(JoinTol(reg.Radius), _opt.CylinderFitTolerance)) { Dbg($"  cone axis not snapped to the plane: {dev:E1} off"); continue; }
            reg.Origin = c.Origin; reg.Axis = c.Axis; reg.Radius = c.Radius; reg.Minor = c.Slope;
        }
    }

    /// <summary>
    /// Boundary of a cone: with a coaxial neighbour (perpendicular plane, cylinder, cone, sphere centred on the axis) a
    /// circle around the axis; a plane through the axis cuts a generator line. Other intersections are traced.
    /// </summary>
    private BrepCurve ConeCurve(Region cone, Region other, List<int> pts)
    {
        var a = cone.Axis; double k = cone.Minor;
        const double eps = 1e-9;
        double OffAxis(Vec3 p) { var w = p - cone.Origin; w -= a * w.Dot(a); return w.Length; }
        double H(Vec3 p) => (p - cone.Origin).Dot(a);
        double hm = pts.Average(i => H(_v[i]));
        var hs = new List<double>();
        if (!other.Curved && other.N.Cross(a).Length < eps)
            hs.Add((other.D - other.N.Dot(cone.Origin)) / other.N.Dot(a));
        else if (!other.Curved && Math.Abs(other.N.Dot(a)) < eps && Math.Abs(other.N.Dot(cone.Origin) - other.D) < eps)
        {
            // a plane through the axis: a generator line
            if (pts.Count >= 2) return new BrepLine();
            throw new MeshException("cone generator chain too short");
        }
        else if (other.IsCylinder && other.Axis.Cross(a).Length < eps && OffAxis(other.Origin) < eps)
            hs.Add((other.Radius - cone.Radius) / k);
        else if (other.IsCone && other.Axis.Cross(a).Length < eps && OffAxis(other.Origin) < eps)
        {
            // r0 + k h = r1 + k1 s (h - h1)
            double sg = Math.Sign(other.Axis.Dot(a)), h1 = H(other.Origin), k1 = other.Minor * sg;
            if (Math.Abs(k - k1) < 1e-12) throw new MeshException("two coaxial cones with the same slope");
            hs.Add((other.Radius - k1 * h1 - cone.Radius) / (k - k1));
        }
        else if (other.IsSphere && OffAxis(other.Origin) < eps)
        {
            // (r0 + k h)^2 + (h - hs)^2 = R^2
            double h0 = H(other.Origin), qa = k * k + 1, qb = 2 * (cone.Radius * k - h0), qc = cone.Radius * cone.Radius + h0 * h0 - other.Radius * other.Radius;
            double disc = qb * qb - 4 * qa * qc;
            if (disc < 0) throw new MeshException("sphere does not meet the cone");
            foreach (double sgn in new[] { 1.0, -1.0 }) hs.Add((-qb + sgn * Math.Sqrt(disc)) / (2 * qa));
        }
        else throw new MeshException("cone meets a surface along a curve that is not a circle");
        double h = hs.MinBy(x => Math.Abs(x - hm));
        double radius = cone.Radius + k * h;
        if (radius <= 1e-12) throw new MeshException("cone meets its neighbour at its apex");
        var c = cone.Origin + a * h;
        double turn = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var p = _v[pts[i]] - c; var q = _v[pts[i + 1]] - c;
            turn += Math.Atan2(p.Cross(q).Dot(a), p.Dot(q));
        }
        var normal = turn >= 0 ? a : a * -1;
        return new BrepCircle(c, normal, Perp(normal), radius);
    }

    /// <summary>
    /// Per region, once per search: none of its facets is excluded (forced, peeled or excluded from this kind), and none
    /// is peeled or excluded from this kind (for joining). Asked again and again for the same large plane while growing
    /// clusters, scanning its thousands of facets each time made the search quadratic (bigbox: 80 s in the spheres).
    /// Regions only lose facets during a search, so the flags stay true.
    /// </summary>
    private (bool[] free, bool[] joinable) ExclusionFlags(HashSet<int> kind)
    {
        var free = new bool[_regions.Count]; var join = new bool[_regions.Count];
        for (int r = 0; r < _regions.Count; r++)
        {
            var tris = _regions[r].Tris;
            free[r] = !tris.Any(t => _forced.Contains(t) || _noCurve.Contains(t) || kind.Contains(t));
            join[r] = !tris.Any(t => Peeled(t) || kind.Contains(t) || _keep.Contains(t));
        }
        return (free, join);
    }

    private void FindTori()
    {
        double minStep = Math.Cos(_opt.AngleToleranceDeg * Math.PI / 180), maxStep = Math.Cos(_opt.CylinderMaxStepDeg * Math.PI / 180);
        var nbrs = Neighbours(SharedEdges());
        var (freeOf, joinOf) = ExclusionFlags(_noTorus);
        bool Usable(int r) => r < _regions.Count && !_regions[r].Curved && !_regions[r].Fixed && _regions[r].Tris.Count > 0 && freeOf[r];
        bool Joinable(int r) => r < _regions.Count && !_regions[r].Curved && !_regions[r].Fixed && _regions[r].Tris.Count > 0 && joinOf[r];
        double cosFace = Math.Cos(_opt.FacetNormalToleranceDeg * Math.PI / 180);
        bool Facing(int r, Torus t, bool hollow)
        {
            t.Distance(_regions[r].Centroid, out var g);
            return g.Dot(_regions[r].N) * (hollow ? -1 : 1) > cosFace;
        }
        bool On(int r, Torus t, double tol) => VerticesOf(_regions[r]).All(v => Math.Abs(t.Distance(_v[v], out _)) <= tol);
        List<Vec3> Pts(List<int> cl) => cl.SelectMany(r => VerticesOf(_regions[r])).Distinct().Select(v => _v[v]).ToList();
        List<(Vec3, Vec3)> Lines(List<int> cl) => cl.Where(r => !IsSliver(_regions[r])).Select(r => (_regions[r].Centroid, _regions[r].N)).ToList();
        double fitTol = _opt.CylinderFitTolerance;
        var tried = new HashSet<int>();

        for (int seed = 0; seed < _regions.Count; seed++)
        {
            if ((seed & 255) == 0) _opt.Cancel.ThrowIfCancellationRequested();
            if (!Usable(seed) || tried.Contains(seed) || !nbrs.ContainsKey(seed)) continue;
            var cluster = new List<int> { seed };
            var inCluster = new HashSet<int> { seed };
            for (int ring = 0; ring < 2; ring++)
                foreach (int m in cluster.ToList())
                    foreach (int nb in nbrs[m])
                    {
                        if (inCluster.Contains(nb) || !Usable(nb)) continue;
                        double c0 = _regions[m].N.Dot(_regions[nb].N);
                        if (c0 < minStep && c0 > maxStep) { inCluster.Add(nb); cluster.Add(nb); }
                    }
            if (cluster.Count < 8) continue;
            // the same seed and cluster as in an earlier round (or the other variant) that gave no torus: the same
            // points give the same fit, skip it (organic meshes: almost every facet is a seed, every round)
            var key = ClusterKey(seed, cluster);
            if (_cache.NoTorus.ContainsKey(key)) continue;
            var pts = Pts(cluster);
            // (gives up early on a patch that clearly is no torus: 10x the fit tolerance and no longer improving)
            bool fitOk = TorusFit.Fit(pts, Lines(cluster), out var tor, out double res, giveUpRms: 10 * fitTol);
            if (!fitOk || res > fitTol || tor.R > _opt.MaxCurvedRadius
                // points that also fit a sphere belong to the sphere finder (a torus patch near the sphere limit)
                || (FitSphere(pts, out _, out _, out double sres) && sres <= fitTol))
            { _cache.NoTorus.TryAdd(key, 0); continue; }
            tor.Distance(_regions[seed].Centroid, out var g0);
            bool hollow = g0.Dot(_regions[seed].N) < 0;
            if (!cluster.All(r => Facing(r, tor, hollow))) { _cache.NoTorus.TryAdd(key, 0); continue; }

            // tight growth with refits
            bool grew = true;
            while (grew)
            {
                grew = false;
                var added = new List<int>();
                foreach (int m in cluster.ToList())
                    foreach (int c in nbrs[m])
                        if (!inCluster.Contains(c) && Usable(c) && Facing(c, tor, hollow) && On(c, tor, fitTol))
                        { inCluster.Add(c); cluster.Add(c); added.Add(c); grew = true; }
                if (grew)
                {
                    var all = Pts(cluster);
                    var t2 = tor;
                    if (TorusFit.Refine(all, ref t2) && TorusFit.MaxResidual(all, t2) <= fitTol) tor = t2;
                    else
                    {
                        foreach (int c in added) { inCluster.Remove(c); cluster.Remove(c); }
                        break;
                    }
                }
            }
            // loose growth (fit fixed)
            double join = JoinTol(tor.r);
            grew = join > 0;
            while (grew)
            {
                grew = false;
                foreach (int m in cluster.ToList())
                    foreach (int c in nbrs[m])
                        if (!inCluster.Contains(c) && Joinable(c) && (IgnoreNormal(_regions[c]) || Facing(c, tor, hollow))
                            && On(c, tor, _regions[c].Tris.Count == 1 && _absorb.Contains(_regions[c].Tris[0]) ? SeamFactor * AbsorbTol(tor.r) : join))
                        { inCluster.Add(c); cluster.Add(c); grew = true; }
            }
            tried.UnionWith(cluster);
            double span = 0;
            var solid = cluster.Where(r => !IsSliver(_regions[r])).ToList();
            foreach (int r1 in solid) foreach (int r2 in solid)
                span = Math.Max(span, Math.Acos(Math.Clamp(_regions[r1].N.Dot(_regions[r2].N), -1, 1)));
            if (cluster.Count < _opt.SphereMinRegions || span * 180 / Math.PI < _opt.CylinderMinSpanDeg) continue;

            var reg = _regions[seed];
            foreach (int r in cluster.Skip(1))
            {
                foreach (int t in _regions[r].Tris) { _regionOf[t] = seed; reg.Tris.Add(t); }
                _regions[r].Tris.Clear();
            }
            _torusTris.UnionWith(reg.Tris);
            reg.IsTorus = true; reg.Origin = tor.Centre; reg.Axis = tor.Axis; reg.Radius = tor.R; reg.Minor = tor.r; reg.Hollow = hollow;
            ToriFound++;
            Dbg(FormattableString.Invariant($"torus from {seed}: {cluster.Count} regions, R={tor.R * 1000:F4} mm r={tor.r * 1000:F4} mm c=({tor.Centre.X * 1000:F4},{tor.Centre.Y * 1000:F4},{tor.Centre.Z * 1000:F4}) a=({tor.Axis.X:F6},{tor.Axis.Y:F6},{tor.Axis.Z:F6}), {(hollow ? "hollow" : "solid")}, span {span * 180 / Math.PI:F0} deg"));
        }
        DropEmptyRegions();
    }

    /// <summary>
    /// The cylinders a torus is tangent to (coaxial, radius R + r or R − r: the end rounding next to a corner fillet).
    /// Where the mesh divides such a cylinder in twisted triangles instead of strips along its axis, and puts the torus'
    /// finer rim vertices on the chords of the cylinder's polygon (yubikey: 5.6 µm inside), the strip-based cylinder search
    /// finds nothing and the torus ends up next to loose facets with a nearly tangent, unusable intersection. Starting at
    /// the torus, facets on such a cylinder are collected: vertices within the join tolerance, or, on the seam with the
    /// torus, within the seam tolerance of both.
    /// </summary>
    private void GrowTangentCylinders()
    {
        var nbrs = Neighbours(SharedEdges());
        double cosFace = Math.Cos(_opt.FacetNormalToleranceDeg * Math.PI / 180);
        foreach (int ti in Enumerable.Range(0, _regions.Count).Where(i => _regions[i].IsTorus && _regions[i].Tris.Count > 0).ToList())
        {
            var tr = _regions[ti];
            var tor = new Torus(tr.Origin, tr.Axis, tr.Radius, tr.Minor);
            foreach (double rc in new[] { tr.Radius + tr.Minor, tr.Radius - tr.Minor })
            {
                double join = JoinTol(rc);
                if (rc <= 0 || join <= 0) continue;
                double Rho(Vec3 p) { var w = p - tr.Origin; w -= tr.Axis * w.Dot(tr.Axis); return w.Length; }
                // a seam vertex lies on the circle where the two touch (h = 0), not merely in the band around it where
                // both surfaces are within micrometres of each other (that is the torus' own facets)
                bool VertexOn(int v)
                {
                    double d = Math.Abs(Rho(_v[v]) - rc);
                    return d <= join || (d <= SeamFactor * join && Math.Abs((_v[v] - tr.Origin).Dot(tr.Axis)) <= 0.01 * tr.Minor
                        && Math.Abs(tor.Distance(_v[v], out _)) <= SeamFactor * join);
                }
                bool? hollow = null;
                bool Fits(int r)
                {
                    var reg = _regions[r];
                    if (reg.Curved || reg.Fixed || reg.Tris.Count == 0 || reg.Tris.Any(_forced.Contains) || reg.Tris.Any(Peeled) || reg.Tris.Any(_noCylinder.Contains)) return false;
                    if (!VertexOn(VerticesOf(reg).First()) || !VerticesOf(reg).All(VertexOn)) return false;
                    if (IgnoreNormal(reg)) return true;
                    if (Math.Abs(reg.N.Dot(tr.Axis)) > Math.Sin(_opt.CylinderAngleToleranceDeg * Math.PI / 180) * 10) return false;
                    var rad = reg.Centroid - tr.Origin; rad -= tr.Axis * rad.Dot(tr.Axis);
                    double c = rad.Normalized().Dot(reg.N);
                    if (Math.Abs(c) < cosFace) return false;
                    hollow ??= c < 0;
                    return (c < 0) == hollow;
                }
                var cluster = new List<int>();
                var seen = new HashSet<int> { ti };
                foreach (int s in nbrs.GetValueOrDefault(ti, []))
                    if (seen.Add(s) && Fits(s)) cluster.Add(s);
                for (int qi = 0; qi < cluster.Count; qi++)
                    foreach (int c in nbrs.GetValueOrDefault(cluster[qi], []))
                        if (seen.Add(c) && Fits(c)) cluster.Add(c);
                if (cluster.Sum(r => _regions[r].Tris.Count) < 2 || hollow == null) continue;
                var cyl = _regions[cluster[0]];
                foreach (int r in cluster.Skip(1))
                {
                    foreach (int t in _regions[r].Tris) { _regionOf[t] = cluster[0]; cyl.Tris.Add(t); }
                    _regions[r].Tris.Clear();
                }
                cyl.IsCylinder = true; cyl.Axis = tr.Axis; cyl.Origin = tr.Origin; cyl.Radius = rc; cyl.Hollow = hollow.Value;
                CylindersFound++;
                Dbg(FormattableString.Invariant($"tangent cylinder r={rc * 1000:F4} mm from torus R={tr.Radius * 1000:F4}/r={tr.Minor * 1000:F4}: {cluster.Count} regions, {cyl.Tris.Count} tris"));
            }
        }
        DropEmptyRegions();
    }

    /// <summary>
    /// Single facets marked for absorption join a neighbouring curved surface they lie on. Done once more after all
    /// surfaces exist: during growth the facet's neighbour may still have been something else (a false cylinder on a
    /// sphere that MergeOnSpheres merged into it afterwards).
    /// </summary>
    private void AbsorbFacets()
    {
        // candidates: single facets marked for absorption, and small planar regions (a strip of a cylinder whose vertex
        // lies a few micrometres off is a "plane" of 2 facets, yubikey R5) that lie on a curved neighbour
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int ri = 0; ri < _regions.Count; ri++)
            {
                var reg = _regions[ri];
                if (reg.Curved || reg.Fixed || reg.Tris.Count == 0 || reg.Tris.Count > 8 || reg.Tris.Any(_hintExclude.Contains) || reg.Tris.Any(_keep.Contains)) continue;
                bool marked = reg.Tris.Count == 1 && _absorb.Contains(reg.Tris[0]);
                // next to a user's group, strips that lie on its surface belong to it (the selection missed them)
                bool nextToGroup = reg.Tris.Any(t => Enumerable.Range(0, 3).Any(k => _regions[_regionOf[Across(t, k)]] is { Fixed: true, Soft: false, Curved: true }));
                if (!marked && !nextToGroup && (reg.Tris.Count > 4 || reg.Tris.Any(_forced.Contains) || reg.Tris.Any(Peeled))) continue;
                var verts = VerticesOf(reg).ToList();
                foreach (int oi in reg.Tris.SelectMany(t => Enumerable.Range(0, 3).Select(k => _regionOf[Across(t, k)])).Distinct().ToList())
                {
                    var o = _regions[oi];
                    if (oi == ri || !o.Curved) continue;
                    var ex = o.IsTorus ? _noTorus : o.IsSphere ? _noSphere : o.IsCone ? _noCone : _noCylinder;
                    if (!marked && !o.Fixed && reg.Tris.Any(ex.Contains)) continue; // a later, specific absorb decision wins
                    double tol = Math.Max(AbsorbTol(o.IsTorus ? o.Minor : o.Radius), _opt.CylinderFitTolerance) * (o.IsTorus && marked ? SeamFactor : 1);
                    if (o.Fixed) tol = Math.Max(tol, Math.Min(_opt.MaxHintDeviation, 2e-5));
                    if (!verts.All(v => SurfaceDistance(o, _v[v]) <= tol)) continue;
                    // the facets must face the same way as the surface
                    if (!marked && reg.Tris.Any(t => TriNormal(t).Dot(Grad(o, _v[_t[t][0]])) * (o.Hollow ? -1 : 1) < Math.Cos(_opt.FacetNormalToleranceDeg * Math.PI / 180))) continue;
                    foreach (int t in reg.Tris) { o.Tris.Add(t); _regionOf[t] = oi; }
                    reg.Tris.Clear();
                    changed = true;
                    break;
                }
            }
        }
        DropEmptyRegions();
    }

    /// <summary>
    /// A user's curved group takes in a neighbouring recognised (or kept) surface of the same kind that lies on its
    /// surface within the group tolerance: two faces on (nearly) the same surface cannot share an edge (their intersection
    /// is undefined, "arc bulges away from its mesh edge"), nor a vertex (it would have to move far to lie on both), and
    /// the group, being fixed, would win and dissolve the other into strips (bigbox: a tube of r 13 mm recognised as
    /// several pieces; a group over part of it made a 604-facet piece that touched it in one corner fall apart). The
    /// surface becomes a piece of the group with the group's exact surface; touching pieces are then merged
    /// (<see cref="MergeGroupPieces"/>). Its vertices may move as far as the group's (see the vertex placement).
    /// </summary>
    private void MergeIntoGroups()
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            var regionsAt = new Dictionary<int, HashSet<int>>();
            for (int ri = 0; ri < _regions.Count; ri++)
                foreach (int t in _regions[ri].Tris)
                    foreach (int v in _t[t])
                    {
                        if (!regionsAt.TryGetValue(v, out var set)) regionsAt[v] = set = new();
                        set.Add(ri);
                    }
            for (int gi = 0; gi < _regions.Count; gi++)
            {
                var g = _regions[gi];
                if (!g.Fixed || g.Soft || !g.Curved || g.Tris.Count == 0 || !_hintOf.TryGetValue(g, out int h)) continue;
                foreach (int oi in VerticesOf(g).SelectMany(v => regionsAt[v]).Distinct().ToList())
                {
                    var o = _regions[oi];
                    if (oi == gi || o.Tris.Count == 0 || (o.Fixed && !o.Soft)) continue;
                    if (o.IsCylinder != g.IsCylinder || o.IsSphere != g.IsSphere || o.IsTorus != g.IsTorus || o.IsCone != g.IsCone || o.Hollow != g.Hollow) continue;
                    double dev = VerticesOf(o).Max(v => SurfaceDistance(g, _v[v]));
                    // the other way round: a small group (a few strips: its fit is only good near them) that lies on a
                    // larger recognised surface takes that surface over (bigbox: 29 facets of the tube against a piece
                    // of 604; that piece is off the group's own fit by more than 0.1 mm at its far end)
                    var pieces = _regions.Where(r => r.Fixed && !r.Soft && r.Tris.Count > 0 && _hintOf.TryGetValue(r, out int hp) && hp == h).ToList();
                    bool adopt = false;
                    if (dev > _opt.MaxHintDeviation && o.Tris.Count >= pieces.Sum(r => r.Tris.Count))
                    {
                        double back = pieces.SelectMany(VerticesOf).Distinct().Max(v => SurfaceDistance(o, _v[v]));
                        if (back <= _opt.MaxHintDeviation) { adopt = true; dev = back; }
                    }
                    if (dev > _opt.MaxHintDeviation) continue;
                    if (adopt)
                        foreach (var piece in pieces) { piece.Origin = o.Origin; piece.Axis = o.Axis; piece.Radius = o.Radius; piece.Minor = o.Minor; }
                    Dbg(FormattableString.Invariant($"group {h + 1} takes in a {o.Tris.Count}-facet surface {(adopt ? "and its surface" : "on it")} ({dev * 1000:0.####} mm off)"));
                    // facets left out of the group (where it touched itself in a vertex) stay out: single facets
                    foreach (int t in o.Tris.Where(_hintExclude.Contains).ToList())
                    {
                        o.Tris.Remove(t);
                        var nr = new Region { N = TriNormal(t) };
                        nr.D = nr.N.Dot(_v[_t[t][0]]);
                        nr.Centroid = (_v[_t[t][0]] + _v[_t[t][1]] + _v[_t[t][2]]) * (1.0 / 3);
                        nr.Tris.Add(t);
                        _regionOf[t] = _regions.Count; _regions.Add(nr);
                    }
                    if (o.Tris.Count == 0) continue;
                    o.Fixed = true; o.Soft = false;
                    o.Origin = g.Origin; o.Axis = g.Axis; o.Radius = g.Radius; o.Minor = g.Minor;
                    _hintOf[o] = h;
                    double fd = adopt ? dev : Math.Max(g.FitDeviation, dev);
                    foreach (var piece in pieces.Append(o)) piece.FitDeviation = fd;
                    changed = true;
                }
                if (changed) break;
            }
        }
    }

    /// <summary>Pieces of one user group that touch (after absorbing the strips between them) become one face.</summary>
    private void MergeGroupPieces()
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var ((a, b), _) in SharedEdges())
            {
                var ra = _regions[a]; var rb = _regions[b];
                if (!ra.Fixed || !rb.Fixed || ra.Tris.Count == 0 || rb.Tris.Count == 0) continue;
                if (!_hintOf.TryGetValue(ra, out int ha) || !_hintOf.TryGetValue(rb, out int hb) || ha != hb) continue;
                foreach (int t in rb.Tris) { _regionOf[t] = a; ra.Tris.Add(t); }
                rb.Tris.Clear();
                changed = true;
                break;
            }
        }
        DropEmptyRegions();
    }

    private static Dictionary<int, List<int>> Neighbours(Dictionary<(int, int), List<(int, int)>> shared)
    {
        var nbrs = new Dictionary<int, List<int>>();
        foreach (var (a, b) in shared.Keys)
        {
            if (!nbrs.TryGetValue(a, out var la)) nbrs[a] = la = new(); la.Add(b);
            if (!nbrs.TryGetValue(b, out var lb)) nbrs[b] = lb = new(); lb.Add(a);
        }
        return nbrs;
    }

    /// <summary>
    /// A fillet torus shares its axis with the cylinders, spheres and perpendicular planes around it and touches them
    /// tangentially. The fit is close but not exact, and a tangent intersection is ill-conditioned: (nearly) coaxial
    /// neighbours give the exact axis, and (nearly) tangent ones the exact profile circle position, as long as the torus
    /// still fits its vertices.
    /// </summary>
    private void RegulariseTori(Dictionary<(int, int), List<(int, int)>> shared)
    {
        var nbrs = Neighbours(shared);
        double angTol = Math.Sin(_opt.CylinderAngleToleranceDeg * Math.PI / 180);
        foreach (var (ti, tor) in _regions.Select((r, i) => (i, r)).Where(x => x.r.IsTorus).ToList())
        {
            if (!nbrs.TryGetValue(ti, out var around)) continue;
            var t = tor.Tor;
            // the neighbours' axis must fit the torus nearly as well as its own fit (a loose tolerance lets a wrong
            // neighbour shift the torus noticeably: boltest r 17.85 -> 17.874 mm within 1e-5 m)
            double tol = Math.Max(Math.Min(JoinTol(t.r), 10 * _opt.CylinderFitTolerance), _opt.CylinderFitTolerance);
            double posTol = 0.02 * t.r;
            string source = "own fit";
            // fit only on the core points: loosely joined facets (up to JoinTol off) near a tangent sphere pull a least
            // squares fit along the tangent direction (boltest: r 17.85 -> 17.874 mm with all points)
            var allPts = VerticesOf(tor).Select(v => _v[v]).ToList();
            var pts = allPts.Where(p => Math.Abs(t.Distance(p, out _)) <= _opt.CylinderFitTolerance).ToList();
            if (pts.Count < 8) pts = allPts;
            var nb = around.Select(i => _regions[i]).Where(r => !r.Free).ToList();
            Vec3 Align(Vec3 d) => d.Dot(t.Axis) >= 0 ? d : d * -1;
            double OffAxis(Vec3 p, Vec3 o, Vec3 a) { var w = p - o; w -= a * w.Dot(a); return w.Length; }

            // 1. exact axis
            Vec3 a = t.Axis, o = t.Centre;
            var cyl = nb.Where(r => r.IsCylinder && r.Axis.Cross(t.Axis).Length < angTol && OffAxis(t.Centre, r.Origin, r.Axis) < posTol).MaxBy(Area);
            if (cyl != null)
            {
                a = Align(cyl.Axis);
                o = cyl.Origin + a * (t.Centre - cyl.Origin).Dot(a);
                source = Describe(cyl, t.Centre);
            }
            else if (nb.Where(r => r.IsSphere && OffAxis(r.Origin, t.Centre, t.Axis) < posTol).OrderByDescending(Area).ToList() is { Count: >= 2 } sphs
                && (sphs[1].Origin - sphs[0].Origin).Length > posTol)
            {
                // a fillet between two spheres: its axis runs through both centres (boltest)
                a = Align((sphs[1].Origin - sphs[0].Origin).Normalized());
                o = sphs[0].Origin + a * (t.Centre - sphs[0].Origin).Dot(a);
                source = "centres of " + Describe(sphs[0], t.Centre) + " and " + Describe(sphs[1], t.Centre);
            }
            else
            {
                var cap = nb.Where(r => !r.Curved && r.N.Cross(t.Axis).Length < angTol).MaxBy(Area);
                if (cap != null) { a = Align(cap.N); source = Describe(cap, t.Centre); }
                var sph = nb.Where(r => r.IsSphere && OffAxis(r.Origin, t.Centre, a) < posTol).MaxBy(Area);
                if (sph != null) { o = sph.Origin + a * (t.Centre - sph.Origin).Dot(a); source += "; centre from " + Describe(sph, t.Centre); }
            }
            if (!TorusFit.FitWithAxis(pts, o, a, out var t1, out double res1) || res1 > tol)
            {
                Dbg(FormattableString.Invariant($"torus R={t.R * 1000:F4}: no fit on the neighbours' axis ({res1:E1} m) from {source}"));
                continue;
            }
            Dbg(FormattableString.Invariant($"torus R={t.R * 1000:F4} r={t.r * 1000:F4} -> R={t1.R * 1000:F4} r={t1.r * 1000:F4} ({res1:E1} m) on axis from {source}"));
            t = t1;
            // spheres next to it: centre exactly on the axis, if the sphere still fits
            foreach (var sp in nb.Where(r => r.IsSphere))
            {
                double off = OffAxis(sp.Origin, t.Centre, t.Axis);
                if (off == 0 || off > posTol) continue;
                var c = t.Centre + t.Axis * (sp.Origin - t.Centre).Dot(t.Axis);
                if (!sp.Soft && VerticesOf(sp).All(v => Math.Abs((_v[v] - c).Length - sp.Radius) <= JoinTol(sp.Radius))) sp.Origin = c;
            }

            // other (nearly) coaxial cylinders: exactly on the axis, if they still fit (a groove between two pieces of one
            // rod: their two fits differ slightly)
            foreach (var cy in nb.Where(r => r.IsCylinder && r.Axis.Cross(t.Axis).Length < angTol && OffAxis(t.Centre, r.Origin, r.Axis) < posTol))
            {
                var ca = cy.Axis.Dot(t.Axis) >= 0 ? t.Axis : t.Axis * -1;
                var co = t.Centre + t.Axis * (cy.Origin - t.Centre).Dot(t.Axis);
                double ctol = Math.Max(JoinTol(cy.Radius), _opt.CylinderFitTolerance);
                bool fits = VerticesOf(cy).All(v => { var w = _v[v] - co; w -= ca * w.Dot(ca); double dc = Math.Abs(w.Length - cy.Radius); return dc <= ctol || (dc <= SeamFactor * ctol && Math.Abs(t.Distance(_v[v], out _)) <= SeamFactor * ctol); });
                if (fits && !cy.Soft) { cy.Axis = ca; cy.Origin = co; }
            }

            // planes perpendicular to the axis: exactly perpendicular (as cylinder caps), if they still fit
            foreach (var pl in nb.Where(r => !r.Curved && r.N.Cross(t.Axis).Length < angTol && r.N.Cross(t.Axis).Length > 0))
            {
                var n = pl.N.Dot(t.Axis) > 0 ? t.Axis : t.Axis * -1;
                double d = VerticesOf(pl).Average(v => n.Dot(_v[v]));
                if (VerticesOf(pl).All(v => Math.Abs(n.Dot(_v[v]) - d) <= Math.Max(_opt.DistanceTolerance, _opt.CylinderFitTolerance)) && !pl.Soft) { pl.N = n; pl.D = d; }
            }

            // 2. tangency, in the (rho, h) half-plane around the axis, h measured from the torus centre; r stays fixed
            var cons = new List<(int kind, double x, double y, double s, double area)>(); // 0: rho = x, 1: h = x, 2: circle (x, y) radius s
            foreach (var r in nb)
            {
                if (r.IsCylinder && r.Axis.Cross(t.Axis).Length < 1e-12 && OffAxis(r.Origin, t.Centre, t.Axis) < 1e-12)
                    cons.Add((0, r.Radius, 0, 0, Area(r)));
                else if (!r.Curved && r.N.Cross(t.Axis).Length < 1e-12)
                    cons.Add((1, (r.D - r.N.Dot(t.Centre)) / r.N.Dot(t.Axis), 0, 0, Area(r)));
                else if (r.IsSphere && OffAxis(r.Origin, t.Centre, t.Axis) < 1e-12)
                    cons.Add((2, 0, (r.Origin - t.Centre).Dot(t.Axis), r.Radius, Area(r)));
            }
            double R = t.R, h0 = 0, rr = t.r;
            double Gap((int kind, double x, double y, double s, double area) c, double pr, double ph) => c.kind switch
            {
                0 => Math.Abs(Math.Abs(pr - c.x) - rr),
                1 => Math.Abs(Math.Abs(ph - c.x) - rr),
                _ => Math.Min(Math.Abs(Dist2(pr, ph, c.x, c.y) - (c.s + rr)), Math.Abs(Dist2(pr, ph, c.x, c.y) - Math.Abs(c.s - rr))),
            };
            var tangent = cons.Where(c => Gap(c, R, h0) < 0.01 * rr).OrderByDescending(c => c.area)
                .DistinctBy(c => (c.kind, c.x, c.y, c.s)).Take(2).ToList();
            if (tangent.Count == 0) { Apply(t); SnapMeridianCylinders(t, nb, angTol); continue; }
            // each tangency is a locus for the profile centre: a line (rho or h fixed) or a circle around the other centre
            var loci = tangent.Select(c => c.kind switch
            {
                0 => (kind: 0, x: c.x + Math.Sign(R - c.x) * rr, y: 0.0, s: 0.0),
                1 => (kind: 1, x: c.x + Math.Sign(h0 - c.x) * rr, y: 0.0, s: 0.0),
                _ => (kind: 2, x: c.x, y: c.y, s: Math.Abs(Dist2(R, h0, c.x, c.y) - (c.s + rr)) < Math.Abs(Dist2(R, h0, c.x, c.y) - Math.Abs(c.s - rr)) ? c.s + rr : Math.Abs(c.s - rr)),
            }).ToList();
            (double r, double h)? best = null;
            var cands = new List<(double r, double h)>();
            if (loci.Count == 1) cands.Add(Nearest(loci[0], R, h0));
            else cands.AddRange(Meet(loci[0], loci[1]));
            foreach (var c in cands)
                if (best == null || Dist2(c.r, c.h, R, h0) < Dist2(best.Value.r, best.Value.h, R, h0)) best = c;
            if (best is { } b && b.r > rr)
            {
                var t2 = new Torus(t.Centre + t.Axis * b.h, t.Axis, b.r, rr);
                if (TorusFit.MaxResidual(pts, t2) <= tol) t = t2;
                else Dbg(FormattableString.Invariant($"torus R={t.R * 1000:F4}: tangent position does not fit ({TorusFit.MaxResidual(pts, t2):E1} m)"));
            }
            Dbg(FormattableString.Invariant($"torus tangent to {tangent.Count} neighbours: R={t.R * 1000:F6} r={t.r * 1000:F6}"));
            Apply(t);
            SnapMeridianCylinders(t, nb, angTol);

            void Apply(Torus x) { if (tor.Soft) return; tor.Origin = x.Centre; tor.Axis = x.Axis; tor.Radius = x.R; tor.Minor = x.r; }
        }

        static double Dist2(double r1, double h1, double r2, double h2) => Math.Sqrt((r1 - r2) * (r1 - r2) + (h1 - h2) * (h1 - h2));
        static (double r, double h) Nearest((int kind, double x, double y, double s) l, double r, double h)
        {
            if (l.kind == 0) return (l.x, h);
            if (l.kind == 1) return (r, l.x);
            double d = Dist2(r, h, l.x, l.y);
            return (l.x + (r - l.x) * l.s / d, l.y + (h - l.y) * l.s / d);
        }
        static IEnumerable<(double r, double h)> Meet((int kind, double x, double y, double s) p, (int kind, double x, double y, double s) q)
        {
            if (p.kind == 2 && q.kind != 2) (p, q) = (q, p);
            if (p.kind != 2 && q.kind != 2)
            {
                if (p.kind == q.kind) yield break;
                yield return p.kind == 0 ? (p.x, q.x) : (q.x, p.x);
                yield break;
            }
            if (p.kind != 2)
            {
                // line with circle q
                double off = p.kind == 0 ? p.x - q.x : p.x - q.y;
                double w2 = q.s * q.s - off * off;
                if (w2 < 0) w2 = 0;
                double w = Math.Sqrt(w2);
                foreach (double sgn in new[] { 1.0, -1.0 })
                    yield return p.kind == 0 ? (p.x, q.y + sgn * w) : (q.x + sgn * w, p.x);
                yield break;
            }
            foreach (var x in CircleCircle(p.x, p.y, p.s, q.x, q.y, q.s, 1e-9)) yield return x;
        }
    }

    /// <summary>
    /// A straight fillet meeting a corner torus (rounded box corner, yubikey R4/r1 with r1 edge fillets) touches it along a
    /// meridian circle: the cylinder's axis is perpendicular to the torus axis, passes through the tube's centre circle,
    /// and its radius equals the tube radius. Made exact, if the cylinder still fits its vertices.
    /// </summary>
    private void SnapMeridianCylinders(Torus t, List<Region> nb, double angTol)
    {
        foreach (var cy in nb.Where(r => r.IsCylinder && Math.Abs(r.Axis.Dot(t.Axis)) < angTol && Math.Abs(r.Radius - t.r) < 0.02 * t.r))
        {
            var d = (cy.Axis - t.Axis * cy.Axis.Dot(t.Axis)).Normalized();
            // tube centre point on the cylinder's side: the meridian plane has normal d
            var u = t.Axis.Cross(d).Normalized();
            var w = cy.Origin - t.Centre;
            if (w.Dot(u) < 0) u = u * -1;
            var P = t.Centre + u * t.R;
            // the cylinder axis line must pass close to P
            var q = P - cy.Origin; q -= cy.Axis * q.Dot(cy.Axis);
            if (q.Length > 0.02 * t.r) { Dbg(FormattableString.Invariant($"  meridian cylinder r={cy.Radius * 1000:F4}: axis {q.Length * 1000:F4} mm off the tube centre")); continue; }
            double tol = Math.Max(JoinTol(cy.Radius), _opt.CylinderFitTolerance);
            bool fits = VerticesOf(cy).All(v => { var x = _v[v] - P; x -= d * x.Dot(d); return Math.Abs(x.Length - t.r) <= tol; });
            Dbg(FormattableString.Invariant($"  meridian cylinder r={cy.Radius * 1000:F4} off {q.Length * 1000:F4} mm: {(fits ? "snapped" : "does not fit")}"));
            if (fits && !cy.Soft) { cy.Axis = cy.Axis.Dot(d) >= 0 ? d : d * -1; cy.Origin = P; cy.Radius = t.r; }
        }
    }

    /// <summary>A cylinder lying exactly on a torus' tube along a meridian (after <see cref="SnapMeridianCylinders"/>).</summary>
    private static bool MeridianCylinder(Region tor, Region cy)
    {
        if (!tor.IsTorus || !cy.IsCylinder || Math.Abs(cy.Axis.Dot(tor.Axis)) > 1e-12 || Math.Abs(cy.Radius - tor.Minor) > 1e-12) return false;
        var w = cy.Origin - tor.Origin;
        double h = w.Dot(tor.Axis);
        return Math.Abs(h) < 1e-12 && Math.Abs((w - tor.Axis * h).Length - tor.Radius) < 1e-9;
    }

    /// <summary>
    /// Moves along a curve P(t) to satisfy further surfaces: Gauss-Newton on the sum of squared distances, with step
    /// halving. Where a surface touches the curve (a double root, or a gap of a few nanometres), plain Newton creeps or
    /// overshoots; this ends at the closest point instead.
    /// </summary>
    private static double Slide(Func<double, Vec3> P, Func<double, Vec3> dP, double t, List<Region> rest)
    {
        if (rest.Count == 0) return t;
        double Cost(double x) { double c = 0; foreach (var r in rest) { double f = Signed(r, P(x), out _); c += f * f; } return c; }
        double cost = Cost(t);
        for (int it = 0; it < 200 && cost > 0; it++)
        {
            var dp = dP(t);
            double num = 0, den = 0;
            foreach (var r in rest) { double f = Signed(r, P(t), out var g); double j = g.Dot(dp); num += j * f; den += j * j; }
            if (den < 1e-300) break;
            double step = num / den, tn = t - step, cn = Cost(tn);
            for (int h = 0; h < 30 && cn > cost; h++) { step *= 0.5; tn = t - step; cn = Cost(tn); }
            if (cn > cost) break;
            t = tn; cost = cn;
            if (Math.Abs(step) < 1e-15) break;
        }
        return t;
    }

    /// <summary>Puts x0 on a circle and slides it along the circle to satisfy further surfaces (1D Newton).</summary>
    private static (Vec3 p, double residual) PlaceOnCircle(Vec3 x0, Vec3 c, Vec3 n, double radius, List<Region> rest, List<Region> all)
    {
        var e1 = Perp(n); var e2 = n.Cross(e1);
        var d0 = x0 - c;
        double th = Math.Atan2(d0.Dot(e2), d0.Dot(e1));
        Vec3 P(double t) => c + e1 * (radius * Math.Cos(t)) + e2 * (radius * Math.Sin(t));
        th = Slide(P, t => e1 * (-radius * Math.Sin(t)) + e2 * (radius * Math.Cos(t)), th, rest);
        var p = P(th);
        return (p, all.Max(r => Math.Abs(Signed(r, p, out _))));
    }

    /// <summary>Intersections of two circles in a plane; nearly tangent circles (gap below tangentTol) give their touching point.</summary>
    private static List<(double x, double y)> CircleCircle(double x1, double y1, double r1, double x2, double y2, double r2, double tangentTol)
    {
        var res = new List<(double, double)>();
        double dx = x2 - x1, dy = y2 - y1, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-15) return res;
        double a = (d * d + r1 * r1 - r2 * r2) / (2 * d);
        double h2 = r1 * r1 - a * a;
        if (h2 < 0)
        {
            if (Math.Abs(d - (r1 + r2)) > tangentTol && Math.Abs(d - Math.Abs(r1 - r2)) > tangentTol) return res;
            h2 = 0;
        }
        double h = Math.Sqrt(h2), ux = dx / d, uy = dy / d;
        // (made) tangent circles: two intersections a few nm apart are one touching point (else a vertex and the edge
        // through it can pick different ones)
        if (h < 1e-6 && Math.Min(Math.Abs(d - (r1 + r2)), Math.Abs(d - Math.Abs(r1 - r2))) < 1e-9) h = 0;
        res.Add((x1 + ux * a - uy * h, y1 + uy * a + ux * h));
        if (h > 0) res.Add((x1 + ux * a + uy * h, y1 + uy * a - ux * h));
        return res;
    }

    /// <summary>
    /// Boundary of a torus: with a coaxial neighbour (perpendicular plane, cylinder, sphere centred on the axis, torus) a
    /// circle around the axis, found by intersecting the profiles in the (rho, h) half-plane; otherwise a meridian circle
    /// when the chain lies in a plane through the axis. Other intersections are not circles and not supported.
    /// </summary>
    /// <summary>Intersections of the torus' profile circle with a coaxial neighbour's profile in the (rho, h) half-plane
    /// (h from the torus centre); null when the neighbour is not coaxial.</summary>
    private static List<(double rho, double h)>? ProfileHits(Region tor, Region other)
    {
        var T = tor.Tor; var a = T.Axis;
        const double eps = 1e-9;
        double OffAxis(Vec3 p) { var w = p - T.Centre; w -= a * w.Dot(a); return w.Length; }
        var hits = new List<(double rho, double h)>();
        if (other.IsTorus && other.Axis.Cross(a).Length < eps && OffAxis(other.Origin) < eps)
            hits = CircleCircle(T.R, 0, T.r, other.Radius, (other.Origin - T.Centre).Dot(a), other.Minor, eps);
        else if (other.IsSphere && OffAxis(other.Origin) < eps)
            hits = CircleCircle(T.R, 0, T.r, 0, (other.Origin - T.Centre).Dot(a), other.Radius, eps);
        else if (other.IsCylinder && other.Axis.Cross(a).Length < eps && OffAxis(other.Origin) < eps)
        {
            double off = other.Radius - T.R, q = T.r * T.r - off * off;
            if (q >= 0 || Math.Abs(off) - T.r < eps) foreach (double sg in new[] { 1.0, -1.0 }) hits.Add((other.Radius, sg * Math.Sqrt(Math.Max(0, q))));
        }
        else if (other.IsCone && other.Axis.Cross(a).Length < eps && OffAxis(other.Origin) < eps)
        {
            // cone profile rho = r0 + k s (h - hc) (s = +-1: the cone's axis along or against the torus axis) meets the
            // tube circle (rho - R)^2 + h^2 = r^2
            double sg = Math.Sign(other.Axis.Dot(a)), hc = (other.Origin - T.Centre).Dot(a), k = other.Minor * sg;
            double b0 = other.Radius - k * hc - T.R; // rho - R = b0 + k h
            double qa = 1 + k * k, qb = 2 * b0 * k, qc = b0 * b0 - T.r * T.r, disc = qb * qb - 4 * qa * qc;
            if (disc >= 0 || disc > -eps)
                foreach (double sgn in new[] { 1.0, -1.0 })
                {
                    double h = (-qb + sgn * Math.Sqrt(Math.Max(0, disc))) / (2 * qa);
                    hits.Add((T.R + b0 + k * h, h));
                }
        }
        else if (!other.Curved && other.N.Cross(a).Length < eps)
        {
            double hp = (other.D - other.N.Dot(T.Centre)) / other.N.Dot(a), q = T.r * T.r - hp * hp;
            if (q >= 0 || Math.Abs(hp) - T.r < eps) foreach (double sg in new[] { 1.0, -1.0 }) hits.Add((T.R + sg * Math.Sqrt(Math.Max(0, q)), hp));
        }
        else return null;
        return hits;
    }

    /// <summary>A plane parallel to the cylinder's axis that touches it (exactly, after regularisation).</summary>
    private static bool TangentPlane(Region cyl, Region pl) =>
        cyl.IsCylinder && !pl.Curved && Math.Abs(pl.N.Dot(cyl.Axis)) < 1e-12
        && Math.Abs(Math.Abs(pl.N.Dot(cyl.Origin) - pl.D) - cyl.Radius) < 1e-9;

    /// <summary>
    /// A vertex on a cylinder and a tangent plane lies on their tangent line; Gauss-Newton stalls there (parallel
    /// gradients), so it is put on the line directly and only slides along it for any further surfaces.
    /// </summary>
    private static (Vec3 p, double residual)? PlaceOnTangentLine(Vec3 x0, List<Region> constraints)
    {
        foreach (var cyl in constraints.Where(r => r.IsCylinder))
            foreach (var pl in constraints.Where(r => TangentPlane(cyl, r)))
            {
                // the line: the axis moved by r towards the plane
                double side = Math.Sign(pl.N.Dot(cyl.Origin) - pl.D);
                var o = cyl.Origin - pl.N * (side * cyl.Radius);
                var a = cyl.Axis;
                double s = (x0 - o).Dot(a);
                var rest = constraints.Where(r => r != cyl && r != pl).ToList();
                s = Slide(t => o + a * t, _ => a, s, rest);
                var p = o + a * s;
                return (p, constraints.Max(r => Math.Abs(Signed(r, p, out _))));
            }
        return null;
    }

    /// <summary>
    /// A vertex on a torus and a coaxial neighbour lies on their common circle. When they touch tangentially, Gauss-Newton
    /// stalls (the two gradients are parallel), so the vertex is put on that circle directly and only moves along it for
    /// any further surfaces. Null when no torus with a coaxial neighbour is among the constraints.
    /// </summary>
    private static (Vec3 p, double residual)? PlaceOnTorusCircle(Vec3 x0, List<Region> constraints)
    {
        foreach (var tor in constraints.Where(r => r.IsTorus))
            foreach (var other in constraints.Where(r => r != tor))
            {
                var hits = ProfileHits(tor, other);
                if (hits == null || hits.Count == 0) continue;
                var T = tor.Tor;
                var (rho0, h0) = T.Profile(x0);
                var best = hits.MinBy(x => (x.rho - rho0) * (x.rho - rho0) + (x.h - h0) * (x.h - h0));
                if (best.rho <= 1e-12) continue;
                var c = T.Centre + T.Axis * best.h;
                var e1 = Perp(T.Axis); var e2 = T.Axis.Cross(e1);
                var d0 = x0 - c;
                double th = Math.Atan2(d0.Dot(e2), d0.Dot(e1));
                Vec3 P(double t) => c + e1 * (best.rho * Math.Cos(t)) + e2 * (best.rho * Math.Sin(t));
                var rest = constraints.Where(r => r != tor && r != other).ToList();
                th = Slide(P, t => e1 * (-best.rho * Math.Sin(t)) + e2 * (best.rho * Math.Cos(t)), th, rest);
                var p = P(th);
                return (p, constraints.Max(r => Math.Abs(Signed(r, p, out _))));
            }
        // meridian: a cylinder lying on the tube
        foreach (var tor in constraints.Where(r => r.IsTorus))
            foreach (var cy in constraints.Where(r => MeridianCylinder(tor, r)))
                return PlaceOnCircle(x0, cy.Origin, cy.Axis, tor.Minor, constraints.Where(r => r != tor && r != cy).ToList(), constraints);
        return null;
    }

    private BrepCurve TorusCurve(Region tor, Region other, List<int> pts)
    {
        var T = tor.Tor; var a = T.Axis;
        const double eps = 1e-9;
        double rm = 0, hm = 0;
        foreach (int i in pts) { var (rho, h) = T.Profile(_v[i]); rm += rho; hm += h; }
        rm /= pts.Count; hm /= pts.Count;
        var hits = ProfileHits(tor, other);
        bool coaxial = hits != null;

        Vec3 c, n; double radius;
        if (hits != null)
        {
            if (hits.Count == 0) throw new MeshException("torus does not meet its coaxial neighbour");
            var best = hits.MinBy(x => (x.rho - rm) * (x.rho - rm) + (x.h - hm) * (x.h - hm));
            if (best.rho <= 1e-12) throw new MeshException("torus meets its neighbour in a point on the axis");
            c = T.Centre + a * best.h; n = a; radius = best.rho;
        }
        else
        {
            // meridian: the chain lies in a plane through the axis
            var mean = new Vec3(0, 0, 0);
            foreach (int i in pts) mean += _v[i];
            mean = mean * (1.0 / pts.Count);
            var u = mean - T.Centre; u -= a * u.Dot(a);
            if (u.Length < 1e-12) throw new MeshException("torus boundary chain on the axis");
            u = u.Normalized();
            var m = a.Cross(u);
            double tol = Math.Max(JoinTol(T.r), _opt.CylinderFitTolerance);
            if (pts.Count < 2 || pts.Any(i => Math.Abs(m.Dot(_v[i] - T.Centre)) > tol))
                throw new MeshException("torus meets a surface along a curve that is not a circle (not supported)");
            c = T.Centre + u * T.R; n = m; radius = T.r;
        }
        double turn = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var p = _v[pts[i]] - c; var q = _v[pts[i + 1]] - c;
            turn += Math.Atan2(p.Cross(q).Dot(n), p.Dot(q));
        }
        var normal = turn >= 0 ? n : n * -1;
        return new BrepCircle(c, normal, Perp(normal), radius);
    }

    /// <summary>Boundary of a sphere: a circle with a plane, or with a cylinder on the same axis.</summary>
    private BrepCurve SphereCurve(Region sph, Region other, List<int> pts)
    {
        Vec3 c, n; double r;
        if (other.IsSphere)
        {
            // two spheres meet in a circle, in the plane perpendicular to the line between the centres
            var dv = other.Origin - sph.Origin;
            double d = dv.Length;
            if (d < 1e-12) throw new MeshException("two concentric spheres meet");
            var u = dv * (1 / d);
            double a = (d * d + sph.Radius * sph.Radius - other.Radius * other.Radius) / (2 * d);
            double r2 = sph.Radius * sph.Radius - a * a;
            if (r2 <= 0) throw new MeshException("spheres do not intersect");
            n = u; c = sph.Origin + u * a; r = Math.Sqrt(r2);
        }
        else if (other.IsCylinder)
        {
            var w = sph.Origin - other.Origin; w -= other.Axis * w.Dot(other.Axis);
            if (w.Length > 1e-9) throw new MeshException("sphere meets a cylinder that is not on its centre (not supported yet)");
            double h2 = sph.Radius * sph.Radius - other.Radius * other.Radius;
            if (h2 < -1e-15) throw new MeshException("cylinder wider than the sphere it meets");
            double h = Math.Sqrt(Math.Max(0, h2));
            double side = pts.Average(i => (_v[i] - sph.Origin).Dot(other.Axis));
            n = other.Axis;
            c = sph.Origin + n * (side >= 0 ? h : -h);
            r = other.Radius;
        }
        else
        {
            n = other.N;
            double d = n.Dot(sph.Origin) - other.D;
            if (Math.Abs(d) >= sph.Radius) throw new MeshException("plane does not cut the sphere");
            c = sph.Origin - n * d;
            r = Math.Sqrt(sph.Radius * sph.Radius - d * d);
        }
        double turn = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var p = _v[pts[i]] - c; var q = _v[pts[i + 1]] - c;
            turn += Math.Atan2(p.Cross(q).Dot(n), p.Dot(q));
        }
        var normal = turn >= 0 ? n : n * -1;
        return new BrepCircle(c, normal, Perp(normal), r);
    }

    private BrepCurve Conic(Region cyl, Region pl, List<int> pts)
    {
        var m = pl.N; var A = cyl.Axis;
        double cosT = m.Dot(A);
        if (Math.Abs(cosT) < 1e-3) throw new MeshException("curved boundary on a plane parallel to the cylinder axis");
        // centre: axis meets plane
        double t = (pl.D - m.Dot(cyl.Origin)) / cosT;
        var c = cyl.Origin + A * t;
        // orientation of the chain around the plane normal
        double turn = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var p = _v[pts[i]] - c; var q = _v[pts[i + 1]] - c;
            turn += Math.Atan2(p.Cross(q).Dot(m), p.Dot(q));
        }
        var normal = turn >= 0 ? m : m * -1;
        bool circle = Math.Abs(Math.Abs(cosT) - 1) < 1e-12;
        if (circle) return new BrepCircle(c, normal, Perp(normal), cyl.Radius);
        var major = (A - m * A.Dot(m)).Normalized();
        return new BrepEllipse(c, normal, major, cyl.Radius / Math.Abs(cosT), cyl.Radius);
    }

    /// <summary>Least-squares plane through points (normal = smallest eigenvector of the covariance).</summary>
    private static Region FitPlane(List<Vec3> pts, out double maxDist)
    {
        var c = new Vec3(0, 0, 0);
        foreach (var p in pts) c += p;
        c = c * (1.0 / pts.Count);
        var m = new double[3, 3];
        foreach (var p in pts)
        {
            var d = p - c; double[] v = [d.X, d.Y, d.Z];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) m[i, j] += v[i] * v[j];
        }
        var (lambda, u) = MeshConverter.Eigen3(m);
        int k = Array.IndexOf(lambda, lambda.Min());
        var n = new Vec3(u[0, k], u[1, k], u[2, k]).Normalized();
        var r = new Region { N = n, D = n.Dot(c), Centroid = c };
        maxDist = pts.Max(p => Math.Abs(n.Dot(p) - r.D));
        return r;
    }

    private static readonly bool DescribeExact = Environment.GetEnvironmentVariable("RSEXACT") == "1";
    private string Describe(Region r, Vec3 at) => Describe0(r, at) + (DescribeExact
        ? string.Create(CultureInfo.InvariantCulture, $" [o=({r.Origin.X:R},{r.Origin.Y:R},{r.Origin.Z:R}) a=({r.Axis.X:R},{r.Axis.Y:R},{r.Axis.Z:R}) R={r.Radius:R} r={r.Minor:R} N=({r.N.X:R},{r.N.Y:R},{r.N.Z:R}) D={r.D:R}]")
        : "");

    private string Describe0(Region r, Vec3 at)
    {
        if (r.IsTorus)
            return string.Create(CultureInfo.InvariantCulture, $"torus R={r.Radius * 1000:0.####}mm r={r.Minor * 1000:0.####}mm {(r.Hollow ? "hollow" : "solid")} axis ({r.Axis.X:0.###},{r.Axis.Y:0.###},{r.Axis.Z:0.###}) {r.Tris.Count} tris dist {r.Tor.Distance(at, out _):E1}");
        if (r.IsCone)
            return string.Create(CultureInfo.InvariantCulture, $"cone r={r.Radius * 1000:0.####}mm half angle {Math.Atan(r.Minor) * 180 / Math.PI:0.###} deg {(r.Hollow ? "hollow" : "solid")} axis ({r.Axis.X:0.###},{r.Axis.Y:0.###},{r.Axis.Z:0.###}) {r.Tris.Count} tris dist {r.Con.Distance(at, out _):E1}");
        if (r.IsSphere)
            return string.Create(CultureInfo.InvariantCulture, $"sphere r={r.Radius * 1000:0.####}mm {(r.Hollow ? "hollow" : "solid")} {r.Tris.Count} tris dist {(at - r.Origin).Length - r.Radius:E1}");
        if (r.IsCylinder)
        {
            var w = at - r.Origin; w -= r.Axis * w.Dot(r.Axis);
            return string.Create(CultureInfo.InvariantCulture, $"cylinder r={r.Radius * 1000:0.####}mm {(r.Hollow ? "hollow" : "solid")} axis ({r.Axis.X:0.###},{r.Axis.Y:0.###},{r.Axis.Z:0.###}) {r.Tris.Count} tris dist {w.Length - r.Radius:E1}");
        }
        return string.Create(CultureInfo.InvariantCulture, $"plane n=({r.N.X:0.####},{r.N.Y:0.####},{r.N.Z:0.####}) {r.Tris.Count} tris dist {r.N.Dot(at) - r.D:E1}");
    }

    private static Vec3 Perp(Vec3 n)
    {
        var u = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        return (u - n * u.Dot(n)).Normalized();
    }

    /// <summary>Gauss-Newton projection onto planes and cylinders (minimum-norm steps).</summary>
    private static (Vec3 p, double residual) Project(Vec3 x0, List<Region> surfaces)
    {
        var x = x0;
        static double Res(Region s, Vec3 p, out Vec3 grad) => Signed(s, p, out grad);
        for (int it = 0; it < 30; it++)
        {
            var m = new double[3, 3]; var b = new double[3];
            double maxR = 0;
            foreach (var s in surfaces)
            {
                double r = Res(s, x, out var g);
                maxR = Math.Max(maxR, Math.Abs(r));
                double[] gv = [g.X, g.Y, g.Z];
                for (int i = 0; i < 3; i++) { b[i] -= gv[i] * r; for (int j = 0; j < 3; j++) m[i, j] += gv[i] * gv[j]; }
            }
            if (maxR < 1e-14) break;
            // (a point on a cylinder's axis or a sphere's centre has no gradient: no projection, the caller gives up)
            if (!double.IsFinite(maxR) || b.Any(v => !double.IsFinite(v)) || m.Cast<double>().Any(v => !double.IsFinite(v)))
                return (x0, double.PositiveInfinity);
            var (lambda, u) = MeshConverter.Eigen3(m);
            double lmax = lambda.Max();
            var d = new double[3];
            for (int k = 0; k < 3; k++)
            {
                if (lambda[k] <= 1e-9 * lmax) continue;
                double c = (u[0, k] * b[0] + u[1, k] * b[1] + u[2, k] * b[2]) / lambda[k];
                for (int i = 0; i < 3; i++) d[i] += c * u[i, k];
            }
            x = new Vec3(x.X + d[0], x.Y + d[1], x.Z + d[2]);
        }
        double res = surfaces.Max(s => Math.Abs(Res(s, x, out _)));
        return double.IsFinite(res) && double.IsFinite(x.X + x.Y + x.Z) ? (x, res) : (x0, double.PositiveInfinity);
    }
}

/// <summary>
/// What recognition learnt about a shell that stays true across rounds and variants (same triangles, same positions):
/// seed clusters that do not fit a torus.
/// </summary>
public sealed class RecognitionCache
{
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, int), byte> NoTorus = new();
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, int), byte> NoCone = new();
}
