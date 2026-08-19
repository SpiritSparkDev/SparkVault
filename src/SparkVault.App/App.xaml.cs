using System.IO;
using System.Windows;
using System.Windows.Forms;
using Serilog;
using SparkVault.Core;
using Application = System.Windows.Application;

namespace SparkVault.App;

public partial class App : Application
{
    public static JobRepository JobRepository { get; private set; } = null!;
    public static RunRepository RunRepository { get; private set; } = null!;
    public static BackupRunner Runner { get; private set; } = null!;

    private BackgroundScheduler? _scheduler;
    private NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SparkVault");
        Directory.CreateDirectory(appDataDir);

        var connectionString = $"Data Source={Path.Combine(appDataDir, "sparkvault.db")}";
        SparkVaultDatabase.EnsureCreated(connectionString);

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(appDataDir, "log.txt"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        JobRepository = new JobRepository(connectionString);
        RunRepository = new RunRepository(connectionString);
        Runner = new BackupRunner(RunRepository, Log.Logger);

        _scheduler = new BackgroundScheduler(
            JobRepository,
            RunRepository,
            Runner,
            job => new LocalTarget(job.DestinationPath),
            pollInterval: TimeSpan.FromMinutes(1));

        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "SparkVault - bereit",
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Beenden", null, (_, _) => Shutdown());
        _trayIcon.ContextMenuStrip = menu;

        Runner.RunStarted += job => Dispatcher.Invoke(() => SetTrayStatus(running: true, job.Name));
        Runner.RunCompleted += (job, status) => Dispatcher.Invoke(() => SetTrayStatus(running: false, job.Name, status));
    }

    private void SetTrayStatus(bool running, string jobName, RunStatus? status = null)
    {
        if (_trayIcon is null) return;

        if (running)
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
            _trayIcon.Text = Truncate($"SparkVault - sichert \"{jobName}\"...");
        }
        else if (status == RunStatus.Failed)
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Warning;
            _trayIcon.Text = Truncate($"SparkVault - Fehler bei \"{jobName}\"");
        }
        else
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
            _trayIcon.Text = "SparkVault - bereit";
        }
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];

    private void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            MainWindow = new MainWindow();
        }
        MainWindow.Show();
        MainWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _scheduler?.DisposeAsync().AsTask().Wait();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
