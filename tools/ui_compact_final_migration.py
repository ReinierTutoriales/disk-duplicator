from pathlib import Path

root = Path(__file__).resolve().parents[1]
xaml_path = root / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml'
code_path = root / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs'
test_path = root / 'dotnet/RepartoCopier.Core.Tests/WinUiCompactProgressContractTests.cs'

xaml = xaml_path.read_text(encoding='utf-8')
repls = {
    '<RowDefinition Height="26" />': '<RowDefinition Height="22" />',
    '<Grid Grid.Row="1" Margin="8,0,8,6" MaxWidth="760" HorizontalAlignment="Stretch">': '<Grid Grid.Row="1" Margin="6,0,6,4" MaxWidth="740" HorizontalAlignment="Stretch">',
    '<Grid x:Name="PreparationPanel" Visibility="Visible" RowSpacing="8" MaxWidth="744" HorizontalAlignment="Stretch">': '<Grid x:Name="PreparationPanel" Visibility="Visible" RowSpacing="6" MaxWidth="728" HorizontalAlignment="Stretch">',
    '<Border Grid.Row="1" Style="{StaticResource SurfaceCardStyle}" Padding="10">': '<Border Grid.Row="1" Style="{StaticResource SurfaceCardStyle}" Padding="8">',
    '<Grid RowSpacing="8">': '<Grid RowSpacing="6">',
    '<ListView x:Name="DestinationList" Grid.Row="3" SelectionMode="None" Height="50" MaxHeight="50"': '<ListView x:Name="DestinationList" Grid.Row="3" SelectionMode="None" Height="40" MaxHeight="40"',
    '<Setter Property="Margin" Value="0,0,6,0"/>': '<Setter Property="Margin" Value="0,0,3,0"/>',
    '<Setter Property="MinHeight" Value="42"/>': '<Setter Property="MinHeight" Value="34"/>',
    '<Border Width="158" Height="42" Padding="8,0" CornerRadius="6"': '<Border Width="86" Height="34" Padding="5,0,3,0" CornerRadius="5"',
    '<Grid ColumnSpacing="7">': '<Grid ColumnSpacing="4">',
    '<FontIcon Glyph="&#xEDA2;" FontSize="13" Opacity="0.68" VerticalAlignment="Center"/>': '<FontIcon Glyph="&#xEDA2;" FontSize="12" Opacity="0.68" VerticalAlignment="Center"/>',
    '<TextBlock Grid.Column="1" Text="{Binding Path}" FontSize="11" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>': '<TextBlock Grid.Column="1" Text="{Binding Path}" FontSize="11" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" MinWidth="22"/>',
    '<Button Grid.Column="2" Width="24" Height="24" Padding="0" CornerRadius="5" Tag="{Binding Path}" Click="RemoveDestination_Click" ToolTipService.ToolTip="Quitar">': '<Button Grid.Column="2" Width="20" Height="20" Padding="0" CornerRadius="4" Tag="{Binding Path}" Click="RemoveDestination_Click" ToolTipService.ToolTip="Quitar">',
    '<FontIcon Glyph="&#xE711;" FontSize="10"/>': '<FontIcon Glyph="&#xE711;" FontSize="9"/>',
    '<StackPanel Orientation="Horizontal" Spacing="16" Padding="0,7,0,2">\n                                <CheckBox x:Name="VerifyCheck" Content="Verificar después de copiar" IsChecked="True"/>\n                                <CheckBox x:Name="SkipSameCheck" Content="Omitir iguales"/>\n                                <CheckBox x:Name="KeepGoingCheck" Content="Continuar ante error"/>\n                            </StackPanel>': '<StackPanel Orientation="Horizontal" Spacing="12" Padding="0,5,0,0">\n                                <CheckBox x:Name="SkipSameCheck" Content="Omitir iguales"/>\n                                <CheckBox x:Name="KeepGoingCheck" Content="Continuar ante error"/>\n                            </StackPanel>',
    '<Grid x:Name="RunningPanel" Visibility="Collapsed" RowSpacing="8" MaxWidth="744" HorizontalAlignment="Stretch" VerticalAlignment="Top" Margin="0,10,0,0">': '<Grid x:Name="RunningPanel" Visibility="Collapsed" RowSpacing="6" MaxWidth="728" HorizontalAlignment="Stretch" VerticalAlignment="Top" Margin="0,6,0,0">',
    '<Border Style="{StaticResource SurfaceCardStyle}" Padding="10,8">': '<Border Style="{StaticResource SurfaceCardStyle}" Padding="8,6">',
    '<Border Grid.Row="2" Padding="16,0"': '<Border Grid.Row="2" Padding="10,0"',
}
for old, new in repls.items():
    if old not in xaml:
        raise SystemExit(f'Missing XAML pattern: {old[:100]}')
    xaml = xaml.replace(old, new, 1)
