# The converter's view — desktop, browser, and inside PushPullMe

- One view (`ConverterView`, a UserControl) with a start project per system around it: `tools/StlConverter` (the
  desktop window `ConverterWindow`; PushPullMe opens it on the desktop) and `tools/StlConverter.Browser` (WebAssembly
  page). Inside PushPullMe's browser app the view is a layer over PushPullMe's view (PushPullMe's `BrowserHost`).
  Each gives it an `IConverterHost`: file dialogs (paths on the desktop, the in-memory folder in a browser), messages
  (dialog window / `ShowMessageOver`), `Done` (closing, or the result handed over), a ✕ of its own where there is no
  window (`HasCloseButton`). Platform code goes into a host, never into the view; this split is what lets the
  converter work on its own and embedded, not a change for PushPullMe.
- `App.Begin`: set by each Program, the desktop opens the window, the browser shows the view.
- Keys are caught on the TopLevel while the view is attached (over PushPullMe it comes and goes).
- `MeshView : GlView` (`src/GlKit`): its GL calls go through `IGl`; a new GL call goes into `IGl`, `SilkGl` and
  `WebGl`. In a browser it draws in Skia's WebGL context (see GlKit's `GlView`).
- Browser apps: `src/BrowserKit` (fonts: Inter + Noto fallbacks for symbols and emoji; files through the in-memory
  folder; `?open=name.stl` in the page's address opens that file from the server). Needs the `wasm-tools` workload;
  publish with `dotnet publish tools/StlConverter.Browser -c Debug -o output/web` and serve `output/web/wwwroot`.
  Not in the solution (CI has no wasm-tools). Single-threaded: recognising runs on the page's thread.
- Theme: `Theme.axaml`, included by the view itself (`avares://StlConverter.View/Theme.axaml`), so it looks the same
  in PushPullMe.
