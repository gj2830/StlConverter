using System.Globalization;
using g3;
using gs;

namespace RsDocx.Core.Stl;

/// <summary>What <see cref="MeshRepair"/> does. Lengths in STL units.</summary>
public sealed record MeshRepairOptions
{
    /// <summary>Close open boundaries (holes).</summary>
    public bool FillHoles { get; init; } = true;

    /// <summary>Remove duplicate triangles and triangles on edges shared by more than two (their holes are filled after).</summary>
    public bool FixNonManifold { get; init; } = true;

    /// <summary>Remove loose pieces whose area is below this fraction of the largest piece (0 = keep all).</summary>
    public double SmallPieceFraction { get; init; } = 0.01;

    /// <summary>Simplify to at most this many triangles (0 = no simplification).</summary>
    public int TargetTriangles { get; init; }

    /// <summary>Corners closer than this are one vertex.</summary>
    public double WeldTolerance { get; init; } = 1e-4;
}

public sealed class MeshRepairStats
{
    public int InputTriangles, DegenerateRemoved, DuplicatesRemoved, NonManifoldRemoved, PiecesRemoved, PieceTrianglesRemoved,
        HolesFilled, FillTriangles, HolesLeft, BowtiesSplit, FlatFixed, FlatLeft, FoldsRemoved, FoldsLeft, TwistsRemoved, OutputTriangles;

    /// <summary>Diagnostics: vertices that share their position with another one, after each step.</summary>
    public List<(string step, int count)> SamePosition { get; } = new();

    /// <summary>Folded triangles removed per fill round (diagnostics).</summary>
    public List<int> FoldRounds { get; } = new();

    /// <summary>Open edges after the repair (0 = closed).</summary>
    public int OpenEdges;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{InputTriangles} triangles in, {DegenerateRemoved} degenerate, {DuplicatesRemoved} duplicates and {NonManifoldRemoved} non-manifold removed, " +
        $"{PiecesRemoved} small pieces ({PieceTrianglesRemoved} triangles) removed, {HolesFilled} holes filled ({FillTriangles} triangles), " +
        $"{HolesLeft} holes left, {TwistsRemoved} twisted triangles removed, {BowtiesSplit} bowtie vertices split, {FoldsRemoved} folded triangles removed ({string.Join("/", FoldRounds)}; {FoldsLeft} folded edges left), {FlatFixed} flat triangles flipped away ({FlatLeft} left), {OutputTriangles} triangles out, {OpenEdges} open edges");
}

