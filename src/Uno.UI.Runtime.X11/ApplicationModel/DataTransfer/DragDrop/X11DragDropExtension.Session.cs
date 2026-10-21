// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;
using Windows.Foundation;
using Uno.Extensions;

namespace Uno.UI.Runtime.X11;

internal partial class X11DragDropExtension
{
	internal readonly struct DragEventSource(long id, Point location) : IDragEventSource
	{
		private static long _nextFrameId;
		private readonly Point _location = location;

		public long Id => id;

		public uint FrameId { get; } = (uint)Interlocked.Increment(ref _nextFrameId);

		/// <inheritdoc />
		public (Point location, DragDropModifiers modifier) GetState() => (_location, DragDropModifiers.None);

		/// <inheritdoc />
		public Point GetPosition(object? relativeTo)
		{
			if (relativeTo is null)
			{
				return _location;
			}

			if (relativeTo is UIElement elt)
			{
				var eltToRoot = UIElement.GetTransform(elt, null);
				var rootToElt = eltToRoot.Inverse();

				return rootToElt.Transform(_location);
			}

			throw new InvalidOperationException("The relative to must be a UIElement.");
		}
	}

	// XdndEnter has no coordinates; Leave and Drop reuse the last position. All members run on the UI thread.
	internal sealed class XdndSession(IntPtr version, IntPtr sourceWindow, IntPtr[] availableFormats, DataPackageOperation sourceActions)
	{
		public long PointerId { get; } = Pointer.CreateUniqueIdForUnknownPointer();
		public DragEventSource CreateEventSource(Point position) => new(PointerId, position);
		public IntPtr Version { get; } = version;
		public IntPtr SourceWindow { get; } = sourceWindow;
		public IntPtr[] AvailableFormats { get; } = availableFormats;
		public DataPackageOperation SourceActions { get; } = sourceActions;
		public X11DragTargetStatus Status { get; } = new();
		public X11SelectionReadSession Selection { get; } = new(sourceWindow);
		public CoreDragInfo? Info { get; set; }
		public Point Position { get; set; }
		public DataPackageOperation Requested { get; set; }
		public bool Dropped { get; set; }
		public bool Finished { get; set; }
	}
}
