// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using Windows.ApplicationModel.DataTransfer;

namespace Uno.UI.Runtime.X11;

internal static class X11DragActions
{
	internal const uint ShiftMask = 1;
	internal const uint ControlMask = 4;

	internal static uint ChooseButton(uint mask)
		=> (mask & 0x100) != 0 ? 1u : (mask & 0x200) != 0 ? 2u : (mask & 0x400) != 0 ? 3u : 0u;

	internal static bool IsButtonHeld(uint mask, uint button)
		=> button is >= 1 and <= 3 && (mask & (0x100u << (int)(button - 1))) != 0;

	internal static bool IsReleaseButton(uint button, uint initiatingButton)
		=> initiatingButton is >= 1 and <= 3 && button == initiatingButton;

	internal static DataPackageOperation Allowed(DataPackageOperation allowed)
		=> allowed == DataPackageOperation.None ? DataPackageOperation.Copy : allowed;

	internal static DataPackageOperation Choose(uint modifiers, DataPackageOperation allowed)
	{
		allowed = Allowed(allowed);
		var requested = (modifiers & (ShiftMask | ControlMask)) switch
		{
			ShiftMask | ControlMask => DataPackageOperation.Link,
			ShiftMask => DataPackageOperation.Move,
			ControlMask => DataPackageOperation.Copy,
			_ => DataPackageOperation.None
		};
		return (requested & allowed) != 0 ? requested
			: (allowed & DataPackageOperation.Copy) != 0 ? DataPackageOperation.Copy
			: (allowed & DataPackageOperation.Move) != 0 ? DataPackageOperation.Move
			: (allowed & DataPackageOperation.Link) != 0 ? DataPackageOperation.Link
			: DataPackageOperation.Copy;
	}

	internal static string ToName(DataPackageOperation operation) => operation switch
	{
		DataPackageOperation.Move => "XdndActionMove",
		DataPackageOperation.Link => "XdndActionLink",
		_ => "XdndActionCopy"
	};

	internal static DataPackageOperation FromName(string? name) => name switch
	{
		"XdndActionMove" => DataPackageOperation.Move,
		"XdndActionLink" => DataPackageOperation.Link,
		_ => DataPackageOperation.Copy
	};

	internal static uint PackPosition(int x, int y) => ((uint)(ushort)x << 16) | (ushort)y;

	internal static uint EnterFlags(int version, int targetCount) => ((uint)version << 24) | (targetCount > 3 ? 1u : 0u);
}
