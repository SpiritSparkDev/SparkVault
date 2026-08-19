using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using SparkVault.Core;

namespace SparkVault.App;

public partial class JobEditorWindow : Window
{
    private readonly int? _jobId;

    public JobEditorWindow(int? jobId)
    {
        InitializeComponent();
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
        DestinationPathBox.Text = job.DestinationPath;
        ExcludePatternsBox.Text = string.Join(Environment.NewLine, job.ExcludePatterns);
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
        var path = PickFolder();
        if (path is not null) SourcePathBox.Text = path;
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder();
        if (path is not null) DestinationPathBox.Text = path;
    }

    private static string? PickFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) ||
            string.IsNullOrWhiteSpace(SourcePathBox.Text) ||
            string.IsNullOrWhiteSpace(DestinationPathBox.Text))
        {
            MessageBox.Show(this, "Name, Quellpfad und Zielpfad sind Pflichtfelder.", "SparkVault",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Destination inside the source makes every run re-scan its own output.
        var fullSource = Path.GetFullPath(SourcePathBox.Text.Trim());
        var fullDest = Path.GetFullPath(DestinationPathBox.Text.Trim());
        var sourcePrefix = fullSource.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (fullDest.Equals(fullSource, StringComparison.OrdinalIgnoreCase) ||
            fullDest.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Der Zielpfad darf nicht innerhalb des Quellpfads liegen.", "SparkVault",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
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
            DestinationPath = DestinationPathBox.Text.Trim(),
            ExcludePatterns = ExcludePatternsBox.Text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            ScheduleType = scheduleType,
            IntervalHours = intervalHours,
            DailyAtTime = dailyAtTime,
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