/// <summary>
/// Cleans a triangle mesh before conversion (as MeshLab would): duplicate, degenerate and non-manifold triangles out, small
/// loose pieces out, holes filled, optionally simplified. Built on geometry3Sharp (src/Geometry3Sharp).
/// <para>Careful with CAD meshes: no smoothing or remeshing, and simplification only collapses edges onto existing vertices,
/// so every vertex that remains lies exactly where it was (on the original planes, cylinders and spheres).</para>
/// </summary>
public static class MeshRepair
{
    public static StlFile Repair(StlFile stl, MeshRepairOptions opt, out MeshRepairStats stats)
    {
        stats = new MeshRepairStats { InputTriangles = stl.Triangles.Count };
        var mesh = Build(stl, opt, stats);

        var st = stats; // (an out parameter cannot be used in a local function)
        void Dup(string step)
        {
            if (Environment.GetEnvironmentVariable("RSREPAIRDBG") != "1") return;
            var seen = new HashSet<(double, double, double)>(); int d = 0;
            foreach (int v in mesh.VertexIndices()) { var p = mesh.GetVertex(v); if (!seen.Add((p.x, p.y, p.z))) d++; }
            // and pairs closer than the weld tolerance (the converter joins those)
            double tl = opt.WeldTolerance; int near = 0;
            var grid = new Dictionary<(long, long, long), List<int>>();
            foreach (int v in mesh.VertexIndices())
            {
                var p = mesh.GetVertex(v);
                var k = ((long)Math.Floor(p.x / tl), (long)Math.Floor(p.y / tl), (long)Math.Floor(p.z / tl));
                for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue((k.Item1 + dx, k.Item2 + dy, k.Item3 + dz), out var l))
                        foreach (int w in l) if (mesh.GetVertex(w).Distance(p) <= tl) near++;
                if (!grid.TryGetValue(k, out var own)) grid[k] = own = new List<int>();
                own.Add(v);
            }
            st.SamePosition.Add((step + $" (near {near})", d));
        }
        Dup("build");
        FixFlat(mesh, opt.WeldTolerance, stats);
        Dup("flat");
        if (opt.SmallPieceFraction > 0) RemoveSmallPieces(mesh, opt.SmallPieceFraction, stats);
        // consistent orientation before filling: around a hole next to a flipped patch the boundary edges do not join
        // into loops (minion+peaky: 2500 open edges left in "open spans")
        new MeshRepairOrientation(mesh).OrientComponents();
        if (opt.FillHoles)
        {
            // a fill can close a hole with a triangle folded flat onto its neighbour (and the input can have such
            // flaps): remove folds, fill again, until nothing changes
            for (int round = 0; round < 10; round++)
            {
                FillHoles(mesh, stats, opt.WeldTolerance);
                Dup($"fill {round}");
                int folds = RemoveFolds(mesh, stats);
                stats.FoldRounds.Add(folds);
                // (no progress: the fill recreates the same folds; those are flipped triangles of the input lying over
                // their neighbours, which removing does not cure)
                if (folds == 0 || (stats.FoldRounds.Count > 1 && folds >= stats.FoldRounds[^2])) break;
            }
            if (mesh.EdgeIndices().Any(mesh.IsBoundaryEdge)) FillHoles(mesh, stats, opt.WeldTolerance);
            // a twist: two triangles running through their common edge in the same direction (a fill that joined the
            // front and back of a thin wall, minion+peaky); the surface cannot be oriented there and the hole next to
            // it cannot be traced. Both triangles go, and the (now orientable) hole is filled again.
            for (int round = 0; round < 3; round++)
            {
                var twisted = Twisted(mesh);
                if (twisted.Count == 0) break;
                stats.TwistsRemoved += twisted.Count;
                MeshEditor.RemoveTriangles(mesh, twisted, true);
                FillHoles(mesh, stats, opt.WeldTolerance);
            }

            FixFlat(mesh, opt.WeldTolerance, stats);
            // two pieces of surface touching in one vertex (a bowtie) are not a valid solid for the converter: split
            // every one that is left and move the copies apart
            int nv = mesh.VertexCount;
            new MeshEditor(mesh).DisconnectAllBowties();
            if (mesh.VertexCount > nv) { stats.BowtiesSplit += mesh.VertexCount - nv; SeparateCopies(mesh, opt.WeldTolerance); }
            // holes opened again by the steps after the fill (folds, flat triangles: screw ball, 2 µm steps at the
            // start of a thread): a last fill, at worst a fan from the centre of the hole, and its bowties split
            for (int round = 0; round < 3 && mesh.EdgeIndices().Any(mesh.IsBoundaryEdge); round++)
            {
                FillHoles(mesh, stats, opt.WeldTolerance);
                nv = mesh.VertexCount;
                new MeshEditor(mesh).DisconnectAllBowties();
                if (mesh.VertexCount > nv) { stats.BowtiesSplit += mesh.VertexCount - nv; SeparateCopies(mesh, opt.WeldTolerance); }
            }
            Dup("end");
            stats.FoldsLeft = CountFolds(mesh);
        }

        // consistent orientation per piece, outward
        var orient = new MeshRepairOrientation(mesh);
        orient.OrientComponents();
        orient.SolveGlobalOrientation();

        if (opt.TargetTriangles > 0 && mesh.TriangleCount > opt.TargetTriangles)
        {
            var reducer = new Reducer(mesh) { CollapseToEndpoint = true, PreserveBoundaryShape = true };
            reducer.ReduceToTriangleCount(opt.TargetTriangles);
            FixFlat(mesh, opt.WeldTolerance, stats);
        }
        stats.FlatLeft = mesh.TriangleIndices().Count(t => Flat(mesh, t, opt.WeldTolerance) >= 0);

