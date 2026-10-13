#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.WinUI.Runtime.Skia.Wayland.Input;

namespace Wayland.Scanner.Tests;

[TestClass]
public sealed class KeyboardComposeTests
{
	[TestMethod]
	public void DeadKeyIsConsumedAndComposes()
	{
		WithCompose(compose =>
		{
			Assert.IsNull(compose.Feed(0xfe51, 0)); // dead_acute
			Assert.AreEqual(0xe9u, compose.Feed('e', 'e'));
			Assert.AreEqual((uint)'a', compose.Feed('a', 'a'));
		});
	}

	[TestMethod]
	public void ModifiersPassThroughAComposeSequence()
	{
		WithCompose(compose =>
		{
			Assert.IsNull(compose.Feed(0xff20, 0)); // Multi_key
			Assert.AreEqual(0u, compose.Feed(0xffe1, 0)); // Shift_L
			Assert.IsNull(compose.Feed('o', 'o'));
			Assert.AreEqual(0xa9u, compose.Feed('c', 'c'));
		});
	}

	[TestMethod]
	public void CancelledSequenceConsumesTheKeyAndResetClearsIt()
	{
		WithCompose(compose =>
		{
			Assert.IsNull(compose.Feed(0xff20, 0)); // Multi_key
			Assert.IsNull(compose.Feed(0xfe51, 0)); // dead_acute: no such sequence
			Assert.AreEqual((uint)'e', compose.Feed('e', 'e'));
			Assert.IsNull(compose.Feed(0xfe51, 0));
			compose.Reset();
			Assert.AreEqual((uint)'e', compose.Feed('e', 'e'));
		});
	}

	private static void WithCompose(Action<WaylandKeyboardCompose> test)
	{
		var context = WaylandXkb.ContextNew(0);
		Assert.AreNotEqual((nint)0, context);
		try
		{
			using var compose = new WaylandKeyboardCompose(context, "en_US.UTF-8");
			test(compose);
		}
		finally
		{
			WaylandXkb.ContextUnref(context);
		}
	}
}
