using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace RsDocx.Core.Step;

/// <summary>A body as it goes into or comes out of a STEP file: its shape, its name ("Vaste vorm 1"), the colours of its
/// faces (ARGB, by face index; faces without one have none) and the group it is in (an index in
/// <see cref="StepModel.Groups"/>; null: none).</summary>
public sealed record StepBody(BrepBody Body, string Name, IReadOnlyDictionary<int, uint>? FaceColors = null, int? Group = null);

/// <summary>A group of bodies (a sub-assembly in the file): its name, and the group it is in (an index in
/// <see cref="StepModel.Groups"/>; null: at the top). Groups with the same <paramref name="Shared"/> are one part placed
/// more than once (a component): each stands where its <paramref name="Place"/> puts the part's own coordinates. The file
/// holds the shape once, so their bodies must be the same shape, each in its own place; the writer takes those of the
/// first.</summary>
public sealed record StepGroup(string Name, int? Parent = null, int? Shared = null, StepPlace? Place = null);

/// <summary>A turn and a move: a point <c>p</c> of a part's own coordinates goes to <see cref="Origin"/> + p.X
/// <see cref="X"/> + p.Y <see cref="Y"/> + p.Z <see cref="Z"/> (three unit directions square to each other, right-handed).</summary>
public readonly record struct StepPlace(Vec3 Origin, Vec3 X, Vec3 Y, Vec3 Z)
{
    public static readonly StepPlace Identity = new(new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));
}

/// <summary>A loose line or arc (not part of a body), from <see cref="Start"/> to <see cref="End"/>; an arc runs
/// counter-clockwise round <see cref="Arc"/>'s normal.</summary>
public sealed record StepCurve(Vec3 Start, Vec3 End, BrepCircle? Arc = null);

/// <summary>What a STEP file holds for us: bodies (solids and faces without thickness), loose curves and the groups the
/// bodies are in (each group before the groups in it). Lengths in metres, as everywhere in the library.</summary>
public sealed record StepModel(IReadOnlyList<StepBody> Bodies, IReadOnlyList<StepCurve> Curves, IReadOnlyList<StepGroup>? Groups = null)
{
    public IReadOnlyList<StepGroup> Groups { get; init; } = Groups ?? [];
}

/// <summary>Why a STEP file cannot be read (shown to the user as it is).</summary>
public sealed class StepException(string message) : Exception(message);
