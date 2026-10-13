// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
	}
}

public sealed class App : Application
{
	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		var window = new Window { Title = "Uno Wayland sample" };
		var counter = new TextBlock { Text = "Clicks: 0", FontSize = 24, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center };
		var clicks = 0;
		var button = new Button { Content = "Click me", HorizontalAlignment = HorizontalAlignment.Center };
		button.Click += (_, _) => counter.Text = $"Clicks: {++clicks}";
		var panel = new StackPanel { Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
		panel.Children.Add(new TextBlock { Text = "Hello from native Wayland", FontSize = 36, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center });
		panel.Children.Add(new Border { Width = 240, Height = 80, Background = new SolidColorBrush(Colors.Orange), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center });
		panel.Children.Add(counter);
		panel.Children.Add(button);
		window.Content = new Grid { Background = new SolidColorBrush(Colors.SteelBlue), Children = { panel } };
		if (Environment.GetEnvironmentVariable("WAYLAND_SAMPLE_ANIMATE") == "1")
		{
			// Continuous animation: one UI frame per Rendering tick, reported once per second.
			var frames = 0;
			var frameText = new TextBlock { FontSize = 18, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center };
			panel.Children.Add(frameText);
			CompositionTarget.Rendering += (_, _) => frameText.Text = $"UI frames: {++frames}";
			var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
			timer.Tick += (_, _) => Console.WriteLine($"UI frames: {frames}");
			timer.Start();
		}
		window.Activate();
		var display = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
		void ReportDisplay() => Console.WriteLine($"DisplayInformation: {display.ScreenWidthInRawPixels}x{display.ScreenHeightInRawPixels} scale {display.RawPixelsPerViewPixel} dpi {display.LogicalDpi} diagonal {display.DiagonalSizeInInches?.ToString() ?? "n/a"}");
		display.DpiChanged += (_, _) => ReportDisplay();
		ReportDisplay();
		Console.WriteLine("OnLaunched: window activated");
	}

	public App()
	{
		UnhandledException += (_, e) => Console.Error.WriteLine($"Unhandled: {e.Exception}");
	}
}
