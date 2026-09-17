from pathlib import Path

root = Path(__file__).resolve().parents[1]
xaml_path = root / 'dotnet' / 'RepartoCopier.WinUI' / 'MainWindow.xaml'
code_path = root / 'dotnet' / 'RepartoCopier.WinUI' / 'MainWindow.xaml.cs'
test_path = root / 'dotnet' / 'RepartoCopier.Core.Tests' / 'WinUiCompactProgressContractTests.cs'

xaml = xaml_path.read_text(encoding='utf-8')
code = code_path.read_text(encoding='utf-8')
test = test_path.read_text(encoding='utf-8')

replacements = [
    ('<Setter Property="Height" Value="32" />\n                <Setter Property="MinWidth" Value="70" />\n                <Setter Property="Padding" Value="11,0" />',
     '<Setter Property="Height" Value="30" />\n                <Setter Property="MinWidth" Value="64" />\n                <Setter Property="Padding" Value="9,0" />'),
    ('<RowDefinition Height="22" />', '<RowDefinition Height="20" />'),
    ('<Grid Grid.Row="1" Margin="6,0,6,4" MaxWidth="740" HorizontalAlignment="Stretch">',
     '<Grid Grid.Row="1" Margin="4,0,4,2" MaxWidth="720" HorizontalAlignment="Stretch">'),
    ('<Grid x:Name="PreparationPanel" Visibility="Visible" RowSpacing="6" MaxWidth="728" HorizontalAlignment="Stretch">',
     '<Grid x:Name="PreparationPanel" Visibility="Visible" RowSpacing="4" MaxWidth="712" HorizontalAlignment="Stretch">'),
    ('<Grid Margin="2,0,2,2">', '<Grid Margin="0,0,0,2">'),
    ('<Border Grid.Row="1" Style="{StaticResource SurfaceCardStyle}" Padding="8">',
     '<Border Grid.Row="1" Style="{StaticResource SurfaceCardStyle}" Padding="6">'),
    ('<Grid RowSpacing="6">\n                        <Grid.RowDefinitions>', '<Grid RowSpacing="4">\n                        <Grid.RowDefinitions>', 1),
    ('<TextBox x:Name="SourcePathBox" Height="34"', '<TextBox x:Name="SourcePathBox" Height="32"'),
    ('<StackPanel Orientation="Horizontal" Spacing="7" VerticalAlignment="Center">\n                                <FontIcon Glyph="&#xEDA2;"',
     '<StackPanel Orientation="Horizontal" Spacing="5" VerticalAlignment="Center">\n                                <FontIcon Glyph="&#xEDA2;"'),
    ('<StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">\n                                <Button Height="30" Padding="10,0"',
     '<StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="4">\n                                <Button Height="28" Padding="9,0"'),
    ('<Button Width="30" Height="30" Padding="0"', '<Button Width="28" Height="28" Padding="0"'),
    ('<ListView x:Name="DestinationList" Grid.Row="3" SelectionMode="None" Height="40" MaxHeight="40"',
     '<ListView x:Name="DestinationList" Grid.Row="3" SelectionMode="None" Height="34" MaxHeight="34"'),
    ('<Setter Property="Margin" Value="0,0,3,0"/>\n                                    <Setter Property="MinHeight" Value="34"/>',
     '<Setter Property="Margin" Value="0,0,2,0"/>\n                                    <Setter Property="MinHeight" Value="30"/>'),
    ('<Border Width="86" Height="34" Padding="5,0,3,0" CornerRadius="5"',
     '<Border MinWidth="56" MaxWidth="148" Height="30" Padding="5,0,2,0" CornerRadius="5"'),
    ('<Grid ColumnSpacing="4">', '<Grid ColumnSpacing="3">', 1),
    ('FontSize="11" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" MinWidth="22"',
     'FontSize="11" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" MinWidth="18" MaxWidth="92"'),
    ('<Button Grid.Column="2" Width="20" Height="20" Padding="0"',
     '<Button Grid.Column="2" Width="18" Height="18" Padding="0"'),
    ('<Expander Grid.Row="4" Header="Opciones" IsExpanded="False" HorizontalAlignment="Stretch">',
     '<Expander Grid.Row="4" Header="Opciones" IsExpanded="False" MinHeight="28" HorizontalAlignment="Stretch">'),
    ('<StackPanel Orientation="Horizontal" Spacing="12" Padding="0,5,0,0">',
     '<StackPanel Orientation="Horizontal" Spacing="10" Padding="0,3,0,0">'),
    ('                <TextBlock Grid.Row="2" Margin="2,0" Text="La copia usa un único flujo FAN-OUT compartido para todos los destinos." Opacity="0.52" FontSize="10" />\n', ''),
    ('<Grid x:Name="RunningPanel" Visibility="Collapsed" RowSpacing="6" MaxWidth="728" HorizontalAlignment="Stretch" VerticalAlignment="Top" Margin="0,6,0,0">',
     '<Grid x:Name="RunningPanel" Visibility="Collapsed" RowSpacing="4" MaxWidth="712" HorizontalAlignment="Stretch" VerticalAlignment="Top" Margin="0,4,0,0">'),
    ('<Border Style="{StaticResource SurfaceCardStyle}" Padding="8,6">',
     '<Border Style="{StaticResource SurfaceCardStyle}" Padding="6,5">'),
    ('<Grid RowSpacing="8">', '<Grid RowSpacing="6">', 1),
    ('<Border Width="36" Height="36"', '<Border Width="32" Height="32"'),
    ('Glyph="&#xE8A5;" FontSize="18"', 'Glyph="&#xE8A5;" FontSize="16"'),
    ('x:Name="CurrentFileText" Text="Preparando..." FontSize="17"', 'x:Name="CurrentFileText" Text="Preparando..." FontSize="16"'),
    ('x:Name="OverallPercentText" Grid.Column="2" Text="0%" FontSize="23"', 'x:Name="OverallPercentText" Grid.Column="2" Text="0%" FontSize="21"'),
    ('<Grid Grid.Row="2" ColumnSpacing="20">', '<Grid Grid.Row="2" ColumnSpacing="16">'),
    ('Text="0 MiB/s" FontSize="15"', 'Text="0 MiB/s" FontSize="14"'),
    ('Text="--:--:--" FontSize="15"', 'Text="--:--:--" FontSize="14"'),
    ('Text="0/0" FontSize="15"', 'Text="0/0" FontSize="14"'),
    ('Style="{StaticResource CompactButtonStyle}" MinWidth="104"', 'Style="{StaticResource CompactButtonStyle}" MinWidth="96"'),
    ('<Border Grid.Row="2" Padding="10,0"', '<Border Grid.Row="2" Padding="6,0"'),
]

