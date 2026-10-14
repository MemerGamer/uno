# Linux text rendering

The X11 and Wayland hosts register `LinuxTextRenderingExtension` before creating application fonts. Its immutable startup snapshot reads the default fontconfig pattern through `libfontconfig.so.1`. It includes antialiasing, hinting/hintstyle, rgba, lcdfilter and autohint. It does not match individual font faces or sizes, or watch for configuration changes.

Missing fontconfig or missing/invalid properties fall back to antialiasing on, slight hinting and unknown pixel geometry (grayscale). `hinting=false` takes precedence over hintstyle. Fontconfig's medium hinting maps to Skia Normal. Subpixel glyph positioning and baseline snapping are preserved.

Startup overrides:

- `UNO_SKIA_TEXT_HINTING=none|slight|normal|full`
- `UNO_SKIA_TEXT_RGBA=none|rgb|bgr|vrgb|vbgr`

Invalid values keep the fontconfig preference. An rgba override still obeys the presentation safety gates and does not override `antialias=false`.

LCD geometry is supplied to the content surface where text rasterizes: the GPU retained layer or the software content surface. The recorded frame must have a square, opaque root background covering the frame, with no root clip or transform. The picture's transparent clear is followed by that root background; the host's initial clear alone is insufficient. Rotated, skewed, mirrored or perspective text visuals disable LCD for the entire frame, including cached child pictures. Geometry changes recreate the surface and redraw the entire frame.

X11 uses the configured physical order at integer rasterization scales, assuming direct pixel presentation. Wayland additionally requires entered outputs to agree with the configured order, use normal output transforms and have the same integer scale as the buffer. Unknown outputs, conflicting orders, mixed scales and fractional presentation use grayscale. Compositor-side effects or X11 display transformations cannot all be detected; `UNO_SKIA_TEXT_RGBA=none` is the escape hatch.

The [Skia scaler](https://github.com/google/skia/blob/chrome/m119/src/core/SkScalerContext.cpp) falls back to grayscale for unknown geometry. The raster backend does **not** universally disable LCD for translucent paint or rotated glyphs: native SkiaSharp 3.119.0 probes produce chromatic pixels in both cases. Uno explicitly uses grayscale fonts for non-opaque/effect paints and gates frame geometry for transforms. Ordinary [saveLayer surfaces](https://github.com/google/skia/blob/chrome/m119/src/core/SkCanvas.cpp) discard geometry unless LCD preservation is explicitly requested, so opacity/effect layers use grayscale.

`lcdfilter` is read and logged, but cannot be applied through SkiaSharp's managed API. [Skia's FreeType library](https://github.com/google/skia/blob/chrome/m119/src/ports/SkFontHost_FreeType.cpp) uses `FT_LCD_FILTER_DEFAULT`; other fontconfig filter choices require native SkiaSharp support. Full hinting may also be reduced to Normal by Skia when LCD is unavailable, and Skia adjusts hinting for rotated glyphs.

The settings and override tests live in `Uno.UI.Runtime.Skia.Wayland.Tests/Given_LinuxFontRendering.cs`; output safety tests are in `RenderLogicTests.cs`. Run the Wayland test project, then compare text and Fluent icons on X11 and Wayland with both GPU and software rendering at 100%, 125%, 150% and 200%. Include transparency/effects, rotation applied after an initial untransformed draw, resize, monitor moves and conflicting output orders. Pin the overrides and fontconfig configuration for before/after captures; confirm non-Linux hosts retain their existing font flags and unknown geometry.