xaml_path.write_text(xaml, encoding='utf-8')

code = code_path.read_text(encoding='utf-8')
code_repls = {
    'AppWindow.Resize(new SizeInt32(760, 400));': 'AppWindow.Resize(new SizeInt32(740, 340));',
    'Verify: VerifyCheck.IsChecked == true,': 'Verify: true,',
    '            OverallDetailText.Text = options.Verify\n                ? "Preparando copia con verificación rápida..."\n                : "Preparando copia sin verificación posterior...";': '            OverallDetailText.Text = "Preparando copia con verificación rápida...";',
    '            VerifyCheck.IsChecked = profile.VerifyAfterCopy;\n': '',
    '                VerifyCheck.IsChecked == true);': '                true);',
    '        VerifyCheck.IsEnabled = enabled;\n': '',
}
for old, new in code_repls.items():
    if old not in code:
        raise SystemExit(f'Missing code pattern: {old[:100]}')
    code = code.replace(old, new, 1)
code_path.write_text(code, encoding='utf-8')

test = test_path.read_text(encoding='utf-8')
test = test.replace('Assert.IsTrue(code.Contains("SizeInt32(760, 400)", StringComparison.Ordinal));', 'Assert.IsTrue(code.Contains("SizeInt32(740, 340)", StringComparison.Ordinal));')
test = test.replace('Assert.IsTrue(xaml.Contains("VerticalAlignment=\\"Top\\" Margin=\\"0,10,0,0\\"", StringComparison.Ordinal));', 'Assert.IsTrue(xaml.Contains("VerticalAlignment=\\"Top\\" Margin=\\"0,6,0,0\\"", StringComparison.Ordinal));')
insert = '''\n    [TestMethod]\n    public void DestinationChipsAreDenseAndVerificationIsNotAUserToggle()\n    {\n        var root = FindRepositoryRoot();\n        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));\n        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));\n        Assert.IsTrue(xaml.Contains("Width=\\"86\\" Height=\\"34\\" Padding=\\"5,0,3,0\\"", StringComparison.Ordinal));\n        Assert.IsTrue(xaml.Contains("Width=\\"20\\" Height=\\"20\\" Padding=\\"0\\"", StringComparison.Ordinal));\n        Assert.IsTrue(xaml.Contains("Margin\\" Value=\\"0,0,3,0\\"", StringComparison.Ordinal));\n        Assert.IsFalse(xaml.Contains("VerifyCheck", StringComparison.Ordinal));\n        Assert.IsFalse(code.Contains("VerifyCheck", StringComparison.Ordinal));\n        Assert.IsTrue(code.Contains("Verify: true", StringComparison.Ordinal));\n    }\n'''
marker = '\n    private static string FindRepositoryRoot()\n'
if marker not in test:
    raise SystemExit('Test insertion marker missing')
test = test.replace(marker, insert + marker, 1)
test_path.write_text(test, encoding='utf-8')
print('Maximum compact UI migration applied')