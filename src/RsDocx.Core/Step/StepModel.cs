using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace RsDocx.Core.Step;

/// <summary>A body as it goes into or comes out of a STEP file: its shape, its name ("Vaste vorm 1"), the colours of its
/// faces (ARGB, by face index; faces without one have none) and the group it is in (an index in
/// <see cref="StepModel.Groups"/>; null: none).</summary>
public sealed record StepBody(BrepBody Body, string Name, IReadOnlyDictionary<int, uint>? FaceColors = null, int? Group = null);

/// <summary>A group of bodies (a sub-assembly in the file): its name, and the group it is in (an index in
/// <see cref="StepModel.Groups"/>; null: at the top).</summary>
public sealed record StepGroup(string Name, int? Parent = null);

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
