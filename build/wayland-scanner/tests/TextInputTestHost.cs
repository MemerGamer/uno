#nullable enable

using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

// Headless host for the linked production extension. Composition mutations mirror TextBox.IME.skia.cs.
namespace Microsoft.UI.Xaml.Controls
{
	internal sealed class TextBox
	{
		public static object IsReadOnlyProperty { get; } = new();
		public static object IsEnabledProperty { get; } = new();
		private readonly Dictionary<long, (object Property, Action<object, object> Callback)> _callbacks = new();
		private long _nextToken;
		private bool _readOnly;
		public bool IsReadOnly
		{
			get => _readOnly;
			set
			{
				_readOnly = value;
				foreach (var entry in _callbacks.Values.ToArray())
				{
					if (entry.Property == IsReadOnlyProperty)
					{
						entry.Callback(this, IsReadOnlyProperty);
					}
				}
			}
		}
		public bool IsEnabled { get; set; } = true;
		public string Text { get; set; } = string.Empty;
		public int SelectionStart { get; private set; }
		public int SelectionLength { get; private set; }
		public bool IsBackwardSelection { get; set; }
		public int CompositionStartIndex { get; set; }
		public int CompositionLength { get; set; }
		public object XamlRoot { get; set; } = null!;
		public TestView? TextBoxView => null;
		public long RegisterPropertyChangedCallback(object property, Action<object, object> callback)
		{
			_callbacks[++_nextToken] = (property, callback);
			return _nextToken;
		}
		public void UnregisterPropertyChangedCallback(object property, long token) => _callbacks.Remove(token);
		public void Select(int start, int length) => (SelectionStart, SelectionLength) = (start, length);
	}
	internal sealed class TestView
	{
		public TestBlock? DisplayBlock => null;
	}
	internal sealed class TestBlock
	{
		public TestParsedText? ParsedText => null;
		public TestTransform TransformToVisual(object? visual) => new();
	}
	internal sealed class TestParsedText
	{
		public (double Left, double Top, double Height) GetRectForIndex(int index) => default;
	}
	internal sealed class TestTransform
	{
		public Windows.Foundation.Point TransformPoint(Windows.Foundation.Point point) => point;
	}
}
namespace Windows.Foundation
{
	internal record struct Point(double X, double Y);
}
namespace Uno.UI.Hosting
{
	internal static class XamlRootMap
	{
		public static object GetHostForRoot(object root) => root;
	}
}
namespace Uno.WinUI.Runtime.Skia.Wayland
{
	internal sealed class WaylandXamlRootHost
	{
		public WlSurface Surface { get; set; } = null!;
	}
	internal static class WaylandDiagnostics
	{
		public static void Trace(string message) { }
	}
	internal sealed class WaylandConnection
	{
		public WaylandDisplay Display => null!;
		public ZwpTextInputManagerV3? TextInputManager => null;
		public WlSeat? Seat => null;
		public static Queue<Action> Pending { get; } = new();
		public static void PostToUI(Action action) => Pending.Enqueue(action);
		public static void Drain()
		{
			while (Pending.TryDequeue(out var action))
			{
				action();
			}
		}
	}
}
