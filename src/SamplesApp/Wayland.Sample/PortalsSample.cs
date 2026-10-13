// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;

namespace Wayland.Sample;

// Exercises the desktop integration services when UNO_WAYLAND_SAMPLE_PORTALS=1; results go to stdout.
internal static class PortalsSample
{
	internal static bool IsEnabled => Environment.GetEnvironmentVariable("UNO_WAYLAND_SAMPLE_PORTALS") == "1";

	internal static void Attach(Panel panel)
	{
		var textBox = new TextBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Center };
		textBox.TextCompositionStarted += (_, e) => Console.WriteLine($"portals: composition started at {e.StartIndex}");
		textBox.TextCompositionChanged += (_, e) => Console.WriteLine($"portals: composition changed '{textBox.Text}'");
		textBox.TextCompositionEnded += (_, e) => Console.WriteLine($"portals: composition ended '{textBox.Text}'");
		textBox.TextChanged += (_, _) => Console.WriteLine($"portals: text '{textBox.Text}'");
		var other = new Button { Content = "Unfocus", HorizontalAlignment = HorizontalAlignment.Center };
		panel.Children.Add(textBox);
		panel.Children.Add(other);

		textBox.Loaded += async (_, _) =>
		{
			Console.WriteLine($"portals: requested theme {Application.Current.RequestedTheme}, text scale {new UISettings().TextScaleFactor}");
			LogOpenGLVersion(textBox.XamlRoot!);
			await Task.Delay(1500);
			Console.WriteLine($"portals: focus {textBox.Focus(FocusState.Programmatic)}");
			await Task.Delay(1000);
			Console.WriteLine($"portals: unfocus {other.Focus(FocusState.Programmatic)}");
			await Task.Delay(500);
			Console.WriteLine($"portals: refocus {textBox.Focus(FocusState.Programmatic)}");
			await Task.Delay(1500);
			Console.WriteLine($"portals: resize {ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size(900, 560))}");
			await Task.Delay(1500);
			var picker = new FileOpenPicker();
			picker.FileTypeFilter.Add("*");
			Console.WriteLine("portals: opening file picker");
			var file = await picker.PickSingleFileAsync();
			Console.WriteLine($"portals: picked {file?.Path ?? "<none>"}");
		};
	}

	private delegate nint GetStringDelegate(uint name);

	// INativeOpenGLWrapper is internal, the way GLCanvasElement consumes it; reflection is fine in this test head.
	private static void LogOpenGLVersion(XamlRoot xamlRoot)
	{
		try
		{
			var wrapperType = Type.GetType("Uno.Graphics.INativeOpenGLWrapper, Uno.UI", throwOnError: true)!;
			var create = typeof(Uno.Foundation.Extensibility.ApiExtensibility).GetMethod("CreateInstance", 1, [typeof(object), Type.MakeGenericMethodParameter(0).MakeByRefType()])!.MakeGenericMethod(wrapperType);
			var arguments = new object?[] { xamlRoot, null };
			if (!(bool)create.Invoke(null, arguments)!)
			{
				Console.WriteLine("portals: no INativeOpenGLWrapper registered");
				return;
			}
			using var wrapper = (IDisposable)arguments[1]!;
			using var current = (IDisposable)wrapperType.GetMethod("MakeCurrent")!.Invoke(wrapper, null)!;
			var getString = Marshal.GetDelegateForFunctionPointer<GetStringDelegate>((nint)wrapperType.GetMethod("GetProcAddress")!.Invoke(wrapper, ["glGetString"])!);
			Console.WriteLine($"portals: GL_VERSION {Marshal.PtrToStringUTF8(getString(0x1F02))}, GL_RENDERER {Marshal.PtrToStringUTF8(getString(0x1F01))}");
		}
		catch (Exception error)
		{
			Console.WriteLine($"portals: OpenGL wrapper failed: {error}");
		}
	}
}
