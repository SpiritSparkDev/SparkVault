using System.Windows;
using SparkVault.Core;

namespace SparkVault.App;

public partial class LogWindow : Window
{
    private sealed record RunRow(DateTime StartedAt, DateTime? EndedAt, RunStatus Status, int FileCount, long TotalBytes, string? ErrorMessage);

    public LogWindow(int jobId, string jobName)
    {
        InitializeComponent();
        Title = $"Log – {jobName}";
        RunsGrid.ItemsSource = App.RunRepository.GetByJobId(jobId)
            .Select(r => new RunRow(r.StartedAt.ToLocalTime(), r.EndedAt?.ToLocalTime(), r.Status, r.FileCount, r.TotalBytes, r.ErrorMessage))
            .ToList();
    }
}
