using System.Globalization;
using RsDocx.Core.Xt;

namespace RsDocx.Core.Stl;

/// <summary>Conversion progress: <paramref name="Done"/> of <paramref name="Bodies"/> bodies converted; the largest body still
/// busy has <paramref name="Triangles"/> triangles and is in recognition round <paramref name="Round"/> (0: none busy).</summary>
public readonly record struct MeshProgress(int Done, int Bodies, int Triangles, int Round);

public sealed record MeshOptions
{
    /// <summary>Factor from STL units to metres (default 0.001: STL in mm).</summary>
    public double Scale { get; init; } = 0.001;
    /// <summary>Vertices closer than this (metres) are welded.</summary>
    public double WeldTolerance { get; init; } = 1e-7;
    public bool MergeCoplanar { get; init; }
    /// <summary>Max angle (degrees) between triangle normals that may be merged into one planar face.</summary>
    public double AngleToleranceDeg { get; init; } = 0.01;
    /// <summary>Max distance (metres) of a vertex from the face plane when merging.</summary>
    public double DistanceTolerance { get; init; } = 1e-7;
    /// <summary>Max distance (metres) a vertex may be moved to lie exactly on all its face planes.</summary>
    public double SnapTolerance { get; init; } = 1e-6;

    /// <summary>Recognise cylinders (strips of facets around a common axis) and write them as real cylinders.</summary>
    public bool RecognizeCylinders { get; init; }
    /// <summary>Recognise spheres and write them as real spheres.</summary>
    public bool RecognizeSpheres { get; init; }
    /// <summary>Recognise (ring) tori, e.g. fillets around a rod, and write them as real tori.</summary>
    public bool RecognizeTori { get; init; }
    /// <summary>Also recognise cones (drafted walls, countersinks, chamfers around an axis).</summary>
    public bool RecognizeCones { get; init; }
    /// <summary>Merge flat pieces into planes through the recognition (with which triangle ended in which face, for
    /// <see cref="MeshLabels"/>) even when no curved surface is searched (the app's "▭ Platte vlakken").</summary>
    public bool RecognizePlanes { get; init; }
    /// <summary>Where two surfaces meet along a curve that is not a line, circle or ellipse, trace the exact intersection
    /// as a B-spline edge (otherwise one of the surfaces is turned into facets).</summary>
    public bool TraceIntersections { get; init; } = true;
    /// <summary>Groups of triangles chosen by the user that must each become one face (see <see cref="ShapeHint"/>).</summary>
    public IReadOnlyList<ShapeHint> Hints { get; init; } = [];
    /// <summary>Only the <see cref="Hints"/> become faces; every other triangle stays a separate facet (no planes merged,
    /// nothing recognised): the user converts the model piece by piece.</summary>
    public bool OnlyHints { get; init; }
    /// <summary>When set: only these triangles (indices into the input) and the <see cref="Hints"/> may form new faces;
    /// every other triangle stays a separate facet. For converting again around a changed group while the rest of an
    /// earlier result (passed as soft hints) stays as it was.</summary>
    public IReadOnlyCollection<int>? FreeTriangles { get; init; }
    /// <summary>Triangles (indices into the input) that stay single facets: not merged into a plane and never taken into
    /// a surface (the user detached them from a face). A hint covering one of them wins.</summary>
    public IReadOnlyCollection<int>? KeepFacets { get; init; }
    /// <summary>A group whose surface lies further than this (metres) from its triangles is refused (it would distort the model).</summary>
    public double MaxHintDeviation { get; init; } = 1e-4;
    /// <summary>Separate planar regions whose vertices all lie within this distance (metres) of one plane get that exact
    /// plane (boltest has two such regions touching in one vertex, 1e-8..1e-7 m apart).</summary>
    public double CoplanarUnifyTolerance { get; init; } = 1e-6;
    /// <summary>Max angle (degrees) between a facet's normal and the true surface normal at its centroid (cylinder/sphere).</summary>
    public double FacetNormalToleranceDeg { get; init; } = 10;
    /// <summary>Cylinders and spheres with a larger radius (metres) are not accepted (nearly flat facets fit huge radii).</summary>
    public double MaxCurvedRadius { get; init; } = 1;
    /// <summary>A cylinder/sphere with more facets than this is not given up for one awkward edge; it is peeled there.</summary>
    public int PeelThreshold { get; init; } = 50;
    /// <summary>Minimum number of facet regions before a sphere is accepted.</summary>
    public int SphereMinRegions { get; init; } = 6;
    /// <summary>Angular tolerance (degrees) for "parallel to the axis" / "perpendicular to the axis".</summary>
    public double CylinderAngleToleranceDeg { get; init; } = 0.5;
    /// <summary>Largest angle (degrees) between neighbouring strips of one cylinder.</summary>
    public double CylinderMaxStepDeg { get; init; } = 45;
    /// <summary>Minimum angle (degrees) the strips of a cylinder must turn through together.</summary>
    public double CylinderMinSpanDeg { get; init; } = 15;
    /// <summary>Minimum number of strips before a cylinder is accepted.</summary>
    public int CylinderMinStrips { get; init; } = 3;
    /// <summary>Max distance (metres) of the strip vertices from the fitted cylinder. Tessellation vertices of a real
    /// cylinder lie on it to float precision (~1e-9 m in sleutelrek); false fits were 4e-6 m and more.</summary>
    public double CylinderFitTolerance { get; init; } = 1e-7;
    /// <summary>Once a cylinder is established, neighbouring facets whose vertices are within this distance (metres) of it
    /// are added too (and their vertices moved onto it). Spoelhouder has single vertices ~3e-6 m off an R12 arc.</summary>
    public double CylinderJoinTolerance { get; init; } = 1e-5;
    /// <summary>Single facets lying within this distance (metres) of a neighbouring curved surface are taken into it.</summary>
    public double AbsorbTolerance { get; init; } = 1e-5;
    /// <summary>Planes along a cylinder whose normal is within this angle (degrees) of the cylinder normal are made tangent.</summary>
    public double TangentToleranceDeg { get; init; } = 3;
    /// <summary>Max recognition rounds (each round turns fitted surfaces that cannot be made exact into facets).</summary>
    public int RecognitionRounds { get; init; } = 300;

