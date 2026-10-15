namespace RsDocx.Core.Stl;

public enum FaceKind { Plane, Facet, Cylinder, Sphere, Torus, Cone }

/// <summary>One face of a converted body as seen from the mesh: its kind and size (metres).</summary>
public sealed record FaceInfo(FaceKind Kind, double Radius = 0, double MinorRadius = 0)
{
    /// <summary>Index of the user's group (shape hint) this face came from, or -1.</summary>
    public int Group { get; init; } = -1;
    /// <summary>For a group: largest distance of its vertices from the fitted surface (metres); they are moved onto it.</summary>
    public double Deviation { get; init; }
    /// <summary>Exact surface: plane point / cylinder axis point / sphere or torus centre (metres).</summary>
    public Vec3 Origin { get; init; }
    /// <summary>Exact surface: plane normal / cylinder or torus axis.</summary>
    public Vec3 Axis { get; init; }
    /// <summary>Hollow cylinder/sphere/torus (material outside).</summary>
    public bool Hollow { get; init; }

    /// <summary>Distance (metres) from a point to the exact surface; infinite for a facet.</summary>
    public double SurfaceDistance(Vec3 p)
    {
        switch (Kind)
        {
            case FaceKind.Plane: return Math.Abs(Axis.Dot(p - Origin));
            case FaceKind.Sphere: return Math.Abs((p - Origin).Length - Radius);
            case FaceKind.Cylinder:
                var w = p - Origin; w -= Axis * w.Dot(Axis);
                return Math.Abs(w.Length - Radius);
            case FaceKind.Torus: return Math.Abs(new Torus(Origin, Axis, Radius, MinorRadius).Distance(p, out _));
            case FaceKind.Cone: return Math.Abs(new Cone(Origin, Axis, Radius, MinorRadius).Distance(p, out _));
            default: return double.PositiveInfinity;
        }
    }
}

/// <summary>A group of input triangles (indices into <see cref="StlFile.Triangles"/>) that must become one face of the given
/// kind; <see cref="FaceKind.Facet"/> means "automatic" (the simplest of plane, cylinder, sphere, torus that fits).</summary>
/// <param name="Soft">A face kept from an earlier result: held as it is, but it may give way when it conflicts with the
/// user's own (hard) groups or its neighbours.</param>
/// <param name="Surface">The exact surface to use (from an earlier result) instead of fitting one.</param>
/// <param name="Force">The user forces the group into its kind ("make it flat"): the fitted surface is used however far the
/// triangles are off it (normally at most <see cref="MeshOptions.MaxHintDeviation"/>), and their vertices, the rim
/// included, are moved onto it; neighbouring faces follow.</param>
public sealed record ShapeHint(IReadOnlyList<int> SourceTriangles, FaceKind Kind, bool Soft = false, FaceInfo? Surface = null, bool Force = false);

/// <summary>A triangle of the prepared (welded, oriented) mesh with the face it ended up in.</summary>
/// <param name="Source">Index of the triangle in the input file (<see cref="StlFile.Triangles"/>).</param>
/// <param name="Body">Index of the body (shell).</param>
/// <param name="Face">Index of the face in that body.</param>
public readonly record struct LabeledTriangle(int A, int B, int C, int Source, int Body, int Face);

/// <summary>Which face every mesh triangle became: for previews (colour by surface type) and for selecting groups.</summary>
public sealed class MeshLabels
{
    /// <summary>Welded vertices, in metres.</summary>
    public required IReadOnlyList<Vec3> Vertices { get; init; }
    public required IReadOnlyList<LabeledTriangle> Triangles { get; init; }
    /// <summary>Per body, per face.</summary>
    public required IReadOnlyList<IReadOnlyList<FaceInfo>> Faces { get; init; }

    public FaceInfo FaceOf(LabeledTriangle t) => Faces[t.Body][t.Face];
}
