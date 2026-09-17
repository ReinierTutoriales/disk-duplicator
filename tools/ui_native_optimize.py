from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
xaml_path = root / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml'
code_path = root / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs'
test_path = root / 'dotnet/RepartoCopier.Core.Tests/WinUiCompactProgressContractTests.cs'

xaml = xaml_path.read_text(encoding='utf-8')
code = code_path.read_text(encoding='utf-8')
test = test_path.read_text(encoding='utf-8')

old_list = re.search(r'\s*<ListView x:Name="DestinationList".*?</ListView>', xaml, re.S)
if not old_list:
    raise SystemExit('Destination ListView block not found')

new_list = '''
                        <ScrollViewer Grid.Row="3" Height="34"
                                      HorizontalScrollMode="Auto"
                                      HorizontalScrollBarVisibility="Auto"
                                      VerticalScrollMode="Disabled"
                                      VerticalScrollBarVisibility="Disabled"
                                      IsTabStop="False">
                            <ItemsRepeater x:Name="DestinationList">
                                <ItemsRepeater.Layout>
                                    <StackLayout Orientation="Horizontal" Spacing="3" />
                                </ItemsRepeater.Layout>
                                <ItemsRepeater.ItemTemplate>
                                    <DataTemplate>
                                        <Border MinWidth="58" MaxWidth="148" Height="30" Padding="5,0,2,0" CornerRadius="5"
                                                Background="{ThemeResource ControlFillColorDefaultBrush}"
                                                BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}" BorderThickness="1"
                                                ToolTipService.ToolTip="{Binding Path}">
                                            <Grid ColumnSpacing="2">
                                                <Grid.ColumnDefinitions>
                                                    <ColumnDefinition Width="Auto"/>
                                                    <ColumnDefinition Width="Auto"/>
                                                    <ColumnDefinition Width="Auto"/>
                                                </Grid.ColumnDefinitions>
                                                <FontIcon Glyph="&#xEDA2;" FontSize="11" Opacity="0.68" VerticalAlignment="Center"/>
                                                <TextBlock Grid.Column="1" Text="{Binding Path}" FontSize="11"
                                                           MaxWidth="92" TextTrimming="CharacterEllipsis"
                                                           VerticalAlignment="Center"/>
                                                <Button Grid.Column="2" Width="18" Height="18" MinWidth="18" MinHeight="18"
                                                        Padding="0" Margin="1,0,0,0" CornerRadius="4"
                                                        Tag="{Binding Path}" Click="RemoveDestination_Click"
                                                        ToolTipService.ToolTip="Quitar">
                                                    <FontIcon Glyph="&#xE711;" FontSize="8"/>
                                                </Button>
                                            </Grid>
                                        </Border>
                                    </DataTemplate>
                                </ItemsRepeater.ItemTemplate>
                            </ItemsRepeater>
                        </ScrollViewer>'''
xaml = xaml[:old_list.start()] + new_list + xaml[old_list.end():]

