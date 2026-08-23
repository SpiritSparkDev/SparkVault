using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using SparkVault.Core;
using MessageBox = System.Windows.MessageBox;

namespace SparkVault.App;

public sealed class JobRow
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string LastRunDisplay { get; init; } = "-";
    public System.Windows.Media.Brush StatusBrush { get; init; } = System.Windows.Media.Brushes.Gray;
}

public partial class MainWindow : Window
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

    private readonly ObservableCollection<JobRow> _jobs = new();
    private readonly ObservableCollection<TargetListItem> _settingsTargets = new();
    private int? _jobId;
    private bool _suppressJobsListSelection;
    private CancellationTokenSource? _runCts;
    private PauseToken? _pauseToken;
    private DateTime _speedSampleAt;
    private long _speedSampleBytes;
    private CancellationTokenSource? _restoreCts;

    public MainWindow()
    {
        InitializeComponent();
        JobsList.ItemsSource = _jobs;
        SettingsTargetsListBox.ItemsSource = _settingsTargets;
        ReloadJobs();

        if (_jobs.Count > 0)
            SelectJob(_jobs[0].Id);
        else
            ShowEmptyState();
    }

    private void ReloadJobs()
    {
        var previousId = _jobId;
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
                LastRunDisplay = lastRunStartedAt?.ToLocalTime().ToString("g") ?? "-",
                StatusBrush = lastStatus switch
                {
                    RunStatus.Success => (System.Windows.Media.Brush)FindResource("AccentBrush"),
                    RunStatus.Failed => (System.Windows.Media.Brush)FindResource("Accent600Brush"),
                    _ => (System.Windows.Media.Brush)FindResource("Accent300Brush"),
                },
            });
        }

        _suppressJobsListSelection = true;
        JobsList.SelectedItem = _jobs.FirstOrDefault(j => j.Id == previousId);
        _suppressJobsListSelection = false;
    }

    internal static string DescribeNextRun(BackupJob job, DateTime? lastRunStartedAt)
    {
        switch (job.ScheduleType)
        {
            case ScheduleType.Interval when job.IntervalHours is { } hours:
                if (lastRunStartedAt is null) return "fällig";
                return lastRunStartedAt.Value.ToLocalTime().AddHours(hours).ToString("g");

            case ScheduleType.DailyAt when job.DailyAtTime is { } time:
            {
                var todayTarget = DateTime.Today + time.ToTimeSpan();
                var next = DateTime.Now < todayTarget ? todayTarget : todayTarget.AddDays(1);
                return next.ToString("g");
            }

            case ScheduleType.Weekdays when job.DailyAtTime is { } time:
            {
                var candidate = DateTime.Today + time.ToTimeSpan();
                if (DateTime.Now >= candidate) candidate = candidate.AddDays(1);
                while (candidate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    candidate = candidate.AddDays(1);
                return candidate.ToString("g");
            }

            case ScheduleType.Weekly when job.DailyAtTime is { } time && job.WeeklyDay is { } weeklyDay:
            {
                var candidate = DateTime.Today + time.ToTimeSpan();
                while (candidate.DayOfWeek != weeklyDay || DateTime.Now >= candidate)
                    candidate = candidate.AddDays(1);
                return candidate.ToString("g");
            }

            case ScheduleType.Monthly when job.DailyAtTime is { } time && job.MonthlyDay is { } monthlyDay:
                return NextMonthlyOccurrence(DateTime.Now, time, monthlyDay).ToString("g");

            case ScheduleType.OnChange:
                return "Beim nächsten Programmstart, falls Änderungen";

            default:
                return "-";
        }
    }

    private static DateTime NextMonthlyOccurrence(DateTime now, TimeOnly time, int monthlyDay)
    {
        var year = now.Year;
        var month = now.Month;
        for (var i = 0; i < 13; i++)
        {
            var day = Math.Min(monthlyDay, DateTime.DaysInMonth(year, month));
            var candidate = new DateTime(year, month, day) + time.ToTimeSpan();
            if (candidate > now) return candidate;
            month++;
            if (month > 12) { month = 1; year++; }
        }
        return now; // unreachable in practice — 13 months always finds a match
    }

    private BackupJob? CurrentJob => _jobId is { } id ? App.JobRepository.GetById(id) : null;

    private void JobsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressJobsListSelection) return;
        if (JobsList.SelectedItem is not JobRow row) return;
        SelectJob(row.Id);
    }

    // Switches to an existing job, keeping whichever tab is currently active (so browsing
    // Verlauf for one job and clicking another keeps showing Verlauf, for that job).
    private void SelectJob(int jobId)
    {
        _jobId = jobId;
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        SidebarNavPanel.Visibility = Visibility.Visible;
        NavOverview.IsEnabled = true;
        NavFiles.IsEnabled = true;
        NavHistory.IsEnabled = true;
        NavRestore.IsEnabled = true;
        NavSettings.IsEnabled = true;
        DeleteJobButton.IsEnabled = true;

        _suppressJobsListSelection = true;
        JobsList.SelectedItem = _jobs.FirstOrDefault(j => j.Id == jobId);
        _suppressJobsListSelection = false;

        var anyChecked = NavOverview.IsChecked == true || NavFiles.IsChecked == true || NavHistory.IsChecked == true ||
                          NavRestore.IsChecked == true || NavSettings.IsChecked == true;
        if (!anyChecked)
            NavOverview.IsChecked = true;
        else
            RefreshCurrentTab();
    }

    private void ShowEmptyState()
    {
        _jobId = null;
        OverviewPanel.Visibility = Visibility.Collapsed;
        FilesPanel.Visibility = Visibility.Collapsed;
        HistoryGrid.Visibility = Visibility.Collapsed;
        RestorePanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility = Visibility.Visible;
        SidebarNavPanel.Visibility = Visibility.Collapsed;
    }

    // Draft mode: no job exists yet. Every tab but Einstellungen is locked until the required
    // fields are filled in and "Job erstellen" persists the job for the first time.
    public void StartNewJobDraft()
    {
        _jobId = null;
        _suppressJobsListSelection = true;
        JobsList.SelectedItem = null;
        _suppressJobsListSelection = false;

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        SidebarNavPanel.Visibility = Visibility.Visible;
        NavOverview.IsEnabled = false;
        NavFiles.IsEnabled = false;
        NavHistory.IsEnabled = false;
        NavRestore.IsEnabled = false;
        NavSettings.IsEnabled = true;
        DeleteJobButton.IsEnabled = false;
        NavSettings.IsChecked = true;
        LoadSettings();
    }

    private void AddJobButton_Click(object sender, RoutedEventArgs e) => StartNewJobDraft();

    private void AppSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        new AppSettingsWindow { Owner = this }.ShowDialog();
    }

    // Re-snapshots brushes that were captured via FindResource(...) rather than a live
    // DynamicResource binding (JobRow.StatusBrush, ProgressRing.RingBrush), so a Farbschema
    // switch repaints them too instead of only the XAML-declared colors.
    public void RefreshTheme()
    {
        ReloadJobs();
        RefreshCurrentTab();
    }

    private void DeleteJobButton_Click(object sender, RoutedEventArgs e)
    {
        var job = CurrentJob;
        if (job is null) return;
        var result = MessageBox.Show(this, $"Job \"{job.Name}\" wirklich löschen?", "SparkVault",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        App.JobRepository.Delete(job.Id);
        ReloadJobs();
        if (_jobs.Count > 0) SelectJob(_jobs[0].Id); else ShowEmptyState();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        OverviewPanel.Visibility = NavOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = NavFiles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryGrid.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RestorePanel.Visibility = NavRestore.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshCurrentTab();
    }

    private void RefreshCurrentTab()
    {
        if (NavOverview.IsChecked == true) LoadOverview();
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
        if (job is null) return;

        var latestGroupId = App.RunRepository.GetLatestRunGroupId(job.Id);
        var groupRuns = latestGroupId is { } groupId ? App.RunRepository.GetByRunGroupId(groupId) : new List<BackupRun>();

        if (groupRuns.Count == 0)
        {
            StatusRing.Percent = 0;
            StatusRing.RingBrush = (System.Windows.Media.Brush)FindResource("Accent300Brush");
            StatusRing.CenterValue = "–";
            StatusRing.CenterLabel = "Kein Backup";
            StatusTitleText.Text = "Noch kein Backup";
            StatusMetaText.Text = "Das erste Backup startet mit \"Jetzt sichern\".";
        }
        else
        {
            var lastRunStartedAt = groupRuns.Min(r => r.StartedAt);
            var allSuccess = groupRuns.All(r => r.Status == RunStatus.Success);
            var anyCancelled = groupRuns.Any(r => r.Status == RunStatus.Cancelled);

            if (allSuccess)
            {
                StatusRing.Percent = 100;
                StatusRing.RingBrush = (System.Windows.Media.Brush)FindResource("RingGradientBrush");
                StatusRing.CenterValue = "100%";
                StatusRing.CenterLabel = "Abgesichert";
                StatusTitleText.Text = "Letztes Backup erfolgreich";
            }
            else if (anyCancelled)
            {
                StatusRing.Percent = 0;
                StatusRing.RingBrush = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
                StatusRing.CenterValue = "–";
                StatusRing.CenterLabel = "Abgebrochen";
                StatusTitleText.Text = "Letztes Backup abgebrochen";
            }
            else
            {
                StatusRing.Percent = 100;
                StatusRing.RingBrush = (System.Windows.Media.Brush)FindResource("Accent600Brush");
                StatusRing.CenterValue = "!";
                StatusRing.CenterLabel = "Fehlgeschlagen";
                StatusTitleText.Text = "Letztes Backup fehlgeschlagen";
            }

            var totalBytes = groupRuns.Sum(r => r.TotalBytes);
            StatusMetaText.Text = $"{lastRunStartedAt.ToLocalTime():g} · {FormatBytes(totalBytes)} gesichert";
            LastSizeText.Text = FormatBytes(totalBytes);
        }

        NextRunText.Text = DescribeNextRun(job, groupRuns.Count > 0 ? groupRuns.Min(r => r.StartedAt) : null);
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
        RestoreRing.Percent = 0;
        RestoreRing.CenterValue = "0%";
        RestoreRing.CenterLabel = "läuft";
        RestoreCurrentFileText.Text = "";

        _restoreCts = new CancellationTokenSource();
        var progress = new Progress<TransferProgress>(p =>
        {
            var pct = p.BytesTotal == 0 ? 0 : (double)p.BytesDone / p.BytesTotal * 100;
            RestoreRing.Percent = pct;
            RestoreRing.CenterValue = $"{(int)pct}%";
            RestoreCurrentFileText.Text = p.CurrentFile;
        });

        var restoreRunner = new RestoreRunner(App.RunFileRepository, App.QuarantineRepository, App.Runner.RunLock, Serilog.Log.Logger);

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
                ScheduleType.Weekdays => 3,
                ScheduleType.Weekly => 4,
                ScheduleType.Monthly => 5,
                ScheduleType.OnChange => 6,
                _ => 0,
            };
            SettingsIntervalHoursBox.Text = job.IntervalHours?.ToString() ?? "";
            SettingsDailyAtTimeBox.Text = job.DailyAtTime?.ToString("HH:mm") ?? "";
            SettingsWeeklyDayCombo.SelectedIndex = job.WeeklyDay is { } weeklyDay ? DayOfWeekToComboIndex(weeklyDay) : -1;
            SettingsMonthlyDayBox.Text = job.MonthlyDay?.ToString() ?? "";
            SettingsVerifyTargetCheckBox.IsChecked = job.VerifyTargetBeforeRun;
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
            SettingsWeeklyDayCombo.SelectedIndex = -1;
            SettingsMonthlyDayBox.Text = "";
            SettingsVerifyTargetCheckBox.IsChecked = false;
        }
    }

    // SettingsWeeklyDayCombo lists Montag..Sonntag (index 0-6); DayOfWeek numbers Sunday=0..Saturday=6.
    private static int DayOfWeekToComboIndex(DayOfWeek day) => ((int)day + 6) % 7;
    private static DayOfWeek ComboIndexToDayOfWeek(int index) => (DayOfWeek)((index + 1) % 7);

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
        // DailyAt, Weekdays, Weekly, and Monthly all share the same "time of day" field.
        var hasTimeOfDay = SettingsScheduleTypeCombo.SelectedIndex is 2 or 3 or 4 or 5;
        var isWeekly = SettingsScheduleTypeCombo.SelectedIndex == 4;
        var isMonthly = SettingsScheduleTypeCombo.SelectedIndex == 5;

        SettingsIntervalLabel.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
        SettingsIntervalHoursBox.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
        SettingsWeeklyDayLabel.Visibility = isWeekly ? Visibility.Visible : Visibility.Collapsed;
        SettingsWeeklyDayCombo.Visibility = isWeekly ? Visibility.Visible : Visibility.Collapsed;
        SettingsMonthlyDayLabel.Visibility = isMonthly ? Visibility.Visible : Visibility.Collapsed;
        SettingsMonthlyDayBox.Visibility = isMonthly ? Visibility.Visible : Visibility.Collapsed;
        SettingsDailyAtLabel.Visibility = hasTimeOfDay ? Visibility.Visible : Visibility.Collapsed;
        SettingsDailyAtTimeBox.Visibility = hasTimeOfDay ? Visibility.Visible : Visibility.Collapsed;
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
            3 => ScheduleType.Weekdays,
            4 => ScheduleType.Weekly,
            5 => ScheduleType.Monthly,
            6 => ScheduleType.OnChange,
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

        // DailyAt, Weekdays, Weekly, and Monthly all share the same "time of day" field.
        TimeOnly? dailyAtTime = null;
        if (scheduleType is ScheduleType.DailyAt or ScheduleType.Weekdays or ScheduleType.Weekly or ScheduleType.Monthly)
        {
            if (!TimeOnly.TryParse(SettingsDailyAtTimeBox.Text, out var time))
            {
                System.Windows.MessageBox.Show(this, "Bitte eine gültige Uhrzeit im Format HH:mm angeben.", "SparkVault",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            dailyAtTime = time;
        }

        DayOfWeek? weeklyDay = null;
        if (scheduleType == ScheduleType.Weekly)
        {
            if (SettingsWeeklyDayCombo.SelectedIndex < 0)
            {
                System.Windows.MessageBox.Show(this, "Bitte einen Wochentag auswählen.", "SparkVault",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            weeklyDay = ComboIndexToDayOfWeek(SettingsWeeklyDayCombo.SelectedIndex);
        }

        int? monthlyDay = null;
        if (scheduleType == ScheduleType.Monthly)
        {
            if (!int.TryParse(SettingsMonthlyDayBox.Text, out var day) || day is < 1 or > 31)
            {
                System.Windows.MessageBox.Show(this, "Bitte einen Tag im Monat zwischen 1 und 31 angeben.", "SparkVault",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            monthlyDay = day;
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
            WeeklyDay = weeklyDay,
            MonthlyDay = monthlyDay,
            VerifyTargetBeforeRun = SettingsVerifyTargetCheckBox.IsChecked == true,
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
            DeleteJobButton.IsEnabled = true;
            ReloadJobs();
            NavOverview.IsChecked = true;
        }
        else
        {
            App.JobRepository.Update(job);
            ReloadJobs();
            LoadSettings();
        }
    }

    private async void RunNowButton_Click(object sender, RoutedEventArgs e)
    {
        var job = CurrentJob;
        if (job is null) return;

        IdleView.Visibility = Visibility.Collapsed;
        RunningView.Visibility = Visibility.Visible;
        RunRing.Percent = 0;
        RunRing.CenterValue = "0%";
        RunRing.CenterLabel = "läuft";
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
            var pct = p.BytesTotal == 0 ? 0 : (double)p.BytesDone / p.BytesTotal * 100;
            RunRing.Percent = pct;
            RunRing.CenterValue = $"{(int)pct}%";
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
            ReloadJobs();
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

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
