using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RepartoCopier.WinUI;

internal static class NativeWindowMaterial
{
    // Roots start with a theme-aware solid fallback in XAML. Only expose the compositor once attached.
    // Windows controls transparency, high contrast, inactive-window and battery/graphics fallbacks.
    internal static void Apply(Window window, Panel root)
    {
        try
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            root.Background = new SolidColorBrush(Colors.Transparent);
        }
        catch
        {
            // Keep the XAML theme fallback when a backdrop cannot be attached.
        }
    }
}
