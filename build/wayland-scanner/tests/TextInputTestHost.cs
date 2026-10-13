#nullable enable

using Microsoft.UI.Xaml.Input;
using Uno.WinUI.Runtime.Skia.Wayland.Input;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

// Headless host for the linked production extension. Composition mutations mirror TextBox.IME.skia.cs.
namespace Microsoft.UI.Xaml.Controls
{
	internal sealed class TextBox
	{
		public static object IsReadOnlyProperty { get; } = new();
		public static object TextProperty { get; } = new();
		public static object IsEnabledProperty { get; } = new();
		public static object IsTextPredictionEnabledProperty { get; } = new();
		public static object IsSpellCheckEnabledProperty { get; } = new();
		public static object InputScopeProperty { get; } = new();
		public static object AcceptsReturnProperty { get; } = new();
		private readonly Dictionary<long, (object Property, Action<object, object> Callback)> _callbacks = new();
		private long _nextToken;
		private bool _readOnly;
		private bool _enabled = true;
		private bool _prediction = true;
		private bool _spelling = true;
		private bool _acceptsReturn;
		private InputScope? _inputScope;
		private string _text = string.Empty;
		public bool IsReadOnly
		{
			get => _readOnly;
			set => SetProperty(ref _readOnly, value, IsReadOnlyProperty);
		}
		public bool IsEnabled
		{
			get => _enabled;
			set => SetProperty(ref _enabled, value, IsEnabledProperty);
		}
		public bool IsTextPredictionEnabled
		{
			get => _prediction;
			set => SetProperty(ref _prediction, value, IsTextPredictionEnabledProperty);
		}
		public bool IsSpellCheckEnabled
		{
			get => _spelling;
			set => SetProperty(ref _spelling, value, IsSpellCheckEnabledProperty);
		}
		public InputScope? InputScope
		{
			get => _inputScope;
			set => SetProperty(ref _inputScope, value, InputScopeProperty);
		}
		public bool AcceptsReturn
		{
			get => _acceptsReturn;
			set => SetProperty(ref _acceptsReturn, value, AcceptsReturnProperty);
		}
		public string Text
		{
			get => _text;
			set => SetProperty(ref _text, value, TextProperty);
		}
		public bool IsComposing { get; set; }
		internal int PropertyCallbackCount => _callbacks.Count;
		private void SetProperty<T>(ref T field, T value, object property)
		{
			if (EqualityComparer<T>.Default.Equals(field, value))
			{
				return;
			}
			field = value;
			foreach (var entry in _callbacks.Values.ToArray())
			{
				if (entry.Property == property)
				{
					entry.Callback(this, property);
				}
			}
		}
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
		public WaylandKeyboardInputSource KeyboardSource { get; } = new();
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
		public void NotifyTextInputReceived() { }
		public static Queue<Action> Pending { get; } = new();
		public void PostToUI(Action action) => Pending.Enqueue(action);
		public static void Drain()
		{
			while (Pending.TryDequeue(out var action))
			{
				action();
			}
		}
	}
}
namespace Microsoft.UI.Xaml.Input
{
	internal sealed class InputScope
	{
		public IList<InputScopeName> Names { get; } = new List<InputScopeName>();
	}
	internal sealed class InputScopeName(InputScopeNameValue nameValue)
	{
		public InputScopeNameValue NameValue { get; set; } = nameValue;
	}
}
namespace Uno.WinUI.Runtime.Skia.Wayland.Input
{
	internal sealed class WaylandKeyboardInputSource
	{
	}
}
