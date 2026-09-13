# Voice-Translater — UI Branding & Design

Status: UI/branding redesign of the existing WPF desktop app. No translation, audio,
provider, or business-logic behavior was changed — see "Production isolation" below.

## Company branding

**AUTRAXIS SYSTEMS INC.** is the primary/secondary brand hierarchy used throughout:

- Primary: **AUTRAXIS**
- Secondary: **SYSTEMS INC.**
- Symbol: the supplied standalone blue/gray mark.

The supplied logo and symbol files are the **authoritative** assets — nothing in this
codebase recreates, redraws, recolors, or approximates them. See "Asset locations" below
for exactly where to place the two official files; until they're placed, every branded
surface falls back gracefully to a plain "AUTRAXIS SYSTEMS INC." text wordmark (no
placeholder logo was fabricated).

## App icon

`Branding/BrandAssets.cs` loads `Assets/Branding/autraxis-icon.png` at startup (if
present) and applies it as:

- The main window's `Icon` (taskbar + Alt-Tab while running).
- The About window's `Icon`.

The compiled `.exe`'s own icon (`<ApplicationIcon>` in `VTTranslate.App.csproj`) is
**not yet wired** — that requires a proper multi-resolution `.ico` file generated from
the source PNG, which doesn't exist in the repository yet. The csproj has a commented-out
`<ApplicationIcon>` line ready to uncomment once `Assets/Branding/app.ico` exists.

## Color system

Derived from the supplied mark's own two-tone blue arc over dark navy text — a UI color
system *inspired by* the brand, not a reproduction of the artwork. Defined in
`Themes/BrandTheme.xaml`:

| Token | Approx. hex | Use |
|---|---|---|
| `BrandBlueLightBrush` | `#1CA6F2` | Gradient start (primary buttons, accents) |
| `BrandBlueDeepBrush` | `#0B4DE8` | Gradient end, focus rings |
| `BrandNavyBrush` | `#0E1526` | Primary text, wordmark |
| `BrandNavySoftBrush` | `#3A4256` | Secondary text |
| `BrandGraySubtleBrush` | `#8B93A3` | Tertiary/hint text |
| `BrandBackgroundBrush` | `#F7F9FC` | Window background |
| `BrandSurfaceBrush` | `#FFFFFF` | Card surfaces |
| `BrandBorderBrush` | `#DDE3EC` | Card/control borders |
| `StatusGoodBrush` | `#1BA672` | Running/connected |
| `StatusWarnBrush` | `#C98A12` | Starting/reconnecting |
| `StatusErrorBrush` | `#D33B3B` | Error/muted |
| `StatusMutedBrush` | `#7A8496` | Idle/neutral |

**Color is never the only status signal** — every status pill and the mute button pair
color with text (and, for mute, an icon character) per the accessibility requirement.

## Typography

Segoe UI throughout (`BrandFont` resource), the Windows system default — chosen for
maximum readability and native look, not a custom brand typeface (none was supplied).
Section headers: 13px semibold. Field labels: 12px. Hints/diagnostics: 11px, muted color.

## Main UI structure

`MainWindow.xaml`:

- **Header** — logo (or text fallback) + "Voice-Translater" + a live status pill (color
  + dot + text, driven by the new `MainViewModel.StatusKind` property) + an "About" button.
- **Left column** (scrollable): Session card (start/stop, mute, the two always-on
  translation channels EN→DE / DE→EN, warnings/errors), Azure Speech Provider status
  card, Audio Devices card (four clearly-labeled, tooltipped device roles).
- **Right column**: Live Translation transcript (large, primary focus) above a compact,
  secondary Latency diagnostics strip (5 high-level numbers, not raw logs).

`AboutWindow.xaml` (new): logo/wordmark, "Voice-Translater", and the assembly version
(read from `Assembly.GetExecutingAssembly().GetName().Version` — a real, always-present
.NET value, never fabricated).

## Status states

`AppStatusKind` (new enum in `MainViewModel.cs`) is a **presentation-only** classification
computed from state the pipeline already exposes — `Status`, `IsMicrophoneMuted`, and
`ReconnectStatus` — never an invented connection state:

| StatusKind | Derived from |
|---|---|
| `Idle` | Not running, no other flag set |
| `Starting` | `Status == "Starting..."` |
| `Running` | Running, unmuted, not mid-reconnect |
| `Muted` | Running + `IsMicrophoneMuted` |
| `Reconnecting` | `ReconnectStatus` contains "Reconnecting" (the literal Azure provider reconnect-attempt message — its normal "Connected" message does NOT trigger this) |
| `Error` | `Status == "Error"` |
| `ConfigurationError` | `Status == "Configuration error"` |
| `Stopped` | `Status == "Stopped"` |

## Accessibility

- All status/mute states pair color with text (and an icon glyph for mute).
- Every device selector has both a visible descriptive sub-label and a `ToolTip`.
- Standard WPF `Button`/`ComboBox`/`ListBox` controls are used throughout (not custom
  hit-test-only shapes), preserving default keyboard navigation and focus visuals; the
  primary button additionally defines an explicit focus-visual style.
- Layout uses `Grid`/`ScrollViewer`/`UniformGrid`/`DockPanel` (no hard-coded positions),
  so text reflows and controls remain reachable at the window's `MinWidth`/`MinHeight`
  (820×560) rather than clipping.

## Animation

None was added. State changes (status pill color, mute button style) are instant
`DataTrigger` style swaps — no transition/storyboard animation was introduced, consistent
with "the application may run for hours during meetings" and the instruction to avoid
resource use from continuous animation.

## Asset locations

```
src/VTTranslate.App/Assets/Branding/
  README.md              — exact filenames expected + what happens if they're absent
  autraxis-icon.png       — NOT YET SUPPLIED (place the official standalone symbol here)
  autraxis-logo.png       — NOT YET SUPPLIED (place the official full logo here)
  app.ico                 — NOT YET GENERATED (multi-res icon, once autraxis-icon.png exists)
```

`src/VTTranslate.App/Branding/BrandAssets.cs` is the single place that loads these files;
`src/VTTranslate.App/Themes/BrandTheme.xaml` is the single place the color/typography
tokens are defined. Both are new files, isolated from the rest of the app.

## What was intentionally NOT done (and why)

- **The two supplied image files were not saved into the repository.** This session had
  no mechanism to extract the pixel data of images pasted into the chat and write them to
  disk — attempting to approximate or hand-redraw the logo instead would have directly
  violated "do not recreate, redraw, alter, recolor, distort, or reinterpret the supplied
  logo." Every branded surface therefore currently shows the text-only fallback. See the
  final report for exactly what to do to complete this.
- **`<ApplicationIcon>` was not wired into the `.csproj`** — it requires a real `.ico`
  file, which requires the source PNG, which hasn't been supplied yet (see above).
- **No direction "switch" was added** — the existing architecture always runs both
  translation directions simultaneously (two concurrent `DirectionPipeline` instances);
  there is no toggle to improve, so both directions are shown as parallel, always-on
  channels instead, per the instruction not to invent a mechanism that doesn't exist.
- **No Push-to-Talk control** — not implemented in the pipeline, so none was added.
- **No Settings screen reorganization** — the existing app has no separate
  settings/configuration screen to reorganize (all configuration is inline in the main
  window); nothing was fabricated to fill that section of the request.
