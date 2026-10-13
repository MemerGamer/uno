#nullable enable
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Runtime.Wayland;
using Uno.UI.Runtime.Wayland.Native;

namespace Uno.UI.Runtime.Wayland.Tests;

[TestClass]
public sealed partial class DataTransferTests
{
	private static readonly string[] ParsedUriList = ["/a", "/b", "/c", "/f"];
	private static readonly string[] ParsedCut = ["/x"];

	[TestMethod]
	public void UriListRoundTripsLocalPaths()
	{
		var paths = new[] { "/tmp/plain.txt", "/home/user/My Files/100% #1+ä.png" };
		var uriList = WaylandFileUris.FormatUriList(paths);
		Assert.AreEqual("file:///tmp/plain.txt\r\nfile:///home/user/My%20Files/100%25%20%231%2B%C3%A4.png\r\n", uriList);
		CollectionAssert.AreEqual(paths, WaylandFileUris.ParseUriList(uriList));
	}

	[TestMethod]
	public void UriListSkipsCommentsRemoteHostsAndOtherSchemes()
	{
		var text = "# comment\nfile:///a\nfile://localhost/b\nfile:/c\nfile://server/d\nhttps://example.com/e\n\nfile:///f\0";
		CollectionAssert.AreEqual(ParsedUriList, WaylandFileUris.ParseUriList(text));
	}

	[TestMethod]
	public void GnomeCopiedFilesRoundTrips()
	{
		var paths = new[] { "/tmp/a b", "/tmp/c" };
		var text = WaylandFileUris.FormatGnomeCopiedFiles(paths);
		Assert.AreEqual("copy\nfile:///tmp/a%20b\nfile:///tmp/c", text);
		CollectionAssert.AreEqual(paths, WaylandFileUris.ParseGnomeCopiedFiles(text));
		CollectionAssert.AreEqual(ParsedCut, WaylandFileUris.ParseGnomeCopiedFiles("cut\nfile:///x\n"));
		Assert.AreEqual(0, WaylandFileUris.ParseGnomeCopiedFiles("copy").Count);
	}

	[TestMethod]
	public void PipeTransfersLargePayloadsAcrossThreads()
	{
		RequireLinux();
		var data = new byte[3 * 1024 * 1024 + 17];
		new Random(1).NextBytes(data);
		var (read, write) = WaylandPipe.Create();
		var writer = Task.Run(() => WaylandPipe.WriteAll(write, data, TimeSpan.FromSeconds(5)));
		var received = WaylandPipe.ReadToEnd(read, TimeSpan.FromSeconds(5));
		Assert.IsTrue(writer.Result);
		CollectionAssert.AreEqual(data, received);
	}

	[TestMethod]
	public void PipeReadTimesOutWhenTheWriterStaysSilent()
	{
		RequireLinux();
		var (read, write) = WaylandPipe.Create();
		try
		{
			Assert.IsNull(WaylandPipe.ReadToEnd(read, TimeSpan.FromMilliseconds(50)));
		}
		finally
		{
			_ = Posix.close(write);
		}
	}

	[TestMethod]
	public void PipeWriteFailsWhenTheReaderIsGone()
	{
		RequireLinux();
		var (read, write) = WaylandPipe.Create();
		_ = Posix.close(read);
		Assert.IsFalse(WaylandPipe.WriteAll(write, Encoding.UTF8.GetBytes("text"), TimeSpan.FromSeconds(1)));
	}
	[TestMethod]
	public async Task PipeWriteTimesOutWithANonReadingPeer()
	{
		RequireLinux();
		var (read, write) = WaylandPipe.Create();
		try
		{
			var writer = Task.Run(() => WaylandPipe.WriteAll(write, new byte[3 * 1024 * 1024], TimeSpan.FromMilliseconds(100)));
			Assert.IsFalse(await writer.WaitAsync(TimeSpan.FromSeconds(3)));
		}
		finally
		{
			_ = Posix.close(read);
		}
	}

	[TestMethod]
	public async Task PipeRejectsTransfersAboveTheSizeLimit()
	{
		RequireLinux();
		var (read, write) = WaylandPipe.Create();
		var writer = Task.Run(() => WaylandPipe.WriteAll(write, new byte[128 * 1024], TimeSpan.FromSeconds(1)));
		Assert.IsNull(WaylandPipe.ReadToEnd(read, TimeSpan.FromSeconds(1), maximumSize: 1024));
		await writer.WaitAsync(TimeSpan.FromSeconds(3));
	}

	[TestMethod]
	public void DropFinishRequiresAcceptedMimeDropAndMatchingSelectedAction()
	{
		var negotiation = new WaylandDropNegotiation { AcceptedActions = 3, SelectedAction = 2 };
		Assert.IsFalse(negotiation.CanDrop);
		negotiation.AcceptedMime = true;
		Assert.IsTrue(negotiation.CanDrop);
		Assert.IsFalse(negotiation.CanFinish(2));
		negotiation.Dropped = true;
		Assert.IsFalse(negotiation.CanFinish(1));
		Assert.IsTrue(negotiation.CanFinish(2));
		negotiation.SelectedAction = 0;
		Assert.IsFalse(negotiation.CanFinish(0));
		negotiation.SelectedAction = 3;
		Assert.IsFalse(negotiation.CanFinish(3));
		negotiation.SelectedAction = 4;
		Assert.IsFalse(negotiation.CanDrop);
		negotiation.SelectedAction = 2;
		negotiation.AcceptedActions = 1;
		Assert.IsFalse(negotiation.CanDrop);
		negotiation.AcceptedActions = 3;
		negotiation.AcceptedMime = false;
		Assert.IsFalse(negotiation.CanFinish(2));
	}

