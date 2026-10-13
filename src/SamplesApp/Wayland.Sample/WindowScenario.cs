// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Wayland.Sample;

// Window management controls, plus a scripted run (WAYLAND_SAMPLE_SCENARIO=window) for headless compositors.
internal static class WindowScenario
{
	internal static UIElement CreateControls(Window window)
	{
		var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
		void Add(string text, Action action)
		{
			var button = new Button { Content = text };
			button.Click += (_, _) => action();
			row.Children.Add(button);
		}
		Add("Maximize", () => Overlapped(window)?.Maximize());
		Add("Restore", () => Overlapped(window)?.Restore());
		Add("Minimize", () => Overlapped(window)?.Minimize());
		Add("Full screen", () => window.AppWindow.SetPresenter(window.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.FullScreen));
		Add("Extend title bar", () => window.ExtendsContentIntoTitleBar = !window.ExtendsContentIntoTitleBar);
		Add("New window", () => CreateSecondary().Activate());
		return row;
	}

	internal static void RunIfRequested(Window window)
	{
		if (Environment.GetEnvironmentVariable("WAYLAND_SAMPLE_SCENARIO") != "window")
		{
			return;
		}
		Window? secondary = null;
		var steps = new List<(string Name, Action Action)>
		{
			Step("initial", () => { }),
			Step("maximize", () => Overlapped(window)!.Maximize()),
			Step("restore", () => Overlapped(window)!.Restore()),
			Step("resize 900x600", () => window.AppWindow.Resize(new SizeInt32 { Width = 900, Height = 600 })),
			Step("TryResizeView 800x560", () => Console.WriteLine($"scenario: TryResizeView returned {Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size(800, 560))}")),
			Step("min size 700x500", () => { Overlapped(window)!.PreferredMinimumWidth = 700; Overlapped(window)!.PreferredMinimumHeight = 500; }),
			Step("full screen", () => window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)),
			Step("overlapped", () => window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped)),
			Step("extend title bar", () => window.ExtendsContentIntoTitleBar = true),
			Step("restore title bar", () => window.ExtendsContentIntoTitleBar = false),
			Step("not resizable", () => Overlapped(window)!.IsResizable = false),
			Step("resizable", () => Overlapped(window)!.IsResizable = true),
			Step("open secondary", () => { secondary = CreateSecondary(); secondary.Activate(); }),
			Step("close secondary", () => secondary?.Close()),
			Step("minimize", () => Overlapped(window)!.Minimize()),
			Step("restore and activate", () => Overlapped(window)!.Restore(true)),
			Step("rename", () => window.Title = "Uno Wayland sample (renamed)"),
			Step("icon", () => window.AppWindow.SetIcon(Environment.GetEnvironmentVariable("WAYLAND_SAMPLE_ICON") ?? "")),
		};
		var index = 0;
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
		timer.Tick += (_, _) =>
		{
			if (index > 0)
			{
				Log(window, steps[index - 1].Name);
			}
			if (index == steps.Count)
			{
				timer.Stop();
				Console.WriteLine("scenario: done");
				return;
			}
			Console.WriteLine($"scenario: {steps[index].Name}");
			steps[index++].Action();
		};
		timer.Start();
	}

	private static (string Name, Action Action) Step(string name, Action action) => (name, action);

	private static OverlappedPresenter? Overlapped(Window window) => window.AppWindow.Presenter as OverlappedPresenter;

	private static void Log(Window window, string step)
	{
		var appWindow = window.AppWindow;
		var state = Overlapped(window)?.State.ToString() ?? appWindow.Presenter.Kind.ToString();
		Console.WriteLine($"scenario: after {step}: presenter={appWindow.Presenter.Kind} state={state} size={appWindow.Size.Width}x{appWindow.Size.Height} client={appWindow.ClientSize.Width}x{appWindow.ClientSize.Height} bounds={window.Bounds.Width}x{window.Bounds.Height} title=\"{appWindow.Title}\"");
	}

	private static Window CreateSecondary()
	{
		var secondary = new Window { Title = "Secondary window" };
		secondary.Content = new Grid
		{
			Background = new SolidColorBrush(Colors.DarkSlateGray),
			Children = { new TextBlock { Text = "Secondary window", FontSize = 28, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
		};
		secondary.Activated += (_, e) => Console.WriteLine($"secondary: activated {e.WindowActivationState}");
		secondary.Closed += (_, _) => Console.WriteLine("secondary: closed");
		return secondary;
	}
}
