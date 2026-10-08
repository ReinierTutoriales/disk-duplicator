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
            manager.Show(new AppNotificationBuilder().AddText(title).AddText(message).BuildNotification());
        }
        catch (Exception)
        {
            // Notifications are a convenience; the window already shows the result.
            _unavailable = true;
        }
    }

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
