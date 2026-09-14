from pathlib import Path


def replace_exact(text: str, old: str, new: str, expected: int, label: str) -> str:
    count = text.count(old)
    if count != expected:
        raise RuntimeError(f"{label}: expected {expected} matches, found {count}")
    return text.replace(old, new)

# Keep CopyEngine's API default unchanged. The WinUI production workflow already
# forces Verify=true without exposing a selector, while tests and API callers can
# still opt out explicitly when they need copy-only behavior.
policy_path = Path("dotnet/RepartoCopier.Core/FanoutPerformancePolicy.cs")
policy = policy_path.read_text(encoding="utf-8")
replacements = {
    "Targets are intentionally expressed as multiples of the 16 MiB large-file": "Targets are intentionally expressed as multiples of the 32 MiB large-file",
    "64L * MiB;  // 4 x 16 MiB": "64L * MiB;  // 2 x 32 MiB",
    "128L * MiB;   // 8 x 16 MiB": "128L * MiB;   // 4 x 32 MiB",
    "256L * MiB;       // 16 x 16 MiB": "256L * MiB;       // 8 x 32 MiB",
    "512L * MiB;         // 32 x 16 MiB": "512L * MiB;         // 16 x 32 MiB",
}
for old, new in replacements.items():
    if old not in policy:
        raise RuntimeError(f"storage policy baseline missing: {old}")
    policy = policy.replace(old, new)
policy_path.write_text(policy, encoding="utf-8", newline="\n")

xaml_path = Path("dotnet/RepartoCopier.WinUI/MainWindow.xaml")
xaml = xaml_path.read_text(encoding="utf-8")
old_logo = '''            <Image x:Name="LogoImage"
                   Width="54"
                   Height="54"
                   Source="ms-appx:///Assets/AppLogo.png"
                   Stretch="Uniform"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center" />'''
new_logo = '''            <Image x:Name="LogoImage"
                   Width="48"
                   Height="48"
                   Margin="3"
                   Source="ms-appx:///Assets/AppLogo.png"
                   Stretch="Uniform"
                   UseLayoutRounding="True"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center" />'''
xaml = replace_exact(xaml, old_logo, new_logo, 1, "titlebar logo layout")
xaml_path.write_text(xaml, encoding="utf-8", newline="\n")

print("runtime unification patch applied")
