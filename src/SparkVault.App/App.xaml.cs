using System.IO;
using System.Windows;
using System.Windows.Forms;
using Serilog;
using SparkVault.Core;
using Application = System.Windows.Application;

namespace SparkVault.App;

public partial class App : Application
{
    public static string ConnectionString { get; private set; } = null!;
    public static JobRepository JobRepository { get; private set; } = null!;
    public static RunRepository RunRepository { get; private set; } = null!;
    public static RunFileRepository RunFileRepository { get; private set; } = null!;
    public static QuarantineRepository QuarantineRepository { get; private set; } = null!;
    public static BackupRunner Runner { get; private set; } = null!;

    private BackgroundScheduler? _scheduler;
    private CancellationTokenSource? _onChangeCts;
    private Task? _onChangeTask;
    private NotifyIcon? _trayIcon;
    private bool _welcomeChecked;
    private string _welcomeMarkerPath = null!;
    private MainWindow? _mainWindowInstance;

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
        _welcomeMarkerPath = Path.Combine(appDataDir, ".welcome-shown");

        ThemeManager.Initialize(appDataDir);
        ThemeManager.Apply(ThemeManager.CurrentMode);
        // Live-track OS theme changes while "System" is selected, so the app follows Windows'
        // light/dark switch without needing a restart.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category != Microsoft.Win32.UserPreferenceCategory.General || ThemeManager.CurrentMode != ThemeMode.System)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                ThemeManager.Apply(ThemeMode.System);
                _mainWindowInstance?.RefreshTheme();
            });
        };

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(appDataDir, "log.txt"), rollingInterval: RollingInterval.Day)
            .CreateLogger();
        Log.Information("SparkVault gestartet (PID {ProcessId})", Environment.ProcessId);

        EnsureAutostartRegistered();

        var connectionString = $"Data Source={Path.Combine(appDataDir, "sparkvault.db")}";
        SparkVaultDatabase.EnsureCreated(connectionString);

        ConnectionString = connectionString;
        JobRepository = new JobRepository(connectionString);
        RunRepository = new RunRepository(connectionString);
        RunFileRepository = new RunFileRepository(connectionString);
        QuarantineRepository = new QuarantineRepository(connectionString);
        Runner = new BackupRunner(RunRepository, RunFileRepository, QuarantineRepository, Log.Logger);

        _scheduler = new BackgroundScheduler(
            JobRepository,
            RunRepository,
            Runner,
            pollInterval: TimeSpan.FromMinutes(1),
            Log.Logger);

        _trayIcon = new NotifyIcon
        {
            Icon = AppTrayIcon,
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
                    await Runner.RunAsync(job, progress: null, CancellationToken.None);
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

        // First-ever launch: greet the user proactively instead of leaving them to discover the
        // tray icon on their own before they've even created a job. Every later launch still
        // starts silently in the tray, unchanged.
        if (!File.Exists(_welcomeMarkerPath) && JobRepository.GetAll().Count == 0)
            ShowMainWindow();

        _onChangeCts = new CancellationTokenSource();
        _onChangeTask = Task.Run(() => OnChangeJobChecker.RunDueJobsAsync(
            JobRepository, RunRepository, RunFileRepository, Runner, Log.Logger, _onChangeCts.Token));
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
            _trayIcon.Icon = AppTrayIcon;
            _trayIcon.Text = Truncate($"SparkVault - sichert \"{jobName}\"...");
        }
        else if (status == RunStatus.Failed)
        {
            // Warning stays a system icon on purpose: a scan of the tray for problems benefits
            // from a universally recognizable shape more than from brand consistency here.
            _trayIcon.Icon = System.Drawing.SystemIcons.Warning;
            _trayIcon.Text = Truncate($"SparkVault - Fehler bei \"{jobName}\"");
            _trayIcon.ShowBalloonTip(5000, "SparkVault", $"Sicherung von \"{jobName}\" fehlgeschlagen.", ToolTipIcon.Warning);
        }
        else
        {
            _trayIcon.Icon = AppTrayIcon;
            _trayIcon.Text = "SparkVault - bereit";
        }
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];

    // The app's own logo (baked into the exe via <ApplicationIcon>), not the generic Windows
    // "Application" icon the tray previously fell back to.
    private static readonly System.Drawing.Icon AppTrayIcon = LoadAppTrayIcon();

    private static System.Drawing.Icon LoadAppTrayIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            return System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    // First-ever launch (no jobs yet, welcome never shown before): greet the user and hand off
    // to job creation instead of dropping them on an empty hero-card list with no explanation.
    // A marker file (not a DB flag) keeps this one-time regardless of jobs being deleted later.
    public void ShowMainWindow()
    {
        if (!_welcomeChecked)
        {
            _welcomeChecked = true;
            if (!File.Exists(_welcomeMarkerPath) && JobRepository.GetAll().Count == 0)
            {
                File.WriteAllText(_welcomeMarkerPath, "");
                new WelcomeWindow().Show();
                return;
            }
        }

        // WPF auto-assigns Application.MainWindow to the first Window ever shown — since
        // WelcomeWindow can be shown before this ever runs, checking "MainWindow is null" here
        // would silently skip constructing the real MainWindow. Track it ourselves instead.
        if (_mainWindowInstance is null)
        {
            _mainWindowInstance = new MainWindow();
            MainWindow = _mainWindowInstance;
        }
        _mainWindowInstance.Show();
        _mainWindowInstance.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("SparkVault wird beendet (ExitCode={ExitCode})", e.ApplicationExitCode);
        _trayIcon?.Dispose();
        _scheduler?.DisposeAsync().AsTask().Wait();
        _onChangeCts?.Cancel();
        try { _onChangeTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort on shutdown */ }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
