from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
XAML = ROOT / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml'
CODE = ROOT / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs'
TEST = ROOT / 'dotnet/RepartoCopier.Core.Tests/WinUiCompactProgressContractTests.cs'

xaml = r'''<Window
    x:Class="RepartoCopier.WinUI.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d"
    Title="RepartoCopier">

    <Grid x:Name="Root" Background="Transparent">
        <Grid.Resources>
            <Style x:Key="SurfaceCardStyle" TargetType="Border">
                <Setter Property="Background" Value="{ThemeResource CardBackgroundFillColorDefaultBrush}" />
                <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}" />
                <Setter Property="BorderThickness" Value="1" />
                <Setter Property="CornerRadius" Value="8" />
            </Style>
            <Style x:Key="CompactButtonStyle" TargetType="Button">
                <Setter Property="Height" Value="34" />
                <Setter Property="MinWidth" Value="76" />
                <Setter Property="Padding" Value="11,0" />
                <Setter Property="CornerRadius" Value="6" />
            </Style>
            <Style x:Key="MenuButtonStyle" TargetType="Button">
                <Setter Property="Height" Value="40" />
                <Setter Property="HorizontalContentAlignment" Value="Left" />
                <Setter Property="Padding" Value="11,0" />
                <Setter Property="CornerRadius" Value="6" />
                <Setter Property="Background" Value="Transparent" />
                <Setter Property="BorderThickness" Value="0" />
            </Style>
        </Grid.Resources>

        <Grid.RowDefinitions>
            <RowDefinition Height="48" />
            <RowDefinition Height="*" />
            <RowDefinition Height="28" />
        </Grid.RowDefinitions>

        <Grid x:Name="AppTitleBar" Grid.Row="0" Padding="14,0,142,0" Background="Transparent">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <Image x:Name="LogoImage" Width="28" Height="28" Source="ms-appx:///Assets/AppLogo.png"
                   Stretch="Uniform" VerticalAlignment="Center" />
            <StackPanel Grid.Column="1" Margin="9,0,0,0" VerticalAlignment="Center" Spacing="0">
                <TextBlock Text="RepartoCopier" FontSize="15" FontWeight="SemiBold" />
                <TextBlock Text="FAN-OUT" Opacity="0.56" FontSize="10" />
            </StackPanel>
            <Button x:Name="AppMenuButton" Grid.Column="3" Width="34" Height="32" Padding="0"
                    VerticalAlignment="Center" HorizontalAlignment="Right" CornerRadius="6"
                    ToolTipService.ToolTip="Menú">
                <FontIcon Glyph="&#xE700;" FontSize="16" />
                <Button.Flyout>
                    <Flyout Placement="BottomEdgeAlignedRight">
                        <Border Width="208" Padding="6" CornerRadius="8"
                                Background="{ThemeResource AcrylicBackgroundFillColorDefaultBrush}"
                                BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1">
                            <StackPanel Spacing="1">
                                <Button Style="{StaticResource MenuButtonStyle}" Click="LoadProfile_Click">
                                    <StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE8E5;" FontSize="16"/><TextBlock Text="Cargar copia..." /></StackPanel>
                                </Button>
                                <Button Style="{StaticResource MenuButtonStyle}" Click="SaveProfile_Click">
                                    <StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE74E;" FontSize="16"/><TextBlock Text="Guardar copia..." /></StackPanel>
                                </Button>
                                <Rectangle Height="1" Margin="6,4" Fill="{ThemeResource DividerStrokeColorDefaultBrush}" />
                                <Button Style="{StaticResource MenuButtonStyle}" Click="Settings_Click">
                                    <StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE713;" FontSize="16"/><TextBlock Text="Ajustes" /></StackPanel>
                                </Button>
                                <Button x:Name="AboutMenuButton" Style="{StaticResource MenuButtonStyle}" Click="About_Click">
                                    <StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE946;" FontSize="16"/><TextBlock Text="Acerca de" /></StackPanel>
                                </Button>
                                <Rectangle Height="1" Margin="6,4" Fill="{ThemeResource DividerStrokeColorDefaultBrush}" />
                                <Button Style="{StaticResource MenuButtonStyle}" Click="Exit_Click">
                                    <StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE8BB;" FontSize="16"/><TextBlock Text="Salir" /></StackPanel>
                                </Button>
                            </StackPanel>
                        </Border>
                    </Flyout>
                </Button.Flyout>
            </Button>
        </Grid>

        <Grid Grid.Row="1" Margin="14,4,14,10" MaxWidth="820" HorizontalAlignment="Stretch">
            <InfoBar x:Name="ErrorBar" VerticalAlignment="Top" Margin="0,0,0,8" IsOpen="False"
                     Severity="Error" IsClosable="True" Canvas.ZIndex="10" />

            <Grid x:Name="PreparationPanel" Visibility="Visible" RowSpacing="8" MaxWidth="720" HorizontalAlignment="Center">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                </Grid.RowDefinitions>

                <Grid Margin="2,0,2,2">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                    <StackPanel Spacing="1">
                        <TextBlock Text="Nueva copia" FontSize="20" FontWeight="SemiBold" />
                        <TextBlock Text="Un origen · varios destinos" Opacity="0.62" FontSize="11" />
                    </StackPanel>
                    <Button x:Name="StartButton" Grid.Column="1" Style="{StaticResource AccentButtonStyle}"
                            Height="36" MinWidth="126" CornerRadius="6" Click="Start_Click" VerticalAlignment="Center">
                        <StackPanel Orientation="Horizontal" Spacing="7"><FontIcon Glyph="&#xE768;" FontSize="13"/><TextBlock Text="Iniciar copia"/></StackPanel>
                    </Button>
                </Grid>

                <Border Grid.Row="1" Style="{StaticResource SurfaceCardStyle}" Padding="12">
                    <Grid RowSpacing="10">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="1" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="Auto" />
                        </Grid.RowDefinitions>

                        <Grid ColumnSpacing="8">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                            <TextBox x:Name="SourcePathBox" Height="36" PlaceholderText="Archivo o carpeta de origen" IsReadOnly="True" CornerRadius="6" />
                            <Button Grid.Column="1" Style="{StaticResource CompactButtonStyle}" Click="PickSourceFile_Click" ToolTipService.ToolTip="Elegir archivo">
                                <StackPanel Orientation="Horizontal" Spacing="6"><FontIcon Glyph="&#xE8A5;" FontSize="13"/><TextBlock Text="Archivo"/></StackPanel>
                            </Button>
                            <Button Grid.Column="2" Style="{StaticResource CompactButtonStyle}" Click="PickSourceFolder_Click" ToolTipService.ToolTip="Elegir carpeta">
                                <StackPanel Orientation="Horizontal" Spacing="6"><FontIcon Glyph="&#xE8B7;" FontSize="13"/><TextBlock Text="Carpeta"/></StackPanel>
                            </Button>
                        </Grid>

                        <Grid Grid.Row="1">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                            <StackPanel Orientation="Horizontal" Spacing="7" VerticalAlignment="Center">
                                <FontIcon Glyph="&#xEDA2;" FontSize="14" Opacity="0.72"/>
                                <TextBlock Text="Destinos" FontSize="13" FontWeight="SemiBold"/>
                                <TextBlock x:Name="DestinationCountText" Text="0 destinos" Opacity="0.58" FontSize="11" VerticalAlignment="Center"/>
                            </StackPanel>
                            <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                <Button Height="30" Padding="10,0" CornerRadius="6" Click="AddDestinations_Click">
                                    <StackPanel Orientation="Horizontal" Spacing="5"><FontIcon Glyph="&#xE710;" FontSize="12"/><TextBlock Text="Agregar"/></StackPanel>
                                </Button>
                                <Button Width="30" Height="30" Padding="0" CornerRadius="6" Click="ClearDestinations_Click" ToolTipService.ToolTip="Quitar todos">
                                    <FontIcon Glyph="&#xE74D;" FontSize="12"/>
                                </Button>
                            </StackPanel>
                        </Grid>

                        <Rectangle Grid.Row="2" Fill="{ThemeResource DividerStrokeColorDefaultBrush}" />

                        <ListView x:Name="DestinationList" Grid.Row="3" SelectionMode="None" MaxHeight="108" Padding="0" IsTabStop="False">
                            <ListView.ItemContainerStyle>
                                <Style TargetType="ListViewItem">
                                    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                                    <Setter Property="Padding" Value="0"/>
                                    <Setter Property="Margin" Value="0"/>
                                    <Setter Property="MinHeight" Value="32"/>
                                    <Setter Property="CornerRadius" Value="5"/>
                                </Style>
                            </ListView.ItemContainerStyle>
                            <ListView.ItemTemplate>
                                <DataTemplate>
                                    <Grid MinHeight="30" Padding="7,1" ColumnSpacing="8">
                                        <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                                        <FontIcon Glyph="&#xEDA2;" FontSize="13" Opacity="0.65" VerticalAlignment="Center"/>
                                        <TextBlock Grid.Column="1" Text="{Binding Path}" FontSize="11" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                                        <Button Grid.Column="2" Width="26" Height="26" Padding="0" CornerRadius="5" Tag="{Binding Path}" Click="RemoveDestination_Click" ToolTipService.ToolTip="Quitar">
                                            <FontIcon Glyph="&#xE711;" FontSize="10"/>
                                        </Button>
                                    </Grid>
                                </DataTemplate>
                            </ListView.ItemTemplate>
                        </ListView>

                        <Expander Grid.Row="4" Header="Opciones" IsExpanded="False" HorizontalAlignment="Stretch">
                            <StackPanel Orientation="Horizontal" Spacing="16" Padding="0,7,0,2">
                                <CheckBox x:Name="VerifyCheck" Content="Verificar" IsChecked="True"/>
                                <CheckBox x:Name="SkipSameCheck" Content="Omitir iguales"/>
                                <CheckBox x:Name="KeepGoingCheck" Content="Continuar ante error"/>
                            </StackPanel>
                        </Expander>
                    </Grid>
                </Border>

                <TextBlock Grid.Row="2" Margin="2,0" Text="La copia usa un único flujo FAN-OUT compartido para todos los destinos." Opacity="0.52" FontSize="10" />
            </Grid>

            <Grid x:Name="RunningPanel" Visibility="Collapsed" RowSpacing="8" MaxWidth="760" HorizontalAlignment="Center" VerticalAlignment="Center">
                <Border Style="{StaticResource SurfaceCardStyle}" Padding="14,12">
                    <Grid RowSpacing="11">
                        <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>

                        <Grid ColumnSpacing="10">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                            <Border Width="36" Height="36" CornerRadius="7" Background="{ThemeResource ControlFillColorDefaultBrush}" VerticalAlignment="Center">
                                <FontIcon x:Name="OperationIcon" Glyph="&#xE8A5;" FontSize="18" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                            </Border>
                            <StackPanel Grid.Column="1" Spacing="0" VerticalAlignment="Center">
                                <TextBlock x:Name="OperationTitleText" Text="Copiando..." FontSize="12" Opacity="0.66"/>
                                <TextBlock x:Name="CurrentFileText" Text="Preparando..." FontSize="17" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" MaxLines="1"/>
                                <TextBlock x:Name="CurrentPathText" Opacity="0.52" FontSize="10" TextTrimming="CharacterEllipsis" MaxLines="1"/>
                            </StackPanel>
                            <TextBlock x:Name="OverallPercentText" Grid.Column="2" Text="0%" FontSize="23" FontWeight="SemiBold" VerticalAlignment="Center" />
                        </Grid>

                        <Grid Grid.Row="1">
                            <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                            <ProgressBar x:Name="OverallProgressBar" Height="6" Minimum="0" Maximum="100" Value="0" />
                            <TextBlock x:Name="OverallDetailText" Grid.Row="1" Margin="0,5,0,0" Text="Preparando..." Opacity="0.58" FontSize="10"/>
                        </Grid>

                        <Grid Grid.Row="2" ColumnSpacing="20">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <StackPanel><TextBlock Text="Velocidad" Opacity="0.52" FontSize="10"/><TextBlock x:Name="SpeedMetricText" Text="0 MiB/s" FontSize="15" FontWeight="SemiBold"/></StackPanel>
                            <StackPanel Grid.Column="1"><TextBlock Text="Restante" Opacity="0.52" FontSize="10"/><TextBlock x:Name="RemainingMetricText" Text="--:--:--" FontSize="15" FontWeight="SemiBold"/></StackPanel>
                            <StackPanel Grid.Column="2"><TextBlock Text="Archivos" Opacity="0.52" FontSize="10"/><TextBlock x:Name="FilesMetricText" Text="0/0" FontSize="15" FontWeight="SemiBold"/></StackPanel>
                            <CheckBox x:Name="ShutdownCheck" Grid.Column="3" Content="Apagar al terminar" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                        </Grid>

                        <Grid Grid.Row="3" ColumnSpacing="7">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <Button x:Name="PauseButton" Style="{StaticResource CompactButtonStyle}" MinWidth="104" IsEnabled="False" Click="Pause_Click">
                                <StackPanel Orientation="Horizontal" Spacing="6"><FontIcon x:Name="PauseIcon" Glyph="&#xE769;" FontSize="12"/><TextBlock x:Name="PauseButtonText" Text="Pausar"/></StackPanel>
                            </Button>
                            <Button x:Name="CancelButton" Grid.Column="1" Style="{StaticResource CompactButtonStyle}" MinWidth="104" IsEnabled="False" Click="Cancel_Click">
                                <StackPanel Orientation="Horizontal" Spacing="6"><FontIcon Glyph="&#xE711;" FontSize="12"/><TextBlock Text="Cancelar"/></StackPanel>
                            </Button>
                        </Grid>
                    </Grid>
                </Border>
            </Grid>
        </Grid>

        <Border Grid.Row="2" Padding="16,0" BorderThickness="0,1,0,0" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
            <Grid>
                <TextBlock x:Name="StatusText" Text="Listo" FontSize="10" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                <TextBlock x:Name="ElapsedText" HorizontalAlignment="Right" FontSize="10" Opacity="0.62" VerticalAlignment="Center"/>
            </Grid>
        </Border>
    </Grid>
</Window>
'''
XAML.write_text(xaml, encoding='utf-8')

