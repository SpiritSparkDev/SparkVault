using System.Windows;
using SparkVault.Core;

namespace SparkVault.App;

public partial class LogWindow : Window
{
    private sealed record RunRow(DateTime StartedAt, DateTime? EndedAt, string Target, RunStatus Status, int FileCount, long TotalBytes, string? ErrorMessage);

    public LogWindow(int jobId, string jobName)
    {
        InitializeComponent();
        Title = $"Log – {jobName}";

        var targetsById = (App.JobRepository.GetById(jobId)?.Targets ?? new List<BackupTarget>())
            .ToDictionary(t => t.Id, t => t.Describe());

        RunsGrid.ItemsSource = App.RunRepository.GetByJobId(jobId)
            .Select(r => new RunRow(
                r.StartedAt.ToLocalTime(),
                r.EndedAt?.ToLocalTime(),
                targetsById.TryGetValue(r.TargetId, out var desc) ? desc : $"Ziel #{r.TargetId}",
                r.Status, r.FileCount, r.TotalBytes, r.ErrorMessage))
            .ToList();
    }
}
