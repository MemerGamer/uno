#nullable enable

using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.RuntimeTests.Helpers;
using static Private.Infrastructure.TestServices;

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Controls;

[TestClass]
[RunsOnUIThread]
[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWayland)]
public sealed class Given_Wayland_ImeSession
{
	[TestMethod]
	public async Task When_Focus_Moves_Between_TextBox_And_RichEditBox()
	{
		var first = new TextBox { Text = "café 😀 日本語", Width = 240 };
		var second = new TextBox { Text = "second", Width = 240 };
		var rich = new RichEditBox { Width = 240, Height = 80 };
		rich.Document.SetText(TextSetOptions.None, "العربية 😀");
		var firstHost = ((ITextBoxHost)first).Core;
		var secondHost = ((ITextBoxHost)second).Core;
		var richHost = (IImeSessionHost)rich;
		try
		{
			await UITestHelper.Load(new StackPanel { Children = { first, second, rich } });
			Assert.IsTrue(first.Focus(FocusState.Programmatic));
			await WindowHelper.WaitForIdle();
			RequireWaylandExtension(firstHost);
			first.Select(5, 2);
			await WindowHelper.WaitForIdle();
			Assert.AreEqual(5, ((IImeSessionHost)firstHost).SelectionStart);
			Assert.AreEqual(2, ((IImeSessionHost)firstHost).SelectionLength);

			Assert.IsTrue(second.Focus(FocusState.Programmatic));
			await WindowHelper.WaitForIdle();
			Assert.IsNull(ImeSessionCoordinator.GetExtension(firstHost));
			RequireWaylandExtension(secondHost);

			Assert.IsTrue(rich.Focus(FocusState.Programmatic));
			await WindowHelper.WaitForIdle();
			Assert.IsNull(ImeSessionCoordinator.GetExtension(secondHost));
			RequireWaylandExtension(richHost);
			Assert.IsFalse(richHost.IsComposing);
		}
		finally
		{
			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();
		}
		Assert.IsNull(ImeSessionCoordinator.GetExtension(firstHost));
		Assert.IsNull(ImeSessionCoordinator.GetExtension(secondHost));
		Assert.IsNull(ImeSessionCoordinator.GetExtension(richHost));
	}

	private static void RequireWaylandExtension(IImeSessionHost host)
	{
		var extension = ImeSessionCoordinator.GetExtension(host);
		if (extension is null)
		{
			Assert.Inconclusive("The compositor must advertise text-input-v3 to exercise native Wayland IME sessions.");
			return;
		}
		Assert.AreEqual("Uno.UI.Runtime.Wayland", extension.GetType().Assembly.GetName().Name);
	}
}
