from pathlib import Path

root = Path(__file__).resolve().parents[1]

# 1) Apply official WinUI compact density resources.
app = root / 'dotnet/RepartoCopier.WinUI/App.xaml'
text = app.read_text(encoding='utf-8')
old = '''    <Application.Resources>\n        <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />\n    </Application.Resources>'''
new = '''    <Application.Resources>\n        <ResourceDictionary>\n            <ResourceDictionary.MergedDictionaries>\n                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />\n                <ResourceDictionary Source="ms-appx:///Microsoft.UI.Xaml/DensityStyles/Compact.xaml" />\n            </ResourceDictionary.MergedDictionaries>\n        </ResourceDictionary>\n    </Application.Resources>'''
if old not in text:
    raise SystemExit('App.xaml resource block not found')
app.write_text(text.replace(old, new), encoding='utf-8')

# 2) Replace hand-built title bar with Windows App SDK TitleBar and compact running layout.
xaml = root / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml'
text = xaml.read_text(encoding='utf-8')
text = text.replace('<RowDefinition Height="36" />', '<RowDefinition Height="Auto" />', 1)
start = text.index('        <Grid x:Name="AppTitleBar"')
end_marker = '        <Grid Grid.Row="1" Margin="12,2,12,8" MaxWidth="780" HorizontalAlignment="Stretch">'
end = text.index(end_marker)
new_title = '''        <TitleBar x:Name="AppTitleBar" Grid.Row="0" Title="RepartoCopier">\n            <TitleBar.IconSource>\n                <ImageIconSource ImageSource="ms-appx:///Assets/AppLogo.png" />\n            </TitleBar.IconSource>\n            <TitleBar.RightHeader>\n                <Button x:Name="AppMenuButton" Width="30" Height="28" Padding="0" CornerRadius="6"\n                        ToolTipService.ToolTip="Menú">\n                    <FontIcon Glyph="&#xE700;" FontSize="16" />\n                    <Button.Flyout>\n                        <Flyout Placement="BottomEdgeAlignedRight">\n                            <Border Width="208" Padding="6" CornerRadius="8"\n                                    Background="{ThemeResource AcrylicBackgroundFillColorDefaultBrush}"\n                                    BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1">\n                                <StackPanel Spacing="1">\n                                    <Button Style="{StaticResource MenuButtonStyle}" Click="LoadProfile_Click">\n                                        <StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE8E5;" FontSize="16"/><TextBlock Text="Cargar copia..." /></StackPanel>\n                                    </Button>\n                                    <Button Style="{StaticResource MenuButtonStyle}" Click="SaveProfile_Click">\n                                        <StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE74E;" FontSize="16"/><TextBlock Text="Guardar copia..." /></StackPanel>\n                                    </Button>\n                                    <Rectangle Height="1" Margin="6,4" Fill="{ThemeResource DividerStrokeColorDefaultBrush}" />\n                                    <Button Style="{StaticResource MenuButtonStyle}" Click="Settings_Click">\n                                        <StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE713;" FontSize="16"/><TextBlock Text="Ajustes" /></StackPanel>\n                                    </Button>\n                                    <Button x:Name="AboutMenuButton" Style="{StaticResource MenuButtonStyle}" Click="About_Click">\n                                        <StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE946;" FontSize="16"/><TextBlock Text="Acerca de" /></StackPanel>\n                                    </Button>\n                                    <Rectangle Height="1" Margin="6,4" Fill="{ThemeResource DividerStrokeColorDefaultBrush}" />\n                                    <Button Style="{StaticResource MenuButtonStyle}" Click="Exit_Click">\n                                        <StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE8BB;" FontSize="16"/><TextBlock Text="Salir" /></StackPanel>\n                                    </Button>\n                                </StackPanel>\n                            </Border>\n                        </Flyout>\n                    </Button.Flyout>\n                </Button>\n            </TitleBar.RightHeader>\n        </TitleBar>\n\n'''
text = text[:start] + new_title + text[end:]
text = text.replace(end_marker, '        <Grid Grid.Row="1" Margin="8,0,8,6" MaxWidth="760" HorizontalAlignment="Stretch">')
text = text.replace('MaxWidth="680" HorizontalAlignment="Center"', 'MaxWidth="744" HorizontalAlignment="Stretch"', 1)
text = text.replace('MaxWidth="700" HorizontalAlignment="Center" VerticalAlignment="Center"', 'MaxWidth="744" HorizontalAlignment="Stretch" VerticalAlignment="Top" Margin="0,14,0,0"', 1)
text = text.replace('<Border Style="{StaticResource SurfaceCardStyle}" Padding="12,10">', '<Border Style="{StaticResource SurfaceCardStyle}" Padding="10,8">', 1)
text = text.replace('<Grid RowSpacing="11">', '<Grid RowSpacing="8">', 1)
text = text.replace('<ProgressBar x:Name="OverallProgressBar" Height="6" Minimum="0" Maximum="100" Value="0" />', '''<ProgressBar x:Name="OverallProgressBar" Height="10" MinHeight="10" Minimum="0" Maximum="100" Value="0">\n                                <ProgressBar.Resources>\n                                    <x:Double x:Key="ProgressBarTrackHeight">10</x:Double>\n                                </ProgressBar.Resources>\n                            </ProgressBar>''', 1)
text = text.replace('Margin="0,5,0,0" Text="Preparando..."', 'Margin="0,4,0,0" Text="Preparando..."', 1)
text = text.replace('<Grid Grid.Row="2" ColumnSpacing="20">', '<Grid Grid.Row="2" ColumnSpacing="16">', 1)
xaml.write_text(text, encoding='utf-8')

