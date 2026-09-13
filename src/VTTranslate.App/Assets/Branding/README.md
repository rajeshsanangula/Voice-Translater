# AUTRAXIS SYSTEMS INC. brand assets

The two officially supplied asset files are in place:

- `autraxis-icon.png` (532×512, RGBA) — the standalone blue/gray symbol. Used for the
  window/taskbar icon at runtime (`Branding/BrandAssets.cs`) and as the source for `app.ico`.
- `autraxis-logo.png` (2048×664, RGBA) — the full logo (symbol + "AUTRAXIS" /
  "SYSTEMS INC." wordmark). Used in the main window header and the About window.
- `app.ico` — a 7-frame (16/24/32/48/64/128/256px) multi-resolution icon generated
  losslessly from `autraxis-icon.png` (resized + centered on a transparent square canvas
  with high-quality bicubic resampling only — never redrawn, recolored, or distorted; the
  532×512 source's exact aspect ratio is preserved at every size). Wired into
  `VTTranslate.App.csproj`'s `<ApplicationIcon>`, so the compiled `.exe` itself carries the
  AUTRAXIS icon in Explorer, the taskbar, and Alt-Tab.

**Do not recreate, redraw, recolor, or otherwise alter the supplied files** — copy the
originals in as-is (PNG, ideally with transparency, at least 512×512 for the icon and a
reasonably wide flat resolution for the logo so it stays crisp when scaled down for the header).

## What happens automatically

`BrandAssets.cs` checks for `autraxis-icon.png`/`autraxis-logo.png` at startup:
- If found (now the case), they are loaded and used for the window icon, taskbar icon,
  main-window header branding, and the About window.
- If NOT found, the app falls back to a plain "AUTRAXIS SYSTEMS INC." text wordmark and
  the default Windows application icon — it still builds and runs correctly either way.

This graceful fallback is kept intentionally (rather than removed now that the assets
exist) so the app degrades safely if these files are ever deleted or fail to copy.
