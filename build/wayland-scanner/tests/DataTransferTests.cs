#nullable enable
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.WinUI.Runtime.Skia.Wayland;
using Uno.WinUI.Runtime.Skia.Wayland.Native;

namespace Wayland.Scanner.Tests;

[TestClass]
public sealed class DataTransferTests
{
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
		CollectionAssert.AreEqual(new[] { "/a", "/b", "/c", "/f" }, WaylandFileUris.ParseUriList(text));
	}

	[TestMethod]
	public void GnomeCopiedFilesRoundTrips()
	{
		var paths = new[] { "/tmp/a b", "/tmp/c" };
		var text = WaylandFileUris.FormatGnomeCopiedFiles(paths);
		Assert.AreEqual("copy\nfile:///tmp/a%20b\nfile:///tmp/c", text);
		CollectionAssert.AreEqual(paths, WaylandFileUris.ParseGnomeCopiedFiles(text));
		CollectionAssert.AreEqual(new[] { "/x" }, WaylandFileUris.ParseGnomeCopiedFiles("cut\nfile:///x\n"));
		Assert.AreEqual(0, WaylandFileUris.ParseGnomeCopiedFiles("copy").Count);
	}

	[TestMethod]
	public void PipeTransfersLargePayloadsAcrossThreads()
	{
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
		var (read, write) = WaylandPipe.Create();
		try
		{
			Assert.IsNull(WaylandPipe.ReadToEnd(read, TimeSpan.FromMilliseconds(50)));
		}
		finally
		{
			Posix.close(write);
		}
	}

	[TestMethod]
	public void PipeWriteFailsWhenTheReaderIsGone()
	{
		var (read, write) = WaylandPipe.Create();
		Posix.close(read);
		Assert.IsFalse(WaylandPipe.WriteAll(write, Encoding.UTF8.GetBytes("text"), TimeSpan.FromSeconds(1)));
	}
}