# 3) Use logical completion rate for the number the user sees; source rate stays diagnostics only.
code = root / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs'
text = code.read_text(encoding='utf-8')
text = text.replace('    private DateTimeOffset? _copyStartedAt;\n', '    private DateTimeOffset? _copyStartedAt;\n    private readonly LogicalProgressRate _copyProgressRate = new();\n')
text = text.replace('        AppWindow.Resize(new SizeInt32(800, 460));', '        AppWindow.Resize(new SizeInt32(760, 400));')
text = text.replace('        Activated += MainWindow_Activated;\n', '')
method_start = text.find('    private void MainWindow_Activated(')
if method_start >= 0:
    method_end = text.index('    private void ApplySavedTheme()', method_start)
    text = text[:method_start] + text[method_end:]
text = text.replace('            _copyStartedAt = DateTimeOffset.Now;\n', '            _copyStartedAt = DateTimeOffset.Now;\n            _copyProgressRate.Reset();\n')
text = text.replace('            var diagnostics = _job.DiagnosticsSnapshot();\n            var speed = paused ? 0d : diagnostics.SourceRead5sBytesPerSecond;\n            percent = total == 0 ? 0 : Math.Clamp(written * 100.0 / total, 0, 100);', '            var speed = paused ? 0d : _copyProgressRate.Observe(written);\n            percent = total == 0 ? 0 : Math.Clamp(written * 100.0 / total, 0, 100);', 1)
insert = '''\n    private sealed class LogicalProgressRate\n    {\n        private long _lastTimestamp;\n        private ulong _lastBytes;\n        private double _smoothedBytesPerSecond;\n\n        internal void Reset()\n        {\n            _lastTimestamp = 0;\n            _lastBytes = 0;\n            _smoothedBytesPerSecond = 0;\n        }\n\n        internal double Observe(ulong bytes)\n        {\n            var now = Stopwatch.GetTimestamp();\n            if (_lastTimestamp == 0 || bytes < _lastBytes)\n            {\n                _lastTimestamp = now;\n                _lastBytes = bytes;\n                _smoothedBytesPerSecond = 0;\n                return 0;\n            }\n\n            var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;\n            if (elapsed < 0.15)\n                return _smoothedBytesPerSecond;\n\n            var delta = bytes - _lastBytes;\n            var instant = delta / elapsed;\n            _lastTimestamp = now;\n            _lastBytes = bytes;\n\n            if (delta == 0)\n            {\n                _smoothedBytesPerSecond *= 0.82;\n                if (_smoothedBytesPerSecond < 1024) _smoothedBytesPerSecond = 0;\n                return _smoothedBytesPerSecond;\n            }\n\n            _smoothedBytesPerSecond = _smoothedBytesPerSecond <= 0\n                ? instant\n                : (_smoothedBytesPerSecond * 0.70) + (instant * 0.30);\n            return _smoothedBytesPerSecond;\n        }\n    }\n'''
marker = '\n    private static string FormatDestinationCount(int count) =>'
if marker not in text:
    raise SystemExit('MainWindow helper marker not found')
text = text.replace(marker, insert + marker, 1)
code.write_text(text, encoding='utf-8')