code = CODE.read_text(encoding='utf-8')
code = code.replace('AppWindow.Resize(new SizeInt32(960, 620));', 'AppWindow.Resize(new SizeInt32(840, 520));')
old = '''        try { SystemBackdrop = new MicaBackdrop(); } catch { }\n\n        ApplySavedTheme();'''
new = '''        try { SystemBackdrop = new MicaBackdrop(); } catch { }\n        ConfigureNativeWindowChrome();\n\n        ApplySavedTheme();'''
if old not in code:
    raise RuntimeError('constructor chrome insertion point not found')
code = code.replace(old, new, 1)
marker = '''    private void ApplySavedTheme()\n    {'''
chrome = '''    private void ConfigureNativeWindowChrome()\n    {\n        try\n        {\n            var titleBar = AppWindow.TitleBar;\n            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;\n            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;\n            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(24, 128, 128, 128);\n            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 128, 128, 128);\n        }\n        catch { }\n    }\n\n'''
if marker not in code:
    raise RuntimeError('ApplySavedTheme marker not found')
code = code.replace(marker, chrome + marker, 1)
old_copy = '''            var total = snapshots.Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => checked(sum + item.Total));\n            var written = snapshots.Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => checked(sum + item.Written));'''
new_copy = '''            var active = snapshots\n                .Where(item => item.Phase is not DestinationPhase.Failed and not DestinationPhase.Cancelled)\n                .ToArray();\n            var total = snapshots.Select(item => item.Total).DefaultIfEmpty(0UL).Max();\n            var written = active.Length == 0\n                ? snapshots.Select(item => item.Written).DefaultIfEmpty(0UL).Max()\n                : active.Min(item => item.Written);'''
if old_copy not in code:
    raise RuntimeError('copy progress aggregate block not found')