    /// <summary>Stops the conversion with an <see cref="OperationCanceledException"/> once cancelled.</summary>
    public CancellationToken Cancel { get; init; }

    /// <summary>Reports how many bodies (shells) are done, and the largest one still busy with its recognition round.</summary>
    public IProgress<MeshProgress>? Progress { get; init; }

    /// <summary>Shells converted at the same time (null = one per processor core).</summary>
    public int? MaxParallelism { get; init; }
}

public sealed class MeshException(string message) : Exception(message);

public sealed class MeshStats
{
    /// <summary>The user's groups (index in <see cref="MeshOptions.Hints"/>) that could not be fitted, with the reason;
    /// the result was made without them.</summary>
    public List<(int group, string reason)> HintFailures { get; } = new();
    /// <summary>Time spent in recognition (MeshToBrep.Build) and in validating its trial results, in milliseconds.</summary>
    public long RecognitionMs, ValidationMs;

    public int InputTriangles, DegenerateTriangles, WeldedVertices, FlippedTriangles, Bodies, Faces, MergedFallbacks, UnmergedFaces, CollinearRemoved, Cylinders, Spheres, Tori, Cones, TracedEdges, RecognitionFallbacks;
    public List<string> Warnings = new();
    public double MaxSnap;
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{InputTriangles} triangles in, {DegenerateTriangles} degenerate dropped, {WeldedVertices} unique vertices, " +
        $"{FlippedTriangles} triangles re-oriented, {Bodies} bodies, {Faces} faces, {MergedFallbacks} coplanar regions kept as triangles, " +
        $"{Cylinders} cylinders, {Spheres} spheres, {Tori} tori, {Cones} cones, {TracedEdges} B-spline edges, {RecognitionFallbacks} bodies without recognition, {UnmergedFaces} merged faces reverted to triangles (inconsistent planes), {CollinearRemoved} collinear vertices removed, max snap {MaxSnap * 1000:0.######} mm");
}

/// <summary>Converts an STL triangle soup to closed planar polyhedra (one per connected shell).</summary>
public static class MeshConverter
{
    /// <summary>Planar facets only (no recognition), as polyhedra.</summary>
    public static List<Polyhedron> Convert(StlFile stl, MeshOptions opt, out MeshStats stats)
    {
        var (verts, shells, _) = Prepare(stl, opt, out stats);
        var st = stats;
        var bodies = shells.Select(ct => BuildBody(ct, verts, opt, st)).ToList();
        stats.Bodies = bodies.Count;
        stats.Faces = bodies.Sum(b => b.Faces.Count);
        return bodies;
    }

    /// <summary>B-rep bodies; with <see cref="MeshOptions.RecognizeCylinders"/> cylinders become real cylinders
    /// (per shell, falling back to planar facets when recognition does not give exact geometry).</summary>
    public static List<Brep.BrepBody> ConvertToBrep(StlFile stl, MeshOptions opt, out MeshStats stats) =>
        ConvertToBrep(stl, opt, out stats, out _);

