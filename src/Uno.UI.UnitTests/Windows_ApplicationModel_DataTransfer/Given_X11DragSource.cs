// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Runtime.X11;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Uno.UI.Tests.Windows_ApplicationModel_DataTransfer;

[TestClass]
public sealed class Given_X11DragSource
{
	private const DataPackageOperation AllOperations = DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link;
	private static DataPackage CreatePackage()
	{
		var package = new DataPackage();
		GC.SuppressFinalize(package);
		return package;
	}

	[TestMethod]
	public void PrivateOnlyAndClipboardVerbOfferNothing()
	{
		var package = CreatePackage();
		package.Properties["Files_ActionBinder"] = "private";
		package.SetData("UnoPrivate", new object());
		package.SetData(X11FileUris.GnomeCopiedFilesMimeType, "cut\nfile:///tmp/a");
		Assert.AreEqual(0, X11DragFormats.GetDragMimeTypes(package.GetView()).Count);
	}

	[TestMethod]
	public async Task FilesOfferUriListAndTextWithoutClipboardVerb()
	{
		var package = CreatePackage();
		package.SetData(X11FileUris.UriListMimeType, "# comment\r\nfile:///tmp/a%20b\r\nfile:///tmp/c\r\n");
		package.SetData(X11FileUris.GnomeCopiedFilesMimeType, "copy\nfile:///tmp/c");
		var formats = X11DragFormats.GetDragMimeTypes(package.GetView());
		Assert.AreEqual("text/uri-list", formats[0]);
		CollectionAssert.IsSubsetOf(X11DragFormats.TextMimeTypes, formats);
		CollectionAssert.DoesNotContain(formats, X11FileUris.GnomeCopiedFilesMimeType);
		Assert.AreEqual(1, formats.Count(format => format == "text/uri-list"));
		foreach (var target in X11DragFormats.TextMimeTypes)
		{
			Assert.AreEqual("file:///tmp/a%20b\nfile:///tmp/c", Encoding.UTF8.GetString((await X11DragFormats.GetBytesAsync(package.GetView(), target))!));
		}
	}

	[TestMethod]
	public async Task StorageItemsProduceEscapedUris()
	{
		var package = CreatePackage();
		package.SetStorageItems([await StorageFile.GetFileFromPathAsync("/tmp/a b"), await StorageFile.GetFileFromPathAsync("/tmp/é")]);
		var view = package.GetView();
		Assert.AreEqual("file:///tmp/a%20b\r\nfile:///tmp/%C3%A9\r\n", Encoding.UTF8.GetString((await X11DragFormats.GetBytesAsync(view, "text/uri-list"))!));
		Assert.AreEqual("file:///tmp/a%20b\nfile:///tmp/%C3%A9", Encoding.UTF8.GetString((await X11DragFormats.GetBytesAsync(view, "TEXT"))!));
	}

