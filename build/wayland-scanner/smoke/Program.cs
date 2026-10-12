#nullable enable
using Uno.WinUI.Runtime.Skia.Wayland;

try
{
	using var display = WaylandDisplay.Connect();
	foreach (var global in display.Globals)
	{
		Console.WriteLine($"{global.Name}: {global.Interface} v{global.Version}");
	}
	if (args.Contains("--dispatch"))
	{
		display.StartDispatch();
		Thread.Sleep(100);
		display.StopDispatch();
		if (display.DispatchError is { } error)
		{
			throw error;
		}
	}
	return 0;
}
catch (Exception error)
{
	Console.Error.WriteLine(error.Message);
	return 1;
}
