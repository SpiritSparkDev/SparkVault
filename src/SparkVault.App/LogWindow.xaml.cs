using System.Windows;

namespace SparkVault.App;

public partial class LogWindow : Window
{
    public LogWindow(int jobId, string jobName)
    {
        InitializeComponent();
        Title = $"Log – {jobName}";
        RunsGrid.ItemsSource = App.RunRepository.GetByJobId(jobId);
    }
}
