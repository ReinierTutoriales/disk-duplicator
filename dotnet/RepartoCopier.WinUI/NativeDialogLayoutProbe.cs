using System.Runtime.InteropServices;

namespace RepartoCopier.WinUI;

/// <summary>Actual native geometry check used by --layout-check (not a substitute for pixel/contrast checks).</summary>
internal static class NativeDialogLayoutProbe
{
    internal static string Check(nint dialog, nint expectedOwner)
    {
        if (GetWindow(dialog, 4) != expectedOwner) throw new InvalidOperationException("Dialog lost its owner.");
        if (IsWindowEnabled(expectedOwner)) throw new InvalidOperationException("Dialog owner is not modal-disabled.");
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowRect(dialog, out var bounds) || !GetMonitorInfo(MonitorFromWindow(dialog, 2), ref monitor))
            throw new InvalidOperationException("Cannot inspect native dialog bounds.");
        // DWM invisible resize borders may extend by a few pixels around the visible frame.
        const int tolerance = 8;
        if (bounds.Left < monitor.Work.Left - tolerance || bounds.Top < monitor.Work.Top - tolerance ||
            bounds.Right > monitor.Work.Right + tolerance || bounds.Bottom > monitor.Work.Bottom + tolerance)
            throw new InvalidOperationException($"Native dialog exceeds work area: {bounds.Left},{bounds.Top}–{bounds.Right},{bounds.Bottom}.");
        return $"owned/modal; {bounds.Right - bounds.Left}×{bounds.Bottom - bounds.Top}px; DPI {GetDpiForWindow(dialog)}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);
}