# 4) Bound adaptive queue-depth exploration. 8 MiB operations make runaway QD expensive.
profile = root / 'dotnet/RepartoCopier.Core/StorageIoProfile.cs'
text = profile.read_text(encoding='utf-8')
text = text.replace('    long DeviceBacklogTargetBytes)\n{', '    long DeviceBacklogTargetBytes,\n    int MaximumQueueDepth = 64)\n{')
text = text.replace('return new(StorageProfileKind.Network, 1, NetworkBacklog);', 'return new(StorageProfileKind.Network, 1, NetworkBacklog, 2);')
text = text.replace('return new(StorageProfileKind.Rotational, 1, RotationalBacklog);', 'return new(StorageProfileKind.Rotational, 1, RotationalBacklog, 2);')
text = text.replace('return new(StorageProfileKind.UsbFlash, 1, UsbFlashBacklog);', 'return new(StorageProfileKind.UsbFlash, 1, UsbFlashBacklog, 4);')
text = text.replace('                exactPhysicalIdentity ? UsbSsdBacklog : RotationalBacklog);', '                exactPhysicalIdentity ? UsbSsdBacklog : RotationalBacklog,\n                exactPhysicalIdentity ? 8 : 2);')
text = text.replace('return new(StorageProfileKind.SataSsd, 8, SataSsdBacklog);', 'return new(StorageProfileKind.SataSsd, 8, SataSsdBacklog, 8);')
text = text.replace('return new(StorageProfileKind.Nvme, 16, NvmeBacklog);', 'return new(StorageProfileKind.Nvme, 16, NvmeBacklog, 32);')
text = text.replace('return new(StorageProfileKind.StorageSpaces, 1, ConservativeBacklog);', 'return new(StorageProfileKind.StorageSpaces, 1, ConservativeBacklog, 8);')
text = text.replace('return new(StorageProfileKind.Virtual, 1, ConservativeBacklog);', 'return new(StorageProfileKind.Virtual, 1, ConservativeBacklog, 8);')
text = text.replace('return new(StorageProfileKind.Conservative, 1, ConservativeBacklog);', 'return new(StorageProfileKind.Conservative, 1, ConservativeBacklog, 4);')
profile.write_text(text, encoding='utf-8')

sched = root / 'dotnet/RepartoCopier.Core/DeviceScheduler.cs'
text = sched.read_text(encoding='utf-8')
text = text.replace('    private readonly int _initialQueueDepth;\n', '    private readonly int _initialQueueDepth;\n    private readonly int _maximumQueueDepth;\n')
text = text.replace('        long backlogTargetBytes,\n        DeviceIdentityConfidence identityConfidence = DeviceIdentityConfidence.Unknown)', '        long backlogTargetBytes,\n        DeviceIdentityConfidence identityConfidence = DeviceIdentityConfidence.Unknown,\n        int maximumQueueDepth = 64)')
text = text.replace('        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlogTargetBytes);\n\n        DeviceId', '        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlogTargetBytes);\n        if (maximumQueueDepth < initialQueueDepth) throw new ArgumentOutOfRangeException(nameof(maximumQueueDepth));\n\n        DeviceId')
text = text.replace('        _initialQueueDepth = initialQueueDepth;\n        _currentQueueDepth', '        _initialQueueDepth = initialQueueDepth;\n        _maximumQueueDepth = maximumQueueDepth;\n        _currentQueueDepth')
text = text.replace('    public int InitialQueueDepth => _initialQueueDepth;\n', '    public int InitialQueueDepth => _initialQueueDepth;\n    public int MaximumQueueDepth => _maximumQueueDepth;\n')
text = text.replace('    private static int NextExplorationDepth(int current) =>\n        current >= int.MaxValue / 2 ? int.MaxValue : Math.Max(current + 1, current * 2);', '    private int NextExplorationDepth(int current) =>\n        Math.Min(_maximumQueueDepth, current >= int.MaxValue / 2 ? int.MaxValue : Math.Max(current + 1, current * 2));')
text = text.replace('            var backlogTarget = profiles.Min(profile => profile.DeviceBacklogTargetBytes);\n', '            var backlogTarget = profiles.Min(profile => profile.DeviceBacklogTargetBytes);\n            var maximumDepth = profiles.Min(profile => profile.MaximumQueueDepth);\n')
text = text.replace('            if (sharesSource)\n                initialDepth = 1;\n\n            var scheduler = new DeviceScheduler(group.Key, initialDepth, backlogTarget, confidence);', '            if (sharesSource)\n            {\n                initialDepth = 1;\n                maximumDepth = 1;\n            }\n\n            var scheduler = new DeviceScheduler(group.Key, initialDepth, backlogTarget, confidence, maximumDepth);')
sched.write_text(text, encoding='utf-8')

