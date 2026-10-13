// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using SkiaSharp;
using Uno.WinUI.Runtime.Skia.Wayland.Input;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Uno.WinUI.Runtime.Skia.Wayland.Rendering;
using Windows.UI.Core;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// Client-side title bar and resize border, used when the compositor lacks xdg-decoration or picks client-side
// mode. A synchronized subsurface below the content surface, so its state is applied with the next content commit.
// Dispatch thread only.
internal sealed class WaylandClientDecorations : IDisposable
{
	private const uint LeftButton = 0x110; // BTN_LEFT
	private const uint RightButton = 0x111; // BTN_RIGHT
	private const uint SeatPointerCapability = 1;
	private const uint DoubleClickTime = 500;
	private const double DoubleClickDistance = 4;

	private static readonly CoreCursor _arrow = new(CoreCursorType.Arrow, 0);
	private static readonly CoreCursor _sizeNorthSouth = new(CoreCursorType.SizeNorthSouth, 0);
	private static readonly CoreCursor _sizeWestEast = new(CoreCursorType.SizeWestEast, 0);
	private static readonly CoreCursor _sizeNorthwestSoutheast = new(CoreCursorType.SizeNorthwestSoutheast, 0);
	private static readonly CoreCursor _sizeNortheastSouthwest = new(CoreCursorType.SizeNortheastSouthwest, 0);

	private readonly WaylandConnection _connection;
	private readonly XdgToplevel _toplevel;
	private readonly int _windowId;
	private readonly IWaylandDecorationsOwner _owner;
	private readonly WlSurface _surface;
	private readonly WlSubsurface _subsurface;
	private readonly WpViewport? _viewport;
	private readonly WaylandRenderScale _scale;
	private readonly WaylandSoftwareRenderer _renderer;
	private readonly bool _dark;

	private WaylandDecorationLayout _layout;
	private string _title = string.Empty;
	private bool _activated;
	private bool _maximized;
	private bool _shown;
	private bool _dirty;
	private int _frame;

	private WlPointer? _pointer;
	private WaylandCursor? _cursor;
	private bool _pointerInside;
	private uint _enterSerial;
	private double _pointerX;
	private double _pointerY;
	private WaylandDecorationPart _hovered;
	private uint _hoveredEdges;
	private WaylandDecorationPart _pressed;
	private uint _lastClickTime;
	private double _lastClickX;
	private double _lastClickY;
	private bool _disposed;

	internal WaylandClientDecorations(WaylandConnection connection, WlSurface parent, XdgToplevel toplevel, int windowId, bool dark, IWaylandDecorationsOwner owner)
	{
		_connection = connection;
		_toplevel = toplevel;
		_windowId = windowId;
		_dark = dark;
		_owner = owner;
		_surface = connection.Compositor.CreateSurface();
		_subsurface = connection.Subcompositor!.GetSubsurface(_surface, parent);
		_subsurface.PlaceBelow(parent);
		_viewport = connection.Viewporter?.GetViewport(_surface);
		_scale = new WaylandRenderScale(_surface, _viewport);
		_renderer = new WaylandSoftwareRenderer(_surface, connection.Shm, connection.Display.Post);
		_renderer.BufferAvailable += OnBufferAvailable;
		if (connection.Seat is { } seat)
		{
			seat.Capabilities += OnSeatCapabilities;
			UpdatePointer(connection.SeatCapabilities);
		}
	}

	internal bool IsReady => !_dirty;

	internal void Update(WaylandDecorationLayout layout, string title, bool activated, bool maximized, uint preferredScale, int integerScale)
	{
		if (_disposed)
		{
			return;
		}
		if (preferredScale != 0)
		{
			_scale.SetPreferredScale(preferredScale);
		}
		_scale.SetIntegerScale(integerScale);
		var moved = layout.SurfaceX != _layout.SurfaceX || layout.SurfaceY != _layout.SurfaceY;
		_layout = layout;
		_title = title;
		_activated = activated;
		_maximized = maximized;
		if (moved)
		{
			_subsurface.SetPosition(layout.SurfaceX, layout.SurfaceY);
		}
		if (_pointerInside)
		{
			UpdateHover();
		}
		Redraw();
	}

	internal void SetTitle(string title)
	{
		if (!_disposed && title != _title)
		{
			_title = title;
			Redraw();
		}
	}

