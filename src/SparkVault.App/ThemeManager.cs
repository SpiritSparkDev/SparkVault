using System.IO;
using System.Windows;

namespace SparkVault.App;

public enum ThemeMode { Light, Dark, System }

// Swaps which Palette.*.xaml dictionary is merged into Application.Resources. Every color Setter
// in Theme.xaml reads its brush via DynamicResource, so this repaints already-open windows
// immediately — no window reconstruction needed. C# code that snapshots a brush via
// FindResource(...) (JobRow.StatusBrush, ProgressRing.RingBrush) does NOT get that for free and
// must be re-run after a switch; MainWindow.RefreshTheme() does that.
public static class ThemeManager
{
    private const string DarkPaletteFile = "Palette.Dark.xaml";
    private const string LightPaletteFile = "Palette.Light.xaml";

    private static string? _settingsPath;

    public static ThemeMode CurrentMode { get; private set; } = ThemeMode.System;

    public static void Initialize(string appDataDir)
    {
        _settingsPath = Path.Combine(appDataDir, "theme.txt");
        if (File.Exists(_settingsPath) && Enum.TryParse<ThemeMode>(File.ReadAllText(_settingsPath).Trim(), out var saved))
            CurrentMode = saved;
    }

    public static void Apply(ThemeMode mode)
    {
        CurrentMode = mode;
        if (_settingsPath is not null)
        {
            try { File.WriteAllText(_settingsPath, mode.ToString()); }
            catch { /* non-essential: worst case the choice doesn't survive a restart */ }
        }

        var wantsDark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemDarkMode(),
        };
        var paletteFile = wantsDark ? DarkPaletteFile : LightPaletteFile;

        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d =>
            d.Source is not null && (d.Source.OriginalString.EndsWith(DarkPaletteFile) || d.Source.OriginalString.EndsWith(LightPaletteFile)));
        if (existing is not null)
            merged.Remove(existing);
        merged.Insert(0, new ResourceDictionary { Source = new Uri(paletteFile, UriKind.Relative) });
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int lightMode && lightMode == 0;
        }
        catch
        {
            return true; // registry unreadable: fall back to the app's original dark identity
        }
    }
}
