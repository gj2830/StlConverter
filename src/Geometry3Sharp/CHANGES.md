# Eigen wijzigingen t.o.v. upstream geometry3Sharp

Bron: https://github.com/gradientspace/geometry3Sharp, commit ece336493111ffe372a4bfc7fee5026d4127dade (2026-01-09),
Boost Software License 1.0 (LICENSE).

- Projectbestanden vervangen door Geometry3Sharp.csproj (net10.0); appveyor.yml, .asmdef en Properties/ weggelaten.
- `mesh/Reducer.cs`: optie `CollapseToEndpoint` — een rand klapt samen op het eindpunt met de laagste quadric-fout, zodat
  alle overgebleven hoekpunten originele hoekpunten zijn (blijven exact op de oorspronkelijke vlakken/cilinders/bollen
  liggen, nodig voor de vormherkenning).
