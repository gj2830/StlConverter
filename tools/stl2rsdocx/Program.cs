using System.Globalization;
using RsDocx.Core;
using RsDocx.Core.Stl;

// stl2rsdocx input.stl output.rsdocx|output.step [--merge-coplanar] [--scale 0.001] [--angle-tol deg] [--dist-tol mm] [--fit-um µm] [--join-um µm]

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var positional = new List<string>();
bool merge = false, cylinders = false, spheres = false, tori = false, cones = false, repair = false;
int simplify = 0;
string? repairedStl = null;
double scale = 0.001, angleTol = 0.01, distTolMm = 1e-4, weldMm = 1e-4;
double? fitUm = null, joinUm = null;
try
{
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--merge-coplanar": merge = true; break;
            case "--cylinders": cylinders = true; merge = true; break;
            case "--spheres": spheres = true; merge = true; break;
            case "--tori": tori = true; merge = true; break;
            case "--cones": cones = true; merge = true; break;
            case "--curved": cylinders = spheres = tori = cones = true; merge = true; break;
            case "--scale": scale = Num(args[++i]); break;
            case "--fit-um": fitUm = Num(args[++i]); break;
            case "--join-um": joinUm = Num(args[++i]); break;
            case "--angle-tol": angleTol = Num(args[++i]); break;
            case "--dist-tol": distTolMm = Num(args[++i]); break;
            case "--weld": weldMm = Num(args[++i]); break;
            case "--repair": repair = true; break;
            case "--simplify": simplify = int.Parse(args[++i], CultureInfo.InvariantCulture); repair = true; break;
            case "--repaired-stl": repairedStl = args[++i]; repair = true; break;
            case "-h" or "--help": Usage(); return 0;
            default:
                if (args[i].StartsWith("--")) throw new ArgumentException($"unknown option {args[i]}");
                positional.Add(args[i]);
                break;
        }
    }
    if (positional.Count != 2) { Usage(); return 2; }
}
catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or FormatException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    Usage();
    return 2;
}

try
{
    var stl = StlReader.Read(positional[0]);
    Console.WriteLine($"{positional[0]}: {stl.Format}, {stl.Triangles.Count} triangles, header '{stl.Header}'");
    if (repair)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        stl = MeshRepair.Repair(stl, new MeshRepairOptions { TargetTriangles = simplify, WeldTolerance = weldMm / 1000 / scale }, out var rs);
        Console.WriteLine($"repair ({sw.ElapsedMilliseconds} ms): {rs}");
        if (repairedStl != null) { File.WriteAllBytes(repairedStl, StlWriter.WriteBinary(stl.Triangles, "repaired by stl2rsdocx")); Console.WriteLine($"written {repairedStl}"); }
    }
    var opt = new MeshOptions
    {
        Scale = scale, MergeCoplanar = merge, AngleToleranceDeg = angleTol, DistanceTolerance = distTolMm / 1000,
        WeldTolerance = weldMm / 1000, RecognizeCylinders = cylinders, RecognizeSpheres = spheres, RecognizeTori = tori, RecognizeCones = cones,
    };
    // (as the app's "fit" and "join" sliders, in micrometres)
    if (fitUm is double fu) opt = opt with { CylinderFitTolerance = fu * 1e-6 };
    if (joinUm is double ju) opt = opt with { CylinderJoinTolerance = ju * 1e-6, AbsorbTolerance = ju * 1e-6 };
    var bodies = MeshConverter.ConvertToBrep(stl, opt, out var stats);
    Console.WriteLine(stats);
    foreach (var w in stats.Warnings) Console.WriteLine("warning: " + w);
    // (.step or .stp: STEP, for any CAD program; otherwise DesignSpark's .rsdocx)
    if (positional[1].EndsWith(".step", StringComparison.OrdinalIgnoreCase) || positional[1].EndsWith(".stp", StringComparison.OrdinalIgnoreCase))
        File.WriteAllText(positional[1], RsDocx.Core.Step.StepWriter.Write(new RsDocx.Core.Step.StepModel(
            bodies.Select((b, i) => new RsDocx.Core.Step.StepBody(b, $"Solid {i + 1}")).ToList(), []), Path.GetFileName(positional[1])));
    else RsDocxComposer.Compose(bodies).Save(positional[1]);
    Console.WriteLine($"written {positional[1]} ({new FileInfo(positional[1]).Length} bytes)");
    return 0;
}
catch (Exception e) when (e is StlFormatException or MeshException or IOException or InvalidOperationException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

static void Usage() => Console.Error.WriteLine("""
    usage: stl2rsdocx input.stl|input.3mf output.rsdocx|output.step [options]
      --merge-coplanar   merge coplanar adjacent triangles into one planar face
      --cylinders        also recognise cylinders (fillets, holes, bosses); implies --merge-coplanar
      --spheres          also recognise spheres; implies --merge-coplanar
      --tori             also recognise tori (round fillets around an axis); implies --merge-coplanar
      --cones            also recognise cones (drafted walls, countersinks); implies --merge-coplanar
      --curved           --cylinders, --spheres, --tori and --cones
      --scale <f>        STL units to metres (default 0.001 = STL in mm)
      --angle-tol <deg>  max normal deviation for merging (default 0.01)
      --dist-tol <mm>    max distance from face plane for merging (default 0.0001)
      --weld <mm>        vertices closer than this are merged (default 0.0001)
      --repair           clean the mesh first: duplicate/non-manifold triangles out, small loose pieces out, holes filled
      --simplify <n>     --repair and simplify to at most n triangles (vertices stay original vertices)
      --repaired-stl <f> --repair and also write the cleaned mesh as STL
    """);
