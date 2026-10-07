using System.ComponentModel;
using Microsoft.UI.Xaml;
using RepartoCopier.Core;

namespace RepartoCopier.WinUI;

public sealed record DestinationRow(string Path);

/// <summary>
/// One running destination card. Bound with compiled x:Bind; every property raises a change only when its
/// value really changes, so a 250 ms refresh of an idle card costs no layout or binding work.
/// </summary>
public sealed class RunningDestinationRow(string path) : INotifyPropertyChanged
{
    private enum State { Active, Success, Critical, Caution }

    private readonly string _fullPath = path;
    private State _state = State.Active;

    public string Label { get; } = ShortLabel(path);
    public string Progress { get; private set; } = "Preparando";
    public string Detail { get; private set; } = path;
    public string ActiveGlyph { get; private set; } = "";
    public string CautionGlyph { get; private set; } = "";
    public double Percent { get; private set; }
    public bool IsFailed { get; private set; }
    public bool IsPaused { get; private set; }
    public Visibility ActiveVisibility => _state == State.Active ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SuccessVisibility => _state == State.Success ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CriticalVisibility => _state == State.Critical ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CautionVisibility => _state == State.Caution ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(DestinationSnapshot snapshot, bool paused)
    {
        var progress = DestinationProgressText.Format(snapshot);
        var percent = snapshot.Phase switch
        {
            DestinationPhase.Verifying => Ratio(snapshot.VerifiedBytes, snapshot.VerifyBytesTotal),
            DestinationPhase.Done => 100,
            _ => Ratio(snapshot.Written, snapshot.Total),
        };
        var working = snapshot.Phase is DestinationPhase.Copying or DestinationPhase.Verifying;
        var (state, activeGlyph, cautionGlyph) = snapshot.Phase switch
        {
            DestinationPhase.Done when snapshot.FilesErrored > 0 => (State.Caution, ActiveGlyph, ""),
            DestinationPhase.Done => (State.Success, ActiveGlyph, CautionGlyph),
            DestinationPhase.Failed => (State.Critical, ActiveGlyph, CautionGlyph),
            DestinationPhase.Cancelled => (State.Caution, ActiveGlyph, ""),
            _ when paused && working => (State.Caution, ActiveGlyph, ""),
            DestinationPhase.Verifying => (State.Active, "", CautionGlyph),
            _ => (State.Active, "", CautionGlyph),
        };

        if (Progress != progress)
        {
            Progress = progress;
            Raise(nameof(Progress));
            // The multi-line tooltip only changes with the visible progress text or an error.
            Detail = BuildDetail(snapshot, progress);
            Raise(nameof(Detail));
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.Error) && !Detail.EndsWith(snapshot.Error, StringComparison.Ordinal))
        {
            Detail = BuildDetail(snapshot, progress);
            Raise(nameof(Detail));
        }
        if (Math.Abs(Percent - percent) >= 0.1) { Percent = percent; Raise(nameof(Percent)); }
        if (ActiveGlyph != activeGlyph) { ActiveGlyph = activeGlyph; Raise(nameof(ActiveGlyph)); }
        if (CautionGlyph != cautionGlyph) { CautionGlyph = cautionGlyph; Raise(nameof(CautionGlyph)); }
        if (_state != state)
        {
            _state = state;
            Raise(nameof(ActiveVisibility));
            Raise(nameof(SuccessVisibility));
            Raise(nameof(CriticalVisibility));
            Raise(nameof(CautionVisibility));
        }
        var failed = snapshot.Phase == DestinationPhase.Failed;
        if (IsFailed != failed) { IsFailed = failed; Raise(nameof(IsFailed)); }
        var showPaused = paused && working;
        if (IsPaused != showPaused) { IsPaused = showPaused; Raise(nameof(IsPaused)); }
    }

    private string BuildDetail(DestinationSnapshot snapshot, string progress)
    {
        var detail = $"{_fullPath}\n{progress}\nCopiados: {MainWindow.FormatBytes(snapshot.Written)} de {MainWindow.FormatBytes(snapshot.Total)}";
        if (snapshot.VerifyBytesTotal > 0)
            detail += $"\nVerificados: {MainWindow.FormatBytes(snapshot.VerifiedBytes)} de {MainWindow.FormatBytes(snapshot.VerifyBytesTotal)}";
        if (!string.IsNullOrWhiteSpace(snapshot.Error)) detail += $"\n{snapshot.Error}";
        return detail;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static double Ratio(ulong done, ulong total) =>
        total == 0 ? 0 : Math.Clamp(done * 100d / total, 0, 100);

    // Drive plus final folder ("D: · ISOS") keeps cards readable at narrow widths; the tooltip has the full path.
    private static string ShortLabel(string fullPath)
    {
        var trimmed = System.IO.Path.TrimEndingDirectorySeparator(fullPath);
        var root = System.IO.Path.GetPathRoot(trimmed)?.TrimEnd('\\', '/') ?? string.Empty;
        var leaf = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(leaf) || string.IsNullOrEmpty(root) ? fullPath : $"{root} · {leaf}";
    }
}
