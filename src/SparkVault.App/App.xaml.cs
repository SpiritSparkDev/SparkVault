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

    /// <summary>Single place that decides how a job's backup target is built.</summary>
    public static IBackupTarget CreateTarget(BackupJob job) => new LocalTarget(job.DestinationPath);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Logger.Fatal(args.Exception, "Unhandled UI exception");
            System.Windows.MessageBox.Show($"Ein unerwarteter Fehler ist aufgetreten: {args.Exception.Message}",
                "SparkVault", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SparkVault");
        Directory.CreateDirectory(appDataDir);

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(appDataDir, "log.txt"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        EnsureAutostartRegistered();

        var connectionString = $"Data Source={Path.Combine(appDataDir, "sparkvault.db")}";
        SparkVaultDatabase.EnsureCreated(connectionString);

        JobRepository = new JobRepository(connectionString);
        RunRepository = new RunRepository(connectionString);
        Runner = new BackupRunner(RunRepository, Log.Logger);

        _scheduler = new BackgroundScheduler(
            JobRepository,
            RunRepository,
            Runner,
            CreateTarget,
            pollInterval: TimeSpan.FromMinutes(1),
            Log.Logger);

        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "SparkVault - bereit",
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new ContextMenuStrip();

        var runNowMenu = new ToolStripMenuItem("Jetzt sichern");
        // A ToolStripMenuItem with an empty DropDownItems collection never opens,
        // so seed one placeholder; DropDownOpening replaces it with the real jobs.
        runNowMenu.DropDownItems.Add(new ToolStripMenuItem("(keine Jobs)") { Enabled = false });
        runNowMenu.DropDownOpening += (_, _) =>
        {
            runNowMenu.DropDownItems.Clear();
            foreach (var job in JobRepository.GetAll())
            {
                var jobItem = new ToolStripMenuItem(job.Name);
                jobItem.Click += async (_, _) =>
                    await Runner.RunAsync(job, CreateTarget(job), progress: null, CancellationToken.None);
                runNowMenu.DropDownItems.Add(jobItem);
            }
            if (runNowMenu.DropDownItems.Count == 0)
                runNowMenu.DropDownItems.Add(new ToolStripMenuItem("(keine Jobs)") { Enabled = false });
        };
        menu.Items.Add(runNowMenu);

        menu.Items.Add("Öffnen", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Beenden", null, (_, _) => Shutdown());
        _trayIcon.ContextMenuStrip = menu;

        Runner.RunStarted += job => Dispatcher.BeginInvoke(() => SetTrayStatus(running: true, job.Name));
        Runner.RunCompleted += (job, status) => Dispatcher.BeginInvoke(() => SetTrayStatus(running: false, job.Name, status));
    }

    private static void EnsureAutostartRegistered()
    {
        const string valueName = "SparkVault";
        try
        {
            var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;

            var current = key.GetValue(valueName) as string;
            if (!string.Equals(current, exePath, StringComparison.OrdinalIgnoreCase))
                key.SetValue(valueName, exePath);
        }
        catch (Exception ex)
        {
            // Registry access can fail in locked-down environments - never block startup.
            Log.Logger.Warning(ex, "Autostart registration failed");
        }
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
