#nullable enable

using SkiaSharp;

namespace Microsoft.UI.Xaml.Documents.TextFormatting;

internal interface ISkiaTextRenderingExtension
{
	bool UsesLcd { get; }

	void Apply(SKFont font);

	SKFont GetRenderFont(SKFont font, SKMatrix matrix, SKPaint paint);

	// Returns the paint to draw glyphs with; it may be a shared paint that only stays valid until the next call.
	SKPaint GetRenderPaint(SKPaint paint);
}
