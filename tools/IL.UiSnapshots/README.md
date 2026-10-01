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
settings; empty and loaded task dialogs; file information; success and error notifications; and all six first-run pages.

The harness runs the dispatcher event loop and checks that dialog smoke layers
cover the entire owner, content fits the inner surface, and notifications survive
page changes while expiring after 5 seconds (success) and 15 seconds (error). The outer shell
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


Performance and motion probes:

```sh
dotnet run --project tools/IL.UiSnapshots -- --performance --output artifacts/motion/after
dotnet run --project tools/IL.UiSnapshots -- --motion --output artifacts/motion/animation
dotnet run --project tools/IL.UiSnapshots -- --motion --dark --output artifacts/motion/animation-dark
```

The performance fixture contains 250 cues and 50 library entries. It measures
synchronous UI construction and allocations, checks bounded transcript realization
after scrolling, and checks realization after following a distant cue. The library
batch timing measures collection-change callbacks; deferred list construction runs
after that measurement. Results are written to `performance.json`.

The motion probe renders the production page transition and Windows welcome-window
factory on the headless platform. It checks intermediate opacity and translation,
intro progress, completion, consent gating, cached back-navigation state and reduced
motion. `motion.json` records the results. A 16 ms headless render tick drives the
animation clock while the dispatcher event loop runs.
