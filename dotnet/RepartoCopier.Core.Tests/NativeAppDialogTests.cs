using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;
using RepartoCopier.WinUI;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class NativeAppDialogTests
{
    [TestMethod]
    public void TaskDialogStructuresMatchTheCommctrlAbi()
    {
        Assert.AreEqual(IntPtr.Size == 8 ? 160 : 96, Marshal.SizeOf<NativeTaskDialog.Config>());
        Assert.AreEqual(4 + IntPtr.Size, Marshal.SizeOf<NativeTaskDialog.Button>());
    }

    [TestMethod]
    [DataRow("settings-apply")]
    [DataRow("settings-close")]
    [DataRow("about-github")]
    [DataRow("about-license")]
    [DataRow("shutdown-yes")]
    [DataRow("shutdown-no")]
    [DataRow("results-save")]
    [DataRow("results-close")]
    [DataRow("callback-error")]
    public void NativeActionsAndPaginationKeepOwnershipAndSafety(string scenario)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows task dialogs.");
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            nint shown = nint.Zero;
            void Click(nint window, int button)
            {
                shown = window;
                NativeDialogLayoutProbe.Check(window, owner);
                NativeTaskDialog.PostMessage(window, NativeTaskDialog.ClickButton, button, nint.Zero);
            }
            if (scenario.StartsWith("settings", StringComparison.Ordinal))
            {
                var selected = NativeAppDialogs.Settings(owner, ThemePreference.System, timeout.Token, window =>
                {
                    NativeTaskDialog.SendMessage(window, NativeTaskDialog.ClickRadio, 1002, nint.Zero);
                    Click(window, scenario == "settings-apply" ? 100 : 101);
                });
                if (scenario == "settings-apply") Assert.AreEqual(ThemePreference.Dark, selected);
                else Assert.IsNull(selected);
            }
            else if (scenario.StartsWith("about", StringComparison.Ordinal))
            {
                string? link = null;
                NativeAppDialogs.About(owner, "2.1.1", url =>
                {
                    link = url;
                    NativeTaskDialog.PostMessage(shown, NativeTaskDialog.ClickButton, 100, nint.Zero);
                }, timeout.Token, window => Click(window, scenario == "about-github" ? 101 : 102));
                Assert.AreEqual(scenario == "about-github" ? "https://github.com/ReinierTutoriales/disk-duplicator"
                    : "https://github.com/ReinierTutoriales/disk-duplicator/blob/main/LICENSE", link);
            }
            else if (scenario.StartsWith("shutdown", StringComparison.Ordinal))
            {
                var authorized = NativeAppDialogs.Shutdown(owner, timeout.Token,
                    window => Click(window, scenario == "shutdown-yes" ? 100 : 101));
                Assert.AreEqual(scenario == "shutdown-yes", authorized);
            }
            else if (scenario == "callback-error")
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeAppDialogs.About(owner, "2.1.1", _ => { },
                    timeout.Token, _ => throw new InvalidOperationException("Managed callback failure")));
            }
            else
            {
                var rows = Enumerable.Range(0, 8).Select(index => new DestinationSnapshot(
                    $"D:\\{index}-" + new string('x', 240), 100, 100, 10, 0, 0, 0, 0,
                    DestinationPhase.Done, null, "", 0, 0)).ToArray();
                var pages = 0;
                var save = NativeAppDialogs.Results(owner, rows, false, scenario == "results-save", timeout.Token, window =>
                {
                    ++pages;
                    Click(window, pages < 3 ? 102 : scenario == "results-save" ? 100 : 103);
                });
                Assert.AreEqual(3, pages);
                Assert.AreEqual(scenario == "results-save", save);
            }
            Assert.IsFalse(timeout.IsCancellationRequested, "Test must complete through its intended button, not timeout.");
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
