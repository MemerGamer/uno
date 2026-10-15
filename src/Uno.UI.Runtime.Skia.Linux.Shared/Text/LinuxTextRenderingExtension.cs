#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Documents.TextFormatting;
using SkiaSharp;
using Tmds.DBus.Protocol;
using Uno.Foundation.Logging;
using Uno.WinUI.Runtime.Skia.Linux.DBus;

namespace Uno.WinUI.Runtime.Skia.Linux;

internal sealed class LinuxTextRenderingExtension : ISkiaTextRenderingExtension
{
	internal static LinuxTextRenderingExtension Instance { get; } = new();

	private const int MaxCachedShaders = 64;

	private readonly ConditionalWeakTable<SKFont, SKFont> _grayscaleFonts = new();
	private readonly Dictionary<uint, SKShader> _colorShaders = new();
	private readonly SKPaint _renderPaint = new();
	private volatile LinuxTextGeometry _desktopGeometry = LinuxTextGeometry.Unknown;

	internal LinuxFontRenderingSettings Settings { get; }
	internal LinuxTextGeometry ConfiguredGeometry => Settings.GetConfiguredGeometry(_desktopGeometry);
	internal SKPixelGeometry PreferredGeometry => Settings.GetPreferredGeometry(_desktopGeometry);

	internal event EventHandler? DesktopGeometryChanged;

	public bool UsesLcd => Settings.Edging == SKFontEdging.SubpixelAntialias;

	private LinuxTextRenderingExtension()
		: this(FontConfig.ReadDefault().WithOverrides(
			Environment.GetEnvironmentVariable("UNO_SKIA_TEXT_HINTING"), Environment.GetEnvironmentVariable("UNO_SKIA_TEXT_RGBA"),
			Environment.GetEnvironmentVariable("UNO_SKIA_TEXT_GAMMA"), Environment.GetEnvironmentVariable("UNO_SKIA_TEXT_LCD")))
	{
		if (ConfiguredGeometry == LinuxTextGeometry.Unknown)
		{
			if (KdeTextGeometry.IsDesktop("KDE"))
			{
				SetDesktopGeometry(KdeTextGeometry.Read());
			}
			if (ConfiguredGeometry == LinuxTextGeometry.Unknown && KdeTextGeometry.IsDesktop("GNOME"))
			{
				_ = ReadDesktopGeometryAsync();
			}
		}
	}

	internal LinuxTextRenderingExtension(LinuxFontRenderingSettings settings)
	{
		Settings = settings;
		LogSettings();
	}

