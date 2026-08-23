using System.Windows;
using SparkVault.Core;

namespace SparkVault.App;

public partial class JobDashboardWindow : Window
{
    private sealed record HistoryRow(DateTime StartedAt, DateTime? EndedAt, string Target, RunStatus Status, int FileCount, long TotalBytes, string? ErrorMessage);

    private readonly int _jobId;
    private CancellationTokenSource? _runCts;

    public JobDashboardWindow(int jobId)
    {
        InitializeComponent();
        _jobId = jobId;
        LoadOverview();
        NavOverview.IsChecked = true;
    }

    private BackupJob? CurrentJob => App.JobRepository.GetById(_jobId);

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        OverviewPanel.Visibility = NavOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryGrid.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavHistory.IsChecked == true) LoadHistory();
        if (NavSettings.IsChecked == true) LoadSettings();
    }

    private void LoadOverview()
    {
        var job = CurrentJob;
        if (job is null) { Close(); return; }

        JobNameHeader.Text = job.Name;

        var latestGroupId = App.RunRepository.GetLatestRunGroupId(job.Id);
        var groupRuns = latestGroupId is { } groupId ? App.RunRepository.GetByRunGroupId(groupId) : new List<BackupRun>();

        if (groupRuns.Count == 0)
        {
            StatusDot.Fill = (System.Windows.Media.Brush)FindResource("Accent300Brush");
            StatusTitleText.Text = "Noch kein Backup";
            StatusMetaText.Text = "Das erste Backup startet mit \"Jetzt sichern\".";
        }
        else
        {
            var lastRunStartedAt = groupRuns.Min(r => r.StartedAt);
            var allSuccess = groupRuns.All(r => r.Status == RunStatus.Success);
            var anyCancelled = groupRuns.Any(r => r.Status == RunStatus.Cancelled);

            (StatusDot.Fill, StatusTitleText.Text) = allSuccess
                ? ((System.Windows.Media.Brush)FindResource("AccentBrush"), "Letztes Backup erfolgreich")
                : anyCancelled
                    ? ((System.Windows.Media.Brush)FindResource("MutedTextBrush"), "Letztes Backup abgebrochen")
                    : ((System.Windows.Media.Brush)FindResource("Accent600Brush"), "Letztes Backup fehlgeschlagen");

            var totalBytes = groupRuns.Sum(r => r.TotalBytes);
            StatusMetaText.Text = $"{lastRunStartedAt.ToLocalTime():g} · {FormatBytes(totalBytes)} gesichert";
            LastSizeText.Text = FormatBytes(totalBytes);
        }

        NextRunText.Text = MainWindow.DescribeNextRun(job, groupRuns.Count > 0 ? groupRuns.Min(r => r.StartedAt) : null);
        TargetsText.Text = job.Targets.Count == 0 ? "Keine Ziele" : string.Join(", ", job.Targets.Select(t => t.Describe()));
        if (groupRuns.Count == 0) LastSizeText.Text = "-";
    }

    private static string FormatBytes(long bytes)
    {
        double b = bytes;
        string[] units = ["Bytes", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {units[i]}";
    }

    private void LoadHistory()
    {
        var job = CurrentJob;
        if (job is null) return;

        var targetsById = job.Targets.ToDictionary(t => t.Id, t => t.Describe());
        HistoryGrid.ItemsSource = App.RunRepository.GetByJobId(job.Id)
            .Select(r => new HistoryRow(
                r.StartedAt.ToLocalTime(),
                r.EndedAt?.ToLocalTime(),
                targetsById.TryGetValue(r.TargetId, out var desc) ? desc : $"Ziel #{r.TargetId}",
                r.Status, r.FileCount, r.TotalBytes, r.ErrorMessage))
            .ToList();
    }

    private void LoadSettings()
    {
        var job = CurrentJob;
        if (job is null) return;

        SettingsNameText.Text = job.Name;
        SettingsSourceText.Text = job.SourcePath;
        SettingsTargetsText.Text = job.Targets.Count == 0 ? "Keine Ziele" : string.Join(", ", job.Targets.Select(t => t.Describe()));
        SettingsScheduleText.Text = job.ScheduleType switch
        {
            ScheduleType.Interval => $"Alle {job.IntervalHours} Std.",
            ScheduleType.DailyAt => $"Täglich um {job.DailyAtTime:HH\\:mm}",
            _ => "Manuell",
        };
    }

    private void EditSettings_Click(object sender, RoutedEventArgs e)
    {
        var editor = new JobEditorWindow(jobId: _jobId) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            LoadOverview();
            LoadSettings();
        }
    }

    private async void RunNowButton_Click(object sender, RoutedEventArgs e)
    {
        var job = CurrentJob;
        if (job is null) return;

        RunNowButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Visible;
        RunProgressBar.Value = 0;
        CurrentFileText.Text = "";
        _runCts = new CancellationTokenSource();
        var progress = new Progress<TransferProgress>(p =>
        {
            RunProgressBar.Value = p.FilesTotal == 0 ? 0 : (double)p.FilesDone / p.FilesTotal * 100;
            CurrentFileText.Text = p.CurrentFile;
        });

        try
        {
            await App.Runner.RunAsync(job, progress, _runCts.Token);
        }
        finally
        {
            RunNowButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Collapsed;
            _runCts.Dispose();
            _runCts = null;
            LoadOverview();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _runCts?.Cancel();
    }
}
