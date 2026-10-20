# Parasolid XT and the .rsdocx package (DesignSpark Mechanical)

## Package (`Package/RsDocxPackage`)
- An .rsdocx is a zip: document.xml (urn:nom), renderlist.xml, and per PartDef with bodies a `partNbodies.x_b`
  (Parasolid XT, binary) linked by the relationship `partBodyGeometry#<guid>:<number>`.
- document.xml ids ↔ XT body names: see `XtDesignSpark`; the `EncryptedData` CipherValue is unknown and copied as is.
- Components: the design PartDef uses others through `ComponentDef` (source = <document guid>:<PartDef number>,
  trans 4×4 per row, shift in the last column); a pattern PartDef holds `ComponentPatternMemberDef`s. Bodies are in
  their PartDef's own coordinates. renderlist.xml: an Item per place (Path = ComponentDef ids from the design).
- Colours: `FaceColorDef` in the body's PartDef. A see-through colour is written "A, R, G, B" (as .NET's
  ColorConverter, which DesignSpark uses; an opaque one as before). Whether DesignSpark shows the alpha: issue.

## XT (`XtReader`, `XtWriter`, `XtSchema`, `XtValidator`)
- Header and embedded schema as in kubus.rsdocx; a written file reads back byte-identical (test).
- Tolerance: everything must hold within 1e-8 m (Parasolid's check). Intersection curves are sampled every 0.25 mm
  (at least 97 points) to stay inside it.
- Point loops: a LOOP with one FIN without an edge (a pole of a revolved spline) → `BrepFace.Points`.
- `NurbsFormat` keeps how a NURBS surface was stored (periodic/closed, knot types, form, rational, ends); null = our
  fixed defaults.
- The validator (`XtValidator`, `XtBrepBuilder.TopologyError`) is strict on purpose: a file that passes opens in
  DesignSpark. A B-curve is only measured at its ends against the curve itself (speed); plane distance for all points.

## Brep (`Brep/`)
- `BrepBody` (faces, loops, edges, vertices) is the neutral model behind .rsdocx, STEP and STL; surfaces plane,
  cylinder, cone, sphere, torus, NURBS; curves line, circle, ellipse, B-spline.
- `BrepNurbsSurface.Parameters`: Newton from the 4 best grid points (at a pole one start hangs); `Near`: Newton only.
