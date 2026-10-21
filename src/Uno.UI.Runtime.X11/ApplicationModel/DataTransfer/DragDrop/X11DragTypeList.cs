// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Linq;

namespace Uno.UI.Runtime.X11;

internal static class X11DragTypeList
{
	internal static IntPtr[] Read(bool hasList, IntPtr first, IntPtr second, IntPtr third, Func<bool, IntPtr[]> readProperty)
		=> (hasList ? readProperty(false) : new[] { first, second, third }).Where(type => type != IntPtr.Zero).ToArray();
}