	[TestMethod]
	public void ChosenDropActionIsSingleAndSupportedBySource()
	{
		Assert.AreEqual(1u, WaylandDropNegotiation.ChooseAction(3, 3));
		Assert.AreEqual(2u, WaylandDropNegotiation.ChooseAction(3, 2));
		Assert.AreEqual(2u, WaylandDropNegotiation.ChooseAction(2, 3));
		Assert.AreEqual(0u, WaylandDropNegotiation.ChooseAction(1, 2));
		Assert.AreEqual(0u, WaylandDropNegotiation.ChooseAction(0, 3));
	}

	[TestMethod]
	public async Task PipeReadDeadlineIsNotExtendedByProgress()
	{
		RequireLinux();
		var (read, write) = WaylandPipe.Create();
		using var cancellation = new CancellationTokenSource();
		var writer = Task.Run(async () =>
		{
			try
			{
				while (!cancellation.IsCancellationRequested)
				{
					if (!WriteByte(write))
					{
						break;
					}
					await Task.Delay(10);
				}
			}
			finally
			{
				_ = Posix.close(write);
			}
		});
		try
		{
			var reader = Task.Run(() => WaylandPipe.ReadToEnd(read, TimeSpan.FromMilliseconds(100)));
			Assert.IsNull(await reader.WaitAsync(TimeSpan.FromSeconds(3)));
		}
		finally
		{
			cancellation.Cancel();
			await writer;
		}
	}

	[TestMethod]
	public void OversizedWriteClosesTheSendDescriptor()
	{
		RequireLinux();
		var (read, write) = WaylandPipe.Create();
		try
		{
			Assert.IsFalse(WaylandPipe.WriteAll(write, new byte[WaylandPipe.MaximumTransferSize + 1], TimeSpan.FromSeconds(1)));
			Assert.AreEqual(-1, Posix.fcntl(write, 3, 0));
		}
		finally
		{
			_ = Posix.close(read);
		}
	}

	[TestMethod]
	public async Task CancelledWriteClosesTheSendDescriptor()
	{
		RequireLinux();
		var (read, write) = WaylandPipe.Create();
		using var cancellation = new CancellationTokenSource();
		try
		{
			var writer = Task.Run(() => WaylandPipe.WriteAll(write, new byte[3 * 1024 * 1024], TimeSpan.FromSeconds(5), cancellation.Token));
			cancellation.Cancel();
			Assert.IsFalse(await writer.WaitAsync(TimeSpan.FromSeconds(3)));
			Assert.AreEqual(-1, Posix.fcntl(write, 3, 0));
		}
		finally
		{
			_ = Posix.close(read);
		}
	}

	private static void RequireLinux()
	{
		if (!OperatingSystem.IsLinux())
		{
			Assert.Inconclusive("Linux pipe interop is required for this transfer test.");
		}
	}

	private static unsafe bool WriteByte(int fd)
	{
		byte value = 1;
		return Posix.write(fd, &value, 1) == 1;
	}

	[TestMethod]
	public async Task StalledProductionClosesTheSendDescriptor()
	{
		RequireLinux();
		var (read, write) = WaylandPipe.Create();
		try
		{
			var production = new TaskCompletionSource<byte[]?>();
			await Assert.ThrowsAsync<TimeoutException>(() => WaylandDataTransfer.SendAsync(write, production.Task, TimeSpan.FromMilliseconds(50)));
			Assert.AreEqual(-1, Posix.fcntl(write, 3, 0));
			CollectionAssert.AreEqual(Array.Empty<byte>(), WaylandPipe.ReadToEnd(read, TimeSpan.FromSeconds(1)));
			read = -1;
		}
		finally
		{
			if (read >= 0)
			{
				_ = Posix.close(read);
			}
		}
	}

	[TestMethod]
	public async Task FaultedProductionClosesTheSendDescriptor()
	{
		RequireLinux();
		var (read, write) = WaylandPipe.Create();
		try
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() => WaylandDataTransfer.SendAsync(write, Task.FromException<byte[]?>(new InvalidOperationException()), TimeSpan.FromSeconds(1)));
			Assert.AreEqual(-1, Posix.fcntl(write, 3, 0));
		}
		finally
		{
			_ = Posix.close(read);
		}
	}

	[TestMethod]
	public async Task CancelledProductionClosesTheSendDescriptor()
	{
		RequireLinux();
		var (read, write) = WaylandPipe.Create();
		using var cancellation = new CancellationTokenSource();
		try
		{
			var production = new TaskCompletionSource<byte[]?>();
			var transfer = WaylandDataTransfer.SendAsync(write, production.Task, TimeSpan.FromSeconds(5), cancellation.Token);
			cancellation.Cancel();
			await Assert.ThrowsAsync<OperationCanceledException>(() => transfer);
			Assert.AreEqual(-1, Posix.fcntl(write, 3, 0));
		}
		finally
		{
			_ = Posix.close(read);
		}
	}

}
