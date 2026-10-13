#nullable enable

using System;
using Microsoft.UI.Xaml;
using Uno.ApplicationModel.Core;
using Uno.Extensions.Storage.Pickers;
using Uno.Extensions.System;
using Uno.Foundation.Extensibility;
using Uno.Graphics;
using Uno.Helpers.Theming;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Extensions.System;
using Uno.UI.Runtime.Linux;
using Uno.UI.Xaml.Controls;
using Uno.UI.Xaml.Controls.Extensions;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace Uno.UI.Runtime.Wayland;

internal static class WaylandRegistrations
{
	private static bool _registered;

	// Called once the Wayland host is selected and connected; probing never registers services.
	internal static void Register(WaylandConnection connection, WaylandCoreApplicationExtension coreApplication)
	{
		if (_registered)
		{
			return;
		}
		_registered = true;
		ApiExtensibility.Register(typeof(ICoreApplicationExtension), _ => coreApplication);
		ApiExtensibility.Register(typeof(INativeWindowFactoryExtension), _ => new WaylandNativeWindowFactoryExtension(connection));
		ApiExtensibility.Register(typeof(Windows.Graphics.Display.IDisplayInformationExtension), owner => new WaylandDisplayInformationExtension(owner, connection));
		ApiExtensibility.Register<IXamlRootHost>(typeof(IUnoCorePointerInputSource), host => ((WaylandXamlRootHost)host).PointerSource);
		ApiExtensibility.Register<IXamlRootHost>(typeof(IUnoKeyboardInputSource), host => ((WaylandXamlRootHost)host).KeyboardSource);
		ApiExtensibility.Register(typeof(ILauncherExtension), owner => new LinuxLauncherExtension(owner));
		if (WaylandImeTextBoxExtension.TryCreate(connection) is { } ime)
		{
			ApiExtensibility.Register(typeof(IImeTextBoxExtension), _ => ime);
			ApiExtensibility.Register(typeof(ITextBoxNotificationsProviderSingleton), _ => new WaylandTextBoxNotificationsProviderSingleton(ime));
		}
		var portalParentWindow = new WaylandPortalParentWindowProvider(connection);
		ApiExtensibility.Register<FileOpenPicker>(typeof(IFileOpenPickerExtension), picker => new LinuxFilePickerExtension(picker, portalParentWindow));
		ApiExtensibility.Register<FolderPicker>(typeof(IFolderPickerExtension), picker => new LinuxFilePickerExtension(picker, portalParentWindow));
		ApiExtensibility.Register<FileSavePicker>(typeof(IFileSavePickerExtension), picker => new LinuxFileSaverExtension(picker, portalParentWindow));
		ApiExtensibility.Register<XamlRoot>(typeof(INativeOpenGLWrapper), xamlRoot => new WaylandNativeOpenGLWrapper(xamlRoot, connection));
		ApiExtensibility.Register(typeof(ISystemThemeHelperExtension), _ => LinuxSystemThemeHelper.Instance);
		ApiExtensibility.Register(typeof(ITextScaleFactorExtension), _ => LinuxTextScaleFactorExtension.Instance);
	}
}

internal sealed class WaylandNativeWindowFactoryExtension : INativeWindowFactoryExtension
{
	private readonly WaylandConnection _connection;

	internal WaylandNativeWindowFactoryExtension(WaylandConnection connection) => _connection = connection;

	public bool SupportsClosingCancellation => true;
	public bool SupportsMultipleWindows => true;
	public INativeWindowWrapper CreateWindow(Window window, XamlRoot xamlRoot) => new WaylandWindowWrapper(_connection, window, xamlRoot);
}

internal sealed class WaylandCoreApplicationExtension : ICoreApplicationExtension
{
	private readonly Func<WaylandConnection?> _connection;
	private volatile bool _exitRequested;

	internal WaylandCoreApplicationExtension(Func<WaylandConnection?> connection) => _connection = connection;

	public bool CanExit => true;

	internal bool ExitRequested => _exitRequested;

	public void Exit()
	{
		_exitRequested = true;
		_connection()?.CloseAllWindows();
	}
}
