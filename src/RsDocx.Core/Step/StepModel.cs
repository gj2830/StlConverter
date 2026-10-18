using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace RsDocx.Core.Step;

/// <summary>A body as it goes into or comes out of a STEP file: its shape, its name ("Vaste vorm 1") and the colours of
/// its faces (ARGB, by face index; faces without one have none).</summary>
public sealed record StepBody(BrepBody Body, string Name, IReadOnlyDictionary<int, uint>? FaceColors = null);

/// <summary>A loose line or arc (not part of a body), from <see cref="Start"/> to <see cref="End"/>; an arc runs
/// counter-clockwise round <see cref="Arc"/>'s normal.</summary>
public sealed record StepCurve(Vec3 Start, Vec3 End, BrepCircle? Arc = null);

/// <summary>What a STEP file holds for us: bodies (solids and faces without thickness) and loose curves. Lengths in
/// metres, as everywhere in the library.</summary>
public sealed record StepModel(IReadOnlyList<StepBody> Bodies, IReadOnlyList<StepCurve> Curves);

/// <summary>Why a STEP file cannot be read (shown to the user as it is).</summary>
public sealed class StepException(string message) : Exception(message);
