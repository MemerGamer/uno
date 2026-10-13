// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Uno.Foundation.Logging;
using Uno.Helpers;
using Uno.UI.Dispatching;
using Uno.UI.Runtime.Skia;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Windows.UI.Core;

namespace Uno.WinUI.Runtime.Skia.Wayland;

public sealed class WaylandApplicationHost : SkiaHost, ISkiaApplicationHost, IDisposable
{
	// The connection that passed probing, handed to the selected host to avoid a probe/create race.
	private static WaylandDisplay? _probedDisplay;

	[ThreadStatic] private static bool _isDispatcherThread;

	private readonly Func<Application> _appBuilder;
	private readonly EventLoop? _eventLoop;
	private readonly WaylandCoreApplicationExtension _coreApplication;
	private WaylandConnection? _connection;

	public WaylandApplicationHost(Func<Application> appBuilder)
	{
		_appBuilder = appBuilder ?? throw new ArgumentNullException(nameof(appBuilder));
		WaylandDiagnostics.InstallLoggerIfRequested();
		_coreApplication = new WaylandCoreApplicationExtension(() => _connection);

		if (CoreDispatcher.DispatchOverride is null)
		{
			_eventLoop = new EventLoop();
			_eventLoop.Schedule(() =>
			{
				Thread.CurrentThread.Name = "Uno Event Loop";
				_isDispatcherThread = true;
			});
			CoreDispatcher.DispatchOverride = (action, _) => _eventLoop.Schedule(action);
			CoreDispatcher.HasThreadAccessOverride = () => _isDispatcherThread;
		}
		else
		{
			// Another host already owns the UI thread (secondary app); reuse its dispatcher.
			CoreDispatcher.DispatchOverride(() => _isDispatcherThread = true, NativeDispatcherPriority.Normal);
		}
	}

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
		if (_probedDisplay is { IsDisconnected: false, DispatchError: null })
		{
			return true;
		}
		Interlocked.Exchange(ref _probedDisplay, null)?.Dispose();
		try
		{
			// Connect performs the initial registry roundtrip.
			var display = WaylandDisplay.Connect();
			if (HasRequiredGlobals(display))
			{
				_probedDisplay = display;
				return true;
			}
			display.Dispose();
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
		var display = Interlocked.Exchange(ref _probedDisplay, null) ?? WaylandDisplay.Connect();
		try
		{
			if (!HasRequiredGlobals(display))
			{
				throw new PlatformNotSupportedException("Required Wayland globals are unavailable.");
			}
			_connection = new WaylandConnection(display);
			if (Environment.GetEnvironmentVariable("UNO_WAYLAND_APP_ID") is { Length: > 0 } appId)
			{
				_connection.AppId = appId;
			}
			display.StartDispatch();
		}
		catch
		{
			display.Dispose();
			_connection = null;
			throw;
		}
		WaylandRegistrations.Register(_connection, _coreApplication);
		WaylandDiagnostics.Trace("host: connected, dispatch thread started");
	}

	protected override Task RunLoop()
	{
		Thread.CurrentThread.Name = "Main Thread (keep-alive)";
		if (_eventLoop is not null)
		{
			_eventLoop.Schedule(StartApp);
		}
		else
		{
			CoreDispatcher.DispatchOverride(StartApp, NativeDispatcherPriority.Normal);
		}

		while (!ShouldExit())
		{
			Thread.Sleep(100);
		}

		if (_connection?.Display.DispatchError is { } error)
		{
			WaylandDiagnostics.Trace($"host: dispatch failed: {error}");
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error("The Wayland connection failed.", error);
			}
			throw new IOException("The Wayland connection failed.", error);
		}
		WaylandDiagnostics.Trace("host: exiting");
		return Task.CompletedTask;
	}

	private bool ShouldExit()
	{
		if (_coreApplication.ExitRequested || _connection?.Display.DispatchError is not null)
		{
			return true;
		}
		return Application.Current?.DispatcherShutdownMode == DispatcherShutdownMode.OnLastWindowClose
			&& _connection?.AllWindowsClosed == true;
	}

	private void StartApp()
	{
		Application.Start(_ =>
		{
			var app = _appBuilder();
			app.Host = this;
			return app;
		});
	}

	public void Dispose()
	{
		_connection?.Dispose();
		_connection = null;
	}
}
