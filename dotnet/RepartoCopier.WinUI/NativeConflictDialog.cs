using System.Runtime.InteropServices;
using RepartoCopier.Core;

namespace RepartoCopier.WinUI;

/// <summary>Owned Win32 task dialog. Windows sizes it independently of the compact WinUI window.
/// ABI layout follows commctrl.h (TASKDIALOGCONFIG and TASKDIALOG_BUTTON use pack 1).</summary>
internal static class NativeConflictDialog
{
    internal const int CancelId = 2;
    internal const int KeepId = 100;
    internal const int CompareId = 101;
    internal const int ReplaceId = 102;
    internal const int MetadataId = 103;
    private const uint ClickButtonMessage = 0x0400 + 102;

    internal static ExistingFilePolicy? PolicyForButton(int button) => button switch
    {
        KeepId => ExistingFilePolicy.KeepExisting,
        CompareId => ExistingFilePolicy.ReplaceDifferent,
        ReplaceId => ExistingFilePolicy.ReplaceAll,
        MetadataId => ExistingFilePolicy.ReplaceMetadataDifferent,
        _ => null,
    };

    internal static ExistingFilePolicy? Show(
        nint owner, ExistingFilesConflictException conflict, CancellationToken token,
        Action<nint>? onCreated = null)
    {
        if (token.IsCancellationRequested) return null;
        using var memory = new NativeMemory();
        var labels = new[]
        {
            "Conservar existentes\nCopiar solo los archivos que faltan, sin comparar contenido.",
            "Omitir idénticos y reemplazar distintos\nLeer el contenido. Los discos lentos pueden hacer que esta comparación tarde.",
            "Reemplazar todos sin comparar\nSustituir los archivos existentes con los del origen.",
            "Omitir por tamaño y fecha (rápido)\nNo lee contenido. Reemplaza los que difieran en tamaño o fecha; no detecta daños con los mismos metadatos.",
        };
        var buttons = memory.Buttons(labels, KeepId);
        var shown = nint.Zero;
        using var cancellation = token.Register(() =>
        {
            var window = Interlocked.CompareExchange(ref shown, nint.Zero, nint.Zero);
            if (window != nint.Zero) PostMessage(window, ClickButtonMessage, CancelId, nint.Zero);
        });
        Callback callback = (window, notification, _, _, _) =>
        {
            if (notification == 0) // TDN_CREATED
            {
                Interlocked.Exchange(ref shown, window);
                if (token.IsCancellationRequested)
                    PostMessage(window, ClickButtonMessage, CancelId, nint.Zero);
                onCreated?.Invoke(window);
            }
            else if (notification == 5) // TDN_DESTROYED
            {
                Interlocked.Exchange(ref shown, nint.Zero);
            }
            return 0;
        };
        var total = conflict.Destinations.Sum(item => (long)item.ExistingFiles);
        var details = string.Join(Environment.NewLine,
            conflict.Destinations.Select(item => $"{item.Destination}: {item.ExistingFiles} de {item.TotalFiles} existentes"));
        var config = new Config
        {
            Size = (uint)Marshal.SizeOf<Config>(),
            Owner = owner,
            Flags = 0x0010 | 0x1000 | 0x0008, // command links, owned positioning, cancellation
            CommonButtons = 0x0008, // cancel
            WindowTitle = memory.String("RepartoCopier"),
            MainInstruction = memory.String("Hay archivos que ya existen"),
            Content = memory.String($"{total} archivos existentes en {conflict.Destinations.Count} destinos. Elige cómo continuar."),
            ButtonCount = (uint)labels.Length,
            Buttons = buttons,
            DefaultButton = CancelId,
            ExpandedInformation = memory.String(details),
            ExpandedControlText = memory.String("Ocultar destinos"),
            CollapsedControlText = memory.String("Ver destinos"),
            Callback = Marshal.GetFunctionPointerForDelegate(callback),
        };
        var result = TaskDialogIndirect(ref config, out var button, nint.Zero, nint.Zero);
        GC.KeepAlive(callback);
        Marshal.ThrowExceptionForHR(result);
        return token.IsCancellationRequested ? null : PolicyForButton(button);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Callback(nint window, uint notification, nint wParam, nint lParam, nint data);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Config
    {
        public uint Size;
        public nint Owner;
        public nint Instance;
        public uint Flags;
        public uint CommonButtons;
        public nint WindowTitle;
        public nint MainIcon;
        public nint MainInstruction;
        public nint Content;
        public uint ButtonCount;
        public nint Buttons;
        public int DefaultButton;
        public uint RadioCount;
        public nint RadioButtons;
        public int DefaultRadio;
        public nint VerificationText;
        public nint ExpandedInformation;
        public nint ExpandedControlText;
        public nint CollapsedControlText;
        public nint FooterIcon;
        public nint Footer;
        public nint Callback;
        public nint CallbackData;
        public uint Width;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Button
    {
        public int Id;
        public nint Text;
    }

    private sealed class NativeMemory : IDisposable
    {
        private readonly List<nint> _allocations = [];
        internal nint String(string value)
        {
            var pointer = Marshal.StringToHGlobalUni(value);
            _allocations.Add(pointer);
            return pointer;
        }
        internal nint Buttons(string[] labels, int firstId)
        {
            var size = Marshal.SizeOf<Button>();
            var pointer = Marshal.AllocHGlobal(checked(size * labels.Length));
            _allocations.Add(pointer);
            for (var index = 0; index < labels.Length; index++)
                Marshal.StructureToPtr(new Button { Id = firstId + index, Text = String(labels[index]) },
                    pointer + index * size, false);
            return pointer;
        }
        public void Dispose()
        {
            for (var index = _allocations.Count - 1; index >= 0; index--)
                Marshal.FreeHGlobal(_allocations[index]);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern int TaskDialogIndirect(ref Config config, out int button, nint radio, nint verified);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
