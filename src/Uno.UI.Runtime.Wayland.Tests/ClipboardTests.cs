#nullable enable

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.ApplicationModel.DataTransfer;
using Uno.UI.Runtime.Wayland.Protocols;

namespace Uno.UI.Runtime.Wayland.Tests;

[TestClass]
public sealed class ClipboardTests
{
	[TestMethod]
	public async Task NoInputSerialKeepsLocalCopyAfterPublicationIsCancelled()
	{
		var device = new ClipboardDevice();
		var clipboard = new WaylandClipboardExtension(device, action => action());
		var package = new DataPackage();
		package.SetText("unowned copy");
		clipboard.SetContent(package);
		Assert.AreEqual("unowned copy", await clipboard.GetContent().GetTextAsync());
		device.CancelPendingSelection();
		device.NotifySelection(null);
		Assert.AreEqual("unowned copy", await clipboard.GetContent().GetTextAsync());
		Assert.AreEqual(0, device.Published);
	}

	[TestMethod]
	public async Task ForeignOfferReplacesLocalCopyAndCancelsDeferredPublication()
	{
		var device = new ClipboardDevice();
		var clipboard = new WaylandClipboardExtension(device, action => action());
		var package = new DataPackage();
		package.SetText("local");
		clipboard.SetContent(package);
		device.NotifySelection("foreign");
		Assert.AreEqual("foreign", await clipboard.GetContent().GetTextAsync());
		device.Focus();
		Assert.AreEqual(0, device.Published);
		device.NotifySelection(null);
		Assert.AreEqual(0, clipboard.GetContent().AvailableFormats.Count);
	}

	[TestMethod]
	public void ClearHidesAnOlderForeignOfferBeforeOwnershipIsGranted()
	{
		var device = new ClipboardDevice();
		var clipboard = new WaylandClipboardExtension(device, action => action());
		device.NotifySelection("foreign");
		Assert.IsTrue(clipboard.GetContent().Contains(StandardDataFormats.Text));
		clipboard.Clear();
		Assert.AreEqual(0, clipboard.GetContent().AvailableFormats.Count);
	}

	[TestMethod]
	public async Task NoSeatKeepsLocalTextAndRichFormats()
	{
		var clipboard = new WaylandClipboardExtension(null, action => action());
		Assert.IsNotNull(clipboard.GetContent());
		Assert.AreEqual(0, clipboard.GetContent().AvailableFormats.Count);
		var package = new DataPackage();
		package.SetText("local copy");
		package.SetRtf("{\\rtf1 local copy}");
		clipboard.SetContent(package);
		clipboard.Flush();
		Assert.AreEqual("local copy", await clipboard.GetContent().GetTextAsync());
		Assert.AreEqual("{\\rtf1 local copy}", await clipboard.GetContent().GetRtfAsync());
		clipboard.Clear();
		Assert.IsNotNull(clipboard.GetContent());
		Assert.AreEqual(0, clipboard.GetContent().AvailableFormats.Count);
	}

	[TestMethod]
	public async Task NoSeatKeepsDeferredProviderAndLatestCopy()
	{
		var clipboard = new WaylandClipboardExtension(null, action => action());
		var calls = 0;
		var package = new DataPackage();
		package.SetDataProvider(StandardDataFormats.Text, request =>
		{
			calls++;
			request.SetData("deferred copy");
		});
		clipboard.SetContent(package);
		Assert.AreEqual(0, calls);
		Assert.AreEqual("deferred copy", await clipboard.GetContent().GetTextAsync());
		var replacement = new DataPackage();
		replacement.SetText("latest copy");
		clipboard.SetContent(replacement);
		Assert.AreEqual("latest copy", await clipboard.GetContent().GetTextAsync());
	}

	[TestMethod]
	public async Task LocalChangesNotifyWithoutCompositorEvents()
	{
		var clipboard = new WaylandClipboardExtension(null, action => action());
		var observed = new List<string>();
		clipboard.ContentChanged += (_, _) => observed.Add(clipboard.GetContent().Contains(StandardDataFormats.Text) ? "text" : "empty");
		clipboard.StartContentChanged();
		var package = new DataPackage();
		package.SetText("copy");
		clipboard.SetContent(package);
		Assert.AreEqual("copy", await clipboard.GetContent().GetTextAsync());
		clipboard.Clear();
		clipboard.StopContentChanged();
		clipboard.SetContent(package);
		CollectionAssert.AreEqual(new[] { "text", "empty" }, observed);
	}

	private sealed class ClipboardDevice : IWaylandClipboardDevice
	{
		private readonly WaylandSelectionSerial _serial = new();
		private string? _foreignText;
		public event EventHandler? SelectionChanged;
		public bool HasSelection => _foreignText is not null;
		internal int Published { get; private set; }
		public void Post(Action action) => action();
		public void RequestSelection(Action publish, Action? cancel = null) => _serial.Request(publish, cancel);
		public void CancelPendingSelection() => _serial.CancelPendingSelection();
		public WlDataSource CreateSource() => throw new InvalidOperationException("Selection must not be published without input focus.");
		public void SetSelection(WlDataSource? source) => Published++;
		public void FillContent(DataPackage package)
		{
			if (_foreignText is { } text)
			{
				package.SetText(text);
			}
		}
		internal void Focus() => _serial.Enter(1);
		internal void NotifySelection(string? text)
		{
			_foreignText = text;
			SelectionChanged?.Invoke(this, EventArgs.Empty);
		}
	}
}