    /// <summary>As <see cref="ConvertToBrep(StlFile, MeshOptions, out MeshStats)"/>, plus which face every triangle became.</summary>
    public static List<Brep.BrepBody> ConvertToBrep(StlFile stl, MeshOptions opt, out MeshStats stats, out MeshLabels labels)
    {
        var (verts, shells, sourceOf) = Prepare(stl, opt, out stats);
        // the shells are independent: converted in parallel (largest first, so a big one does not end up last), each
        // with its own statistics, merged in shell order afterwards
        var results = new (Brep.BrepBody body, List<LabeledTriangle> labelled, IReadOnlyList<FaceInfo> infos, MeshStats st)[shells.Count];
        var roundOf = new int[shells.Count]; // recognition round per busy shell (0: waiting or done)
        int done = 0;
        var gate = new object();
        void Report()
        {
            if (opt.Progress == null) return;
            int busy = -1, busyRound = 0;
            lock (gate)
            {
                for (int i = 0; i < shells.Count; i++)
                    if (roundOf[i] > 0 && (busy < 0 || shells[i].Count > shells[busy].Count)) busy = i;
                if (busy >= 0) busyRound = roundOf[busy];
            }
            opt.Progress.Report(new MeshProgress(Volatile.Read(ref done), shells.Count, busy < 0 ? 0 : shells[busy].Count, busyRound));
        }
        var order = Enumerable.Range(0, shells.Count).OrderByDescending(i => shells[i].Count).ToList();
        // with fewer shells than cores the two recognition variants of a shell also run side by side
        bool parallelVariants = shells.Count * 2 <= Environment.ProcessorCount && opt.MaxParallelism is null or > 1;
        var po = new ParallelOptions { CancellationToken = opt.Cancel, MaxDegreeOfParallelism = Math.Max(1, opt.MaxParallelism ?? Environment.ProcessorCount) };
        try
        {
            Parallel.ForEach(order, po, c =>
            {
                results[c] = ConvertShell(c, shells[c], verts, sourceOf, opt, r => { lock (gate) roundOf[c] = Math.Max(r, 1); Report(); }, parallelVariants);
                lock (gate) roundOf[c] = 0;
                Interlocked.Increment(ref done);
                Report();
            });
        }
        catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException)) { throw new OperationCanceledException(opt.Cancel); }
        catch (AggregateException ae) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw(); }

        var bodies = new List<Brep.BrepBody>();
        var labelled = new List<LabeledTriangle>();
        var faceInfos = new List<IReadOnlyList<FaceInfo>>();
        foreach (var (body, lab, infos, st) in results)
        {
            bodies.Add(body); labelled.AddRange(lab); faceInfos.Add(infos);
            stats.HintFailures.AddRange(st.HintFailures); stats.Warnings.AddRange(st.Warnings);
            stats.RecognitionMs += st.RecognitionMs; stats.ValidationMs += st.ValidationMs;
            stats.Cylinders += st.Cylinders; stats.Spheres += st.Spheres; stats.Tori += st.Tori; stats.Cones += st.Cones; stats.TracedEdges += st.TracedEdges;
            stats.RecognitionFallbacks += st.RecognitionFallbacks; stats.MergedFallbacks += st.MergedFallbacks;
            stats.UnmergedFaces += st.UnmergedFaces; stats.CollinearRemoved += st.CollinearRemoved; stats.MaxSnap = Math.Max(stats.MaxSnap, st.MaxSnap);
        }
        labels = new MeshLabels { Vertices = verts, Triangles = labelled, Faces = faceInfos };
        stats.Bodies = bodies.Count;
        stats.Faces = bodies.Sum(b => b.Faces.Count);
        return bodies;
    }

    /// <summary>One shell: recognition (two variants, leaving groups out when they do not fit) or planar facets.</summary>
    private static (Brep.BrepBody body, List<LabeledTriangle> labelled, IReadOnlyList<FaceInfo> infos, MeshStats stats) ConvertShell(
        int c, List<int[]> ct, List<Vec3> verts, Dictionary<int[], int> sourceOf, MeshOptions opt, Action<int> round, bool parallelVariants)
    {
        var stats = new MeshStats();
        long recMs = 0; // (summed over variants that may run in parallel)
        var labelled = new List<LabeledTriangle>(ct.Count);
        IReadOnlyList<FaceInfo> infos;
        var cache = new RecognitionCache(); // shared by all attempts on this shell
        opt.Cancel.ThrowIfCancellationRequested();
        round(0);
        Brep.BrepBody? body = null;
        MeshToBrep? best = null;
        // the user's groups, as triangle indices in this shell (with their number in opt.Hints)
        var local = new List<(IReadOnlyList<int> tris, FaceKind kind, int group, bool soft, FaceInfo? surface)>();
        if (opt.Hints.Count > 0)
        {
            var indexOfSource = new Dictionary<int, int>();
            for (int i = 0; i < ct.Count; i++) indexOfSource[sourceOf[ct[i]]] = i;
            for (int g = 0; g < opt.Hints.Count; g++)
            {
                var tl = opt.Hints[g].SourceTriangles.Where(indexOfSource.ContainsKey).Select(si => indexOfSource[si]).ToList();
                if (tl.Count > 0) local.Add((tl, opt.Hints[g].Kind, g, opt.Hints[g].Soft, opt.Hints[g].Surface));
            }
        }
        int[] groupOf = [];
        HashSet<int>? frozen = null;
        if (opt.FreeTriangles != null)
        {
            var free = opt.FreeTriangles as ISet<int> ?? new HashSet<int>(opt.FreeTriangles);
            frozen = new HashSet<int>(Enumerable.Range(0, ct.Count).Where(i => !free.Contains(sourceOf[ct[i]])));
        }
        HashSet<int>? keep = null;
        if (opt.KeepFacets is { Count: > 0 })
        {
            var ks = opt.KeepFacets as ISet<int> ?? new HashSet<int>(opt.KeepFacets);
            keep = new HashSet<int>(Enumerable.Range(0, ct.Count).Where(i => ks.Contains(sourceOf[ct[i]])));
            if (keep.Count == 0) keep = null;
        }
        if (opt.RecognizeCylinders || opt.RecognizeSpheres || opt.RecognizeTori || opt.RecognizeCones || opt.RecognizePlanes || local.Count > 0 || keep != null)
        {
            // two variants: with and without loosely joining facets to established cylinders; keep the one with
            // fewer faces (loose joining helps noisy arcs such as Spoelhouder's R12, but can cascade elsewhere)
            (Brep.BrepBody? body, MeshToBrep? rec, string? error) Attempt(List<(IReadOnlyList<int> tris, FaceKind kind, int group, bool soft, FaceInfo? surface)> hints)
            {
                string? err = null;
                // validation (a trial build of the XT) costs about as much as recognition on facet-heavy shells: only
                // the candidate that would be kept is validated, the other one only when that fails
                var failed = new HashSet<Brep.BrepBody>(ReferenceEqualityComparer.Instance);
                bool Valid(Brep.BrepBody candidate)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var check = Xt.XtValidator.Validate(new Brep.XtBrepBuilder().Build(
                            [(candidate, new Brep.BrepNames { Body = "0:1", Faces = candidate.Faces.Select((_, i) => "f" + i).ToList(), Edges = candidate.Edges.Select((_, i) => "e" + i).ToList() })]));
                        if (check.Count == 0) return true;
                        err = "validation: " + string.Join("; ", check.Take(3));
                    }
                    catch (Exception e) when (e is MeshException or ArgumentException or InvalidOperationException or KeyNotFoundException) { err = e.Message; }
                    finally { stats.ValidationMs += sw.ElapsedMilliseconds; }
                    failed.Add(candidate);
                    return false;
                }
                // (only the user's groups: nothing is recognised, so the second variant would give the same)
                MeshOptions[] variants = opt.OnlyHints ? [opt] : [opt, opt with { CylinderJoinTolerance = 0 }];
                var built = new (Brep.BrepBody? body, MeshToBrep? rec, string? error)[variants.Length];
                (Brep.BrepBody?, MeshToBrep?, string?) BuildOne(MeshOptions o)
                {
                    try
                    {
                        var forceFit = new HashSet<int>(Enumerable.Range(0, hints.Count).Where(k => opt.Hints[hints[k].group].Force));
                        var rec = new MeshToBrep(verts, ct.Select(t => (int[])t.Clone()).ToList(), o, hints.Select(h => (h.tris, h.kind, h.soft, h.surface)).ToList(), frozen, cache, forceFit, keep);
                        rec.RoundStarted = round;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try { return (rec.Build(), rec, null); } finally { Interlocked.Add(ref recMs, sw.ElapsedMilliseconds); }
                    }
                    catch (Exception e) when (e is MeshException or ArgumentException or InvalidOperationException or KeyNotFoundException)
                    {
                        return (null, null, e.Message);
                    }
                }
                if (parallelVariants && variants.Length > 1)
                {
                    // few shells, idle cores: both variants at the same time (the cache is shared and thread-safe); when
                    // the first one leaves no single facets the second one is stopped (it cannot do better)
                    using var stopSecond = CancellationTokenSource.CreateLinkedTokenSource(opt.Cancel);
                    var second = variants[1] with { Cancel = stopSecond.Token };
                    bool secondStopped = false;
                    try
                    {
                        Parallel.Invoke(new ParallelOptions { CancellationToken = opt.Cancel },
                            () => { built[0] = BuildOne(variants[0]); if (built[0].rec is { SingleFacets: 0 }) stopSecond.Cancel(); },
                            () =>
                            {
                                try { built[1] = BuildOne(second); }
                                catch (OperationCanceledException) when (!opt.Cancel.IsCancellationRequested) { secondStopped = true; }
                            });
                    }
                    catch (AggregateException ae) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw(); }
                    if (built[0] is { body: { } b0, rec: { SingleFacets: 0 } r0 } && Valid(b0)) return (b0, r0, null);
                    if (secondStopped) built[1] = BuildOne(variants[1]); // (the first one failed validation after all)
                }
                else
                    for (int i = 0; i < variants.Length; i++)
                    {
                        built[i] = BuildOne(variants[i]);
                        // every facet is in a surface: the stricter variant cannot do better
                        if (built[i] is { body: { } bi, rec: { SingleFacets: 0 } ri } && Valid(bi)) return (bi, ri, null);
                    }
                var candidates = new List<(Brep.BrepBody body, MeshToBrep rec)>();
                foreach (var (bb, rr, ee) in built)
                    if (bb != null) candidates.Add((bb, rr!));
                    else if (ee != null) err = ee;
                // fewest faces first (on a tie the first variant, as before)
                foreach (var (cb, cr) in candidates.OrderBy(x => x.body.Faces.Count))
                    if (!failed.Contains(cb) && Valid(cb)) return (cb, cr, err);
                return (null, null, err);
            }

            var used = local;
            var (b0, r0, e0) = Attempt(local);
            if (b0 == null && local.Count > 0)
            {
                // a group that does not fit must not take everything else down: leave groups out until it works
                var hard = Enumerable.Range(0, local.Count).Where(k => !local[k].soft).ToList();
                var soft = local.Where(h => h.soft).ToList();
                var tries = hard.Count > 1
                    ? hard.Select(k => local.Where((_, x) => x != k).ToList()).Append(soft)
                    : [soft];
                if (soft.Count > 0) tries = tries.Append([]);
                foreach (var hs in tries)
                {
                    var (b1, r1, _) = Attempt(hs);
                    if (b1 == null) continue;
                    (b0, r0, used) = (b1, r1, hs);
                    break;
                }
                foreach (var h in local.Where(h => !h.soft && !used.Contains(h)))
                    stats.HintFailures.Add((h.group, e0 ?? "unknown"));
            }
            (body, best) = (b0, r0);
            groupOf = used.Select(h => h.group).ToArray();
            if (body != null) { stats.Cylinders += best!.CylindersFound; stats.Spheres += best.SpheresFound; stats.Tori += best.ToriFound; stats.Cones += best.ConesFound; stats.TracedEdges += best.TracedEdges; }
            else
            {
                stats.RecognitionFallbacks++;
                stats.Warnings.Add($"shell {c + 1}: cylinder recognition failed ({e0}); written as planar facets");
            }
        }
        if (best != null && body != null)
        {
            var face = best.FaceOfTriangle;
            for (int i = 0; i < ct.Count; i++) labelled.Add(new(ct[i][0], ct[i][1], ct[i][2], sourceOf[ct[i]], c, face[i]));
            infos = best.FaceInfos.Select(f => f.Group >= 0 ? f with { Group = groupOf[f.Group] } : f).ToList();
        }
        else
        {
            // planar facets only: no per-triangle face mapping, shown as facets
            for (int i = 0; i < ct.Count; i++) labelled.Add(new(ct[i][0], ct[i][1], ct[i][2], sourceOf[ct[i]], c, 0));
            infos = [new FaceInfo(FaceKind.Facet)];
        }
        if (body == null)
        {
            body = Brep.BrepShapes.FromPolyhedron(BuildBody(ct, verts, opt, stats));
            // merging coplanar facets can break the topology where the mesh folds flat onto itself (a flap without
            // thickness: once the vertex on its T-junction drops out, four facets share an edge); then this shell
            // is written as single facets
            if (opt.MergeCoplanar && Brep.XtBrepBuilder.TopologyError(body) is string why)
            {
                stats.Warnings.Add($"shell {c + 1}: merged planes gave an invalid topology ({why}); written as single facets");
                body = Brep.BrepShapes.FromPolyhedron(BuildBody(ct, verts, opt with { MergeCoplanar = false }, stats));
            }
        }
        stats.RecognitionMs += recMs;
        return (body, labelled, infos, stats);
    }

    /// <summary>
    /// Edges with one triangle (holes) and with more than two, counted exactly as the conversion does (after welding and
    /// dropping triangles without area); both 0 = the mesh can be converted.
    /// </summary>
    public static (int open, int nonManifold) CountBadEdges(StlFile stl, MeshOptions opt)
    {
        var (_, _, _, edgeTris) = WeldAndEdges(stl, opt, new MeshStats());
        return (edgeTris.Values.Count(l => l.Count == 1), edgeTris.Values.Count(l => l.Count > 2));
    }

    /// <summary>
    /// Vertices where two parts of the surface touch in a single point (the triangles around it form more than one fan),
    /// which the conversion refuses ("non-manifold vertex") although every edge is fine. Independent of the triangles'
    /// orientation: around each vertex the far edges of its triangles must form one ring.
    /// </summary>
    public static int CountPinchedVertices(StlFile stl, MeshOptions opt)
    {
        var (_, tris, _, edgeTris) = WeldAndEdges(stl, opt, new MeshStats());
        // (per piece of surface joined by edges: two separate bodies touching in a point are fine, they become separate
        // shells; bigbox has 6)
        var comp = new int[tris.Count]; Array.Fill(comp, -1);
        int nComp = 0;
        for (int s0 = 0; s0 < tris.Count; s0++)
        {
            if (comp[s0] >= 0) continue;
            var stack = new Stack<int>(); stack.Push(s0); comp[s0] = nComp;
            while (stack.TryPop(out int t))
                for (int k = 0; k < 3; k++)
                {
                    int u = tris[t][k], w = tris[t][(k + 1) % 3];
                    if (!edgeTris.TryGetValue(u < w ? (u, w) : (w, u), out var l)) continue;
                    foreach (int oi in l) if (comp[oi] < 0) { comp[oi] = nComp; stack.Push(oi); }
                }
            nComp++;
        }
        var ring = new Dictionary<(int, int), Dictionary<int, List<int>>>();
        for (int i = 0; i < tris.Count; i++)
            for (int k = 0; k < 3; k++)
            {
                var t = tris[i];
                int v = t[k], a = t[(k + 1) % 3], b = t[(k + 2) % 3];
                if (!ring.TryGetValue((v, comp[i]), out var g)) ring[(v, comp[i])] = g = new();
                if (!g.TryGetValue(a, out var la)) g[a] = la = new(); la.Add(b);
                if (!g.TryGetValue(b, out var lb)) g[b] = lb = new(); lb.Add(a);
            }
        int pinched = 0;
        foreach (var g in ring.Values)
        {
            // one connected ring (or chain, at a hole) of neighbours
            var seen = new HashSet<int>();
            var stack = new Stack<int>(); stack.Push(g.Keys.First()); seen.Add(g.Keys.First());
            while (stack.TryPop(out int u)) foreach (int w in g[u]) if (seen.Add(w)) stack.Push(w);
            if (seen.Count < g.Count) pinched++;
        }
        return pinched;
    }

    /// <summary>Scale, weld, check watertightness, split into shells and orient each shell outward.</summary>
    private static (List<Vec3> verts, List<List<int[]>> shells, Dictionary<int[], int> sourceOf) Prepare(StlFile stl, MeshOptions opt, out MeshStats stats)
    {
        stats = new MeshStats { InputTriangles = stl.Triangles.Count };
        if (stl.Triangles.Count == 0) throw new MeshException("STL contains no triangles");
        var (verts, tris, sourceOf, edgeTris) = WeldAndEdges(stl, opt, stats);
        var open = edgeTris.Where(e => e.Value.Count == 1).ToList();
        var nonManifold = edgeTris.Where(e => e.Value.Count > 2).ToList();
        if (open.Count > 0 || nonManifold.Count > 0)
        {
            string P(int v) => $"({verts[v].X / opt.Scale:0.###}, {verts[v].Y / opt.Scale:0.###}, {verts[v].Z / opt.Scale:0.###})";
            var ex = open.Concat(nonManifold).Take(5).Select(e => $"{P(e.Key.Item1)}-{P(e.Key.Item2)} used by {e.Value.Count} triangles");
            throw new MeshException(string.Create(CultureInfo.InvariantCulture,
                $"Mesh is not watertight: {open.Count} open edges (holes), {nonManifold.Count} edges shared by more than two triangles. " +
                $"Examples (STL units): {string.Join("; ", ex)}. Repair the mesh first (stl2rsdocx --repair, or 🧹 in the app)."));
        }
        return Shells(verts, tris, sourceOf, edgeTris, stats);
    }

    /// <summary>1. scale + weld (triangles that lose a corner or their area are dropped); 2. edge -> triangles.</summary>
    private static (List<Vec3> verts, List<int[]> tris, Dictionary<int[], int> sourceOf, Dictionary<(int, int), List<int>> edgeTris) WeldAndEdges(StlFile stl, MeshOptions opt, MeshStats stats)
    {
        // 1. scale + weld
        var verts = new List<Vec3>();
        var grid = new Dictionary<(long, long, long), List<int>>();
        double cell = opt.WeldTolerance;
        int Weld(Vec3 p)
        {
            var key = ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var list))
                            foreach (int i in list)
                                if ((verts[i] - p).Length <= opt.WeldTolerance) return i;
            verts.Add(p);
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
            l.Add(verts.Count - 1);
            return verts.Count - 1;
        }
        var tris = new List<int[]>();
        var sourceOf = new Dictionary<int[], int>(ReferenceEqualityComparer.Instance); // triangle array -> input index
        for (int si = 0; si < stl.Triangles.Count; si++)
        {
            var t = stl.Triangles[si];
            int a = Weld(t.A * opt.Scale), b = Weld(t.B * opt.Scale), c = Weld(t.C * opt.Scale);
            if (a == b || b == c || a == c || (verts[b] - verts[a]).Cross(verts[c] - verts[a]).Length < 1e-24)
            { stats.DegenerateTriangles++; continue; }
            int[] tri = [a, b, c];
            tris.Add(tri);
            sourceOf[tri] = si;
        }
        stats.WeldedVertices = verts.Count;

        // 2. edge -> triangles, manifold check
        var edgeTris = new Dictionary<(int, int), List<int>>();
        for (int t = 0; t < tris.Count; t++)
            for (int k = 0; k < 3; k++)
            {
                int a = tris[t][k], b = tris[t][(k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (!edgeTris.TryGetValue(key, out var l)) edgeTris[key] = l = new List<int>();
                l.Add(t);
            }
        return (verts, tris, sourceOf, edgeTris);
    }

    /// <summary>3. connected components with a consistent orientation, each turned outward.</summary>
    private static (List<Vec3> verts, List<List<int[]>> shells, Dictionary<int[], int> sourceOf) Shells(List<Vec3> verts, List<int[]> tris, Dictionary<int[], int> sourceOf, Dictionary<(int, int), List<int>> edgeTris, MeshStats stats)
    {
        // 3. connected components + consistent orientation (BFS)
        var comp = new int[tris.Count];
        Array.Fill(comp, -1);
        int nComp = 0;
        for (int seed = 0; seed < tris.Count; seed++)
        {
            if (comp[seed] >= 0) continue;
            var queue = new Queue<int>([seed]);
            comp[seed] = nComp;
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                for (int k = 0; k < 3; k++)
                {
                    int a = tris[t][k], b = tris[t][(k + 1) % 3];
                    foreach (int n in edgeTris[a < b ? (a, b) : (b, a)])
                    {
                        if (n == t) continue;
                        bool sameDir = HasDirected(tris[n], a, b);
                        if (comp[n] < 0)
                        {
                            comp[n] = nComp;
                            if (sameDir) { Array.Reverse(tris[n]); stats.FlippedTriangles++; }
                            queue.Enqueue(n);
                        }
                        else if (sameDir)
                            throw new MeshException("Mesh is not orientable (e.g. a Möbius-like or self-overlapping surface)");
                    }
                }
            }
            nComp++;
        }

        var shells = new List<List<int[]>>();
        for (int c = 0; c < nComp; c++)
        {
            var ct = Enumerable.Range(0, tris.Count).Where(t => comp[t] == c).Select(t => tris[t]).ToList();
            double vol = ct.Sum(t => verts[t[0]].Dot(verts[t[1]].Cross(verts[t[2]]))) / 6;
            if (Math.Abs(vol) < 1e-18) throw new MeshException($"Shell {c + 1} encloses no volume");
            if (vol < 0) { foreach (var t in ct) Array.Reverse(t); stats.FlippedTriangles += ct.Count; }
            CheckManifoldVertices(ct, c);
            shells.Add(ct);
        }
        return (verts, shells, sourceOf);
    }

    private static bool HasDirected(int[] t, int a, int b) =>
        (t[0] == a && t[1] == b) || (t[1] == a && t[2] == b) || (t[2] == a && t[0] == b);

    private static void CheckManifoldVertices(List<int[]> tris, int comp)
    {
        // around each vertex the incident triangles must form one fan
        var around = new Dictionary<int, List<int[]>>();
        foreach (var t in tris) foreach (int v in t) { if (!around.TryGetValue(v, out var l)) around[v] = l = new(); l.Add(t); }
        foreach (var (v, list) in around)
        {
            // walk: in each triangle, the edge after v leads to the next triangle
            var next = new Dictionary<int, int>(); // from "prev" neighbour vertex to "next" neighbour vertex
            foreach (var t in list)
            {
                int i = Array.IndexOf(t, v);
                next[t[(i + 1) % 3]] = t[(i + 2) % 3];
            }
            int start = next.Keys.First(), cur = start, steps = 0;
            do { cur = next[cur]; steps++; } while (cur != start && steps <= list.Count);
            if (steps != list.Count)
                throw new MeshException($"Shell {comp + 1}: non-manifold vertex (two parts touch in a single point)");
        }
    }

    private static Polyhedron BuildBody(List<int[]> tris, List<Vec3> verts, MeshOptions opt, MeshStats stats)
    {
        // faces as (loops of vertex ids: outer first, then holes; triangles in the face)
        var faces = new List<(List<List<int>> loops, List<int[]> tris)>();
        if (!opt.MergeCoplanar)
            foreach (var t in tris) faces.Add(([t.ToList()], [t]));
        else
            MergeCoplanar(tris, verts, opt, stats, faces);

        // Merged faces get a fitted plane and their vertices are snapped onto it. Where merged faces meet
        // inconsistently at a vertex, the smallest of them falls back to triangles. Triangle faces need no
        // fitting: their plane is defined by their (snapped) vertices, so they are always exact.
        var positions = new Dictionary<int, Vec3>();
        if (opt.MergeCoplanar) ResolveMergedPlanes(faces, verts, opt, stats, positions);

        RemoveCollinear(faces, verts, opt, stats);

        var used = faces.SelectMany(f => f.loops.SelectMany(l => l)).Distinct().ToList();
        var snapped = used.ToDictionary(v => v, v => positions.TryGetValue(v, out var p) ? p : verts[v]);
        var map = used.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        return new Polyhedron
        {
            Vertices = used.Select(v => (snapped[v].X, snapped[v].Y, snapped[v].Z)).ToList(),
            Faces = faces.Select(f => f.loops[0].Select(v => map[v]).ToArray()).ToList(),
            Holes = faces.Any(f => f.loops.Count > 1)
                ? faces.Select(f => f.loops.Skip(1).Select(l => l.Select(v => map[v]).ToArray()).ToArray()).ToList()
                : null,
        };
    }

    private static void MergeCoplanar(List<int[]> tris, List<Vec3> verts, MeshOptions opt, MeshStats stats, List<(List<List<int>>, List<int[]>)> faces)
    {
        var edgeTri = new Dictionary<(int, int), int>(); // directed edge -> triangle
        for (int t = 0; t < tris.Count; t++)
            for (int k = 0; k < 3; k++) edgeTri[(tris[t][k], tris[t][(k + 1) % 3])] = t;
        Vec3 N(int t) => (verts[tris[t][1]] - verts[tris[t][0]]).Cross(verts[tris[t][2]] - verts[tris[t][0]]).Normalized();
        double cosTol = Math.Cos(opt.AngleToleranceDeg * Math.PI / 180);
        var done = new bool[tris.Count];

        for (int seed = 0; seed < tris.Count; seed++)
        {
            if (done[seed]) continue;
            var n0 = N(seed); var p0 = verts[tris[seed][0]];
            var region = new List<int> { seed };
            var inRegion = new HashSet<int> { seed };
            done[seed] = true;
            for (int qi = 0; qi < region.Count; qi++)
            {
                var t = tris[region[qi]];
                for (int k = 0; k < 3; k++)
                {
                    int nb = edgeTri[(t[(k + 1) % 3], t[k])];
                    if (done[nb]) continue;
                    if (N(nb).Dot(n0) < cosTol) continue;
                    if (tris[nb].Any(v => Math.Abs(n0.Dot(verts[v] - p0)) > opt.DistanceTolerance)) continue;
                    done[nb] = true; inRegion.Add(nb); region.Add(nb);
                }
            }

            var loops = region.Count == 1 ? [tris[seed].ToList()] : BoundaryLoops(region.Select(r => tris[r]).ToList(), verts, n0);
            if (loops is null)
            {
                // pinched boundary or no unique outer loop: keep the region as separate triangles
                stats.MergedFallbacks++;
                foreach (int r in region) faces.Add(([tris[r].ToList()], [tris[r]]));
            }
            else faces.Add((loops, region.Select(r => tris[r]).ToList()));
        }
    }

    /// <summary>Boundary loops of a triangle region: the outer loop (CCW around <paramref name="normal"/>) first,
    /// then the holes (CW). Null if the boundary is pinched or there is not exactly one outer loop.</summary>
    private static List<List<int>>? BoundaryLoops(List<int[]> region, List<Vec3> verts, Vec3 normal)
    {
        var directed = new HashSet<(int, int)>();
        foreach (var t in region) for (int k = 0; k < 3; k++) directed.Add((t[k], t[(k + 1) % 3]));
        var next = new Dictionary<int, int>();
        foreach (var (a, b) in directed)
        {
            if (directed.Contains((b, a))) continue; // interior edge
            if (next.ContainsKey(a)) return null;    // pinch: two boundary edges leave a
            next[a] = b;
        }
        var loops = new List<List<int>>();
        var visited = new HashSet<int>();
        foreach (int start in next.Keys)
        {
            if (visited.Contains(start)) continue;
            var loop = new List<int>();
            int cur = start;
            do
            {
                if (!visited.Add(cur)) return null;
                loop.Add(cur);
                if (!next.TryGetValue(cur, out cur)) return null;
            } while (cur != start);
            loops.Add(loop);
        }
        var outer = loops.Where(l => Newell(l, verts).Dot(normal) > 0).ToList();
        if (outer.Count != 1) return null;
        loops.Remove(outer[0]);
        loops.Insert(0, outer[0]);
        return loops;
    }

    private static Vec3 Newell(List<int> loop, List<Vec3> verts)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var c = verts[loop[i]]; var n = verts[loop[(i + 1) % loop.Count]];
            x += (c.Y - n.Y) * (c.Z + n.Z);
            y += (c.Z - n.Z) * (c.X + n.X);
            z += (c.X - n.X) * (c.Y + n.Y);
        }
        return new Vec3(x, y, z);
    }

    private static void RemoveCollinear(List<(List<List<int>> loops, List<int[]> tris)> faces, List<Vec3> verts, MeshOptions opt, MeshStats stats)
    {
        double sinTol = Math.Sin(opt.AngleToleranceDeg * Math.PI / 180);
        bool changed = true;
        while (changed)
        {
            changed = false;
            var nbrs = new Dictionary<int, HashSet<int>>();
            foreach (var loop in faces.SelectMany(f => f.loops))
                for (int i = 0; i < loop.Count; i++)
                {
                    int a = loop[i], b = loop[(i + 1) % loop.Count];
                    if (!nbrs.TryGetValue(a, out var sa)) nbrs[a] = sa = new(); sa.Add(b);
                    if (!nbrs.TryGetValue(b, out var sb)) nbrs[b] = sb = new(); sb.Add(a);
                }
            foreach (var (v, set) in nbrs)
            {
                if (set.Count != 2) continue;
                var (u, w) = (set.First(), set.Last());
                var du = verts[u] - verts[v]; var dw = verts[w] - verts[v];
                if (du.Cross(dw).Length > sinTol * du.Length * dw.Length || du.Dot(dw) >= 0) continue;
                // v lies on a straight edge between exactly two faces: drop it from both loops
                if (faces.SelectMany(f => f.loops).Any(l => l.Contains(v) && l.Count <= 3)) continue;
                foreach (var l in faces.SelectMany(f => f.loops)) l.Remove(v);
                stats.CollinearRemoved++;
                changed = true;
                break;
            }
        }
    }

    private static void ResolveMergedPlanes(List<(List<List<int>> loops, List<int[]> tris)> faces, List<Vec3> verts,
        MeshOptions opt, MeshStats stats, Dictionary<int, Vec3> positions)
    {
        const double maxResidual = 1e-10; // metres; Parasolid's linear precision is 1e-8
        while (true)
        {
            positions.Clear();
            var planes = new Dictionary<int, (Vec3 n, double d)>();
            var mergedOf = new Dictionary<int, List<int>>();
            for (int f = 0; f < faces.Count; f++)
            {
                if (faces[f].tris.Count < 2) continue;
                var n = new Vec3(0, 0, 0);
                foreach (var t in faces[f].tris) n += (verts[t[1]] - verts[t[0]]).Cross(verts[t[2]] - verts[t[0]]);
                n = n.Normalized();
                var vs = faces[f].loops.SelectMany(l => l).Distinct().ToList();
                planes[f] = (n, vs.Average(v => n.Dot(verts[v])));
                foreach (int v in vs) { if (!mergedOf.TryGetValue(v, out var l)) mergedOf[v] = l = new(); l.Add(f); }
            }

            var unmerge = new HashSet<int>();
            foreach (var (v, fs) in mergedOf)
            {
                var (p, residual) = Snap(verts[v], fs.Select(f => planes[f]).ToList());
                double move = (p - verts[v]).Length;
                if (move > opt.SnapTolerance || residual > maxResidual)
                    unmerge.Add(fs.OrderBy(f => faces[f].tris.Count).ThenBy(f => f).First());
                else positions[v] = p;
            }
            if (unmerge.Count == 0) break;

            foreach (int f in unmerge.OrderByDescending(f => f))
            {
                var tris = faces[f].tris;
                faces.RemoveAt(f);
                foreach (var t in tris) faces.Add(([t.ToList()], [t]));
                stats.UnmergedFaces++;
            }
        }
        foreach (var (v, p) in positions) stats.MaxSnap = Math.Max(stats.MaxSnap, (p - verts[v]).Length);
    }

    /// <summary>
    /// Minimum-norm displacement of x0 onto all planes (n.x = d), via the eigen-decomposition of sum(n n^T):
    /// directions with a (numerically) zero eigenvalue are left alone, so parallel planes are handled exactly.
    /// Nearly parallel but inconsistent planes produce a large move, which the caller rejects.
    /// Returns the new point and the largest remaining plane distance.
    /// </summary>
    internal static (Vec3 p, double residual) Snap(Vec3 x0, List<(Vec3 n, double d)> planes)
    {
        var m = new double[3, 3];
        var b = new double[3];
        foreach (var (n, d) in planes)
        {
            double r = d - n.Dot(x0);
            double[] nv = [n.X, n.Y, n.Z];
            for (int i = 0; i < 3; i++)
            {
                b[i] += nv[i] * r;
                for (int j = 0; j < 3; j++) m[i, j] += nv[i] * nv[j];
            }
        }
        var (lambda, u) = Eigen3(m);
        double lmax = lambda.Max();
        double[] delta = new double[3];
        for (int k = 0; k < 3; k++)
        {
            if (lambda[k] <= 1e-9 * lmax) continue;
            double c = (u[0, k] * b[0] + u[1, k] * b[1] + u[2, k] * b[2]) / lambda[k];
            for (int i = 0; i < 3; i++) delta[i] += c * u[i, k];
        }
        var p = new Vec3(x0.X + delta[0], x0.Y + delta[1], x0.Z + delta[2]);
        double residual = planes.Max(pl => Math.Abs(pl.d - pl.n.Dot(p)));
        return (p, residual);
    }

    /// <summary>Eigenvalues and eigenvectors (columns of u) of a symmetric 3x3 matrix (cyclic Jacobi).</summary>
    internal static (double[] lambda, double[,] u) Eigen3(double[,] a)
    {
        a = (double[,])a.Clone();
        var u = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off < 1e-300) break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (a[p, q] == 0) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double ukp = u[k, p], ukq = u[k, q];
                        u[k, p] = c * ukp - s * ukq; u[k, q] = s * ukp + c * ukq;
                    }
                }
        }
        return ([a[0, 0], a[1, 1], a[2, 2]], u);
    }
}
