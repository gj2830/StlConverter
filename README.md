# StlConverter

[![Build and test](https://github.com/gj2830/StlConverter/actions/workflows/build.yml/badge.svg)](https://github.com/gj2830/StlConverter/actions/workflows/build.yml)

*[Nederlands hieronder](#nederlands)*

Reads and writes STEP (AP214) and DesignSpark Mechanical files (`.rsdocx`, with Parasolid geometry) and converts STL and 3MF meshes into
exact geometry: planes, cylinders and cones are recognised and written as true surfaces. It is the file engine of
[PushPullMe](https://github.com/gj2830/PushPullMe).

- **Solids** with planes, cylinders, cones, spheres, tori and NURBS faces, colours per face, several parts per file.
- **Faces without thickness** (DesignSpark's surfaces, "Oppervlak"): written as Parasolid sheet bodies, shown
  transparent in the structure tree as in DesignSpark, and read back.
- Every file it writes is checked first (topology, Euler, orientation), so DesignSpark opens it.

## Contents

- `src/RsDocx.Core`: reading and writing `.rsdocx` and STEP, and converting triangle meshes.
- `src/Geometry3Sharp`: [geometry3Sharp](https://github.com/gradientspace/geometry3Sharp) (Boost licence, see the
  LICENSE in that folder), used to repair meshes.
- `tools/StlConverter`: a window to convert files (English and Dutch, 🌐 button), for Windows, Linux and macOS
  (Avalonia, OpenGL).
- `tools/stl2rsdocx`, `rsdump`, `rsgen`: command-line helpers.

## Download

[Releases](https://github.com/gj2830/StlConverter/releases) has a ready-to-run package per system; .NET does not need
to be installed separately. Each has the window and `stl2rsdocx` (`stl2rsdocx model.stl model.rsdocx --curved` on the
command line).

- **Windows** (64-bit): unpack the `win-x64` zip and start `StlConverter.exe`.
- **Linux** (64-bit): unpack the `linux-x64` tar.gz and start `StlConverter/StlConverter` (it needs OpenGL).
- **macOS**: unpack the zip (`osx-arm64` for Apple silicon, `osx-x64` for Intel) and move `Mesh to Solid.app` to your
  Applications. It is not signed by Apple: the first time, right-click it and choose **Open**.

## Build and test

You need the .NET 10 SDK, on Windows, Linux or macOS.

```
dotnet run --project tools/StlConverter -c Release
dotnet test tests/RsDocx.Tests
```

The tests use the models in `testdata/`.

## Nederlands

Leest en schrijft STEP (AP214) en DesignSpark Mechanical-bestanden (`.rsdocx`, met Parasolid-geometrie) en zet STL- en 3MF-modellen om
naar exacte vormen: platte vlakken, cilinders en kegels worden herkend en als echte vlakken weggeschreven. Vaste vormen
(ook met bollen, ringen en vrije vormen, kleuren per vlak, meerdere delen) en losse vlakken zonder dikte ("Oppervlak"
in DesignSpark) gaan heen en terug; elk bestand wordt eerst gecontroleerd zodat DesignSpark het opent.

**Downloaden:** onder [Releases](https://github.com/gj2830/StlConverter/releases) staat een pakket per systeem: voor
Windows een zip (`StlConverter.exe` starten), voor Linux een tar.gz (`StlConverter/StlConverter`), voor macOS een zip
met `Mesh to Solid.app` (niet door Apple ondertekend: de eerste keer rechtsklikken en **Open** kiezen); `stl2rsdocx`
zit er steeds bij voor de opdrachtregel. **Bouwen en testen:** de .NET 10 SDK op Windows, Linux of macOS, zie hierboven.

## License / Licentie

[PolyForm Noncommercial 1.0.0](LICENSE): free to use, change and share for any noncommercial purpose, and
contributions are welcome; using it in a commercial product is not allowed. Earlier versions were released under MIT.
`src/Geometry3Sharp` keeps its own Boost licence.

Nederlands: vrij te gebruiken, aan te passen en te delen voor niet-commerciële doelen, en meewerken is welkom; gebruik in
een commercieel product mag niet. Eerdere versies zijn onder MIT uitgebracht. `src/Geometry3Sharp` houdt zijn eigen
Boost-licentie.