code = code.replace(old_copy, new_copy, 1)
old_verify = '''            var verifyTotal = snapshots.Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => checked(sum + item.VerifyBytesTotal));\n            var verified = snapshots.Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => checked(sum + item.VerifiedBytes));'''
new_verify = '''            var verifyActive = snapshots\n                .Where(item => item.Phase is not DestinationPhase.Failed and not DestinationPhase.Cancelled)\n                .ToArray();\n            var verifyTotal = snapshots.Select(item => item.VerifyBytesTotal).DefaultIfEmpty(0UL).Max();\n            var verified = verifyActive.Length == 0\n                ? snapshots.Select(item => item.VerifiedBytes).DefaultIfEmpty(0UL).Max()\n                : verifyActive.Min(item => item.VerifiedBytes);'''
if old_verify not in code:
    raise RuntimeError('verify progress aggregate block not found')
code = code.replace(old_verify, new_verify, 1)
CODE.write_text(code, encoding='utf-8')

test = TEST.read_text(encoding='utf-8')
test = test.replace('SizeInt32(960, 620)', 'SizeInt32(840, 520)')
test = test.replace('MaxWidth=\\"980\\"', 'MaxWidth=\\"820\\"')
test = test.replace('MaxHeight=\\"132\\"', 'MaxHeight=\\"108\\"')
if 'OverallProgressBar' not in test:
    raise RuntimeError('compact progress test missing')
TEST.write_text(test, encoding='utf-8')

print('native compact WinUI migration applied')
