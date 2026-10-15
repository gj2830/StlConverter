using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace StlConverter;

/// <summary>
/// What the converter hands over to the program that opened it (PushPullMe), instead of saving: the bodies (in metres;
/// the recognised ones first, then extra ones such as threads), which triangle of the mesh became which face of them
/// (for showing curved faces; null when unknown), the mesh as it was shown (cleaned or not), and a note.
/// </summary>
public sealed record ConverterResult(string Path, IReadOnlyList<BrepBody> Bodies, MeshLabels? Labels, StlFile Mesh, string Note);
