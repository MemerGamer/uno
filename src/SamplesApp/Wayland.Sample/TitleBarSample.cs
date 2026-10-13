// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Wayland.Sample;

// Content extended into the title bar (WAYLAND_SAMPLE_TITLEBAR): a 40 px custom title bar with a button at 200,4 (120x32).
//   default      no drag regions declared: the top title bar height is the caption
//   settitlebar  Window.SetTitleBar(titleBar)
//   regions      InputNonClientPointerSource caption over the title bar, passthrough over the button
internal static class TitleBarSample
{
	internal static UIElement Wrap(Window window, UIElement content)
	{
		var mode = Environment.GetEnvironmentVariable("WAYLAND_SAMPLE_TITLEBAR");
		if (mode is not ("default" or "settitlebar" or "regions"))
		{
			return content;
		}
		window.ExtendsContentIntoTitleBar = true;
		var titleBar = new Canvas { Height = 40, Background = new SolidColorBrush(Colors.DarkSlateGray) };
		var title = new TextBlock { Text = $"Custom title bar ({mode})", Foreground = new SolidColorBrush(Colors.White) };
		Canvas.SetLeft(title, 12);
		Canvas.SetTop(title, 10);
		titleBar.Children.Add(title);
		var clicks = 0;
		var button = new Button { Content = "TB button", Width = 120, Height = 32, Padding = new Thickness(0) };
		button.Click += (_, _) => Console.WriteLine($"TitleBar: button clicked ({++clicks})");
		Canvas.SetLeft(button, 200);
		Canvas.SetTop(button, 4);
		titleBar.Children.Add(button);
		titleBar.PointerPressed += (_, e) => Console.WriteLine($"TitleBar: pressed at {e.GetCurrentPoint(null).Position}");

		var root = new Grid();
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		Grid.SetRow((FrameworkElement)content, 1);
		root.Children.Add(titleBar);
		root.Children.Add(content);

		if (mode == "settitlebar")
		{
			window.SetTitleBar(titleBar);
		}
		else if (mode == "regions")
		{
			// Region rects are physical pixels, so they follow both the size and the rasterization scale.
			void UpdateRegions()
			{
				var scale = titleBar.XamlRoot?.RasterizationScale ?? 1;
				RectInt32 Scaled(double x, double y, double width, double height) => new() { X = (int)(x * scale), Y = (int)(y * scale), Width = (int)(width * scale), Height = (int)(height * scale) };
				var source = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
				source.SetRegionRects(NonClientRegionKind.Caption, [Scaled(0, 0, titleBar.ActualWidth, titleBar.ActualHeight)]);
				source.SetRegionRects(NonClientRegionKind.Passthrough, [Scaled(200, 4, 120, 32)]);
			}
			titleBar.SizeChanged += (_, _) => UpdateRegions();
			titleBar.Loaded += (_, _) => titleBar.XamlRoot!.Changed += (_, _) => UpdateRegions();
		}
		Console.WriteLine($"TitleBar: mode {mode}");
		return root;
	}
}
