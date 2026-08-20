using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using SparkVault.Core;

namespace SparkVault.App;

public partial class JobEditorWindow : Window
{
    private sealed class TargetListItem
    {
        public required BackupTarget Target { get; init; }

        public string Description => Target.Type switch
        {
            TargetType.Local => $"Lokal: {Target.DestinationPath}",
            TargetType.Ftp => $"FTP: {Target.Host}",
            TargetType.Sftp => $"SFTP: {Target.Host}",
            _ => Target.Type.ToString(),
        };
    }

    private readonly int? _jobId;
    private readonly ObservableCollection<TargetListItem> _targets = new();

    public JobEditorWindow(int? jobId)
    {
        InitializeComponent();
        TargetsListBox.ItemsSource = _targets;
        _jobId = jobId;

        if (_jobId is { } id)
        {
            var job = App.JobRepository.GetById(id);
            if (job is not null)
                LoadJob(job);
        }
        else
        {
            ScheduleTypeCombo.SelectedIndex = 0;
        }
    }

    private void LoadJob(BackupJob job)
    {
        NameBox.Text = job.Name;
        SourcePathBox.Text = job.SourcePath;
        ExcludePatternsBox.Text = string.Join(Environment.NewLine, job.ExcludePatterns);
        foreach (var target in job.Targets)
            _targets.Add(new TargetListItem { Target = target });
        ScheduleTypeCombo.SelectedIndex = job.ScheduleType switch
        {
            ScheduleType.None => 0,
            ScheduleType.Interval => 1,
            ScheduleType.DailyAt => 2,
            _ => 0,
        };
        IntervalHoursBox.Text = job.IntervalHours?.ToString() ?? "";
        DailyAtTimeBox.Text = job.DailyAtTime?.ToString("HH:mm") ?? "";
    }

    private void ScheduleTypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var isInterval = ScheduleTypeCombo.SelectedIndex == 1;
        var isDailyAt = ScheduleTypeCombo.SelectedIndex == 2;
        IntervalLabel.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
        IntervalHoursBox.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
        DailyAtLabel.Visibility = isDailyAt ? Visibility.Visible : Visibility.Collapsed;
        DailyAtTimeBox.Visibility = isDailyAt ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SourcePathBox.Text = dialog.SelectedPath;
    }

    private void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        var editor = new TargetEditorWindow(existing: null) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
            _targets.Add(new TargetListItem { Target = editor.Result });
    }

    private void EditTarget_Click(object sender, RoutedEventArgs e)
    {
        if (TargetsListBox.SelectedItem is not TargetListItem selected) return;

        var editor = new TargetEditorWindow(existing: selected.Target) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
        {
            var index = _targets.IndexOf(selected);
            _targets[index] = new TargetListItem { Target = editor.Result };
        }
    }

    private void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (TargetsListBox.SelectedItem is TargetListItem selected)
            _targets.Remove(selected);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(SourcePathBox.Text))
        {
            MessageBox.Show(this, "Name und Quellpfad sind Pflichtfelder.", "SparkVault",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_targets.Count == 0)
        {
            MessageBox.Show(this, "Bitte mindestens ein Ziel hinzufügen.", "SparkVault",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // A local target inside the source makes every run re-scan its own output.
        var fullSource = Path.GetFullPath(SourcePathBox.Text.Trim());
        var sourcePrefix = fullSource.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var item in _targets)
        {
            if (item.Target.Type != TargetType.Local || item.Target.DestinationPath is null)
                continue;

            var fullDest = Path.GetFullPath(item.Target.DestinationPath);
            if (fullDest.Equals(fullSource, StringComparison.OrdinalIgnoreCase) ||
                fullDest.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Ein lokales Ziel darf nicht innerhalb des Quellpfads liegen.", "SparkVault",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var scheduleType = ScheduleTypeCombo.SelectedIndex switch
        {
            1 => ScheduleType.Interval,
            2 => ScheduleType.DailyAt,
            _ => ScheduleType.None,
        };

        int? intervalHours = null;
        if (scheduleType == ScheduleType.Interval)
        {
            if (!int.TryParse(IntervalHoursBox.Text, out var hours) || hours <= 0)
            {
                MessageBox.Show(this, "Bitte eine gültige Stundenzahl angeben.", "SparkVault",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            intervalHours = hours;
        }

        TimeOnly? dailyAtTime = null;
        if (scheduleType == ScheduleType.DailyAt)
        {
            if (!TimeOnly.TryParse(DailyAtTimeBox.Text, out var time))
            {
                MessageBox.Show(this, "Bitte eine gültige Uhrzeit im Format HH:mm angeben.", "SparkVault",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            dailyAtTime = time;
        }

        var job = new BackupJob
        {
            Id = _jobId ?? 0,
            Name = NameBox.Text.Trim(),
            SourcePath = SourcePathBox.Text.Trim(),
            ExcludePatterns = ExcludePatternsBox.Text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            ScheduleType = scheduleType,
            IntervalHours = intervalHours,
            DailyAtTime = dailyAtTime,
            Targets = _targets.Select(t => t.Target).ToList(),
        };

        if (_jobId is null)
            App.JobRepository.Add(job);
        else
            App.JobRepository.Update(job);

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
