// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;

namespace Uno.UI.Hosting;

public static class WaylandHostBuilderExtensions
{
	/// <summary>
	/// Adds the Wayland host before subsequent fallback hosts, such as UseX11().
	/// </summary>
	public static IUnoPlatformHostBuilder UseWayland(this IUnoPlatformHostBuilder builder)
		=> builder.UseWayland(_ => { });

	public static IUnoPlatformHostBuilder UseWayland(this IUnoPlatformHostBuilder builder, Action<WaylandHostBuilder> configure)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(configure);
		builder.AddHostBuilder(() =>
		{
			var hostBuilder = new WaylandHostBuilder();
			if (((IPlatformHostBuilder)hostBuilder).IsSupported)
			{
				configure(hostBuilder);
			}
			return hostBuilder;
		});
		return builder;
	}
}
