# STEP (ISO 10303-21), PushPullMe's main format

- `StepFile`: Part 21 parser (entities, complex entities, strings with '' and \X2\, enums, references, lists, typed
  values). `StepModel`: `StepBody` (BrepBody + name + colours per face + group), `StepCurve` (loose line/arc),
  `StepGroup(Name, Parent)` with `Shared` + `StepPlace` for components.
- Writer: AP214 "automotive design", millimetres, ONE entity per line, numbers without needless digits, so the file
  stays readable and a second write gives the same text (test).
  - Solids: MANIFOLD_SOLID_BREP/CLOSED_SHELL; loose faces: SHELL_BASED_SURFACE_MODEL/OPEN_SHELL per piece; loose
    lines: GEOMETRIC_CURVE_SET of TRIMMED_CURVEs; colours: STYLED_ITEM.
  - Two or more bodies: an assembly (PRODUCT named after the file) with a PRODUCT per body carrying its name, placed by
    NAUO + ITEM_DEFINED_TRANSFORMATION as FreeCAD writes it, so FreeCAD shows our names. Groups = sub-assemblies.
  - A shared component: only the first instance's bodies are written, the others are extra NAUOs with their place.
  - A ring: one vertex where its curve starts; a point loop = VERTEX_LOOP.
- Reader: units (mm, cm, m, inch via CONVERSION_BASED_UNIT, degrees) → metres; BREP_WITH_VOIDS, ORIENTED_*_SHELL;
  SURFACE_CURVE/SEAM_CURVE/INTERSECTION_CURVE/TRIMMED_CURVE as edge curves; a B-spline used in part is resampled every
  0.25 mm; colours on a face or the whole body.
  - Assemblies (FreeCAD/OpenCASCADE): the chain of placements to the top, T = to ∘ from⁻¹; a part used twice comes
    twice; a sub-assembly used twice → groups sharing a component; the path is per REPRESENTATION_RELATIONSHIP.
  - OpenCASCADE writes a whole cylinder/cone/ring as one loop over a seam (the same edge there and back): a closed
    edge touching a seam keeps its vertex.
  - What cannot be read (OFFSET_SURFACE, FACETED_BREP …) is reported by its name, never skipped silently.
- Checked with FreeCAD (valid, same volumes). Known slow: a 260 MB file (see the issues).
