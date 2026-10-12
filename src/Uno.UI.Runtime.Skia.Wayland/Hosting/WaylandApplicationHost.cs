// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Uno.UI.Runtime.Skia;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland;

public sealed class WaylandApplicationHost : SkiaHost, ISkiaApplicationHost, IDisposable
{
	private readonly Func<Application> _appBuilder;
	private WaylandDisplay? _display;

	public WaylandApplicationHost(Func<Application> appBuilder)
		=> _appBuilder = appBuilder ?? throw new ArgumentNullException(nameof(appBuilder));

	internal static bool IsSupported()
	{
		if (!OperatingSystem.IsLinux())
		{
			return false;
		}
		var selection = Environment.GetEnvironmentVariable("UNO_SKIA_HOST");
		if (selection is not null && selection != "auto" && selection != "wayland" && selection != "x11")
		{
			throw new ArgumentException("UNO_SKIA_HOST must be auto, wayland, or x11.");
		}
		if (selection == "x11")
		{
			return false;
		}
		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
		{
			if (selection == "wayland")
			{
				throw new PlatformNotSupportedException("WAYLAND_DISPLAY is required for the Wayland host.");
			}
			return false;
		}
		try
		{
			// Connect performs the initial registry roundtrip.
			using var display = WaylandDisplay.Connect();
			if (HasRequiredGlobals(display))
			{
				return true;
			}
		}
		catch (Exception error) when (selection != "wayland" && error is IOException or DllNotFoundException or EntryPointNotFoundException)
		{
			return false;
		}
		if (selection == "wayland")
		{
			throw new PlatformNotSupportedException("The compositor must advertise wl_compositor, xdg_wm_base, and wl_shm.");
		}
		return false;
	}

	private static bool HasRequiredGlobals(WaylandDisplay display)
		=> display.GetGlobal(WlCompositor.ProtocolName) is not null
			&& display.GetGlobal(XdgWmBase.ProtocolName) is not null
			&& display.GetGlobal(WlShm.ProtocolName) is not null;

	protected override void Initialize()
	{
		_display = WaylandDisplay.Connect();
		if (!HasRequiredGlobals(_display))
		{
			Dispose();
			throw new PlatformNotSupportedException("Required Wayland globals are unavailable.");
		}
		// TODO(proto/map): start a wakeable fd dispatch thread and install the Uno UI dispatcher.
		// Register only after selection succeeds; probing must not replace X11 services.
		// TODO(render/input): install factories before Application.Start invokes user code.
		Dispose();
		throw new PlatformNotSupportedException("The Wayland host dispatcher and rendering integration are pending.");
	}

	protected override Task RunLoop()
	{
		Application.Start(_ =>
		{
			var app = _appBuilder();
			app.Host = this;
			return app;
		});
		// TODO(map): complete on Application.Exit or the configured last-window shutdown policy.
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		_display?.Dispose();
		_display = null;
	}
}
