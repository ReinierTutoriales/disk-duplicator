from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
XAML = ROOT / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml'
CODE = ROOT / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs'


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 match, found {count}')
    return text.replace(old, new, 1)

xaml = XAML.read_text(encoding='utf-8')

# Compact title/chrome and main content footprint.
xaml = replace_once(xaml, '<RowDefinition Height="78" />', '<RowDefinition Height="60" />', 'title height')
xaml = replace_once(xaml, '<RowDefinition Height="42" />', '<RowDefinition Height="34" />', 'status height')
xaml = replace_once(xaml, 'Padding="30,0,156,0"', 'Padding="22,0,148,0"', 'title padding')
xaml = replace_once(xaml, 'Width="48"\n                   Height="48"', 'Width="36"\n                   Height="36"', 'logo size')
xaml = replace_once(xaml, 'Margin="16,0,0,0"', 'Margin="11,0,0,0"', 'brand margin')
xaml = replace_once(xaml, 'Text="RepartoCopier" FontSize="21"', 'Text="RepartoCopier" FontSize="18"', 'brand font')
xaml = replace_once(xaml, 'Text="Copias rápidas y seguras" Opacity="0.66" FontSize="13"', 'Text="Copia FAN-OUT para Windows" Opacity="0.62" FontSize="11"', 'brand subtitle')
xaml = replace_once(xaml, 'Width="48"\n                    Height="44"', 'Width="40"\n                    Height="36"', 'menu size')
xaml = replace_once(xaml, 'Margin="34,8,34,18" MaxWidth="1380"', 'Margin="22,6,22,12" MaxWidth="980"', 'content footprint')

# Preparation view: same capabilities, less vertical and horizontal bulk.
xaml = replace_once(xaml, 'RowSpacing="16" MaxWidth="900"', 'RowSpacing="11" MaxWidth="800"', 'prep spacing')
xaml = replace_once(xaml, 'Text="Nueva copia" FontSize="29"', 'Text="Nueva copia" FontSize="24"', 'prep title')
xaml = xaml.replace('Style="{StaticResource SurfaceCardStyle}" Padding="18,16"', 'Style="{StaticResource SurfaceCardStyle}" Padding="14,12"', 2)
xaml = replace_once(xaml, 'MaxHeight="204"', 'MaxHeight="132"', 'destination list height')
xaml = replace_once(xaml, 'MinHeight="42"', 'MinHeight="36"', 'destination item minheight')
xaml = replace_once(xaml, '<Grid MinHeight="36" Padding="10,3"', '<Grid MinHeight="36" Padding="9,2"', 'destination item grid')
xaml = replace_once(xaml, 'Height="44" MinWidth="152"', 'Height="40" MinWidth="138"', 'start button size')

# Running view becomes one compact job-level dashboard: no per-disk progress cards.
old_rows = '''                <Grid.RowDefinitions>\n                    <RowDefinition Height="Auto" />\n                    <RowDefinition Height="Auto" />\n                    <RowDefinition Height="*" />\n                    <RowDefinition Height="Auto" />\n                </Grid.RowDefinitions>'''
new_rows = '''                <Grid.RowDefinitions>\n                    <RowDefinition Height="Auto" />\n                    <RowDefinition Height="Auto" />\n                    <RowDefinition Height="Auto" />\n                </Grid.RowDefinitions>'''
xaml = replace_once(xaml, old_rows, new_rows, 'running rows')
xaml = replace_once(xaml, '<Border Style="{StaticResource SurfaceCardStyle}" Padding="18,16">', '<Border Style="{StaticResource SurfaceCardStyle}" Padding="14,12">', 'running summary card')
xaml = replace_once(xaml, 'Width="64" Height="64" CornerRadius="12"', 'Width="48" Height="48" CornerRadius="10"', 'running icon tile')
xaml = replace_once(xaml, 'FontSize="30"', 'FontSize="23"', 'running icon')
xaml = replace_once(xaml, 'Spacing="3" VerticalAlignment="Center" MinWidth="330"', 'Spacing="2" VerticalAlignment="Center" MinWidth="250"', 'running title stack')
xaml = replace_once(xaml, 'Text="Preparando..." FontSize="25"', 'Text="Preparando..." FontSize="20"', 'current file font')
xaml = replace_once(xaml, 'Style="{StaticResource SurfaceCardStyle}" Padding="18,14"', 'Style="{StaticResource SurfaceCardStyle}" Padding="14,10"', 'overall card padding')

# Remove the destination-by-destination running section entirely.
section_start = xaml.find('                <Grid Grid.Row="2" RowSpacing="8">')
controls_start = xaml.find('                <Grid Grid.Row="3" Margin="2,2"', section_start)
if section_start < 0 or controls_start < 0:
    raise RuntimeError('running destination section markers not found')
xaml = xaml[:section_start] + xaml[controls_start:]
xaml = replace_once(xaml, '<Grid Grid.Row="3" Margin="2,2"', '<Grid Grid.Row="2" Margin="2,2"', 'running controls row')
xaml = replace_once(xaml, 'Height="40" MinWidth="145"', 'Height="36" MinWidth="116"', 'pause size')
xaml = replace_once(xaml, 'Grid.Column="1" Height="40" MinWidth="145"', 'Grid.Column="1" Height="36" MinWidth="116"', 'cancel size')

XAML.write_text(xaml, encoding='utf-8')

cs = CODE.read_text(encoding='utf-8')
cs = cs.replace('using System.ComponentModel;\n', '')
cs = cs.replace('using System.Runtime.CompilerServices;\n', '')
cs = replace_once(cs, '    private readonly ObservableCollection<ProgressRow> _progressRows = [];\n', '', 'progress rows field')
cs = replace_once(cs, '        ProgressList.ItemsSource = _progressRows;\n', '', 'progress list binding')
cs = replace_once(cs, '        AppWindow.Resize(new SizeInt32(1180, 760));', '        AppWindow.Resize(new SizeInt32(960, 620));', 'window size')
old_start = '''            _progressRows.Clear();\n            foreach (var snapshot in _job.Snapshot()) _progressRows.Add(new ProgressRow(snapshot));\n            RunningDestinationTitle.Text = $"Destinos ({_progressRows.Count})";\n'''
cs = replace_once(cs, old_start, '', 'running per-destination population')
old_refresh = '''        while (_progressRows.Count < snapshots.Count)\n            _progressRows.Add(new ProgressRow(snapshots[_progressRows.Count], paused));\n        for (var index = 0; index < snapshots.Count; index++)\n            _progressRows[index].Update(snapshots[index], paused);\n\n'''
cs = replace_once(cs, old_refresh, '', 'running per-destination refresh')

# The job dashboard still reports aggregate state, but no longer owns presentation-only per-disk rows.
progress_class = cs.find('    public sealed class ProgressRow : INotifyPropertyChanged')
if progress_class < 0:
    raise RuntimeError('ProgressRow class not found')
cs = cs[:progress_class] + '}\n'

CODE.write_text(cs, encoding='utf-8')

# Architectural guard: running UI is job-level only.
if 'ProgressList' in xaml or 'RunningDestinationTitle' in xaml:
    raise RuntimeError('per-destination running UI remains in XAML')
if 'ProgressRow' in cs or '_progressRows' in cs:
    raise RuntimeError('per-destination running view-model remains in code-behind')
if 'SizeInt32(960, 620)' not in cs:
    raise RuntimeError('compact default window size missing')

print('compact single-progress WinUI migration applied')
