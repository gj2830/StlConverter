# Third-party notices

## rvt-rs

`src/RsDocx.Core/Revit` reads Revit files with what [rvt-rs](https://github.com/DrunkOnJava/rvt-rs) found out about
their format: how the database streams are paged and compressed (`RevitStreams.cs`) and the grammar of the schema in
`Formats/Latest` (`RevitSchema.cs`, a C# port of its `formats::schema_classes`, changed: it keeps every field's name and
type). The compound-file reader (`CompoundFile.cs`) follows Microsoft's published [MS-CFB] specification.

    rvt-rs — open reader for Autodesk Revit files
    Copyright 2026 Griffin Long

    Licensed under the Apache License, Version 2.0 (see licenses/rvt-rs-LICENSE.txt).

From its NOTICE:

    "Autodesk" and "Revit" are registered trademarks of Autodesk, Inc.
    This project is not affiliated with, endorsed by, or sponsored by
    Autodesk. All references to "Autodesk" and "Revit" in this project's
    source, tests, documentation, and output are nominative fair use —
    they identify the file format this reader parses, and no more.

The same holds for this project: it is not affiliated with Autodesk, and uses no Autodesk code or SDK. Revit files used
in tests (`testdata/test.rvt`, one of Autodesk's sample projects) are not part of this repository.

## Noto fonts

`src/BrowserKit/Fonts`: Noto Emoji, Noto Sans Math, Noto Sans Symbols and Noto Sans Symbols 2 (from
[google/fonts](https://github.com/google/fonts)), the symbols and emoji of the browser apps (a browser has no fonts of
its own). SIL Open Font License 1.1 (see licenses/noto-fonts-OFL.txt).

    Noto Emoji: Copyright 2013 Google LLC
    Noto Sans Math, Noto Sans Symbols, Noto Sans Symbols 2: Copyright 2022 The Noto Project Authors

## geometry3Sharp

`src/Geometry3Sharp`: [geometry3Sharp](https://github.com/gradientspace/geometry3Sharp), Boost Software License (see the
LICENSE in that folder).

[MS-CFB]: https://learn.microsoft.com/openspecs/windows_protocols/ms-cfb
