using System.Windows;

namespace SparkVault.App;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();
    }

    private void CreateFirstJob_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        app.ShowMainWindow();
        (app.MainWindow as MainWindow)?.StartNewJobDraft();
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        ((App)System.Windows.Application.Current).ShowMainWindow();
        Close();
    }
}
