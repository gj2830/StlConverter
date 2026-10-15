# StlConverter

Leest en schrijft DesignSpark Mechanical-bestanden (`.rsdocx`, met Parasolid-geometrie) en zet STL- en 3MF-modellen om
naar exacte vormen: platte vlakken, cilinders en kegels worden herkend en als echte vlakken weggeschreven.

- `src/RsDocx.Core`: het lezen en schrijven van `.rsdocx` en de omzetting van driehoeksnetten.
- `src/Geometry3Sharp`: [geometry3Sharp](https://github.com/gradientspace/geometry3Sharp) (Boost-licentie, zie de
  LICENSE in die map), gebruikt voor het repareren van netten.
- `tools/RsConverterUI`: een venster om bestanden om te zetten; `tools/stl2rsdocx`, `rsdump`, `rsgen`: hulpprogramma's.

## Bouwen

Nodig: Windows (voor de UI) en de .NET 10 SDK.

```
dotnet build StlConverter.slnx -c Release
```

## Tests

```
dotnet test tests/RsDocx.Tests
```

De meeste tests gebruiken testbestanden (`testdata/`) die niet in deze repository staan; zonder die bestanden slagen
alleen de tests die hun eigen vormen maken.

## Licentie

MIT, zie [LICENSE](LICENSE); `src/Geometry3Sharp` heeft zijn eigen Boost-licentie.
