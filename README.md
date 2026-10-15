# StlConverter

[![Bouwen en testen](https://github.com/gj2830/StlConverter/actions/workflows/build.yml/badge.svg)](https://github.com/gj2830/StlConverter/actions/workflows/build.yml)

Leest en schrijft DesignSpark Mechanical-bestanden (`.rsdocx`, met Parasolid-geometrie) en zet STL- en 3MF-modellen om
naar exacte vormen: platte vlakken, cilinders en kegels worden herkend en als echte vlakken weggeschreven.

- `src/RsDocx.Core`: het lezen en schrijven van `.rsdocx` en de omzetting van driehoeksnetten.
- `src/Geometry3Sharp`: [geometry3Sharp](https://github.com/gradientspace/geometry3Sharp) (Boost-licentie, zie de
  LICENSE in die map), gebruikt voor het repareren van netten.
- `tools/StlConverter`: een venster om bestanden om te zetten (Nederlands en Engels, 🌐-knop); `tools/stl2rsdocx`, `rsdump`, `rsgen`: hulpprogramma's.

## Downloaden

Onder [Releases](https://github.com/gj2830/StlConverter/releases) staat een zip voor Windows (64-bit). Uitpakken en
`StlConverter.exe` starten voor het venster, of `stl2rsdocx.exe model.stl model.rsdocx --curved` op de opdrachtregel;
.NET hoeft niet apart geïnstalleerd te zijn.

## Bouwen

Nodig: Windows (voor de UI) en de .NET 10 SDK.

```
dotnet build StlConverter.slnx -c Release
```

## Tests

```
dotnet test tests/RsDocx.Tests
```

De tests gebruiken de modellen in `testdata/`.

## In English

StlConverter reads and writes DesignSpark Mechanical files (`.rsdocx`, with Parasolid geometry) and converts STL and
3MF meshes into exact geometry: planes, cylinders and cones are recognised and written as true surfaces. It is the
file engine of [PushPullMe](https://github.com/gj2830/PushPullMe). Download a ready-to-run Windows build from
[Releases](https://github.com/gj2830/StlConverter/releases) (`StlConverter.exe` for the window, `stl2rsdocx.exe` on the
command line), or build it with the .NET 10 SDK as shown above.

## Licentie / License

MIT, zie [LICENSE](LICENSE); `src/Geometry3Sharp` heeft zijn eigen Boost-licentie (its own Boost license).