	private void LogSettings()
	{
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			var configured = ConfiguredGeometry;
			// SkiaSharp has no LCD filter setter: Skia's own FreeType library uses FT_LCD_FILTER_DEFAULT.
			this.Log().Debug($"Linux text: fontconfig={Settings.HasFontConfig}, antialias={Settings.Antialias}, hinting={Settings.Hinting}, lcd={Settings.LcdMode}, geometry={configured.PixelGeometry}, source={configured.Source}, explicit none={configured.ExplicitNone}, autohint={Settings.ForceAutoHinting}, linear coverage={Settings.LinearCoverage}, fontconfig lcdfilter={Settings.LcdFilter} (Skia uses default).");
		}
	}

	private void SetDesktopGeometry(LinuxTextGeometry geometry)
	{
		if (_desktopGeometry != geometry)
		{
			_desktopGeometry = geometry;
			LogSettings();
			DesktopGeometryChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	private async Task ReadDesktopGeometryAsync()
	{
		try
		{
			if (DBusAddress.Session is not { } address)
			{
				return;
			}
			using var connection = new DBusConnection(address);
			await connection.ConnectAsync().ConfigureAwait(false);
			var settings = new DBusService(connection, "org.freedesktop.portal.Desktop").CreateSettings("/org/freedesktop/portal/desktop");
			var antialias = (await settings.ReadOneAsync("org.gnome.desktop.interface", "font-antialiasing").ConfigureAwait(false)).GetString();
			if (antialias is "grayscale" or "none")
			{
				SetDesktopGeometry(new(SKPixelGeometry.Unknown, LinuxTextGeometrySource.Gnome, ExplicitNone: true));
			}
			else if (antialias == "rgba")
			{
				var order = (await settings.ReadOneAsync("org.gnome.desktop.interface", "font-rgba-order").ConfigureAwait(false)).GetString();
				if (LinuxTextGeometry.Parse(order, LinuxTextGeometrySource.Gnome) is { ExplicitNone: false } geometry)
				{
					SetDesktopGeometry(geometry);
				}
			}
		}
		catch (Exception error)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"Unable to read desktop text geometry via DBus: {error}");
			}
		}
	}

	public void Apply(SKFont font)
	{
		if (Settings.HasFontConfig || Settings.HintingOverridden)
		{
			font.Hinting = Settings.Hinting;
		}
		font.Edging = Settings.Edging;
		if (Settings.HasFontConfig)
		{
			font.ForceAutoHinting = Settings.ForceAutoHinting;
		}
	}

	public SKFont GetRenderFont(SKFont font, SKMatrix matrix, SKPaint paint, bool grayscale = false)
		=> font.Edging == SKFontEdging.SubpixelAntialias && (grayscale || !LinuxFontRenderingSettings.CanUseLcd(matrix, paint))
			? _grayscaleFonts.GetValue(font, CreateGrayscaleFont) : font;

	// Skia derives its coverage gamma/contrast table from the paint's luminance colour, but only when the paint is
	// "just a colour". A colour shader behind a colour filter reports no luminance, so Skia uses its neutral
	// mid-grey table, which is within a few percent of the raw FreeType coverage Qt and cairo blend.
	public SKPaint GetRenderPaint(SKPaint paint)
	{
		if (!Settings.LinearCoverage || !paint.IsAntialias || paint.Shader is not null || paint.ColorFilter is not null)
		{
			return paint;
		}

		var color = paint.Color;
		_renderPaint.Color = color; // only the alpha applies when a shader is set
		_renderPaint.BlendMode = paint.BlendMode;
		_renderPaint.Style = paint.Style;
		_renderPaint.StrokeWidth = paint.StrokeWidth;
		_renderPaint.MaskFilter = paint.MaskFilter;
		_renderPaint.ImageFilter = paint.ImageFilter;
		_renderPaint.IsAntialias = true;
		_renderPaint.Shader = GetColorShader(color.WithAlpha(255));
		return _renderPaint;
	}

	private SKShader GetColorShader(SKColor opaqueColor)
	{
		if (!_colorShaders.TryGetValue((uint)opaqueColor, out var shader))
		{
			if (_colorShaders.Count >= MaxCachedShaders)
			{
				foreach (var cached in _colorShaders.Values)
				{
					cached.Dispose();
				}
				_colorShaders.Clear();
			}

			using var identity = SKColorFilter.CreateColorMatrix(
			[
				1, 0, 0, 0, 0,
				0, 1, 0, 0, 0,
				0, 0, 1, 0, 0,
				0, 0, 0, 1, 0,
			]);
			shader = SKShader.CreateColor(opaqueColor).WithColorFilter(identity);
			_colorShaders.Add((uint)opaqueColor, shader);
		}

		return shader;
	}

	private static SKFont CreateGrayscaleFont(SKFont font) => new(font.Typeface, font.Size, font.ScaleX, font.SkewX)
	{
		Edging = SKFontEdging.Antialias,
		Hinting = font.Hinting,
		Subpixel = font.Subpixel,
		BaselineSnap = font.BaselineSnap,
		ForceAutoHinting = font.ForceAutoHinting,
		LinearMetrics = font.LinearMetrics,
		EmbeddedBitmaps = font.EmbeddedBitmaps,
		Embolden = font.Embolden,
	};
}