	[TestMethod]
	public async Task TextWinsOverFilesAndStringUsesLatin1()
	{
		var package = CreatePackage();
		package.SetData("text/uri-list", "file:///tmp/c\r\n");
		package.SetText("café");
		CollectionAssert.AreEqual(Encoding.Latin1.GetBytes("café"), (await X11DragFormats.GetBytesAsync(package.GetView(), "STRING"))!);
		CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("café"), (await X11DragFormats.GetBytesAsync(package.GetView(), "TEXT"))!);
		Assert.AreEqual("UTF8_STRING", X11DragFormats.ReplyType("TEXT"));
		Assert.AreEqual("STRING", X11DragFormats.ReplyType("STRING"));
		Assert.AreEqual("text/html", X11DragFormats.ReplyType("text/html"));
	}

	[TestMethod]
	public async Task HtmlBitmapAndCustomMimeAreOffered()
	{
		var package = CreatePackage();
		package.SetHtmlFormat("<b>hello</b>");
		package.SetBitmap(RandomAccessStreamReference.CreateFromStream(new MemoryStream().AsRandomAccessStream()));
		package.SetData("application/x-uno-test", new byte[] { 1, 2, 3 });
		var view = package.GetView();
		CollectionAssert.AreEqual(new[] { "image/png", "text/html", "application/x-uno-test" }, X11DragFormats.GetDragMimeTypes(view));
		CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, (await X11DragFormats.GetBytesAsync(view, "application/x-uno-test"))!);
		Assert.AreEqual("<b>hello</b>", Encoding.UTF8.GetString((await X11DragFormats.GetBytesAsync(view, "text/html"))!));
	}

	[TestMethod]
	public async Task BitmapPngIsPassedThroughAndBorrowedStreamRemainsOpen()
	{
		var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");
		using var stream = new MemoryStream(png);
		var package = CreatePackage();
		package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream.AsRandomAccessStream()));
		CollectionAssert.AreEqual(png, (await X11DragFormats.GetBytesAsync(package.GetView(), "image/png"))!);
		Assert.IsTrue(stream.CanRead);
	}

	[TestMethod]
	public async Task UriAndWebLinkAreText()
	{
		var package = CreatePackage();
		package.SetWebLink(new Uri("https://example.org/path"));
		CollectionAssert.AreEqual(X11DragFormats.TextMimeTypes, X11DragFormats.GetDragMimeTypes(package.GetView()));
		Assert.AreEqual("https://example.org/path", Encoding.UTF8.GetString((await X11DragFormats.GetBytesAsync(package.GetView(), "UTF8_STRING"))!));
	}

	[TestMethod]
	public void LocalUrisRoundTripAndRemoteUrisAreIgnored()
	{
		var paths = new[] { "/tmp/a b", "/tmp/é#%" };
		CollectionAssert.AreEqual(paths, X11FileUris.ParseUriList(X11FileUris.FormatUriList(paths)));
		CollectionAssert.AreEqual(new[] { "/tmp/a" }, X11FileUris.ParseUriList("file://localhost/tmp/a\nfile://remote/tmp/b\nhttps://example.org\n"));
	}

	[TestMethod]
	[DataRow(0u, DataPackageOperation.Copy)]
	[DataRow(1u, DataPackageOperation.Move)]
	[DataRow(4u, DataPackageOperation.Copy)]
	[DataRow(5u, DataPackageOperation.Link)]
	public void ModifiersChooseAction(uint mask, DataPackageOperation expected)
		=> Assert.AreEqual(expected, X11DragActions.Choose(mask, DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link));

	[TestMethod]
	public void DisallowedRequestsUseFirstAllowedAction()
	{
		Assert.AreEqual(DataPackageOperation.Move, X11DragActions.Choose(4, DataPackageOperation.Move | DataPackageOperation.Link));
		Assert.AreEqual(DataPackageOperation.Link, X11DragActions.Choose(1, DataPackageOperation.Link));
		Assert.AreEqual(DataPackageOperation.Copy, X11DragActions.Choose(5, DataPackageOperation.None));
		foreach (var action in new[] { DataPackageOperation.Copy, DataPackageOperation.Move, DataPackageOperation.Link })
		{
			Assert.AreEqual(action, X11DragActions.FromName(X11DragActions.ToName(action)));
		}
		Assert.AreEqual(DataPackageOperation.Copy, X11DragActions.FromName("XdndActionPrivate"));
		Assert.AreEqual(DataPackageOperation.Copy, X11DragActions.FromName("XdndActionAsk"));
		Assert.AreEqual(DataPackageOperation.Copy, X11DragActions.FromName("unknown"));
	}

	[TestMethod]
	[DataRow(0u, 0u)]
	[DataRow(0x1800u, 0u)]
	[DataRow(0x100u, 1u)]
	[DataRow(0x600u, 2u)]
	[DataRow(0x400u, 3u)]
	[DataRow(0x1f00u, 1u)]
	public void InitiatingButtonIsLowestHeldNonWheelButton(uint mask, uint button)
		=> Assert.AreEqual(button, X11DragActions.ChooseButton(mask));

	[TestMethod]
	public void OnlyInitiatingButtonControlsReleaseAndPolling()
	{
		foreach (var initiating in new[] { 1u, 2u, 3u })
		{
			var bit = 0x100u << (int)(initiating - 1);
			Assert.IsTrue(X11DragActions.IsButtonHeld(bit | 0x1800u, initiating));
			Assert.IsFalse(X11DragActions.IsButtonHeld(0x1f00u & ~bit, initiating));
			for (uint released = 1; released <= 7; released++)
			{
				Assert.AreEqual(released == initiating, X11DragActions.IsReleaseButton(released, initiating));
			}
		}
		Assert.IsFalse(X11DragActions.IsButtonHeld(0x1f00u, 0));
		Assert.IsFalse(X11DragActions.IsReleaseButton(0, 0));
	}

	[TestMethod]
	[DataRow(0u, 100u, true)]
	[DataRow(99u, 100u, false)]
	[DataRow(100u, 100u, true)]
	[DataRow(101u, 100u, true)]
	[DataRow(0xfffffff0u, 0x10u, false)]
	[DataRow(0x10u, 0xfffffff0u, true)]
	[DataRow(0x80000010u, 0x80000020u, false)]
	[DataRow(0x80000020u, 0x80000010u, true)]
	public void SelectionTimeRejectsOldRequestsAcrossWrap(uint time, uint ownerTime, bool valid)
		=> Assert.AreEqual(valid, X11DragSourceState.IsSelectionTimeValid(time, ownerTime));

	[TestMethod]
	public void CoordinatesAndEnterFlagsUseWireLayout()
	{
		Assert.AreEqual(0xffff8000u, X11DragActions.PackPosition(-1, -32768));
		Assert.AreEqual(0x01234567u, X11DragActions.PackPosition(0x123, 0x4567));
		Assert.AreEqual(5u << 24, X11DragActions.EnterFlags(5, 3));
		Assert.AreEqual((5u << 24) | 1u, X11DragActions.EnterFlags(5, 4));
	}

	[TestMethod]
	public void OnlyOnePositionIsOutstandingAndNewestWaitsForStatus()
	{
		var state = new X11DragSourceState();
		state.PositionChanged();
		Assert.IsTrue(state.CanSendPosition(0));
		state.PositionSent(0, DataPackageOperation.Copy);
		state.PositionChanged();
		Assert.IsFalse(state.CanSendPosition(249));
		Assert.IsFalse(state.CanSendPosition(250));
		Assert.IsFalse(state.CanSendPosition(10_000));
		state.Status(true, DataPackageOperation.Copy, AllOperations);
		Assert.IsTrue(state.CanSendPosition(100));
		state.PositionSent(100, DataPackageOperation.Move);
		state.Status(true, DataPackageOperation.Move, AllOperations);
		Assert.IsFalse(state.CanSendPosition(200));
	}

	[TestMethod]
	public void DelayedStatusOnlyAcknowledgesItsPositionAndLatestStatusDecidesDrop()
	{
		var state = new X11DragSourceState();
		state.PositionChanged();
		state.PositionSent(0, DataPackageOperation.Copy);
		state.PositionChanged();
		Assert.IsFalse(state.CanSendPosition(300));
		state.PositionSent(300, DataPackageOperation.Move);
		Assert.AreEqual(DataPackageOperation.Copy, state.RequestedAction);
		state.Release(310);
		state.Status(true, DataPackageOperation.Copy, AllOperations);
		Assert.AreEqual(X11DragReleaseDecision.Wait, state.ReleaseDecision(320));
		Assert.IsTrue(state.CanSendPosition(320));
		state.PositionSent(320, DataPackageOperation.Move);
		Assert.AreEqual(X11DragReleaseDecision.Wait, state.ReleaseDecision(330));
		state.Status(true, DataPackageOperation.Move, AllOperations);
		Assert.AreEqual(X11DragReleaseDecision.Drop, state.ReleaseDecision(340));
		Assert.AreEqual(DataPackageOperation.Move, state.StatusAction);
	}

	[TestMethod]
	public void LatestStatusCanRejectAfterAnOlderPositionWasAccepted()
	{
		var state = new X11DragSourceState();
		state.PositionChanged();
		state.PositionSent(0, DataPackageOperation.Copy);
		state.PositionChanged();
		state.Release(300);
		state.Status(true, DataPackageOperation.Copy, AllOperations);
		Assert.AreEqual(X11DragReleaseDecision.Wait, state.ReleaseDecision(310));
		state.PositionSent(310, DataPackageOperation.Move);
		state.Status(false, DataPackageOperation.None, AllOperations);
		Assert.AreEqual(X11DragReleaseDecision.Leave, state.ReleaseDecision(320));
	}

	[TestMethod]
	public void StatusAfterReleaseDeadlineCannotCauseADrop()
	{
		var state = new X11DragSourceState();
		state.PositionChanged();
		state.PositionSent(0, DataPackageOperation.Copy);
		state.PositionChanged();
		state.Release(10);
		state.Status(true, DataPackageOperation.Copy, AllOperations);
		Assert.IsFalse(state.CanSendPosition(510));
		Assert.AreEqual(X11DragReleaseDecision.Leave, state.ReleaseDecision(510));
	}

	[TestMethod]
	public void RejectedReleaseLeavesAndUnansweredReleaseTimesOut()
	{
		var state = new X11DragSourceState();
		state.PositionChanged();
		state.PositionSent(0, DataPackageOperation.Copy);
		state.Release(0);
		Assert.AreEqual(X11DragReleaseDecision.Wait, state.ReleaseDecision(499));
		Assert.AreEqual(X11DragReleaseDecision.Leave, state.ReleaseDecision(500));
		state.Status(false, DataPackageOperation.None, AllOperations);
		Assert.AreEqual(X11DragReleaseDecision.Leave, state.ReleaseDecision(100));
	}

	private static X11DragSourceState Dropped(DataPackageOperation operation = DataPackageOperation.Move)
	{
		var state = new X11DragSourceState();
		state.PositionChanged();
		state.PositionSent(0, operation);
		state.Status(true, operation, AllOperations);
		state.Release(10);
		state.Drop(10);
		return state;
	}

	[TestMethod]
	[DataRow(3)]
	[DataRow(4)]
	public void OlderFinishedUsesLastStatusAction(int version)
	{
		var state = Dropped();
		Assert.IsTrue(state.Finished(version, false, DataPackageOperation.Copy, DataPackageOperation.Move));
		Assert.AreEqual(DataPackageOperation.Move, state.Result);
	}

	[TestMethod]
	public void VersionFiveFinishedChecksAcceptanceAndMissingAction()
	{
		var rejected = Dropped();
		rejected.Finished(5, false, DataPackageOperation.Move, DataPackageOperation.Move);
		Assert.AreEqual(DataPackageOperation.None, rejected.Result);
		var accepted = Dropped();
		accepted.Finished(5, true, null, DataPackageOperation.Move);
		Assert.AreEqual(DataPackageOperation.Move, accepted.Result);
		Assert.IsFalse(accepted.Finished(5, false, null, DataPackageOperation.Move));
	}

	[TestMethod]
	public void DisallowedFinishedActionIsNoneAndTimeoutIsNone()
	{
		var accepted = Dropped();
		accepted.Finished(5, true, DataPackageOperation.Link, DataPackageOperation.Move);
		Assert.AreEqual(DataPackageOperation.None, accepted.Result);
		var timeout = Dropped();
		Assert.IsFalse(timeout.Timeout(60_009));
		Assert.IsTrue(timeout.Timeout(60_010));
		Assert.AreEqual(DataPackageOperation.None, timeout.Result);
		Assert.IsFalse(timeout.Complete(DataPackageOperation.Move));
	}

	[TestMethod]
	[DataRow("XdndActionCopy", DataPackageOperation.Move, false)]
	[DataRow("XdndActionPrivate", DataPackageOperation.Move, false)]
	[DataRow("XdndActionPrivate", DataPackageOperation.Copy, true)]
	[DataRow("XdndActionMove", DataPackageOperation.Copy, false)]
	[DataRow("XdndActionLink", DataPackageOperation.None, false)]
	public void StatusOnlyAcceptsAllowedActions(string name, DataPackageOperation allowed, bool accepted)
	{
		var state = new X11DragSourceState();
		state.PositionChanged();
		state.PositionSent(0, X11DragActions.Choose(0, allowed));
		state.Status(true, X11DragActions.FromName(name), allowed);
		state.Release(10);
		Assert.AreEqual(accepted, state.Accepted);
		Assert.AreEqual(accepted ? X11DragActions.FromName(name) : DataPackageOperation.None, state.StatusAction);
		Assert.AreEqual(accepted ? X11DragReleaseDecision.Drop : X11DragReleaseDecision.Leave, state.ReleaseDecision(10));
	}

	[TestMethod]
	[DataRow(3, DataPackageOperation.Copy)]
	[DataRow(4, DataPackageOperation.Copy)]
	[DataRow(5, DataPackageOperation.Copy)]
	[DataRow(5, DataPackageOperation.None)]
	public void FinishedNeverSubstitutesARequestedAction(int version, DataPackageOperation action)
	{
		var state = new X11DragSourceState();
		state.PositionChanged();
		state.PositionSent(0, DataPackageOperation.Move);
		state.Status(true, DataPackageOperation.Copy, AllOperations);
		state.Drop(10);
		state.Finished(version, true, action, DataPackageOperation.Move);
		Assert.AreEqual(DataPackageOperation.None, state.Result);
	}

	[TestMethod]
	public void VersionFiveFinishedUsesItsAllowedActionOrLastStatusForNone()
	{
		var explicitAction = Dropped();
		explicitAction.Finished(5, true, DataPackageOperation.Copy, AllOperations);
		Assert.AreEqual(DataPackageOperation.Copy, explicitAction.Result);
		var missingAction = Dropped();
		missingAction.Finished(5, true, DataPackageOperation.None, AllOperations);
		Assert.AreEqual(DataPackageOperation.Move, missingAction.Result);
		var privateAction = Dropped();
		privateAction.Finished(5, true, X11DragActions.FromName("XdndActionPrivate"), DataPackageOperation.Move);
		Assert.AreEqual(DataPackageOperation.None, privateAction.Result);
	}

	[TestMethod]
	public void UnsolicitedStatusAndPrematureFinishedAreIgnored()
	{
		var state = new X11DragSourceState();
		state.Status(true, DataPackageOperation.Copy, AllOperations);
		Assert.IsFalse(state.Accepted);
		Assert.IsFalse(state.Finished(5, true, null, DataPackageOperation.Copy));
	}

	[TestMethod]
	public void IconUsesPhysicalPixelsAndLogicalAnchor()
	{
		Assert.AreEqual(new X11DragIconLayout(150, 90, -75, -68), X11DragIconLayout.Compute(150, 90, 1.5, null));
		Assert.AreEqual(new X11DragIconLayout(200, 100, -20, -10), X11DragIconLayout.Compute(200, 100, 2, (-10, -5)));
	}

	[TestMethod]
	public void OversizeIconAndAnchorShrinkTogether()
	{
		Assert.AreEqual(new X11DragIconLayout(1024, 512, -100, -50), X11DragIconLayout.Compute(2048, 1024, 2, (-100, -50)));
		Assert.AreEqual(new X11DragIconLayout(1024, 512, -512, -384), X11DragIconLayout.Compute(2048, 1024, 2, null));
		Assert.AreEqual(new X11DragIconLayout(1, 1, 0, -1), X11DragIconLayout.Compute(0, 0, 0, null));
	}
}
