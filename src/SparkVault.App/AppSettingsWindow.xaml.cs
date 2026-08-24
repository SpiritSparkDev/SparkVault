using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SparkVault.App;

public partial class AppSettingsWindow : Window
{
    private bool _initializing = true;

    public AppSettingsWindow()
    {
        InitializeComponent();

        switch (ThemeManager.CurrentMode)
        {
            case ThemeMode.Light: LightOption.IsChecked = true; break;
            case ThemeMode.Dark: DarkOption.IsChecked = true; break;
            default: SystemOption.IsChecked = true; break;
        }
        _initializing = false;
    }

    private void ThemeOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        var mode = sender == LightOption ? ThemeMode.Light
            : sender == DarkOption ? ThemeMode.Dark
            : ThemeMode.System;

        ThemeManager.Apply(mode);
        (Owner as MainWindow)?.RefreshTheme();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SparkVault");
        Process.Start(new ProcessStartInfo(appDataDir) { UseShellExecute = true });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
