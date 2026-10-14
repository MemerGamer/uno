#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Uno.WinUI.Runtime.Skia.X11;

internal static class X11TextPixelGeometry
{
	internal static int SelectChanges(IntPtr display, IntPtr root)
	{
		try
		{
			using var displayLock = X11Helper.XLock(display);
			if (!HasRandr13(display, out var eventBase))
			{
				return -1;
			}
			X11Helper.XRRSelectInput(display, root, (int)(RandrEventMask.RRScreenChangeNotify
				| RandrEventMask.RRCrtcChangeNotifyMask | RandrEventMask.RROutputChangeNotifyMask
				| RandrEventMask.RROutputPropertyNotifyMask));
			return eventBase;
		}
		catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			return -1;
		}
	}

	internal static unsafe SKPixelGeometry Read(X11Window window, SKPixelGeometry preference)
	{
		if (preference == SKPixelGeometry.Unknown)
		{
			return SKPixelGeometry.Unknown;
		}
		try
		{
			using var displayLock = X11Helper.XLock(window.Display);
			if (!HasRandr13(window.Display, out _))
			{
				return SKPixelGeometry.Unknown;
			}
			XWindowAttributes attributes = default;
			if (XLib.XGetWindowAttributes(window.Display, window.Window, ref attributes) == 0
				|| !XLib.XTranslateCoordinates(window.Display, window.Window, attributes.root, 0, 0, out var x, out var y, out _))
			{
				return SKPixelGeometry.Unknown;
			}
			var bounds = new Rectangle(x, y, attributes.width, attributes.height);
			var resources = X11Helper.XRRGetScreenResourcesCurrent(window.Display, attributes.root);
			if (resources == null)
			{
				return SKPixelGeometry.Unknown;
			}
			try
			{
				var outputs = new List<X11TextPixelGeometryPolicy.Output>();
				foreach (var crtc in new ReadOnlySpan<IntPtr>(resources->crtcs.ToPointer(), resources->ncrtc))
				{
					var info = X11Helper.XRRGetCrtcInfo(window.Display, (IntPtr)resources, crtc);
					if (info == null)
					{
						return SKPixelGeometry.Unknown;
					}
					try
					{
						if (info->mode == IntPtr.Zero)
						{
							continue;
						}
						var crtcBounds = new Rectangle(info->x, info->y, checked((int)info->width), checked((int)info->height));
						if (!bounds.IntersectsWith(crtcBounds))
						{
							continue;
						}
						if (info->noutput <= 0 || info->rotation != (ushort)RandrRotate.RR_Rotate_0
							|| !HasUnscaledMode(resources, info) || !HasIdentityTransform(window.Display, crtc))
						{
							return SKPixelGeometry.Unknown;
						}
						var panning = X11Helper.XRRGetPanning(window.Display, resources, crtc);
						if (panning == null)
						{
							return SKPixelGeometry.Unknown;
						}
						try
						{
							if (panning->width != 0 || panning->height != 0)
							{
								return SKPixelGeometry.Unknown;
							}
						}
						finally
						{
							X11Helper.XRRFreePanning(panning);
						}
						foreach (var output in new ReadOnlySpan<IntPtr>(info->outputs.ToPointer(), info->noutput))
						{
							var outputInfo = X11Helper.XRRGetOutputInfo(window.Display, (IntPtr)resources, output);
							if (outputInfo == null)
							{
								return SKPixelGeometry.Unknown;
							}
							try
							{
								if (outputInfo->connection != 0 /* RR_Connected */ || outputInfo->crtc != crtc)
								{
									return SKPixelGeometry.Unknown;
								}
								outputs.Add(new(crtcBounds, X11TextPixelGeometryPolicy.FromSubpixelOrder(outputInfo->subpixel_order)));
							}
							finally
							{
								X11Helper.XRRFreeOutputInfo(outputInfo);
							}
						}
					}
					finally
					{
						X11Helper.XRRFreeCrtcInfo(info);
					}
				}
				return X11TextPixelGeometryPolicy.GetGeometry(bounds, outputs, preference);
			}
			finally
			{
				X11Helper.XRRFreeScreenResources(resources);
			}
		}
		catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or OverflowException)
		{
			return SKPixelGeometry.Unknown;
		}
	}

	private static bool HasRandr13(IntPtr display, out int eventBase)
		=> XLib.XRRQueryExtension(display, out eventBase, out _) != 0
			&& XLib.XRRQueryVersion(display, out var major, out var minor) != 0 && (major > 1 || major == 1 && minor >= 3);

	private static unsafe bool HasUnscaledMode(X11Helper.XRRScreenResources* resources, X11Helper.XRRCrtcInfo* crtc)
	{
		foreach (var mode in new ReadOnlySpan<X11Helper.XRRModeInfo>(resources->modes.ToPointer(), resources->nmode))
		{
			if (mode.id == crtc->mode)
			{
				return mode.width == crtc->width && mode.height == crtc->height;
			}
		}
		return false;
	}

	private static unsafe bool HasIdentityTransform(IntPtr display, IntPtr crtc)
	{
		X11Helper.XRRCrtcTransformAttributes* attributes = null;
		try
		{
			if (X11Helper.XRRGetCrtcTransform(display, crtc, ref attributes) == 0 || attributes == null)
			{
				return false;
			}
			var filter = Marshal.PtrToStringUTF8(attributes->currentFilter);
			if (filter is not (null or "" or "nearest" or "bilinear"))
			{
				return false;
			}
			var transform = attributes->currentTransform;
			return transform._1_1 == 65536 && transform._2_2 == 65536 && transform._3_3 == 65536
				&& transform._1_2 == 0 && transform._1_3 == 0 && transform._2_1 == 0 && transform._2_3 == 0
				&& transform._3_1 == 0 && transform._3_2 == 0 && attributes->currentNparams == 0;
		}
		finally
		{
			if (attributes != null)
			{
				_ = XLib.XFree((IntPtr)attributes);
			}
		}
	}
}
