# Offscreen UI snapshots

Run from the repository root with .NET 10:

```sh
dotnet run --project tools/IL.UiSnapshots/IL.UiSnapshots.csproj
```

The tool renders the application's real styles, embedded fonts and views through
Avalonia.Headless 12.1.3 with Skia. It writes PNGs, `fonts.json` and
`snapshots.json` under `artifacts/ui-reference/csharp`.

```sh
dotnet run --project tools/IL.UiSnapshots/IL.UiSnapshots.csproj -- \
  --output artifacts/ui-reference/csharp-compact --width 900 --height 900

dotnet run --project tools/IL.UiSnapshots/IL.UiSnapshots.csproj -- \
  --output artifacts/ui-reference/csharp-dark --dark
```

The screenshots cover the student home and loaded lesson; teacher empty,
audio, transcription, grouping, question overview, cloze and completed states;
settings; the empty task dialog; and all six first-run pages. The outer shell
mirrors `MainWindow.axaml` and uses the production navigation-content method.
The actual first-run control is accessed through reflection for each render.

All settings, project, queue, lesson, progress and log stores use a disposable
temporary directory. A fixture audio player supplies loaded playback state.
The production `MainWindow` and `AppServices` are not instantiated. The
headless platform creates in-memory windows and does not control a desktop
application or browser.

Font diagnostics require the embedded Source Han Sans CN regular, medium and
bold faces, plus the original Fabric MDL2 Assets icon face, to resolve under
their declared family names and supply representative Chinese, Latin and icon
glyphs. Font resolution failures stop the run after writing `fonts.json`.

These images establish offscreen rendering and resource integration. They are
review artifacts for comparison with `artifacts/ui-reference/dart`, rather
than an assertion of Windows interaction or visual acceptance.

Rendering follows Avalonia's documented
[headless Skia capture workflow](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform#visual-regression-testing).
