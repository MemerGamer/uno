using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Uno;
using Uno.Extensions;
using Uno.Foundation.Logging;
using Uno.UI.Xaml;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents.TextFormatting;
using Microsoft.UI.Xaml.Media;
using Uno.UI.Composition;
using Uno.UI.Helpers;

#nullable enable

namespace Microsoft.UI.Composition
{
	internal class TextVisual : ContainerVisual
	{
		private readonly WeakReference<TextBlock> _owner;

		public TextVisual(Compositor compositor, TextBlock owner) : base(compositor)
		{
			_owner = new WeakReference<TextBlock>(owner);
		}

		// Whether the cached picture was recorded with grayscale glyphs because TotalMatrix was not LCD-safe.
		private bool _recordedGrayscale;
		private long _rerecordFrame = -1;

		// Returns whether this visual's glyphs, as drawn in the current frame, are safe in an LCD surface. A picture
		// recorded under a different alignment is re-recorded on the next frame. A request still pending in a later
		// frame means the visual was not painted, so it contributed nothing.
		internal bool IsLcdSafeRecording(long frame)
		{
			if (SkiaRenderHelper.IsLcdTextMatrix(TotalMatrix.ToSKMatrix()) != _recordedGrayscale)
			{
				return true;
			}
			if (_rerecordFrame < 0)
			{
				_rerecordFrame = frame;
				Compositor.InvalidateRender(this);
			}
			return _rerecordFrame != frame || _recordedGrayscale;
		}

		internal override SKPath? Paint(in PaintingSession session)
		{
			if (_owner.TryGetTarget(out var owner))
			{
				// The picture is recorded in local coordinates and replayed under TotalMatrix.
				var grayscale = !SkiaRenderHelper.IsLcdTextMatrix(TotalMatrix.ToSKMatrix());
				_recordedGrayscale = grayscale;
				_rerecordFrame = -1;
				var previous = FontDetails.RecordGrayscale;
				FontDetails.RecordGrayscale = grayscale;
				try
				{
					owner.Draw(in session);
				}
				finally
				{
					FontDetails.RecordGrayscale = previous;
				}
			}

			return null;
		}

		internal override bool CanPaint() => true;

		internal override bool PaintsWithinOwnSize => true;
	}
}
