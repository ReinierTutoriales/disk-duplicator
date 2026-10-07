using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace RepartoCopier.WinUI;

internal sealed record NativeDialogSpec(
    string Instruction, string Content, string[] Buttons, int DefaultButton,
    bool CommandLinks = false, bool CommonCancel = false, string? Expanded = null,
    string[]? Radios = null, int SelectedRadio = 0);

internal readonly record struct NativeDialogResult(int Button, int Radio);

/// <summary>One owned, DPI-aware Windows dialog implementation. No dependency on the parent's XAML height.</summary>
internal static class NativeTaskDialog
{
    internal const int FirstButton = 100;
    internal const int FirstRadio = 1000;
    internal const int Cancel = 2;
    internal const uint ClickButton = 0x0400 + 102;
    internal const uint ClickRadio = 0x0400 + 110;
    internal const uint EnableButton = 0x0400 + 111;

    internal static NativeDialogResult Show(nint owner, NativeDialogSpec spec, CancellationToken token,
        Action<nint>? onReady = null, Func<int, string?>? onButton = null)
    {
        if (token.IsCancellationRequested) return new(Cancel, 0);
        if (spec.Radios is { Length: > 0 } radios &&
            (spec.SelectedRadio < 0 || spec.SelectedRadio >= radios.Length))
            throw new ArgumentOutOfRangeException(nameof(spec));
        using var memory = new NativeMemory();
        var shown = nint.Zero;
        Exception? callbackError = null;
        NativeConflictDialog.Config config = default;
        using var cancellation = token.Register(() =>
        {
            var window = Interlocked.CompareExchange(ref shown, nint.Zero, nint.Zero);
            if (window != nint.Zero) PostMessage(window, ClickButton, Cancel, nint.Zero);
        });
        Callback callback = (window, notification, wParam, _, _) =>
        {
            try
            {
                if (notification is 0 or 1) // created or navigated: layout and HWND now exist
                {
                    Interlocked.Exchange(ref shown, window);
                    if (token.IsCancellationRequested) PostMessage(window, ClickButton, Cancel, nint.Zero);
                    else onReady?.Invoke(window);
                }
                else if (notification == 2 && !token.IsCancellationRequested && onButton is not null)
                {
                    // null closes; an empty string keeps the dialog; other text navigates/reflows a page.
                    var content = onButton((int)wParam);
                    if (content is null) return 0;
                    if (content.Length > 0)
                    {
                        config.Content = memory.String(content);
                        SendMessage(window, 0x0400 + 101, nint.Zero, memory.Structure(config));
                    }
                    return 1; // S_FALSE: don't close
                }
                else if (notification == 5) Interlocked.Exchange(ref shown, nint.Zero);
            }
            catch (Exception ex)
            {
                // Managed exceptions must never cross an unmanaged callback. Dismiss, then throw safely.
                callbackError ??= ex;
                PostMessage(window, ClickButton, Cancel, nint.Zero);
            }
            return 0;
        };
        config = new NativeConflictDialog.Config
        {
            Size = (uint)Marshal.SizeOf<NativeConflictDialog.Config>(), Owner = owner,
            Flags = 0x1000 | 0x0008 | (spec.CommandLinks ? 0x0010u : 0u),
            CommonButtons = spec.CommonCancel ? 0x0008u : 0u,
            WindowTitle = memory.String("RepartoCopier"),
            MainInstruction = memory.String(spec.Instruction), Content = memory.String(spec.Content),
            ButtonCount = (uint)spec.Buttons.Length, Buttons = memory.Buttons(spec.Buttons, FirstButton),
            DefaultButton = spec.DefaultButton,
            RadioCount = (uint)(spec.Radios?.Length ?? 0),
            RadioButtons = spec.Radios is { Length: > 0 } ? memory.Buttons(spec.Radios, FirstRadio) : nint.Zero,
            DefaultRadio = spec.Radios is { Length: > 0 } ? FirstRadio + spec.SelectedRadio : 0,
            ExpandedInformation = spec.Expanded is null ? nint.Zero : memory.String(spec.Expanded),
            ExpandedControlText = memory.String("Ocultar destinos"), CollapsedControlText = memory.String("Ver destinos"),
            Callback = Marshal.GetFunctionPointerForDelegate(callback),
        };
        var hr = TaskDialogIndirect(ref config, out var button, out var radio, nint.Zero);
        GC.KeepAlive(callback);
        Marshal.ThrowExceptionForHR(hr);
        if (callbackError is not null) ExceptionDispatchInfo.Capture(callbackError).Throw();
        return token.IsCancellationRequested ? new(Cancel, 0) : new(button, radio);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Callback(nint window, uint notification, nint wParam, nint lParam, nint data);

    private sealed class NativeMemory : IDisposable
    {
        private readonly List<nint> _allocations = [];
        internal nint String(string text)
        {
            var pointer = Marshal.StringToHGlobalUni(text);
            _allocations.Add(pointer);
            return pointer;
        }
        internal nint Structure<T>(T value) where T : struct
        {
            var pointer = Allocate(Marshal.SizeOf<T>());
            Marshal.StructureToPtr(value, pointer, false);
            return pointer;
        }
        private nint Allocate(int size)
        {
            var pointer = Marshal.AllocHGlobal(size);
            _allocations.Add(pointer);
            return pointer;
        }
        internal nint Buttons(string[] labels, int first)
        {
            var size = Marshal.SizeOf<NativeConflictDialog.Button>();
            var pointer = Allocate(checked(size * labels.Length));
            for (var i = 0; i < labels.Length; i++)
                Marshal.StructureToPtr(new NativeConflictDialog.Button { Id = first + i, Text = String(labels[i]) },
                    pointer + i * size, false);
            return pointer;
        }
        public void Dispose()
        {
            for (var i = _allocations.Count - 1; i >= 0; i--) Marshal.FreeHGlobal(_allocations[i]);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern int TaskDialogIndirect(ref NativeConflictDialog.Config config, out int button, out int radio, nint verification);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    internal static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
