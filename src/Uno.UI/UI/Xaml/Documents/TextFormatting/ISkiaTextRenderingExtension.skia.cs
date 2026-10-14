#nullable enable

using SkiaSharp;

namespace Microsoft.UI.Xaml.Documents.TextFormatting;

internal interface ISkiaTextRenderingExtension
{
	bool UsesLcd { get; }

	void Apply(SKFont font);

	SKFont GetRenderFont(SKFont font, SKMatrix matrix, SKPaint paint);
}
