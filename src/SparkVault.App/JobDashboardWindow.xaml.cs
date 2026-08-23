using System.IO;
using System.Windows;
using SparkVault.Core;

namespace SparkVault.App;

public partial class JobDashboardWindow : Window
{
    private sealed record HistoryRow(DateTime StartedAt, DateTime? EndedAt, string Target, RunStatus Status, int FileCount, long TotalBytes, string? ErrorMessage);

    private sealed class FolderRow
    {
        public required string Name { get; init; }
        public required string FullPath { get; init; }
        public required long Size { get; init; }
        public required bool IsIncluded { get; set; }
        public string SizeDisplay => FormatBytes(Size);
    }

    private sealed class VersionRow
    {
        public required int RunId { get; init; }
        public required string Label { get; init; }
        public required int FileCount { get; init; }
        public required long TotalBytes { get; init; }
    }

    private readonly int _jobId;
    private CancellationTokenSource? _runCts;
    private PauseToken? _pauseToken;
    private DateTime _speedSampleAt;
    private long _speedSampleBytes;
    private CancellationTokenSource? _restoreCts;

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
        FilesPanel.Visibility = NavFiles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryGrid.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RestorePanel.Visibility = NavRestore.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavFiles.IsChecked == true) LoadFiles();
        if (NavHistory.IsChecked == true) LoadHistory();
        if (NavRestore.IsChecked == true) LoadRestore();
        if (NavSettings.IsChecked == true) LoadSettings();
    }

    // A folder is "included" unless something already excludes everything under it — covers
    // both a toggle from this tab ("{Name}/*") and a matching pattern the user typed by hand
    // in the Ausschlussmuster box (e.g. "Downloads/*" or "Downloads/**").
    private static string ExcludeAllPattern(string folderName) => $"{folderName}/*";

    private void LoadFiles()
    {
        var job = CurrentJob;
        if (job is null) return;

        if (!Directory.Exists(job.SourcePath))
        {
            FoldersListBox.ItemsSource = null;
            FilesSelectedSizeText.Text = "Quellpfad nicht gefunden.";
            return;
        }

        var rows = new List<FolderRow>();
        foreach (var dir in Directory.EnumerateDirectories(job.SourcePath))
        {
            var name = Path.GetFileName(dir);
            long size;
            try
            {
                size = FileScanner.Scan(dir, Enumerable.Empty<string>()).Sum(f => f.Size);
            }
            catch (UnauthorizedAccessException)
            {
                size = 0;
            }

            rows.Add(new FolderRow
            {
                Name = name,
                FullPath = dir,
                Size = size,
                IsIncluded = !job.ExcludePatterns.Contains(ExcludeAllPattern(name), StringComparer.OrdinalIgnoreCase),
            });
        }

        FoldersListBox.ItemsSource = rows;
        FilesSelectedSizeText.Text = $"{FormatBytes(rows.Where(r => r.IsIncluded).Sum(r => r.Size))} ausgewählt";
    }

    private void FolderCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { DataContext: FolderRow row }) return;
        var job = CurrentJob;
        if (job is null) return;

        var pattern = ExcludeAllPattern(row.Name);
        if (row.IsIncluded)
            job.ExcludePatterns.RemoveAll(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase));
        else if (!job.ExcludePatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            job.ExcludePatterns.Add(pattern);

        App.JobRepository.Update(job);
        FilesSelectedSizeText.Text = $"{FormatBytes(((List<FolderRow>)FoldersListBox.ItemsSource).Where(r => r.IsIncluded).Sum(r => r.Size))} ausgewählt";
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

    private void LoadRestore()
    {
        var job = CurrentJob;
        if (job is null) return;

        RestoreTargetCombo.Items.Clear();
        foreach (var t in job.Targets)
            RestoreTargetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = t.Describe(), Tag = t });
        RestoreTargetCombo.Visibility = job.Targets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (RestoreTargetCombo.Items.Count > 0)
            RestoreTargetCombo.SelectedIndex = 0;

        LoadRestoreVersionsForSelectedTarget();
    }

    private BackupTarget? SelectedRestoreTarget()
    {
        var job = CurrentJob;
        if (job is null || job.Targets.Count == 0) return null;
        if (RestoreTargetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: BackupTarget t }) return t;
        return job.Targets[0];
    }

    private void LoadRestoreVersionsForSelectedTarget()
    {
        var job = CurrentJob;
        var selectedTarget = SelectedRestoreTarget();
        if (job is null || selectedTarget is null)
        {
            RestoreEmptyState.Visibility = Visibility.Visible;
            RestoreContent.Visibility = Visibility.Collapsed;
            return;
        }

        var versions = App.RunRepository.GetByJobId(job.Id)
            .Where(r => r.TargetId == selectedTarget.Id && r.Status == RunStatus.Success)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new VersionRow
            {
                RunId = r.Id,
                Label = r.StartedAt.ToLocalTime().ToString("g"),
                FileCount = r.FileCount,
                TotalBytes = r.TotalBytes,
            })
            .ToList();

        if (versions.Count == 0)
        {
            RestoreEmptyState.Visibility = Visibility.Visible;
            RestoreContent.Visibility = Visibility.Collapsed;
            return;
        }

        RestoreEmptyState.Visibility = Visibility.Collapsed;
        RestoreContent.Visibility = Visibility.Visible;
        RestoreVersionsListBox.ItemsSource = versions;
        RestoreVersionsListBox.SelectedIndex = 0;
    }

    private void RestoreTargetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadRestoreVersionsForSelectedTarget();
    }

    private void RestoreVersionsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RestoreVersionsListBox.SelectedItem is not VersionRow row) return;
        RestoreSelectedLabelText.Text = row.Label;
        RestoreSelectedMetaText.Text = $"{FormatBytes(row.TotalBytes)} · {row.FileCount} Dateien";
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var job = CurrentJob;
        var target = SelectedRestoreTarget();
        if (job is null || target is null) return;
        if (RestoreVersionsListBox.SelectedItem is not VersionRow row) return;

        var result = System.Windows.MessageBox.Show(this,
            $"Dateien vom Stand \"{row.Label}\" werden nach \"{job.SourcePath}\" zurückgespielt und überschreiben dortige Dateien. Fortfahren?",
            "SparkVault", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        RestoreContent.Visibility = Visibility.Collapsed;
        RestoreRunningView.Visibility = Visibility.Visible;
        RestorePercentText.Text = "0";
        RestoreProgressBar.Value = 0;
        RestoreCurrentFileText.Text = "";

        _restoreCts = new CancellationTokenSource();
        var progress = new Progress<TransferProgress>(p =>
        {
            RestoreProgressBar.Value = p.BytesTotal == 0 ? 0 : (double)p.BytesDone / p.BytesTotal * 100;
            RestorePercentText.Text = ((int)RestoreProgressBar.Value).ToString();
            RestoreCurrentFileText.Text = p.CurrentFile;
        });

        var runFileRepo = new RunFileRepository(App.ConnectionString);
        var restoreRunner = new RestoreRunner(runFileRepo, Serilog.Log.Logger);

        try
        {
            await restoreRunner.RestoreAsync(job, target, row.RunId, progress, _restoreCts.Token);
            System.Windows.MessageBox.Show(this, "Wiederherstellung abgeschlossen.", "SparkVault",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            // user-initiated cancel, no error dialog
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Wiederherstellung fehlgeschlagen: {ex.Message}", "SparkVault",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            RestoreRunningView.Visibility = Visibility.Collapsed;
            RestoreContent.Visibility = Visibility.Visible;
            _restoreCts.Dispose();
            _restoreCts = null;
        }
    }

    private void RestoreCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _restoreCts?.Cancel();
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
