#nullable enable
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Runtime.Wayland.Native;
using Uno.UI.Runtime.Wayland.Protocols;
using ScannerProgram = Uno.UI.Runtime.Wayland.Scanner.Program;

[assembly: DoNotParallelize]

namespace Uno.UI.Runtime.Wayland.Tests;

[TestClass]
public sealed class GeneratorTests
{
	[TestMethod]
	public void SignaturesIncludeVersionsNullabilityAndDynamicNewIds()
	{
		var message = XElement.Parse("""
			<request name="bind" since="3">
			  <arg name="name" type="uint" />
			  <arg name="id" type="new_id" />
			  <arg name="surface" type="object" interface="wl_surface" allow-null="true" />
			  <arg name="offset" type="fixed" />
			  <arg name="fd" type="fd" />
			  <arg name="bytes" type="array" />
			</request>
			""");
		Assert.AreEqual("3usun?ofha", ScannerProgram.Signature(message));
	}

	[TestMethod]
	public void GenerationIsDeterministicAndIncludesEveryInterface()
	{
		var first = TemporaryDirectory();
		var second = TemporaryDirectory();
		try
		{
			var protocols = Path.Combine(AppContext.BaseDirectory, "protocols");
			ScannerProgram.Generate(protocols, first);
			ScannerProgram.Generate(protocols, second);
			var expectedCount = Directory.GetFiles(protocols, "*.xml")
				.Sum(path => XDocument.Load(path).Root!.Elements("interface").Count(node => !ScannerProgram.OmittedInterfaces.Contains((string)node.Attribute("name")!)));
			Assert.AreEqual(expectedCount + 1, Directory.GetFiles(first).Length);
			foreach (var file in Directory.GetFiles(first))
			{
				Assert.AreEqual(File.ReadAllText(file), File.ReadAllText(Path.Combine(second, Path.GetFileName(file))));
			}
			var exported = File.ReadAllText(Path.Combine(first, "ZxdgExportedV2.g.cs"));
			StringAssert.Contains(exported, "event EventHandler<HandleEventArgs>? HandleEvent;");
			StringAssert.Contains(exported, "target.HandleEvent?.Invoke(target, args)");
			Assert.IsFalse(File.Exists(Path.Combine(first, "ZxdgImportedV2.g.cs")));
			Assert.IsFalse(File.Exists(Path.Combine(first, "ZxdgImporterV2.g.cs")));
			var registry = File.ReadAllText(Path.Combine(first, "WlRegistry.g.cs"));
			StringAssert.Contains(registry, "RequestArguments(4)");
			StringAssert.Contains(registry, "args.String(1, @interface)");
			StringAssert.Contains(registry, "args.Uint(2, version)");
			var keyboard = File.ReadAllText(Path.Combine(first, "WlKeyboard.g.cs"));
			StringAssert.Contains(keyboard, "UnmanagedCallersOnly");
			StringAssert.Contains(keyboard, "EventHandler<KeymapEventArgs>");
			StringAssert.Contains(keyboard, "Posix.close(arg1)");
		}
		finally
		{
			Directory.Delete(first, true);
			Directory.Delete(second, true);
		}
	}

