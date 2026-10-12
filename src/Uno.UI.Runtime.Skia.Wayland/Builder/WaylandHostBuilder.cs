// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Uno.WinUI.Runtime.Skia.Wayland;

namespace Uno.UI.Hosting;

public sealed class WaylandHostBuilder : IPlatformHostBuilder
{
	internal WaylandHostBuilder() { }

	bool IPlatformHostBuilder.IsSupported => WaylandApplicationHost.IsSupported();

	UnoPlatformHost IPlatformHostBuilder.Create(Func<Microsoft.UI.Xaml.Application> appBuilder, Type applicationType)
		=> new WaylandApplicationHost(appBuilder);
}
