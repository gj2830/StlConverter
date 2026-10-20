# Revit reader (.rvt)

Our own C# reader; rvt-rs (Apache 2.0) is a source of knowledge only, never a dependency (THIRD-PARTY-NOTICES.md).
Open points: GitHub issues of gj2830/StlConverter with "Revit" in the title.

## Layers, bottom up
- `CompoundFile`: MS-CFB (v3/v4, DIFAT, ministream). `RevitStreams`: pages of 65 249 bytes minus 353 checksum bytes
  (Partitions/*, Global/*, Formats/Latest); gzip header + raw DEFLATE without trailer.
- `RevitSchema`: the class grammar from Formats/Latest (as rvt-rs schema_registry); class tag = order from 12.
- `RevitPartitions`: segment/content markers, one gzip member per segment, records per channel (8-byte id, header).
- `RevitGraph`: a record decoded to objects purely through the schema; pointers carry a token (+ class tag).
- `RevitGeometry`: per element (channel 103, GElement) through GGroup/GFilter/GInstance to Geometry (faces with UV
  loops) and GPolyMesh. Feet → metres. `RevitLinks`: linked models (path, name, 4×4 in metres).

## Decisions (keep)
- Left out: …Symbol/…Sym, FilledRegion, RoomElem, LegendComponent; light-source geometry of fixtures (a Geometry next
  to a GImposter of a photometric light, and ImposterLight elements; `LightSources`); voids: elements whose class has
  `m_cutting` and it is on (`Voids`). Counted in the report, never silently.
- Group members ARE drawn: only symbols of instances the walk takes (`Walker.Taken`) are skipped as symbols.
- Curved faces: UV grid as fine as the curvature asks (angle step ≤ 12°, chord ≤ 2.5 mm), clipped per cell.
  Edges are sampled once per Edge object and shared by both faces (no T-junctions: the bodies close).
- Materials: `Face.m_renderStyleId`, else the category's `GStyle.m_materialElemId` (category inherited, -1 = inherit).
  Colour 0x00BBGGRR; `m_transparency` is a Single 0..1 (glass 1, frosted glass 0.55).
- Levels: `m_assocLevelId`, then `m_scheduleOnlyLevelId`, `m_levelId`, curve drivers, FamilyLevelRef; else by height.
  A family level ("Ref. Level") → the storey under the element's lowest point; a copy in a group → the real level.
- The class names compared here ("GComponentRef", "SWall") are Revit's own schema names from the file, not our keys.

## Test data
- testdata/test.rvt (a large sample project) and its linked models are NEVER committed (.gitignore); tests return
  early without them (build servers). The share of faces in closed bodies is ≥ 90 % (was 95 %, lowered on purpose:
  wall sweeps are saved without end caps).