# 5) Replace permanent architecture contracts so regressions are caught.
ui_test = root / 'dotnet/RepartoCopier.Core.Tests/WinUiCompactProgressContractTests.cs'
ui_test.write_text(r'''using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class WinUiCompactProgressContractTests
{
    [TestMethod]
    public void RunningViewUsesOneThickOverallProgressBarAndNoPerDiskRows()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(xaml.Contains("x:Name=\"OverallProgressBar\" Height=\"10\" MinHeight=\"10\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("x:Key=\"ProgressBarTrackHeight\">10", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("ProgressList", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_progressRows", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowUsesOfficialCompactDensityAndWindowsTitleBar()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "App.xaml"));
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(app.Contains("Microsoft.UI.Xaml/DensityStyles/Compact.xaml", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("<TitleBar x:Name=\"AppTitleBar\"", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("SetTitleBar(AppTitleBar)", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("SizeInt32(760, 400)", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("VerticalAlignment=\"Top\" Margin=\"0,14,0,0\"", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("MainWindow_Activated", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DestinationsStayHorizontalAndMicaLayeringRemains()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(xaml.Contains("ItemsStackPanel Orientation=\"Horizontal\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("HorizontalScrollMode=\"Enabled\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("LayerFillColorDefaultBrush", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("SystemBackdrop = new MicaBackdrop()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CopySpeedAndEtaUseTheSameLogicalProgressAsTheProgressBar()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(code.Contains("active.Min(item => item.Written)", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("_copyProgressRate.Observe(written)", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("diagnostics.SourceRead5sBytesPerSecond", StringComparison.Ordinal));
    }

    [TestMethod]
    public void VerifyKeepsLogicalStreamingSpeedAndEta()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        var telemetry = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyTelemetry.cs"));
        Assert.IsTrue(code.Contains("diagnostics.VerifyLogical5sBytesPerSecond", StringComparison.Ordinal));
        Assert.IsTrue(telemetry.Contains("VerifyLogical5sBytesPerSecond", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
''', encoding='utf-8')

qd_test = root / 'dotnet/RepartoCopier.Core.Tests/AdaptiveQueueDepthArchitectureTests.cs'
qd_test.write_text(r'''using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class AdaptiveQueueDepthArchitectureTests
{
    [TestMethod]
    public void AdaptiveQueueDepthHasADeviceProfileCeiling()
    {
        var schedulerProperties = typeof(DeviceScheduler)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(schedulerProperties, "CurrentQueueDepth");
        CollectionAssert.Contains(schedulerProperties, "ExplorationQueueDepth");
        CollectionAssert.Contains(schedulerProperties, "MaximumQueueDepth");

        var profileProperties = typeof(StorageIoProfile)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(profileProperties, "InitialQueueDepth");
        CollectionAssert.Contains(profileProperties, "MaximumQueueDepth");
    }

    [TestMethod]
    public void ExplorationNeverExceedsConfiguredCeiling()
    {
        using var sata = new DeviceScheduler("SATA-test", 8, 256L * 1024 * 1024, maximumQueueDepth: 8);
        using var usb = new DeviceScheduler("USB-test", 4, 256L * 1024 * 1024, maximumQueueDepth: 8);
        using var nvme = new DeviceScheduler("NVMe-test", 16, 512L * 1024 * 1024, maximumQueueDepth: 32);

        Assert.AreEqual(8, sata.ExplorationQueueDepth);
        Assert.AreEqual(8, usb.ExplorationQueueDepth);
        Assert.AreEqual(32, nvme.ExplorationQueueDepth);
    }

    [TestMethod]
    public void QueueDepthFeedbackStillTracksObservedConcurrencyWave()
    {
        var schedulerFields = typeof(DeviceScheduler)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.Contains(schedulerFields, "_samplePeakObservedConcurrency");
        CollectionAssert.Contains(schedulerFields, "_sampleCompletions");
        CollectionAssert.Contains(schedulerFields, "_sampleSawDemand");
        CollectionAssert.Contains(schedulerFields, "_maximumQueueDepth");
    }

    [TestMethod]
    public void ProductionProfilesBoundLargeEightMiBWriteWindows()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "StorageIoProfile.cs"));
        Assert.IsTrue(source.Contains("StorageProfileKind.SataSsd, 8, SataSsdBacklog, 8", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("StorageProfileKind.Nvme, 16, NvmeBacklog, 32", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("exactPhysicalIdentity ? 8 : 2", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
''', encoding='utf-8')

print('WinUI density + copy-rate semantics + bounded QD migration applied')
