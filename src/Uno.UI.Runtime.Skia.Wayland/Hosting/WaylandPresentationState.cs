// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal sealed class WaylandPresentationState
{
	internal long Generation { get; private set; }
	internal WaylandDecorationLayout Layout { get; private set; }
	internal double Scale { get; private set; }

	internal bool Submit(long generation, WaylandDecorationLayout layout, double scale, Func<bool> submit)
	{
		if (!submit())
		{
			return false;
		}
		Layout = layout;
		Scale = scale;
		Generation = generation;
		return true;
	}
}