        stats.OpenEdges = mesh.EdgeIndices().Count(mesh.IsBoundaryEdge);
        var tris = new List<StlTriangle>(mesh.TriangleCount);
        foreach (int t in mesh.TriangleIndices())
        {
            var tv = mesh.GetTriangle(t);
            tris.Add(new StlTriangle(V(mesh.GetVertex(tv.a)), V(mesh.GetVertex(tv.b)), V(mesh.GetVertex(tv.c))));
        }
        stats.OutputTriangles = tris.Count;
        return new StlFile { Header = stl.Header, Binary = stl.Binary, Format = stl.Format, Triangles = tris };
    }

    private static Vec3 V(Vector3d p) => new(p.x, p.y, p.z);

    /// <summary>Weld, drop degenerate and duplicate triangles, and build the mesh; triangles on a full edge are refused.</summary>
    private static DMesh3 Build(StlFile stl, MeshRepairOptions opt, MeshRepairStats stats)
    {
        var mesh = new DMesh3();
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
                                if (V(mesh.GetVertex(i)) is var q && (q - p).Length <= opt.WeldTolerance) return i;
            int id = mesh.AppendVertex(new Vector3d(p.X, p.Y, p.Z));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
            l.Add(id);
            return id;
        }

        var tris = new List<Index3i>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var t in stl.Triangles)
        {
            int a = Weld(t.A), b = Weld(t.B), c = Weld(t.C);
            // (three different corners on one line stay: they carry a T-junction, see FixFlat)
            if (a == b || b == c || a == c) { stats.DegenerateRemoved++; continue; }
            // the same three corners twice (either orientation): one copy only
            int[] s = [a, b, c]; Array.Sort(s);
            if (opt.FixNonManifold && !seen.Add((s[0], s[1], s[2]))) { stats.DuplicatesRemoved++; continue; }
            tris.Add(new Index3i(a, b, c));
        }

        // triangles on edges used more than twice go last: the clean surface around them gets in first
        var count = new Dictionary<(int, int), int>();
        static (int, int) E(int a, int b) => a < b ? (a, b) : (b, a);
        foreach (var t in tris) { foreach (var e in new[] { E(t.a, t.b), E(t.b, t.c), E(t.c, t.a) }) count[e] = count.GetValueOrDefault(e) + 1; }
        bool Bad(Index3i t) => count[E(t.a, t.b)] > 2 || count[E(t.b, t.c)] > 2 || count[E(t.c, t.a)] > 2;
        foreach (var t in tris.Where(t => !Bad(t)).Concat(tris.Where(Bad)))
        {
            int id = mesh.AppendTriangle(t);
            if (id < 0)
            {
                if (!opt.FixNonManifold)
                    throw new MeshException("mesh has edges shared by more than two triangles (turn on non-manifold repair)");
                stats.NonManifoldRemoved++;
            }
        }
        MeshEditor.RemoveUnusedVertices(mesh);
        return mesh;
    }

    /// <summary>The longest edge of a triangle whose height on it is below tol (a corner on the opposite edge), else -1.</summary>
    private static int Flat(DMesh3 mesh, int t, double tol)
    {
        var tv = mesh.GetTriangle(t);
        Vector3d a = mesh.GetVertex(tv.a), b = mesh.GetVertex(tv.b), c = mesh.GetVertex(tv.c);
        double lab = a.Distance(b), lbc = b.Distance(c), lca = c.Distance(a);
        double l = Math.Max(lab, Math.Max(lbc, lca));
        double area2 = (b - a).Cross(c - a).Length;
        if (l <= 0 || area2 / l >= tol) return -1;
        return lab == l ? mesh.FindEdge(tv.a, tv.b) : lbc == l ? mesh.FindEdge(tv.b, tv.c) : mesh.FindEdge(tv.c, tv.a);
    }

    /// <summary>
    /// Triangles with three corners on one line (a T-junction: a vertex lying on the opposite edge) have no area; the
    /// converter drops them, which opens a crack. Flipping their long edge towards the middle vertex gives two proper
    /// triangles instead, when both are proper; otherwise (on a boundary, or a row of slivers along one line where every
    /// flip gives a new flat triangle) the middle vertex is collapsed onto the nearer end of the long edge: it lies on that
    /// line, so the shape does not change.
    /// </summary>
    private static void FixFlat(DMesh3 mesh, double tol, MeshRepairStats stats)
    {
        static double Height(Vector3d a, Vector3d b, Vector3d c)
        {
            double l = Math.Max(a.Distance(b), Math.Max(b.Distance(c), c.Distance(a)));
            return l <= 0 ? 0 : (b - a).Cross(c - a).Length / l;
        }
        for (int pass = 0; pass < 10; pass++)
        {
            int changed = 0;
            foreach (int t in mesh.TriangleIndices().ToList())
            {
                if (!mesh.IsTriangle(t)) continue;
                int e = Flat(mesh, t, tol);
                if (e < 0) continue;
                var ev = mesh.GetEdgeV(e);
                var tv = mesh.GetTriangle(t);
                int m = tv.a != ev.a && tv.a != ev.b ? tv.a : tv.b != ev.a && tv.b != ev.b ? tv.b : tv.c;
                Vector3d pa = mesh.GetVertex(ev.a), pb = mesh.GetVertex(ev.b), pm = mesh.GetVertex(m);
                if (!mesh.IsBoundaryEdge(e))
                {
                    var op = mesh.GetEdgeOpposingV(e);
                    int c = op.a == m ? op.b : op.a;
                    var pc = mesh.GetVertex(c);
                    if (Height(pa, pm, pc) >= tol && Height(pm, pb, pc) >= tol && mesh.FlipEdge(e, out _) == MeshResult.Ok) { changed++; continue; }
                }
                int near = pm.Distance(pa) <= pm.Distance(pb) ? ev.a : ev.b, far = near == ev.a ? ev.b : ev.a;
                if (mesh.CollapseEdge(near, m, out _) == MeshResult.Ok || mesh.CollapseEdge(far, m, out _) == MeshResult.Ok) changed++;
            }
            stats.FlatFixed += changed;
            if (changed == 0) break;
        }
    }

    /// <summary>
    /// Two neighbouring triangles folded back onto each other (normals opposite: a flap without thickness, e.g. a fill
    /// triangle lying on a flap of the original over a T-junction) are both removed; the hole is filled again after.
    /// Without this the mesh is closed but, once coplanar triangles are merged and the vertex on the T-junction drops
    /// out, four triangles share one edge (obj_1_BCons).
    /// </summary>
    private static int RemoveFolds(DMesh3 mesh, MeshRepairStats stats)
    {
        var remove = new HashSet<int>();
        foreach (var (ta, tb) in Folds(mesh)) { remove.Add(ta); remove.Add(tb); }
        if (remove.Count == 0) return 0;
        new MeshEditor(mesh).RemoveTriangles(remove.ToList(), true);
        stats.FoldsRemoved += remove.Count;
        return remove.Count;
    }

    /// <summary>Triangles on an edge that both run through it in the same direction (inconsistent orientation).</summary>
    private static List<int> Twisted(DMesh3 mesh)
    {
        var res = new HashSet<int>();
        foreach (int e in mesh.EdgeIndices())
        {
            if (mesh.IsBoundaryEdge(e)) continue;
            var ev = mesh.GetEdgeV(e); var et = mesh.GetEdgeT(e);
            if (Forward(mesh.GetTriangle(et.a), ev.a, ev.b) == Forward(mesh.GetTriangle(et.b), ev.a, ev.b)) { res.Add(et.a); res.Add(et.b); }
        }
        return res.ToList();
        static bool Forward(Index3i t, int a, int b) => (t.a == a && t.b == b) || (t.b == a && t.c == b) || (t.c == a && t.a == b);
    }

    /// <summary>The triangle pairs on edges where the surface folds flat onto itself (normals opposite within about 1°).</summary>
    private static IEnumerable<(int, int)> Folds(DMesh3 mesh)
    {
        const double folded = -0.9998;
        foreach (int e in mesh.EdgeIndices())
        {
            if (mesh.IsBoundaryEdge(e)) continue;
            var et = mesh.GetEdgeT(e);
            Vector3d na = mesh.GetTriNormal(et.a), nb = mesh.GetTriNormal(et.b);
            if (na.LengthSquared == 0 || nb.LengthSquared == 0) continue;
            if (na.Dot(nb) < folded) yield return (et.a, et.b);
        }
    }

    private static int CountFolds(DMesh3 mesh) => Folds(mesh).Count();

    private static void RemoveSmallPieces(DMesh3 mesh, double fraction, MeshRepairStats stats)
    {
        var comps = new MeshConnectedComponents(mesh);
        comps.FindConnectedT();
        if (comps.Count < 2) return;
        var areas = comps.Components.Select(c => c.Indices.Sum(t => mesh.GetTriArea(t))).ToList();
        double largest = areas.Max();
        var editor = new MeshEditor(mesh);
        for (int i = 0; i < comps.Count; i++)
            if (areas[i] < fraction * largest)
            {
                stats.PiecesRemoved++;
                stats.PieceTrianglesRemoved += comps[i].Indices.Length;
                editor.RemoveTriangles(comps[i].Indices, true);
            }
    }

    /// <summary>
    /// Every boundary loop gets a minimal fill (no smoothing: a flat hole stays flat, sharp edges come back); vertices where
    /// two pieces of surface touch in one corner (bowties) are split first when loops cannot be traced.
    /// </summary>
    /// <summary>
    /// Vertices at the same position (the copies a split bowtie leaves: two fans touching in one point): each is moved
    /// a little (a few weld tolerances, a different distance per copy) towards the middle of its own fan. Otherwise the STL, which only has positions, joins
    /// them again and the fills around them share edges with four triangles (minion+peaky: 1300 such edges).
    /// </summary>
    private static void SeparateCopies(DMesh3 mesh, double tol)
    {
        var at = new Dictionary<(double, double, double), List<int>>();
        foreach (int v in mesh.VertexIndices())
        {
            var p = mesh.GetVertex(v);
            if (!at.TryGetValue((p.x, p.y, p.z), out var l)) at[(p.x, p.y, p.z)] = l = new List<int>(1);
            l.Add(v);
        }
        foreach (var group in at.Values.Where(l => l.Count > 1))
            for (int i = 0; i < group.Count; i++)
            {
                // each copy a different distance (2, 5, 8 ... tolerances): two fans on the same side would otherwise
                // move their copies side by side and keep them within the weld tolerance
                int v = group[i];
                var p = mesh.GetVertex(v);
                var c = Vector3d.Zero; int n = 0;
                foreach (int w in mesh.VtxVerticesItr(v)) { c += mesh.GetVertex(w); n++; }
                if (n == 0) continue;
                var d = c / n - p;
                if (d.Length > 0) mesh.SetVertex(v, p + d.Normalized * Math.Min((2 + 3 * i) * tol, 0.9 * d.Length));
            }
    }

    private static void FillHoles(DMesh3 mesh, MeshRepairStats stats, double tol)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            MeshBoundaryLoops loops;
            try { loops = new MeshBoundaryLoops(mesh); }
            catch (MeshBoundaryLoopsException) { loops = null!; }
            if (loops == null || loops.SawOpenSpans)
            {
                int before = mesh.VertexCount;
                new MeshEditor(mesh).DisconnectAllBowties(); // (returns the number of bowties left, not split)
                stats.BowtiesSplit += mesh.VertexCount - before;
                SeparateCopies(mesh, tol);
                try { loops = new MeshBoundaryLoops(mesh); }
                catch (MeshBoundaryLoopsException) { break; }
            }
            if (loops.Count == 0) break;
            int left = 0;
            foreach (var loop in loops.Loops.ToList())
            {
                int before = mesh.TriangleCount;
                bool ok = false;
                try { ok = new MinimalHoleFill(mesh, loop).Apply(); } catch (Exception e) when (e is not OutOfMemoryException) { }
                if (!ok)
                {
                    var simple = new SimpleHoleFiller(mesh, loop);
                    try { ok = simple.Fill(); } catch (Exception e) when (e is not OutOfMemoryException) { }
                }
                if (ok) { stats.HolesFilled++; stats.FillTriangles += mesh.TriangleCount - before; }
                else left++;
            }
            stats.HolesLeft = left;
            if (left == 0) break;
        }
    }
}
