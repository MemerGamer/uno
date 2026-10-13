// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Threading;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// Decides when a window configure may be presented: the render thread must have its buffer size,
// and the UI thread must have recorded a frame laid out at the configured bounds.
internal sealed class WaylandFrameReadiness
{
	// Bounds the waits, so content Uno never produces at the expected size cannot freeze the window.
	internal const int MaxStaleFrames = 8;

	private long _uiGeneration;

	// UI thread state.
	private bool _layoutPending;
	private long _layoutGeneration;
	private int _layoutFrames;

	// Render thread state.
	private int _staleFrames;

	internal long RenderGeneration { get; private set; }

	// UI thread. Returns whether the generation waits for a frame recorded after layout.
	internal bool ConfigureUI(long generation, bool boundsChanged)
	{
		if (boundsChanged || _layoutPending)
		{
			_layoutPending = true;
			_layoutGeneration = generation;
			_layoutFrames = 0;
			return true;
		}
		Interlocked.Exchange(ref _uiGeneration, generation);
		return false;
	}

	// UI thread, after Uno recorded a frame. Returns whether a waiting generation became ready.
	internal bool OnFrameRecorded(bool arrangedAtBounds)
	{
		if (!_layoutPending || (!arrangedAtBounds && ++_layoutFrames <= MaxStaleFrames))
		{
			return false;
		}
		_layoutPending = false;
		Interlocked.Exchange(ref _uiGeneration, _layoutGeneration);
		return true;
	}

	// Render thread.
	internal void ConfigureRender(long generation)
	{
		RenderGeneration = generation;
		_staleFrames = 0;
	}

	// Render thread. A generation mismatch has no safety valve: presenting it would acknowledge the wrong configure.
	internal bool CanPresent(int canvasWidth, int canvasHeight, int bufferWidth, int bufferHeight)
	{
		if (RenderGeneration != Interlocked.Read(ref _uiGeneration))
		{
			return false;
		}
		// One pixel of tolerance: Uno rounds the canvas size half to even, buffers round half up.
		if ((Math.Abs(canvasWidth - bufferWidth) > 1 || Math.Abs(canvasHeight - bufferHeight) > 1)
			&& ++_staleFrames <= MaxStaleFrames)
		{
			return false;
		}
		_staleFrames = 0;
		return true;
	}
}