	private void Redraw()
	{
		if (!_layout.IsVisible)
		{
			_dirty = false;
			if (_shown)
			{
				// A null buffer unmaps the subsurface; the content commit applies it.
				_shown = false;
				_surface.Attach(null, 0, 0);
				_surface.Commit();
				_owner.RequestParentCommit();
			}
			return;
		}
		var (width, height) = _scale.Apply(_layout.SurfaceWidth, _layout.SurfaceHeight);
		_dirty = !_renderer.Render(width, height, Draw);
		if (!_dirty)
		{
			_shown = true;
			_owner.RequestParentCommit();
		}
	}

	private void OnBufferAvailable(object? sender, EventArgs args)
	{
		if (_dirty && !_disposed)
		{
			Redraw();
		}
	}

	private bool Draw(SKSurface target)
	{
		var canvas = target.Canvas;
		canvas.Clear(SKColors.Transparent);
		canvas.Save();
		canvas.Scale((float)_scale.EffectiveScale);
		var layout = _layout;
		var margin = layout.ResizeMargin;
		var geometry = SKRect.Create(margin, margin, layout.ContentWidth, layout.GeometryHeight);
		if (margin > 0)
		{
			using var shadow = new SKPaint
			{
				Color = new SKColor(0, 0, 0, (byte)(_activated ? 0x50 : 0x30)),
				MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, margin / 3f),
				IsAntialias = true,
			};
			canvas.Save();
			canvas.ClipRect(geometry, SKClipOperation.Difference);
			canvas.DrawRect(geometry, shadow);
			canvas.Restore();
			using var border = new SKPaint { Color = new SKColor(0, 0, 0, 0x40), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
			canvas.DrawRect(SKRect.Inflate(geometry, 0.5f, 0.5f), border);
		}
		if (layout.TitleBarHeight > 0)
		{
			DrawTitleBar(canvas, layout, geometry);
		}
		canvas.Restore();
		_frame++;
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_windowId}: decorations frame {_frame} surface {layout.SurfaceWidth}x{layout.SurfaceHeight}@({layout.SurfaceX},{layout.SurfaceY}) title={layout.TitleBarHeight} margin={margin} buttons={layout.Buttons} activated={_activated} hovered={_hovered}"));
			WaylandDiagnostics.DumpFrame(_windowId, _frame, target, "decorations");
		}
		return true;
	}

	private void DrawTitleBar(SKCanvas canvas, WaylandDecorationLayout layout, SKRect geometry)
	{
		var bar = SKRect.Create(geometry.Left, geometry.Top, geometry.Width, layout.TitleBarHeight);
		var background = _dark
			? (_activated ? new SKColor(0x20, 0x20, 0x20) : new SKColor(0x2B, 0x2B, 0x2B))
			: (_activated ? new SKColor(0xE8, 0xE8, 0xE8) : new SKColor(0xF3, 0xF3, 0xF3));
		var foreground = _dark
			? (_activated ? SKColors.White : new SKColor(0x9A, 0x9A, 0x9A))
			: (_activated ? new SKColor(0x1A, 0x1A, 0x1A) : new SKColor(0x8A, 0x8A, 0x8A));
		using (var fill = new SKPaint { Color = background })
		{
			canvas.DrawRect(bar, fill);
		}

		using var font = new SKFont(SKTypeface.Default, 13);
		using var text = new SKPaint { Color = foreground, IsAntialias = true };
		var available = layout.ButtonsLeft - bar.Left - 24;
		var title = Ellipsize(_title, font, available);
		if (title.Length > 0)
		{
			font.GetFontMetrics(out var metrics);
			var baseline = bar.MidY - (metrics.Ascent + metrics.Descent) / 2;
			canvas.DrawText(title, bar.Left + 12, baseline, SKTextAlign.Left, font, text);
		}

		DrawButton(canvas, WaylandDecorationPart.Minimize, foreground);
		DrawButton(canvas, WaylandDecorationPart.Maximize, foreground);
		DrawButton(canvas, WaylandDecorationPart.Close, foreground);
	}

	private void DrawButton(SKCanvas canvas, WaylandDecorationPart part, SKColor foreground)
	{
		if (!_layout.TryGetButton(part, out var x, out var y, out var width, out var height))
		{
			return;
		}
		var rect = SKRect.Create(x, y, width, height);
		var hovered = _hovered == part && (_pressed == WaylandDecorationPart.None || _pressed == part);
		var pressed = hovered && _pressed == part;
		if (hovered)
		{
			var highlight = part == WaylandDecorationPart.Close
				? new SKColor(0xC4, 0x2B, 0x1C, (byte)(pressed ? 0xCC : 0xFF))
				: _dark ? new SKColor(0xFF, 0xFF, 0xFF, (byte)(pressed ? 0x10 : 0x18)) : new SKColor(0, 0, 0, (byte)(pressed ? 0x24 : 0x12));
			using var fill = new SKPaint { Color = highlight };
			canvas.DrawRect(rect, fill);
			if (part == WaylandDecorationPart.Close)
			{
				foreground = SKColors.White;
			}
		}
		using var glyph = new SKPaint { Color = foreground, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
		var cx = (float)Math.Round(rect.MidX) + 0.5f;
		var cy = (float)Math.Round(rect.MidY) + 0.5f;
		const float half = 5;
		switch (part)
		{
			case WaylandDecorationPart.Minimize:
				canvas.DrawLine(cx - half, cy, cx + half, cy, glyph);
				break;
			case WaylandDecorationPart.Maximize when _maximized:
				canvas.DrawRect(new SKRect(cx - half, cy - half + 2, cx + half - 2, cy + half), glyph);
				canvas.DrawPoints(SKPointMode.Polygon, [new(cx - half + 2, cy - half + 2), new(cx - half + 2, cy - half), new(cx + half, cy - half), new(cx + half, cy + half - 2), new(cx + half - 2, cy + half - 2)], glyph);
				break;
			case WaylandDecorationPart.Maximize:
				canvas.DrawRect(new SKRect(cx - half, cy - half, cx + half, cy + half), glyph);
				break;
			case WaylandDecorationPart.Close:
				canvas.DrawLine(cx - half, cy - half, cx + half, cy + half, glyph);
				canvas.DrawLine(cx - half, cy + half, cx + half, cy - half, glyph);
				break;
		}
	}

	private static string Ellipsize(string title, SKFont font, float available)
	{
		if (available <= 0 || title.Length == 0)
		{
			return string.Empty;
		}
		if (font.MeasureText(title) <= available)
		{
			return title;
		}
		const string ellipsis = "…";
		var length = title.Length;
		while (length > 0 && font.MeasureText(title.AsSpan(0, length)) + font.MeasureText(ellipsis) > available)
		{
			length--;
		}
		return length > 0 ? string.Concat(title.AsSpan(0, length), ellipsis) : string.Empty;
	}

	private void OnSeatCapabilities(object? sender, WlSeat.CapabilitiesEventArgs args) => UpdatePointer(args.Capabilities);

	private void UpdatePointer(uint capabilities)
	{
		var hasPointer = (capabilities & SeatPointerCapability) != 0;
		if (hasPointer && _pointer is null && !_disposed)
		{
			// A separate wl_pointer for the decoration surface; the content surface's input is unaffected.
			_pointer = _connection.Seat!.GetPointer();
			_pointer.Enter += OnPointerEnter;
			_pointer.Leave += OnPointerLeave;
			_pointer.Motion += OnPointerMotion;
			_pointer.Button += OnPointerButton;
		}
		else if ((!hasPointer || _disposed) && _pointer is { } pointer)
		{
			_cursor?.Dispose();
			_cursor = null;
			pointer.Enter -= OnPointerEnter;
			pointer.Leave -= OnPointerLeave;
			pointer.Motion -= OnPointerMotion;
			pointer.Button -= OnPointerButton;
			pointer.Dispose();
			_pointer = null;
			_pointerInside = false;
		}
	}

	private void OnPointerEnter(object? sender, WlPointer.EnterEventArgs args)
	{
		if (args.Surface != _surface)
		{
			return;
		}
		_pointerInside = true;
		_enterSerial = args.Serial;
		_pointerX = args.SurfaceX.ToDouble();
		_pointerY = args.SurfaceY.ToDouble();
		_hoveredEdges = uint.MaxValue; // Force a cursor update for the new enter serial.
		UpdateHover();
	}

	private void OnPointerLeave(object? sender, WlPointer.LeaveEventArgs args)
	{
		if (!_pointerInside || args.Surface != _surface)
		{
			return;
		}
		_pointerInside = false;
		_pressed = WaylandDecorationPart.None;
		SetHovered(WaylandDecorationPart.None);
	}

	private void OnPointerMotion(object? sender, WlPointer.MotionEventArgs args)
	{
		if (_pointerInside)
		{
			_pointerX = args.SurfaceX.ToDouble();
			_pointerY = args.SurfaceY.ToDouble();
			UpdateHover();
		}
	}

	private void UpdateHover()
	{
		var (part, edges) = _layout.HitTest(_pointerX, _pointerY);
		if (edges != _hoveredEdges && _pointer is { } pointer)
		{
			_hoveredEdges = edges;
			_cursor ??= new WaylandCursor(
				_connection.Shm.Handle,
				new WaylandCursorSurface(pointer, _connection.Compositor.CreateSurface()),
				_connection.CursorShape?.GetPointer(pointer));
			_cursor.Set(_enterSerial, CursorFor(edges), _scale.EffectiveScale);
		}
		SetHovered(part);
	}

	private void SetHovered(WaylandDecorationPart part)
	{
		if (_hovered != part)
		{
			var redraw = IsButton(part) || IsButton(_hovered);
			_hovered = part;
			if (redraw)
			{
				Redraw();
			}
		}
	}

	private void OnPointerButton(object? sender, WlPointer.ButtonEventArgs args)
	{
		if (!_pointerInside || _connection.Seat is not { } seat)
		{
			return;
		}
		_connection.InputSerial = args.Serial;
		var (part, edges) = _layout.HitTest(_pointerX, _pointerY);
		var pressed = args.State == 1;
		if (!pressed)
		{
			var target = _pressed;
			_pressed = WaylandDecorationPart.None;
			if (target != WaylandDecorationPart.None)
			{
				Redraw();
				if (target == part)
				{
					Invoke(target);
				}
			}
			return;
		}
		if (args.Button == RightButton && part == WaylandDecorationPart.TitleBar)
		{
			// Window menu coordinates are relative to the window geometry.
			_toplevel.ShowWindowMenu(seat, args.Serial, (int)(_pointerX - _layout.ResizeMargin), (int)(_pointerY - _layout.ResizeMargin));
			return;
		}
		if (args.Button != LeftButton)
		{
			return;
		}
		switch (part)
		{
			case WaylandDecorationPart.ResizeEdge:
				_toplevel.Resize(seat, args.Serial, edges);
				break;
			case WaylandDecorationPart.TitleBar:
				var isDoubleClick = args.Time - _lastClickTime <= DoubleClickTime
					&& Math.Abs(_pointerX - _lastClickX) <= DoubleClickDistance
					&& Math.Abs(_pointerY - _lastClickY) <= DoubleClickDistance;
				_lastClickTime = args.Time;
				_lastClickX = _pointerX;
				_lastClickY = _pointerY;
				if (isDoubleClick)
				{
					_lastClickTime -= DoubleClickTime + 1;
					if ((_layout.Buttons & WaylandCaptionButtons.Maximize) != 0)
					{
						_owner.ToggleMaximized();
					}
				}
				else
				{
					_toplevel.Move(seat, args.Serial);
				}
				break;
			case WaylandDecorationPart.Minimize:
			case WaylandDecorationPart.Maximize:
			case WaylandDecorationPart.Close:
				_pressed = part;
				Redraw();
				break;
		}
	}

	private void Invoke(WaylandDecorationPart part)
	{
		switch (part)
		{
			case WaylandDecorationPart.Minimize:
				_owner.Minimize();
				break;
			case WaylandDecorationPart.Maximize:
				_owner.ToggleMaximized();
				break;
			case WaylandDecorationPart.Close:
				_owner.RequestClose();
				break;
		}
	}

	private static bool IsButton(WaylandDecorationPart part)
		=> part is WaylandDecorationPart.Minimize or WaylandDecorationPart.Maximize or WaylandDecorationPart.Close;

	private static CoreCursor CursorFor(uint edges) => edges switch
	{
		1 or 2 => _sizeNorthSouth,
		4 or 8 => _sizeWestEast,
		5 or 10 => _sizeNorthwestSoutheast,
		6 or 9 => _sizeNortheastSouthwest,
		_ => _arrow,
	};

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (_connection.Seat is { } seat)
		{
			seat.Capabilities -= OnSeatCapabilities;
		}
		UpdatePointer(0);
		_renderer.BufferAvailable -= OnBufferAvailable;
		_renderer.Dispose();
		_viewport?.Dispose();
		_subsurface.Dispose();
		_surface.Dispose();
	}
}

// Window actions requested from the decorations, on the dispatch thread.
internal interface IWaylandDecorationsOwner
{
	void RequestParentCommit();
	void ToggleMaximized();
	void Minimize();
	void RequestClose();
}
