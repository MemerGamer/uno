// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Uno.UI.Hosting;

namespace Wayland.Sample;

public static class Program
{
	[STAThread]
	public static void Main()
	{
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine($"Unhandled: {e.ExceptionObject}");
		// Same as the app-head IcuDataInitializerGenerator output, which this code-only head does not run.
		Type.GetType("Microsoft.UI.Xaml.Documents.UnicodeText+ICU, Uno.UI")
			?.GetMethod("SetDataAssembly", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
			?.Invoke(null, [typeof(App).Assembly]);
		// Never fall back silently: this sample exists to exercise the Wayland host.
		Environment.SetEnvironmentVariable("UNO_SKIA_HOST", "wayland");
		var host = UnoPlatformHostBuilder.Create()
			.App(() => new App())
			.UseWayland()
			.Build();
		host.Run();
		Console.WriteLine("main: host returned");
	}
}

public sealed class App : Application
{
	// Fixed layout so injected input can target logical coordinates: the test compositor places the window at 0,0 without decorations.
	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		var window = new Window { Title = "Uno Wayland sample" };
		var white = new SolidColorBrush(Colors.White);
		var counter = new TextBlock { Text = "Clicks: 0", FontSize = 24, Foreground = white };
		var clicks = 0;
		var button = new Button { Content = "Click me", Width = 160, Height = 48 };
		button.Click += (_, _) =>
		{
			counter.Text = $"Clicks: {++clicks}";
			Console.WriteLine($"Button: clicked ({clicks})");
		};
		button.PointerEntered += (_, e) => Console.WriteLine($"Button: pointer entered at {e.GetCurrentPoint(null).Position}");
		button.PointerExited += (_, e) => Console.WriteLine($"Button: pointer exited at {e.GetCurrentPoint(null).Position}");
		var textBox = new TextBox { Width = 300, PlaceholderText = "Type here" };
		textBox.TextChanged += (_, _) => Console.WriteLine($"TextBox: text=[{textBox.Text}] selection={textBox.SelectionStart}+{textBox.SelectionLength}");
		textBox.SelectionChanged += (_, _) => Console.WriteLine($"TextBox: selection={textBox.SelectionStart}+{textBox.SelectionLength}");
		var items = new StackPanel();
		for (var i = 0; i < 40; i++)
		{
			items.Children.Add(new TextBlock { Text = $"Row {i}", FontSize = 18, Foreground = white });
		}
		var scroller = new ScrollViewer { Width = 300, Height = 200, Content = items, Background = new SolidColorBrush(Colors.DarkSlateBlue) };
		scroller.ViewChanged += (_, e) => { if (!e.IsIntermediate) { Console.WriteLine($"ScrollViewer: offset={scroller.VerticalOffset}"); } };
		var canvas = new Canvas { Background = new SolidColorBrush(Colors.SteelBlue) };
		Place(canvas, new TextBlock { Text = "Hello from native Wayland", FontSize = 28, Foreground = white }, 40, 0);
		Place(canvas, button, 40, 60);
		Place(canvas, counter, 220, 68);
		Place(canvas, textBox, 40, 140);
		Place(canvas, scroller, 40, 220);
		canvas.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => Console.WriteLine($"Key: down {e.Key} repeat={e.KeyStatus.WasKeyDown}")), true);
		canvas.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => Console.WriteLine($"Pointer: moved {e.GetCurrentPoint(null).Position}")), true);
		canvas.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) => Console.WriteLine($"Pointer: wheel {e.GetCurrentPoint(null).Properties.MouseWheelDelta}")), true);
		if (Environment.GetEnvironmentVariable("WAYLAND_SAMPLE_ANIMATE") == "1")
		{
			// Continuous animation: one UI frame per Rendering tick, reported once per second.
			var frames = 0;
			var frameText = new TextBlock { FontSize = 18, Foreground = white };
			Place(canvas, frameText, 400, 68);
			CompositionTarget.Rendering += (_, _) => frameText.Text = $"UI frames: {++frames}";
			var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
			timer.Tick += (_, _) => Console.WriteLine($"UI frames: {frames}");
			timer.Start();
		}
		if (int.TryParse(Environment.GetEnvironmentVariable("WAYLAND_SAMPLE_EXIT_AFTER_MS"), out var exitAfter))
		{
			var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(exitAfter) };
			exitTimer.Tick += (_, _) =>
			{
				exitTimer.Stop();
				Console.WriteLine("main: Application.Exit");
				Current.Exit();
			};
			exitTimer.Start();
		}
		if (PortalsSample.IsEnabled)
		{
			var portals = new StackPanel { Spacing = 8 };
			PortalsSample.Attach(portals);
			Place(canvas, portals, 400, 140);
		}
		Place(canvas, WindowScenario.CreateControls(window), 40, 440);
		// Pinned to the bottom-right corner, so a frame showing an older layout at a new size is visible in frame dumps.
		var edgeMarker = new Border { Width = 16, Height = 16, Background = new SolidColorBrush(Colors.Magenta), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
		window.Content = TitleBarSample.Wrap(window, new Grid { Children = { canvas, edgeMarker } });
		// The canvas has a background, so it is hit-testable as a drop target.
		DataTransferProbe.Attach(canvas);
		window.Activated += (_, e) => Console.WriteLine($"main: activated {e.WindowActivationState}");
		window.Activate();
		var display = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
		void ReportDisplay() => Console.WriteLine($"DisplayInformation: {display.ScreenWidthInRawPixels}x{display.ScreenHeightInRawPixels} scale {display.RawPixelsPerViewPixel} dpi {display.LogicalDpi} diagonal {display.DiagonalSizeInInches?.ToString() ?? "n/a"}");
		display.DpiChanged += (_, _) => ReportDisplay();
		ReportDisplay();
		Console.WriteLine("OnLaunched: window activated");
		WindowScenario.RunIfRequested(window);
	}

	private static void Place(Canvas canvas, UIElement element, double left, double top)
	{
		Canvas.SetLeft(element, left);
		Canvas.SetTop(element, top);
		canvas.Children.Add(element);
	}

	public App()
	{
		Resources.MergedDictionaries.Add(new XamlControlsResources());
		UnhandledException += (_, e) => Console.Error.WriteLine($"Unhandled: {e.Exception}");
	}
}
