using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using SparkVault.Core;

namespace SparkVault.App;

public partial class JobDashboardWindow : Window
{
    private sealed record HistoryRow(DateTime StartedAt, DateTime? EndedAt, string Target, RunStatus Status, int FileCount, long TotalBytes, string? ErrorMessage);

    private sealed class TargetListItem
    {
        public required BackupTarget Target { get; init; }
        public string Description => Target.Describe();
    }

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

    private int? _jobId;
    private CancellationTokenSource? _runCts;
    private PauseToken? _pauseToken;
    private DateTime _speedSampleAt;
    private long _speedSampleBytes;
    private CancellationTokenSource? _restoreCts;
    private readonly ObservableCollection<TargetListItem> _settingsTargets = new();

    public JobDashboardWindow(int jobId)
    {
        InitializeComponent();
        _jobId = jobId;
        SettingsTargetsListBox.ItemsSource = _settingsTargets;
        LoadOverview();
        NavOverview.IsChecked = true;
    }

    // Draft mode: no job exists yet. Every other tab is locked until the required fields on
    // Einstellungen are filled in and "Job erstellen" persists the job for the first time.
    public JobDashboardWindow()
    {
        InitializeComponent();
        _jobId = null;
        SettingsTargetsListBox.ItemsSource = _settingsTargets;
        JobNameHeader.Text = "Neuer Job";
        NavOverview.IsEnabled = false;
        NavFiles.IsEnabled = false;
        NavHistory.IsEnabled = false;
        NavRestore.IsEnabled = false;
        NavSettings.IsChecked = true;
    }

    private BackupJob? CurrentJob => _jobId is { } id ? App.JobRepository.GetById(id) : null;

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
        _settingsTargets.Clear();
        var job = CurrentJob;

        if (job is not null)
        {
            SettingsHeaderText.Text = "Einstellungen";
            SettingsSaveButton.Content = "Speichern";
            SettingsNameBox.Text = job.Name;
            SettingsSourcePathBox.Text = job.SourcePath;
            SettingsExcludePatternsBox.Text = string.Join(Environment.NewLine, job.ExcludePatterns);
            foreach (var target in job.Targets)
                _settingsTargets.Add(new TargetListItem { Target = target });
            SettingsScheduleTypeCombo.SelectedIndex = job.ScheduleType switch
            {
                ScheduleType.Interval => 1,
                ScheduleType.DailyAt => 2,
                _ => 0,
            };
            SettingsIntervalHoursBox.Text = job.IntervalHours?.ToString() ?? "";
            SettingsDailyAtTimeBox.Text = job.DailyAtTime?.ToString("HH:mm") ?? "";
        }
        else
        {
            SettingsHeaderText.Text = "Neuer Job";
            SettingsSaveButton.Content = "Job erstellen";
            SettingsNameBox.Text = "";
            SettingsSourcePathBox.Text = "";
            SettingsExcludePatternsBox.Text = "";
            SettingsScheduleTypeCombo.SelectedIndex = 0;
            SettingsIntervalHoursBox.Text = "";
            SettingsDailyAtTimeBox.Text = "";
        }
    }

    private void SettingsBrowseSource_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SettingsSourcePathBox.Text = dialog.SelectedPath;
    }

    private void SettingsAddTarget_Click(object sender, RoutedEventArgs e)
    {
        var editor = new TargetEditorWindow(existing: null) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
            _settingsTargets.Add(new TargetListItem { Target = editor.Result });
    }

    private void SettingsEditTarget_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsTargetsListBox.SelectedItem is not TargetListItem selected) return;

        var editor = new TargetEditorWindow(existing: selected.Target) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
        {
            var index = _settingsTargets.IndexOf(selected);
            _settingsTargets[index] = new TargetListItem { Target = editor.Result };
        }
    }

    private void SettingsRemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsTargetsListBox.SelectedItem is TargetListItem selected)
            _settingsTargets.Remove(selected);
    }

    private void SettingsScheduleTypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var isInterval = SettingsScheduleTypeCombo.SelectedIndex == 1;
        var isDailyAt = SettingsScheduleTypeCombo.SelectedIndex == 2;
        SettingsIntervalLabel.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
        SettingsIntervalHoursBox.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
        SettingsDailyAtLabel.Visibility = isDailyAt ? Visibility.Visible : Visibility.Collapsed;
        SettingsDailyAtTimeBox.Visibility = isDailyAt ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SettingsNameBox.Text) || string.IsNullOrWhiteSpace(SettingsSourcePathBox.Text))
        {
            System.Windows.MessageBox.Show(this, "Name und Quellpfad sind Pflichtfelder.", "SparkVault",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (_settingsTargets.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "Bitte mindestens ein Ziel hinzufügen.", "SparkVault",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        // A local target inside the source makes every run re-scan its own output.
        var fullSource = Path.GetFullPath(SettingsSourcePathBox.Text.Trim());
        var sourcePrefix = fullSource.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var item in _settingsTargets)
        {
            if (item.Target.Type != TargetType.Local || item.Target.DestinationPath is null)
                continue;

            var fullDest = Path.GetFullPath(item.Target.DestinationPath);
            if (fullDest.Equals(fullSource, StringComparison.OrdinalIgnoreCase) ||
                fullDest.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.MessageBox.Show(this, "Ein lokales Ziel darf nicht innerhalb des Quellpfads liegen.", "SparkVault",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
        }

        var scheduleType = SettingsScheduleTypeCombo.SelectedIndex switch
        {
            1 => ScheduleType.Interval,
            2 => ScheduleType.DailyAt,
            _ => ScheduleType.None,
        };

        int? intervalHours = null;
        if (scheduleType == ScheduleType.Interval)
        {
            if (!int.TryParse(SettingsIntervalHoursBox.Text, out var hours) || hours <= 0)
            {
                System.Windows.MessageBox.Show(this, "Bitte eine gültige Stundenzahl angeben.", "SparkVault",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            intervalHours = hours;
        }

        TimeOnly? dailyAtTime = null;
        if (scheduleType == ScheduleType.DailyAt)
        {
            if (!TimeOnly.TryParse(SettingsDailyAtTimeBox.Text, out var time))
            {
                System.Windows.MessageBox.Show(this, "Bitte eine gültige Uhrzeit im Format HH:mm angeben.", "SparkVault",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            dailyAtTime = time;
        }

        var job = new BackupJob
        {
            Id = _jobId ?? 0,
            Name = SettingsNameBox.Text.Trim(),
            SourcePath = SettingsSourcePathBox.Text.Trim(),
            ExcludePatterns = SettingsExcludePatternsBox.Text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            ScheduleType = scheduleType,
            IntervalHours = intervalHours,
            DailyAtTime = dailyAtTime,
            Targets = _settingsTargets.Select(t => t.Target).ToList(),
        };

        var wasDraft = _jobId is null;
        if (wasDraft)
        {
            App.JobRepository.Add(job);
            _jobId = job.Id;

            NavOverview.IsEnabled = true;
            NavFiles.IsEnabled = true;
            NavHistory.IsEnabled = true;
            NavRestore.IsEnabled = true;
            LoadOverview();
            NavOverview.IsChecked = true;
        }
        else
        {
            App.JobRepository.Update(job);
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
