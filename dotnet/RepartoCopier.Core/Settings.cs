namespace RepartoCopier.Core;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record AppSettings(ThemePreference Theme)
{
    public static AppSettings Default { get; } = new(ThemePreference.System);
}

public static class SettingsStore
{
    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath();
            return File.Exists(path)
                ? Parse(File.ReadAllText(path))
                : AppSettings.Default;
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    public static void Save(AppSettings settings)
    {
        var path = SettingsPath();
        var parent = Path.GetDirectoryName(path)
            ?? throw new IOException("Ruta de configuración inválida.");
        Directory.CreateDirectory(parent);
        AtomicStorage.Write(path, System.Text.Encoding.UTF8.GetBytes(Render(settings)), "la configuración");
    }

    public static string Render(AppSettings settings) =>
        $"theme={settings.Theme switch
        {
            ThemePreference.Light => "light",
            ThemePreference.Dark => "dark",
            _ => "system",
        }}\n";

    public static AppSettings Parse(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator < 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!key.Equals("theme", StringComparison.OrdinalIgnoreCase)) continue;
            return new AppSettings(value.ToLowerInvariant() switch
            {
                "light" => ThemePreference.Light,
                "dark" => ThemePreference.Dark,
                _ => ThemePreference.System,
            });
        }
        return AppSettings.Default;
    }

    private static string SettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            throw new IOException("Windows no proporcionó una carpeta APPDATA para guardar los ajustes.");
        return Path.Combine(appData, "RepartoCopier", "settings.conf");
    }
}
