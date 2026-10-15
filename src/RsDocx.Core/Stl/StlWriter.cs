using System.Text;

namespace RsDocx.Core.Stl;

public static class StlWriter
{
    public static byte[] WriteBinary(IReadOnlyList<StlTriangle> tris, string header = "")
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var h = new byte[80];
        Encoding.ASCII.GetBytes(header, 0, Math.Min(header.Length, 80), h, 0);
        w.Write(h);
        w.Write((uint)tris.Count);
        foreach (var t in tris)
        {
            var n = (t.B - t.A).Cross(t.C - t.A).Normalized();
            foreach (var v in new[] { n, t.A, t.B, t.C }) { w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z); }
            w.Write((ushort)0);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Test shape: UV sphere (radius r, centre c) with nLon x nLat facets; when cutZ is given, the part below z = cutZ is
    /// removed and closed with a flat disc (a dome). Vertices lie exactly on the sphere (up to float precision).</summary>
    public static List<StlTriangle> UvSphere(Vec3 c, double r, int nLon, int nLat, double? cutZ = null)
    {
        double minLat = -Math.PI / 2;
        if (cutZ is double z0) minLat = Math.Asin(Math.Clamp((z0 - c.Z) / r, -1, 1));
        Vec3 P(int i, int j)
        {
            double lat = minLat + (Math.PI / 2 - minLat) * j / nLat, lon = 2 * Math.PI * i / nLon;
            return c + new Vec3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat)) * r;
        }
        var t = new List<StlTriangle>();
        for (int j = 0; j < nLat; j++)
            for (int i = 0; i < nLon; i++)
            {
                var a = P(i, j); var b = P(i + 1, j); var cc = P(i + 1, j + 1); var d = P(i, j + 1);
                if (j == nLat - 1) { t.Add(new(a, b, d)); continue; }               // north pole fan
                if (j == 0 && cutZ is null) { t.Add(new(a, cc, d)); continue; }     // south pole fan (a == b there)
                t.Add(new(a, b, cc)); t.Add(new(a, cc, d));
            }
        if (cutZ is double)
        {
            var centre = new Vec3(c.X, c.Y, P(0, 0).Z);
            for (int i = 0; i < nLon; i++) t.Add(new(centre, P(i + 1, 0), P(i, 0))); // flat bottom, normal -z
        }
        return t;
    }

    /// <summary>Test shape: the profile of <see cref="Brep.BrepShapes.Revolve"/> tessellated with nAround steps around the
    /// axis and arcs split into steps of at most arcStepDeg. Vertices lie exactly on the surfaces.</summary>
    public static List<StlTriangle> Revolve((double Y, double Z) start, IReadOnlyList<Brep.ProfileSegment> segs, int nAround, double arcStepDeg)
    {
        var prof = new List<(double r, double z)> { start };
        var cur = start;
        foreach (var s in segs)
        {
            if (s.ArcCentre is { } c)
            {
                double a0 = Math.Atan2(cur.Z - c.Z, cur.Y - c.Y), a1 = Math.Atan2(s.End.Z - c.Z, s.End.Y - c.Y);
                double rad = Math.Sqrt((cur.Y - c.Y) * (cur.Y - c.Y) + (cur.Z - c.Z) * (cur.Z - c.Z));
                double sweep = a1 - a0;
                if (s.Ccw) { while (sweep <= 1e-12) sweep += 2 * Math.PI; } else { while (sweep >= -1e-12) sweep -= 2 * Math.PI; }
                int k = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) * 180 / Math.PI / arcStepDeg));
                for (int j = 1; j < k; j++)
                {
                    double a = a0 + sweep * j / k;
                    prof.Add((c.Y + rad * Math.Cos(a), c.Z + rad * Math.Sin(a)));
                }
            }
            prof.Add(s.End);
            cur = s.End;
        }
        Vec3 Q(int j, int i)
        {
            double t = 2 * Math.PI * i / nAround;
            var (r, z) = prof[j];
            return new Vec3(r * Math.Cos(t), r * Math.Sin(t), z);
        }
        var tris = new List<StlTriangle>();
        for (int j = 0; j + 1 < prof.Count; j++)
            for (int i = 0; i < nAround; i++)
            {
                // around (+phi) x along the profile = outward normal
                Vec3 a = Q(j, i), b = Q(j, i + 1), c = Q(j + 1, i + 1), d = Q(j + 1, i);
                if (prof[j].r > 1e-12) tris.Add(new(a, b, c));
                if (prof[j + 1].r > 1e-12) tris.Add(new(a, c, d));
            }
        return tris;
    }

    /// <summary>Test shape: square frame (outer size x outer, square through-hole, given thickness), triangulated, z from 0.</summary>
    public static List<StlTriangle> Frame(double outer, double hole, double thickness)
    {
        double a = outer / 2, b = hole / 2;
        Vec3[] O(double z) => [new(-a, -a, z), new(a, -a, z), new(a, a, z), new(-a, a, z)];
        Vec3[] I(double z) => [new(-b, -b, z), new(b, -b, z), new(b, b, z), new(-b, b, z)];
        var (o0, o1, i0, i1) = (O(0), O(thickness), I(0), I(thickness));
        var t = new List<StlTriangle>();
        void Quad(Vec3 p, Vec3 q, Vec3 r, Vec3 s) { t.Add(new(p, q, r)); t.Add(new(p, r, s)); } // CCW seen from outside
        for (int k = 0; k < 4; k++)
        {
            int n = (k + 1) % 4;
            Quad(o1[k], o1[n], i1[n], i1[k]); // top ring, normal +z
            Quad(o0[n], o0[k], i0[k], i0[n]); // bottom ring, normal -z
            Quad(o0[k], o0[n], o1[n], o1[k]); // outer wall, normal outward
            Quad(i0[n], i0[k], i1[k], i1[n]); // hole wall, normal towards the hole axis
        }
        return t;
    }
}
