using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using SparkVault.Core;
using MessageBox = System.Windows.MessageBox;

namespace SparkVault.App;

public sealed class JobRow
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string TargetsDisplay { get; init; } = "";
    public string LastRunDisplay { get; init; } = "-";
    public string NextRunDisplay { get; init; } = "-";
    public string LastStatusDisplay { get; init; } = "-";
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<JobRow> _jobs = new();

    public MainWindow()
    {
        InitializeComponent();
        JobsGrid.ItemsSource = _jobs;
        ReloadJobs();
    }

    private void ReloadJobs()
    {
        _jobs.Clear();
        foreach (var job in App.JobRepository.GetAll())
        {
            var latestGroupId = App.RunRepository.GetLatestRunGroupId(job.Id);
            var groupRuns = latestGroupId is { } groupId ? App.RunRepository.GetByRunGroupId(groupId) : new List<BackupRun>();
            DateTime? lastRunStartedAt = groupRuns.Count > 0 ? groupRuns.Min(r => r.StartedAt) : null;
            RunStatus? lastStatus = groupRuns.Count == 0
                ? null
                : groupRuns.All(r => r.Status == RunStatus.Success) ? RunStatus.Success : RunStatus.Failed;

            _jobs.Add(new JobRow
            {
                Id = job.Id,
                Name = job.Name,
                SourcePath = job.SourcePath,
                TargetsDisplay = string.Join("; ", job.Targets.Select(t => t.Describe())),
                LastRunDisplay = lastRunStartedAt?.ToLocalTime().ToString("g") ?? "-",
                NextRunDisplay = DescribeNextRun(job, lastRunStartedAt),
                LastStatusDisplay = lastStatus?.ToString() ?? "-",
            });
        }
    }

    private static string DescribeNextRun(BackupJob job, DateTime? lastRunStartedAt)
    {
        switch (job.ScheduleType)
        {
            case ScheduleType.Interval when job.IntervalHours is { } hours:
                if (lastRunStartedAt is null) return "fällig";
                return lastRunStartedAt.Value.ToLocalTime().AddHours(hours).ToString("g");

            case ScheduleType.DailyAt when job.DailyAtTime is { } time:
                var todayTarget = DateTime.Today + time.ToTimeSpan();
                var next = DateTime.Now < todayTarget ? todayTarget : todayTarget.AddDays(1);
                return next.ToString("g");

            default:
                return "-";
        }
    }

    private JobRow? SelectedJob => JobsGrid.SelectedItem as JobRow;

    private void AddJobButton_Click(object sender, RoutedEventArgs e)
    {
        var editor = new JobEditorWindow(jobId: null) { Owner = this };
        if (editor.ShowDialog() == true)
            ReloadJobs();
    }

    private void EditJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is null) return;
        var editor = new JobEditorWindow(jobId: SelectedJob.Id) { Owner = this };
        if (editor.ShowDialog() == true)
            ReloadJobs();
    }

    private void DeleteJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is null) return;
        var result = MessageBox.Show(this, $"Job \"{SelectedJob.Name}\" wirklich löschen?", "SparkVault",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            App.JobRepository.Delete(SelectedJob.Id);
            ReloadJobs();
        }
    }

    private async void RunNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is null) return;
        var job = App.JobRepository.GetById(SelectedJob.Id);
        if (job is null) return;

        RunNowButton.IsEnabled = false;
        RunProgressBar.Value = 0;
        var progress = new Progress<TransferProgress>(p =>
            RunProgressBar.Value = p.FilesTotal == 0 ? 0 : (double)p.FilesDone / p.FilesTotal * 100);

        try
        {
            await App.Runner.RunAsync(job, progress, CancellationToken.None);
        }
        finally
        {
            RunNowButton.IsEnabled = true;
            RunProgressBar.Value = 0;
            ReloadJobs();
        }
    }

    private void ShowLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is null) return;
        var logWindow = new LogWindow(SelectedJob.Id, SelectedJob.Name) { Owner = this };
        logWindow.Show();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
