#if __SKIA__
using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Private.Infrastructure;
using Uno.UI.RuntimeTests.Helpers;
using Uno.UI.Helpers;
using Uno.UI.Xaml.Core;
using Uno.UI.Xaml.Islands;

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Media;

[TestClass]
public class Given_CompositionTarget
{
	[TestMethod]
	[RunsOnUIThread]
	[DataRow(false)]
	[DataRow(true)]
	public void When_LcdRootCoverageIsOpaque(bool desktopRoot)
	{
		Panel root = desktopRoot ? new XamlIslandRoot() : new RootVisual(CoreServices.Instance);
		root.Background = new SolidColorBrush(Colors.White);
		root.Visual.Size = new(100, 100);
		Assert.IsTrue(SkiaRenderHelper.HasLcdRootCoverage(root, 100, 100));
		Assert.IsFalse(SkiaRenderHelper.HasLcdRootCoverage(root, 101, 100));
		root.Background = new SolidColorBrush(Colors.Transparent);
		Assert.IsFalse(SkiaRenderHelper.HasLcdRootCoverage(root, 100, 100));
		root.Background = new SolidColorBrush(Colors.White) { Opacity = 0.5 };
		Assert.IsFalse(SkiaRenderHelper.HasLcdRootCoverage(root, 100, 100));
		root.Background = new SolidColorBrush(Colors.White);
		root.Opacity = 0.5;
		Assert.IsFalse(SkiaRenderHelper.HasLcdRootCoverage(root, 100, 100));
	}

	[TestMethod]
	[RunsOnUIThread]
	public async Task When_SkipVisualTreePainting()
	{
		var border = new Border { Width = 100, Height = 100, Background = new SolidColorBrush(Colors.Red) };

		Assert.IsFalse(FeatureConfiguration.Rendering.SkipVisualTreePainting);
		FeatureConfiguration.Rendering.SkipVisualTreePainting = true;
		try
		{
			await UITestHelper.Load(border);

			var target = (CompositionTarget)border.Visual.CompositionTarget!;
			var frameRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Action onFrameRendered = () => frameRendered.TrySetResult();
			target.FrameRendered += onFrameRendered;
			try
			{
				// A property change must still schedule and produce a (blank) frame.
				border.Background = new SolidColorBrush(Colors.Blue);
				await Task.WhenAny(frameRendered.Task, Task.Delay(2000));
				Assert.IsTrue(frameRendered.Task.IsCompleted, "The rendering pipeline should keep producing frames while painting is skipped.");
			}
			finally
			{
				target.FrameRendered -= onFrameRendered;
			}

			// RenderTargetBitmap-based screenshots don't go through the frame pipeline and must still capture actual content.
			var screenshot = await UITestHelper.ScreenShot(border);
			ImageAssert.HasColorAt(screenshot, 50, 50, Colors.Blue, tolerance: 5);
		}
		finally
		{
			FeatureConfiguration.Rendering.SkipVisualTreePainting = false;
		}
	}
}
#endif
