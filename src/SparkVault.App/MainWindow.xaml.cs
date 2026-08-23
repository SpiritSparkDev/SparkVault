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
    public System.Windows.Media.Brush StatusBrush { get; init; } = System.Windows.Media.Brushes.Gray;
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<JobRow> _jobs = new();

    public MainWindow()
    {
        InitializeComponent();
        JobsList.ItemsSource = _jobs;
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
                StatusBrush = lastStatus switch
                {
                    RunStatus.Success => (System.Windows.Media.Brush)FindResource("AccentBrush"),
                    RunStatus.Failed => (System.Windows.Media.Brush)FindResource("Accent600Brush"),
                    _ => (System.Windows.Media.Brush)FindResource("Accent300Brush"),
                },
            });
        }
    }

    internal static string DescribeNextRun(BackupJob job, DateTime? lastRunStartedAt)
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

    private JobRow? SelectedJob => JobsList.SelectedItem as JobRow;

    private void AddJobButton_Click(object sender, RoutedEventArgs e)
    {
        var dashboard = new JobDashboardWindow();
        dashboard.Closed += (_, _) => ReloadJobs();
        dashboard.Show();
    }

    private void OpenJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is null) return;
        var dashboard = new JobDashboardWindow(SelectedJob.Id);
        dashboard.Closed += (_, _) => ReloadJobs();
        dashboard.Show();
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

    private void JobsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        OpenJobButton_Click(sender, e);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
