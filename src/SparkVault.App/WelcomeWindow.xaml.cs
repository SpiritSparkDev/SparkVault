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
        var dashboard = new JobDashboardWindow();
        dashboard.Closed += (_, _) => ((App)System.Windows.Application.Current).ShowMainWindow();
        dashboard.Show();
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        ((App)System.Windows.Application.Current).ShowMainWindow();
        Close();
    }
}
