#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Uno.Foundation.Logging;
using Uno.Helpers;
using Uno.UI.Dispatching;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Wayland.Protocols;
using Windows.UI.Core;

namespace Uno.UI.Runtime.Wayland;

public sealed class WaylandApplicationHost : UnoPlatformHost, IApplicationHost, IDisposable
{
	// The connection that passed probing, handed to the selected host to avoid a probe/create race.
	private static WaylandDisplay? _probedDisplay;

	[ThreadStatic] private static bool _isDispatcherThread;

	private readonly Func<Application> _appBuilder;
	private readonly EventLoop? _eventLoop;
	private readonly WaylandCoreApplicationExtension _coreApplication;
	private WaylandConnection? _connection;

	internal WaylandApplicationHost(Func<Application> appBuilder)
	{
		_appBuilder = appBuilder ?? throw new ArgumentNullException(nameof(appBuilder));
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

	// Probes for a compositor that serves everything the host needs. Never throws: an unusable Wayland session
	// leaves the next registered host (typically X11) to be selected.
	internal static bool IsSupported()
	{
		if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
		{
			return false;
		}
		if (_probedDisplay is { IsDisconnected: false, DispatchError: null })
		{
			return true;
		}
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
			if (typeof(WaylandApplicationHost).Log().IsEnabled(LogLevel.Information))
			{
				typeof(WaylandApplicationHost).Log().Info("The Wayland compositor does not advertise wl_compositor, xdg_wm_base and wl_shm; the Wayland host is unavailable.");
			}
		}
		catch (Exception error)
		{
			if (typeof(WaylandApplicationHost).Log().IsEnabled(LogLevel.Information))
			{
				typeof(WaylandApplicationHost).Log().Info($"Unable to connect to the Wayland compositor; the Wayland host is unavailable ({error.Message}).");
			}
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
