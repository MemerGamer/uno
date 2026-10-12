// Licensed under the MIT License.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

// Single owner thread for one window's renderer, frame scheduler, Skia surfaces and EGL context.
internal sealed class WaylandRenderThread : IDisposable
{
	private readonly BlockingCollection<Action> _queue = new();
	private readonly Thread _thread;
	private readonly Action<Exception> _onError;

	public WaylandRenderThread(string name, Action<Exception> onError)
	{
		_onError = onError;
		_thread = new Thread(Run) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
		_thread.Start();
	}

	public void Post(Action action)
	{
		try
		{
			_queue.Add(action);
		}
		catch (InvalidOperationException)
		{
			// Completed: the window is closing and its renderer is gone.
		}
	}

	private void Run()
	{
		foreach (var action in _queue.GetConsumingEnumerable())
		{
			try
			{
				action();
			}
			catch (Exception error)
			{
				_onError(error);
			}
		}
	}

	// The thread drains queued work, including disposal posted before this call, then exits.
	// Not joined: the UI thread must never wait on rendering.
	public void Dispose() => _queue.CompleteAdding();
}
