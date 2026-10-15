using RsDocx.Core.Stl;

namespace RsDocx.Core.Brep;

/// <summary>Curve of an edge. The edge runs from its start to its end vertex in the curve's parameter direction.</summary>
public abstract record BrepCurve;
/// <summary>Straight line; direction is computed from the edge's vertices.</summary>
public sealed record BrepLine : BrepCurve;
/// <summary>Circle (arc): runs counter-clockwise around <see cref="Normal"/>, starting angle measured from <see cref="XAxis"/>.</summary>
public sealed record BrepCircle(Vec3 Centre, Vec3 Normal, Vec3 XAxis, double Radius) : BrepCurve;

/// <summary>Ellipse (arc): runs counter-clockwise around <see cref="Normal"/>; <see cref="XAxis"/> = major axis.</summary>
public sealed record BrepEllipse(Vec3 Centre, Vec3 Normal, Vec3 XAxis, double MajorRadius, double MinorRadius) : BrepCurve;

public abstract record BrepSurface;
public sealed record BrepPlane(Vec3 Origin, Vec3 Normal, Vec3 XAxis) : BrepSurface;
/// <summary>Cylinder; its natural normal points away from the axis.</summary>
public sealed record BrepCylinder(Vec3 Origin, Vec3 Axis, Vec3 XAxis, double Radius) : BrepSurface;
/// <summary>
/// Cone (as SpaceClaim writes CONE, schaaltje.rsdocx): <see cref="Radius"/> at <see cref="Origin"/>, growing along
/// <see cref="Axis"/> by tan(<see cref="HalfAngle"/>) per unit length (0 &lt; HalfAngle &lt; π/2); its natural normal points
/// away from the axis (and back along it).
/// </summary>
public sealed record BrepCone(Vec3 Origin, Vec3 Axis, Vec3 XAxis, double Radius, double HalfAngle) : BrepSurface;
/// <summary>Sphere; its natural normal points away from the centre. Axis/XAxis only define the parametrisation.</summary>
public sealed record BrepSphere(Vec3 Centre, double Radius, Vec3 Axis, Vec3 XAxis) : BrepSurface;
/// <summary>Ring torus (MajorRadius &gt; MinorRadius); its natural normal points away from the tube's centre circle.</summary>
public sealed record BrepTorus(Vec3 Centre, Vec3 Axis, Vec3 XAxis, double MajorRadius, double MinorRadius) : BrepSurface;

/// <summary>Edge from Start to End vertex (indices into <see cref="BrepBody.Vertices"/>; both -1 for a closed ring edge).</summary>
public sealed record BrepEdge(int Start, int End, BrepCurve Curve);

/// <summary>Use of an edge in a loop; Forward = traversed from Start to End.</summary>
public readonly record struct BrepCoedge(int Edge, bool Forward);

/// <summary>
/// Face on a surface. Reversed = the face normal is opposite to the surface's natural normal (e.g. a hollow cylinder).
/// Loops run counter-clockwise around the face normal (outer) or clockwise (holes).
/// </summary>
public sealed record BrepFace(BrepSurface Surface, bool Reversed, IReadOnlyList<IReadOnlyList<BrepCoedge>> Loops);

public sealed class BrepBody
{
    public required IReadOnlyList<Vec3> Vertices { get; init; }
    public required IReadOnlyList<BrepEdge> Edges { get; init; }
    public required IReadOnlyList<BrepFace> Faces { get; init; }
}