for item in replacements:
    old, new, *count = item
    limit = count[0] if count else -1
    if old not in xaml:
        raise SystemExit(f'XAML anchor not found: {old[:100]!r}')
    xaml = xaml.replace(old, new, limit)

code_replacements = [
    ('Interval = TimeSpan.FromMilliseconds(180)', 'Interval = TimeSpan.FromMilliseconds(250)'),
    ('SizeInt32(740, 340)', 'SizeInt32(720, 320)'),
]
for old, new in code_replacements:
    if old not in code:
        raise SystemExit(f'code anchor not found: {old}')
    code = code.replace(old, new)

# Contract updates: preserve native WinUI density/titlebar and lock the lower-cost layout.
test = test.replace('SizeInt32(740, 340)', 'SizeInt32(720, 320)')
test = test.replace('VerticalAlignment=\\"Top\\" Margin=\\"0,6,0,0\\"', 'VerticalAlignment=\\"Top\\" Margin=\\"0,4,0,0\\"')
test = test.replace('Width=\\"86\\" Height=\\"34\\" Padding=\\"5,0,3,0\\"', 'MinWidth=\\"56\\" MaxWidth=\\"148\\" Height=\\"30\\" Padding=\\"5,0,2,0\\"')
test = test.replace('Width=\\"20\\" Height=\\"20\\" Padding=\\"0\\"', 'Width=\\"18\\" Height=\\"18\\" Padding=\\"0\\"')
test = test.replace('Margin\\" Value=\\"0,0,3,0\\"', 'Margin\\" Value=\\"0,0,2,0\\"')
needle = '        Assert.IsTrue(code.Contains("Verify: true", StringComparison.Ordinal));\n'
if needle not in test:
    raise SystemExit('test insertion anchor not found')
test = test.replace(needle, needle + '        Assert.IsTrue(code.Contains("Interval = TimeSpan.FromMilliseconds(250)", StringComparison.Ordinal));\n        Assert.IsFalse(xaml.Contains("Width=\\"86\\"", StringComparison.Ordinal));\n')

xaml_path.write_text(xaml, encoding='utf-8')
code_path.write_text(code, encoding='utf-8')
test_path.write_text(test, encoding='utf-8')
