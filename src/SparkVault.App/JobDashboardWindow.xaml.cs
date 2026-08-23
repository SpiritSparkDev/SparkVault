using System.Windows;
using SparkVault.Core;

namespace SparkVault.App;

public partial class JobDashboardWindow : Window
{
    private sealed record HistoryRow(DateTime StartedAt, DateTime? EndedAt, string Target, RunStatus Status, int FileCount, long TotalBytes, string? ErrorMessage);

    private readonly int _jobId;
    private CancellationTokenSource? _runCts;
    private PauseToken? _pauseToken;
    private DateTime _speedSampleAt;
    private long _speedSampleBytes;

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

        IdleView.Visibility = Visibility.Collapsed;
        RunningView.Visibility = Visibility.Visible;
        RunProgressBar.Value = 0;
        PercentText.Text = "0";
        CurrentFileText.Text = "";
        SpeedEtaText.Text = "";
        RunFilesText.Text = "0 / 0";
        RunBytesText.Text = "0 Bytes";
        RunTargetText.Text = "";
        PauseButton.Content = "Pausieren";

        _runCts = new CancellationTokenSource();
        _pauseToken = new PauseToken();
        _speedSampleAt = DateTime.UtcNow;
        _speedSampleBytes = 0;

        var progress = new Progress<TransferProgress>(p =>
        {
            RunProgressBar.Value = p.BytesTotal == 0 ? 0 : (double)p.BytesDone / p.BytesTotal * 100;
            PercentText.Text = ((int)RunProgressBar.Value).ToString();
            CurrentFileText.Text = p.CurrentFile;
            RunFilesText.Text = $"{p.FilesDone} / {p.FilesTotal}";
            RunBytesText.Text = FormatBytes(p.BytesDone);
            RunTargetText.Text = p.CurrentTarget;
            SpeedEtaText.Text = ComputeSpeedEta(p.BytesDone, p.BytesTotal);
        });

        try
        {
            await App.Runner.RunAsync(job, progress, _runCts.Token, _pauseToken);
        }
        finally
        {
            IdleView.Visibility = Visibility.Visible;
            RunningView.Visibility = Visibility.Collapsed;
            _runCts.Dispose();
            _runCts = null;
            _pauseToken = null;
            LoadOverview();
        }
    }

    private string ComputeSpeedEta(long bytesDone, long bytesTotal)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _speedSampleAt).TotalSeconds;
        if (elapsed < 0.5) return SpeedEtaText.Text; // too soon for a stable sample, keep the last value

        var bytesPerSecond = (bytesDone - _speedSampleBytes) / elapsed;
        _speedSampleAt = now;
        _speedSampleBytes = bytesDone;

        if (bytesPerSecond <= 0) return "";

        var remaining = bytesTotal - bytesDone;
        var etaSeconds = remaining / bytesPerSecond;
        var eta = etaSeconds < 60 ? "< 1 Min." : $"noch ca. {(int)Math.Ceiling(etaSeconds / 60)} Min.";
        return $"{eta} · {FormatBytes((long)bytesPerSecond)}/s";
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pauseToken is null) return;

        if (_pauseToken.IsPaused)
        {
            _pauseToken.Resume();
            PauseButton.Content = "Pausieren";
        }
        else
        {
            _pauseToken.Pause();
            PauseButton.Content = "Fortsetzen";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // PauseToken.WaitIfPausedAsync uses Task.WaitAsync(ct), so cancelling here unblocks a
        // paused run too — no need to separately resume it first.
        _runCts?.Cancel();
    }
}
