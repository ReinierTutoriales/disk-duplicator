using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;
using RepartoCopier.WinUI;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class NativeConflictDialogTests
{
    [TestMethod]
    [DataRow(NativeConflictDialog.CancelId)]
    [DataRow(NativeConflictDialog.KeepId)]
    [DataRow(NativeConflictDialog.CompareId)]
    [DataRow(NativeConflictDialog.ReplaceId)]
    public void RealWindowsDialogIsOwnedAndReturnsOnlyTheSelectedPolicy(int button)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires native Windows task dialogs.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), $"task-dialog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        nint activation = nint.Zero;
        nint owner = nint.Zero;
        nuint cookie = 0;
        var active = false;
        try
        {
            var manifest = Path.Combine(root, "common-controls.manifest");
            File.WriteAllText(manifest, """
                <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
                  <assemblyIdentity version="1.0.0.0" name="DialogTests"/>
                  <dependency><dependentAssembly>
                    <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls"
                      version="6.0.0.0" processorArchitecture="*"
                      publicKeyToken="6595b64144ccf1df" language="*"/>
                  </dependentAssembly></dependency>
                </assembly>
                """);
            var context = new ActivationContext { Size = (uint)Marshal.SizeOf<ActivationContext>(), Source = manifest };
            activation = CreateActCtx(ref context);
            Assert.AreNotEqual(new nint(-1), activation);
            active = ActivateActCtx(activation, out cookie);
            Assert.IsTrue(active);
            owner = CreateWindowEx(0, "STATIC", "Compact owner", 0x00cf0000,
                100, 100, 720, 320, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
            Assert.AreNotEqual(nint.Zero, owner);
            var conflict = new ExistingFilesConflictException(
                [new DestinationConflict("D:\\ISOS", 5, 5, [new string('a', 240) + ".iso"])]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var observedOwner = nint.Zero;
            var created = false;
            var result = NativeConflictDialog.Show(owner, conflict, timeout.Token, window =>
            {
                created = true;
                observedOwner = GetWindow(window, 4); // GW_OWNER
                NativeConflictDialog.PostMessage(window, 0x0400 + 102, button, nint.Zero);
            });
            Assert.IsTrue(created, "The test must exercise TaskDialogIndirect, not just button mapping.");
            Assert.AreEqual(owner, observedOwner);
            Assert.AreEqual(NativeConflictDialog.PolicyForButton(button), result);
            Assert.AreEqual(IntPtr.Size == 8 ? 160 : 96, Marshal.SizeOf<NativeConflictDialog.Config>());
            Assert.AreEqual(4 + IntPtr.Size, Marshal.SizeOf<NativeConflictDialog.Button>());
        }
        finally
        {
            if (owner != nint.Zero) DestroyWindow(owner);
            if (active) DeactivateActCtx(0, cookie);
            if (activation != nint.Zero && activation != new nint(-1)) ReleaseActCtx(activation);
            Directory.Delete(root, true);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ActivationContext
    {
        public uint Size;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Source;
        public ushort Architecture;
        public ushort Language;
        public nint AssemblyDirectory;
        public nint ResourceName;
        public nint ApplicationName;
        public nint Module;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateActCtxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CreateActCtx(ref ActivationContext context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ActivateActCtx(nint context, out nuint cookie);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeactivateActCtx(uint flags, nuint cookie);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void ReleaseActCtx(nint context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