	[TestMethod]
	public void GeneratedTypesAreInternal()
	{
		var output = TemporaryDirectory();
		try
		{
			ScannerProgram.Generate(Path.Combine(AppContext.BaseDirectory, "protocols"), output);
			foreach (var file in Directory.GetFiles(output, "*.g.cs"))
			{
				var text = File.ReadAllText(file);
				StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex(@"^public ", System.Text.RegularExpressions.RegexOptions.Multiline), file);
			}
		}
		finally
		{
			Directory.Delete(output, true);
		}
	}

	[TestMethod]
	public void OmittedInterfacesAreNotGeneratedAndTheirReferencesAreUntyped()
	{
		var output = TemporaryDirectory();
		try
		{
			ScannerProgram.Generate(Path.Combine(AppContext.BaseDirectory, "protocols"), output);
			Assert.IsFalse(File.Exists(Path.Combine(output, "WlShell.g.cs")));
			Assert.IsFalse(File.Exists(Path.Combine(output, "WlShellSurface.g.cs")));
			var cursorShape = File.ReadAllText(Path.Combine(output, "WpCursorShapeManagerV1.g.cs"));
			StringAssert.Contains(cursorShape, "GetTabletToolV2(WaylandProxy @tablet_tool)");
			var catalog = File.ReadAllText(Path.Combine(output, "ProtocolCatalog.g.cs"));
			Assert.IsFalse(catalog.Contains("\"zwp_tablet_tool_v2\"", StringComparison.Ordinal));
			Assert.IsFalse(catalog.Contains("\"wl_shell\"", StringComparison.Ordinal));
		}
		finally
		{
			Directory.Delete(output, true);
		}
	}

	[TestMethod]
	public void OmittedInterfaceCannotBeCreated()
	{
		var directory = TemporaryDirectory();
		try
		{
			File.WriteAllText(Path.Combine(directory, "bad.xml"), """
				<protocol name="bad"><interface name="bad" version="1">
				<request name="create"><arg name="id" type="new_id" interface="wl_shell" /></request>
				</interface></protocol>
				""");
			Assert.ThrowsExactly<InvalidDataException>(() => ScannerProgram.Generate(directory, Path.Combine(directory, "output")));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void UnresolvedInterfaceIsRejectedBeforeWritingOutput()
	{
		var directory = TemporaryDirectory();
		try
		{
			File.WriteAllText(Path.Combine(directory, "bad.xml"), """
				<protocol name="bad"><interface name="bad" version="1">
				<request name="create"><arg name="id" type="new_id" interface="missing" /></request>
				</interface></protocol>
				""");
			Assert.ThrowsExactly<InvalidDataException>(() => ScannerProgram.Generate(directory, Path.Combine(directory, "output")));
			Assert.IsFalse(Directory.Exists(Path.Combine(directory, "output")));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public unsafe void ManagedCoreMetadataMatchesLibwaylandAbi()
	{
		if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libwayland-client.so.0", out var library))
		{
			Assert.Inconclusive("libwayland-client is required for the ABI comparison.");
			return;
		}
		try
		{
			var count = 0;
			foreach (var spec in ProtocolCatalog.Interfaces.Where(spec => spec.Name.StartsWith("wl_", StringComparison.Ordinal)))
			{
				var native = (WlInterface*)NativeLibrary.GetExport(library, spec.Name + "_interface");
				var managed = ProtocolMetadata.Get(spec.Name);
				Assert.AreEqual(Text(native->Name), Text(managed->Name));
				Assert.AreEqual(native->Version, managed->Version, spec.Name);
				Assert.AreEqual(native->MethodCount, managed->MethodCount, spec.Name);
				Assert.AreEqual(native->EventCount, managed->EventCount, spec.Name);
				CompareMessages(spec.Requests, native->Methods, managed->Methods);
				CompareMessages(spec.Events, native->Events, managed->Events);
				count++;
			}
			Assert.IsTrue(count >= 20);
		}
		finally
		{
			NativeLibrary.Free(library);
		}
	}

	private static unsafe void CompareMessages(MessageSpec[] specs, WlMessage* native, WlMessage* managed)
	{
		for (var i = 0; i < specs.Length; i++)
		{
			Assert.AreEqual(Text(native[i].Name), Text(managed[i].Name));
			Assert.AreEqual(Text(native[i].Signature), Text(managed[i].Signature));
			for (var j = 0; j < specs[i].Types.Length; j++)
			{
				var expected = native[i].Types is null ? null : native[i].Types[j];
				var actual = managed[i].Types[j];
				Assert.AreEqual(expected is null ? null : Text(expected->Name), actual is null ? null : Text(actual->Name));
			}
		}
	}
	private static unsafe string? Text(byte* pointer) => Marshal.PtrToStringUTF8((nint)pointer);
	private static string TemporaryDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "wayland-scanner-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}
}
