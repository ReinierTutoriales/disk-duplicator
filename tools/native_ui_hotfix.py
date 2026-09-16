from pathlib import Path
p = Path('dotnet/RepartoCopier.WinUI/MainWindow.xaml')
s = p.read_text(encoding='utf-8')
old = '<CheckBox x:Name="VerifyCheck" Content="Verificar" IsChecked="True"/>'
new = '<CheckBox x:Name="VerifyCheck" Content="Verificar después de copiar" IsChecked="True"/>'
if old not in s:
    raise RuntimeError('VerifyCheck compact label not found')
p.write_text(s.replace(old, new, 1), encoding='utf-8')
print('verification control contract preserved')
