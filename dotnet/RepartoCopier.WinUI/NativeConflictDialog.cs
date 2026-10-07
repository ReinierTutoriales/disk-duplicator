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
        var labels = new[]
        {
            "Conservar existentes\nCopiar solo los archivos que faltan, sin comparar contenido.",
            "Omitir idénticos y reemplazar distintos\nLeer el contenido. Los discos lentos pueden hacer que esta comparación tarde.",
            "Reemplazar todos sin comparar\nSustituir los archivos existentes con los del origen.",
            "Omitir por tamaño y fecha (rápido)\nNo lee contenido. Reemplaza los que difieran en tamaño o fecha; no detecta daños con los mismos metadatos.",
        };
        var total = conflict.Destinations.Sum(item => (long)item.ExistingFiles);
        var details = string.Join(Environment.NewLine,
            conflict.Destinations.Select(item => $"{item.Destination}: {item.ExistingFiles} de {item.TotalFiles} existentes"));
        var result = NativeTaskDialog.Show(owner, new NativeDialogSpec(
            "Hay archivos que ya existen",
            $"{total} archivos existentes en {conflict.Destinations.Count} destinos. Elige cómo continuar.",
            labels, CancelId, CommandLinks: true, CommonCancel: true, Expanded: details), token, onCreated);
        return PolicyForButton(result.Button);
    }

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

    internal static bool PostMessage(nint window, uint message, nint wParam, nint lParam) =>
        NativeTaskDialog.PostMessage(window, message, wParam, lParam);
}
