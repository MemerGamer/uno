#nullable enable

using System;

namespace Uno.UI.Runtime.Wayland.Input;

// Dead-key and Compose sequences through libxkbcommon, for text not composed by a text-input-v3 input method
// (one consumes the keys of sequences it composes, so they never reach wl_keyboard).
internal sealed class WaylandKeyboardCompose : IDisposable
{
	private nint _state;

	internal WaylandKeyboardCompose(nint context, string locale)
	{
		var table = WaylandXkb.ComposeTableNew(context, locale, 0);
		if (table != 0)
		{
			try
			{
				_state = WaylandXkb.ComposeStateNew(table, 0);
			}
			finally
			{
				WaylandXkb.ComposeTableUnref(table);
			}
		}
	}

	// Returns the text of the key, or null when the sequence consumed it: while it is pending, cancelled, or
	// when its result does not fit KeyEventArgs.UnicodeKey (several characters, or outside the BMP).
	internal uint? Feed(uint keysym, uint unicode)
	{
		if (_state == 0 || WaylandXkb.ComposeFeed(_state, keysym) == 0) // XKB_COMPOSE_FEED_IGNORED: modifiers
		{
			return unicode;
		}
		switch (WaylandXkb.ComposeStatus(_state))
		{
			case 1: // XKB_COMPOSE_COMPOSING
				return null;
			case 2: // XKB_COMPOSE_COMPOSED
				var result = WaylandXkb.KeysymToUtf32(WaylandXkb.ComposeGetSym(_state));
				Reset();
				return result is 0 or > 0xffff ? null : result;
			case 3: // XKB_COMPOSE_CANCELLED
				Reset();
				return null;
			default:
				return unicode;
		}
	}

	internal void Reset()
	{
		if (_state != 0)
		{
			WaylandXkb.ComposeReset(_state);
		}
	}

	public void Dispose()
	{
		if (_state != 0)
		{
			WaylandXkb.ComposeStateUnref(_state);
			_state = 0;
		}
	}
}
