using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace RepartoCopier.WinUI;

internal enum TaskbarState { None = 0, Indeterminate = 0x1, Normal = 0x2, Error = 0x4, Paused = 0x8 }

/// <summary>
/// Copy progress on the taskbar button, like Explorer's own copy dialog. Every call is best effort: a shell
/// without the taskbar list (Server Core, some remote sessions) simply shows nothing.
/// </summary>
internal sealed class TaskbarProgress(nint window)
{
    private ITaskbarList3? _taskbar;
    private bool _unavailable;
    private TaskbarState _state = TaskbarState.None;
    private int _permille = -1;

    internal void Set(TaskbarState state, double percent = 0)
    {
        var permille = (int)Math.Clamp(Math.Round(percent * 10), 0, 1000);
        if (state == _state && (state is TaskbarState.None or TaskbarState.Indeterminate || permille == _permille)) return;
        var taskbar = Taskbar();
        if (taskbar is null) return;
        try
        {
            if (state != _state) taskbar.SetProgressState(window, state);
            if (state is not (TaskbarState.None or TaskbarState.Indeterminate) && permille != _permille)
                taskbar.SetProgressValue(window, (ulong)permille, 1000);
            _state = state;
            _permille = permille;
        }
        catch (COMException)
        {
            _unavailable = true;
        }
    }

    private ITaskbarList3? Taskbar()
    {
        if (_taskbar is not null || _unavailable) return _taskbar;
        try
        {
            var taskbar = (ITaskbarList3)new TaskbarListClass();
            taskbar.HrInit();
            _taskbar = taskbar;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _unavailable = true;
        }
        return _taskbar;
    }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3 (only the members used, in vtable order)
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, TaskbarState state);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListClass { }
}

/// <summary>
/// A Windows notification when a copy ends while the user is in another window. Registration happens on the
/// first notification only, so a session that never needs one leaves no trace in the notification settings.
/// </summary>
internal sealed class CompletionNotifier(Window window)
{
    private bool _registered;
    private bool _unavailable;

    internal void ShowIfInBackground(string title, string message)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (_unavailable || GetForegroundWindow() == handle) return;
        try
        {
            var manager = AppNotificationManager.Default;
            if (!_registered)
            {
                // Clicking the notification brings this window back.
                manager.NotificationInvoked += (_, _) => window.DispatcherQueue.TryEnqueue(() =>
                {
                    if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
                        { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
                        presenter.Restore();
                    window.Activate();
                });
                manager.Register();
                _registered = true;
            }
            manager.Show(BuildNotification(title, message));
        }
        catch (Exception)
        {
            // Notifications are a convenience; the window already shows the result.
            _unavailable = true;
        }
    }

    internal static AppNotification BuildNotification(string title, string message) =>
        new AppNotificationBuilder()
            .AddText(title)
            .AddText(message)
            .SetAppLogoOverride(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.png")))
            .BuildNotification();

    internal void Unregister()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception) { }
        _registered = false;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();
}

/// <summary>
/// Held for the duration of one copy. It keeps Windows from sleeping on its idle timer (the display may still
/// turn off; closing a laptop lid still sleeps) and visible in <c>powercfg /requests</c> with its reason. It also
/// asks Windows not to slow the process down while it runs minimized: on hybrid CPUs EcoQoS would move the
/// BLAKE3 hashing of a fast NVMe copy to efficiency cores. Disposing restores both. Every call is best effort.
/// </summary>
internal sealed class CopyPowerRequest : IDisposable
{
    private IntPtr _request;
    private bool _throttlingOverridden;

    private CopyPowerRequest() { }

    internal static CopyPowerRequest Begin()
    {
        var power = new CopyPowerRequest();
        try
        {
            var reason = new ReasonContext
            {
                Version = 0, // POWER_REQUEST_CONTEXT_VERSION
                Flags = 0x1, // POWER_REQUEST_CONTEXT_SIMPLE_STRING
                SimpleReasonString = "RepartoCopier está copiando archivos",
            };
            var request = PowerCreateRequest(ref reason);
            if (request != IntPtr.Zero && request != new IntPtr(-1))
            {
                if (PowerSetRequest(request, PowerRequestSystemRequired))
                    power._request = request;
                else
                    CloseHandle(request);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }

        power._throttlingOverridden = SetExecutionSpeedThrottling(controlled: true);
        return power;
    }

    public void Dispose()
    {
        var request = Interlocked.Exchange(ref _request, IntPtr.Zero);
        if (request != IntPtr.Zero)
        {
            PowerClearRequest(request, PowerRequestSystemRequired);
            CloseHandle(request);
        }
        if (_throttlingOverridden)
        {
            SetExecutionSpeedThrottling(controlled: false);
            _throttlingOverridden = false;
        }
    }

    // controlled = true opts out of execution-speed throttling (EcoQoS); false hands the decision back to Windows.
    private static bool SetExecutionSpeedThrottling(bool controlled)
    {
        try
        {
            var state = new PowerThrottlingState
            {
                Version = 1, // PROCESS_POWER_THROTTLING_CURRENT_VERSION
                ControlMask = controlled ? 0x1u : 0u, // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                StateMask = 0,
            };
            return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
                (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private const int PowerRequestSystemRequired = 1;
    private const int ProcessPowerThrottling = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

#pragma warning disable SYSLIB1054 // A few one-shot calls per copy; DllImport keeps them readable.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr request, int requestType);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr request, int requestType);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass,
        ref PowerThrottlingState information, uint size);
#pragma warning restore SYSLIB1054
}