repls = {
    'Grid.Row="1" Margin="6,0,6,4" MaxWidth="740"': 'Grid.Row="1" Margin="4,0,4,3" MaxWidth="720"',
    'RowSpacing="6" MaxWidth="728"': 'RowSpacing="5" MaxWidth="712"',
    'Margin="2,0,2,2"': 'Margin="1,0,1,1"',
    'Height="36" MinWidth="126"': 'Height="34" MinWidth="118"',
    'Style="{StaticResource SurfaceCardStyle}" Padding="8"': 'Style="{StaticResource SurfaceCardStyle}" Padding="6"',
    '<Grid RowSpacing="6">': '<Grid RowSpacing="5">',
    'ColumnSpacing="8"': 'ColumnSpacing="6"',
    'Height="34" PlaceholderText="Archivo o carpeta de origen"': 'Height="32" PlaceholderText="Archivo o carpeta de origen"',
    'Height="30" Padding="10,0"': 'Height="28" Padding="8,0"',
    'Width="30" Height="30"': 'Width="28" Height="28"',
    'Grid.Row="2" Margin="2,0"': 'Grid.Row="2" Margin="1,0"',
    'RowSpacing="6" MaxWidth="728" HorizontalAlignment="Stretch" VerticalAlignment="Top" Margin="0,6,0,0"': 'RowSpacing="5" MaxWidth="712" HorizontalAlignment="Stretch" VerticalAlignment="Top" Margin="0,4,0,0"',
    'Style="{StaticResource SurfaceCardStyle}" Padding="8,6"': 'Style="{StaticResource SurfaceCardStyle}" Padding="7,5"',
    '<Grid RowSpacing="8">': '<Grid RowSpacing="6">',
    'ColumnSpacing="10"': 'ColumnSpacing="8"',
    'Width="36" Height="36"': 'Width="34" Height="34"',
    'FontSize="17" FontWeight="SemiBold"': 'FontSize="16" FontWeight="SemiBold"',
    'FontSize="23" FontWeight="SemiBold"': 'FontSize="21" FontWeight="SemiBold"',
    'Grid.Row="2" ColumnSpacing="20"': 'Grid.Row="2" ColumnSpacing="16"',
    'FontSize="15" FontWeight="SemiBold"': 'FontSize="14" FontWeight="SemiBold"',
    'Grid.Row="3" ColumnSpacing="7"': 'Grid.Row="3" ColumnSpacing="6"',
    'MinWidth="104"': 'MinWidth="96"',
    'Grid.Row="2" Padding="10,0"': 'Grid.Row="2" Padding="8,0"',
}
for old, new in repls.items():
    xaml = xaml.replace(old, new)

if '<ListView x:Name="DestinationList"' in xaml:
    raise SystemExit('ListView destination surface still present')
if '<ItemsRepeater x:Name="DestinationList">' not in xaml:
    raise SystemExit('ItemsRepeater migration failed')

code = code.replace('TimeSpan.FromMilliseconds(180)', 'TimeSpan.FromMilliseconds(250)')
code = code.replace('SizeInt32(740, 340)', 'SizeInt32(720, 320)')
if 'TimeSpan.FromMilliseconds(250)' not in code or 'SizeInt32(720, 320)' not in code:
    raise SystemExit('Code-behind compact update failed')

# Update the permanent UI contract so this optimization cannot silently regress.
test = test.replace('Assert.IsTrue(code.Contains("SizeInt32(740, 340)", StringComparison.Ordinal));',
                    'Assert.IsTrue(code.Contains("SizeInt32(720, 320)", StringComparison.Ordinal));')
test = test.replace('Assert.IsTrue(xaml.Contains("VerticalAlignment=\\"Top\\" Margin=\\"0,6,0,0\\"", StringComparison.Ordinal));',
                    'Assert.IsTrue(xaml.Contains("VerticalAlignment=\\"Top\\" Margin=\\"0,4,0,0\\"", StringComparison.Ordinal));')
old_asserts = '''        Assert.IsTrue(xaml.Contains("Width=\\"86\\" Height=\\"34\\" Padding=\\"5,0,3,0\\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("Width=\\"20\\" Height=\\"20\\" Padding=\\"0\\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("Margin\\" Value=\\"0,0,3,0\\"", StringComparison.Ordinal));'''
new_asserts = '''        Assert.IsTrue(xaml.Contains("<ItemsRepeater x:Name=\\"DestinationList\\">", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("<ListView x:Name=\\"DestinationList\\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("MinWidth=\\"58\\" MaxWidth=\\"148\\" Height=\\"30\\" Padding=\\"5,0,2,0\\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("Width=\\"18\\" Height=\\"18\\" MinWidth=\\"18\\" MinHeight=\\"18\\"", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("TimeSpan.FromMilliseconds(250)", StringComparison.Ordinal));'''
if old_asserts not in test:
    raise SystemExit('Old destination UI contract assertions not found')
test = test.replace(old_asserts, new_asserts)

xaml_path.write_text(xaml, encoding='utf-8', newline='\n')
code_path.write_text(code, encoding='utf-8', newline='\n')
test_path.write_text(test, encoding='utf-8', newline='\n')
print('Native UI optimization applied')
